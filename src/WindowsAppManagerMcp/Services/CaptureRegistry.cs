using System.Globalization;
using System.Security.Cryptography;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The capture registry (roadmap M3.11): the last <see cref="DefaultCapacity"/> captures used, each for
/// <see cref="DefaultTimeToLive"/> after it was taken. Ids are <c>c&lt;n&gt;-&lt;tag&gt;</c>: n counts the captures of
/// this server run, so an id is never reused while it runs, and the tag is random per run, so an id the agent kept from
/// an earlier run does not find a capture of this one. Thread-safe.
/// </summary>
public sealed class CaptureRegistry : ICaptureRegistry
{
    /// <summary>How many captures are kept.</summary>
    public const int DefaultCapacity = 32;

    /// <summary>How long a capture can be found.</summary>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly string _tag = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    // Most recently used first; the dictionary finds the node of an id
    private readonly LinkedList<Entry> _entries = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byId = new(StringComparer.Ordinal);
    private long _sequence;

    /// <param name="clock">The clock; default <see cref="TimeProvider.System"/>. Expiry follows its monotonic clock.</param>
    public CaptureRegistry(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public int Capacity => DefaultCapacity;

    /// <inheritdoc />
    public TimeSpan TimeToLive => DefaultTimeToLive;

    /// <inheritdoc />
    public CaptureRecord Register(CaptureGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        lock (_lock)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"c{++_sequence}-{_tag}");
            var record = new CaptureRecord(id, _clock.GetUtcNow(), geometry);
            _byId[id] = _entries.AddFirst(new Entry(record, _clock.GetTimestamp()));

            while (_entries.Count > Capacity)
            {
                Remove(_entries.Last!);
            }

            return record;
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
    }

    /// <summary>A capture and when it was registered, on the monotonic clock.</summary>
    private sealed record Entry(CaptureRecord Record, long Registered);
}
