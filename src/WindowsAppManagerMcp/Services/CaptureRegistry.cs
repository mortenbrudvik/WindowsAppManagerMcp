using System.Globalization;
using System.Security.Cryptography;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The capture registry (roadmap M3.11): the last <see cref="DefaultCapacity"/> captures used, each for
/// <see cref="DefaultTimeToLive"/> after it was taken. Ids are <c>c&lt;n&gt;-&lt;tag&gt;</c>: n counts the captures of
/// this server run, so an id is never reused while it runs, and the tag is random per run, so an id the agent kept from
/// an earlier run does not find a capture of this one. While wait_for_visual is served it also keeps each capture's visual
/// signature (roadmap M6.4), all of them together within <see cref="SignatureByteCap"/>: past it, the least recently used
/// captures lose their signatures but stay registered, so the cap never changes how long a captureId can be clicked on.
/// Thread-safe.
/// </summary>
public sealed class CaptureRegistry : ICaptureRegistry
{
    /// <summary>How many captures are kept.</summary>
    public const int DefaultCapacity = 32;

    /// <summary>How long a capture can be found.</summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The most memory the signatures of the captures kept may take together (owner decision D9): 16 MB, about 60
    /// signatures of a 4K monitor or eight of a 64-megapixel virtual screen.
    /// </summary>
    public const long SignatureByteCap = 16L * 1024 * 1024;

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly string _tag = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    // Most recently used first; the dictionary finds the node of an id
    private readonly LinkedList<Entry> _entries = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byId = new(StringComparer.Ordinal);
    private readonly long _signatureByteCap;
    private long _sequence;
    private long _signatureBytes;

    /// <param name="clock">The clock; default <see cref="TimeProvider.System"/>. Expiry follows its monotonic clock.</param>
    /// <param name="keepSignatures">Keep the captures' visual signatures (<see cref="ICaptureRegistry.KeepsSignatures"/>): the
    /// server passes true only while it serves wait_for_visual.</param>
    /// <param name="signatureByteCap">The signatures' memory cap; default <see cref="SignatureByteCap"/>. Tests pass a small one.</param>
    public CaptureRegistry(TimeProvider? clock = null, bool keepSignatures = false, long? signatureByteCap = null)
    {
        _clock = clock ?? TimeProvider.System;
        KeepsSignatures = keepSignatures;
        _signatureByteCap = signatureByteCap ?? SignatureByteCap;
    }

    /// <inheritdoc />
    public int Capacity => DefaultCapacity;

    /// <inheritdoc />
    public TimeSpan TimeToLive => DefaultTimeToLive;

    /// <inheritdoc />
    public bool KeepsSignatures { get; }

    /// <summary>The memory the signatures kept take now, as <see cref="VisualSignature.ByteSize"/> counts it.</summary>
    public long SignatureBytes
    {
        get
        {
            lock (_lock)
            {
                return _signatureBytes;
            }
        }
    }

    /// <inheritdoc />
    public CaptureRecord Register(CaptureGeometry geometry) => Register(geometry, signature: null);

    /// <inheritdoc />
    public CaptureRecord Register(CaptureGeometry geometry, VisualSignature? signature)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        lock (_lock)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"c{++_sequence}-{_tag}");
            var record = new CaptureRecord(id, _clock.GetUtcNow(), geometry, KeepsSignatures ? signature : null);
            _byId[id] = _entries.AddFirst(new Entry(record, _clock.GetTimestamp()));
            _signatureBytes += record.Signature?.ByteSize ?? 0;

            while (_entries.Count > Capacity)
            {
                Remove(_entries.Last!);
            }

            DropSignaturesOverTheCap();
            return _entries.First!.Value.Record;
        }
    }

    /// <inheritdoc />
    public CaptureRecord? Find(string captureId)
    {
        if (string.IsNullOrEmpty(captureId))
        {
            return null;
        }

        lock (_lock)
        {
            if (!_byId.TryGetValue(captureId, out var node))
            {
                return null;
            }

            if (_clock.GetElapsedTime(node.Value.Registered) >= TimeToLive)
            {
                Remove(node);
                return null;
            }

            _entries.Remove(node);
            _entries.AddFirst(node);
            return node.Value.Record;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node);
        _byId.Remove(node.Value.Record.CaptureId);
        _signatureBytes -= node.Value.Record.Signature?.ByteSize ?? 0;
    }

    /// <summary>
    /// Drops the signatures of the least recently used captures until the rest fit under the cap; the captures stay. A
    /// signature larger than the cap by itself is dropped too.
    /// </summary>
    private void DropSignaturesOverTheCap()
    {
        for (var node = _entries.Last; node != null && _signatureBytes > _signatureByteCap; node = node.Previous)
        {
            if (node.Value.Record.Signature is { } signature)
            {
                _signatureBytes -= signature.ByteSize;
                node.Value = node.Value with { Record = node.Value.Record with { Signature = null } };
            }
        }
    }

    /// <summary>A capture and when it was registered, on the monotonic clock.</summary>
    private sealed record Entry(CaptureRecord Record, long Registered);
}
