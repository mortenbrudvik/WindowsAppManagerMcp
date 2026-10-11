using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <inheritdoc />
public sealed class ClipboardService : IClipboardService
{
    /// <summary>get_clipboard's default maxLength.</summary>
    public const int DefaultMaxLength = 20_000;

    /// <summary>The largest maxLength get_clipboard takes.</summary>
    public const int MaxReadLength = 1_000_000;

    /// <summary>The longest text set_clipboard takes.</summary>
    public const int MaxTextLength = 1_000_000;

    /// <summary>How long a read waits for the application to copy after the agent's copy keys.</summary>
    public static readonly TimeSpan CopyWait = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan CopyPoll = TimeSpan.FromMilliseconds(50);

    private readonly INativeClipboardWrapper _clipboard;
    private readonly IClipboardOrigin _origin;
    private readonly ClipboardOptions _options;
    private readonly TimeSpan _copyWait;
    private readonly object _declinedLock = new();
    private uint? _declined;
    private int _questionOpen;

    /// <param name="clipboard">The clipboard.</param>
    /// <param name="origin">Which content the agent put there.</param>
    /// <param name="options">The user's setting; default <see cref="ClipboardOptions.Default"/>.</param>
    /// <param name="logger">Logs a value of WAM_ALLOW_USER_CLIPBOARD that was not understood.</param>
    /// <param name="copyWait">How long a read waits for a pending copy; default <see cref="CopyWait"/>.</param>
    public ClipboardService(
        INativeClipboardWrapper clipboard,
        IClipboardOrigin origin,
        ClipboardOptions? options = null,
        ILogger<ClipboardService>? logger = null,
        TimeSpan? copyWait = null)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _options = options ?? ClipboardOptions.Default;
        _copyWait = copyWait ?? CopyWait;
        if (_options.Warning is { } warning)
        {
            logger?.LogWarning("{Warning}", warning);
        }
    }

    public bool AllowsUserClipboard => _options.AllowUserClipboard;

    public uint SequenceNumber() => _clipboard.GetSequenceNumber();

    public async Task<GetClipboardResult> ReadAsync(ClipboardFormat format, int maxLength, ClipboardConsent? consent,
        CancellationToken cancellationToken)
    {
        maxLength = Math.Clamp(maxLength, 1, MaxReadLength);
        var userAllowed = _options.AllowUserClipboard || consent != null;

        // Right after the agent's copy keys the application may not have copied yet
        var sequenceNumber = _clipboard.GetSequenceNumber();
        var waited = TimeSpan.Zero;
        while (_origin.IsUnchangedSinceCopyKeys(sequenceNumber) && waited < _copyWait)
        {
            await Task.Delay(CopyPoll, cancellationToken).ConfigureAwait(false);
            waited += CopyPoll;
            sequenceNumber = _clipboard.GetSequenceNumber();
        }

        // The agent meant its own copy: what was there before is not it, whoever put it there
        if (_origin.IsUnchangedSinceCopyKeys(sequenceNumber))
        {
            return Failed(format, ClipboardErrorCode.NothingCopied,
                $"The copy keys were sent, but the clipboard did not change within {_copyWait.TotalSeconds:0.#} s: nothing may have " +
                "been selected, or the application did not copy. Nothing was read. Check the selection and copy again.");
        }

        var kind = _origin.Classify(sequenceNumber);
        if (!IsAgents(kind) && !userAllowed)
        {
            return NotFromAgent(format);
        }

        // Content marked private is read only when it is the text set_clipboard put there (which carries those markers),
        // and only while the clipboard still has that content's number
        var privateContentSequence = kind == ClipboardOriginKind.SetClipboard && format == ClipboardFormat.Text ? sequenceNumber : 0;
        var snapshot = _clipboard.Read(format, maxLength, privateContentSequence);
        if (!snapshot.Opened)
        {
            return Failed(format, ClipboardErrorCode.ClipboardBusy,
                "Another application kept the clipboard open (or did not answer), so it could not be read. Try again in a moment.");
        }

        // The number read while the clipboard was open belongs to the content read: decide on that one, and take content
        // numbered as set_clipboard's as the server's only when it is the server's text (the number alone could be another
        // application's that replaced the text right after set_clipboard)
        kind = _origin.Classify(snapshot.SequenceNumber);
        if (kind == ClipboardOriginKind.SetClipboard && !IsServerContent(snapshot, format))
        {
            kind = ClipboardOriginKind.NotFromAgent;
        }
        if (consent != null && snapshot.SequenceNumber != consent.SequenceNumber && !IsAgents(kind))
        {
            return Failed(format, ClipboardErrorCode.ClipboardNotFromAgent,
                "The clipboard changed after the user allowed reading it, so the new content was not read: the user agreed to " +
                "what was there when they were asked. Call get_clipboard again to ask about the new content.");
        }

        if (!IsAgents(kind) && !userAllowed)
        {
            return NotFromAgent(format);
        }

        if (snapshot.IsPrivate && kind != ClipboardOriginKind.SetClipboard)
        {
            return Failed(format, ClipboardErrorCode.ClipboardPrivate,
                $"The clipboard's content is marked private ({DescribeMarkers(snapshot.Markers)}), as password managers mark the " +
                "passwords and codes they copy, so it is not returned, whoever copied it and whatever the user allowed. If it is " +
                "needed somewhere, ask the user to paste it there.");
        }

        var origin = kind switch
        {
            ClipboardOriginKind.SetClipboard => ClipboardContentOrigin.SetClipboard,
            ClipboardOriginKind.AgentInput => ClipboardContentOrigin.AgentInput,
            _ => ClipboardContentOrigin.User
        };

        if (!Holds(snapshot, format))
        {
            var available = Available(snapshot);
            return Failed(format, ClipboardErrorCode.FormatNotAvailable,
                available.Count == 0
                    ? $"The clipboard holds no {Name(format)} (it is empty, or holds only other formats such as an image)."
                    : $"The clipboard holds no {Name(format)}; it holds {string.Join(" and ", available.Select(Name))}. Ask for that format.") with
            {
                AvailableFormats = available
            };
        }

        if (snapshot.Error is { } error)
        {
            return Failed(format, ClipboardErrorCode.ClipboardFailed, $"The clipboard's {Name(format)} could not be read: {error}");
        }

        if (consent != null && origin == ClipboardContentOrigin.User)
        {
            // The agent has now seen this content with the user's yes, so it may paste it too (the paste guard)
            _origin.UserAllowed(snapshot.SequenceNumber);
        }

        return format switch
        {
            ClipboardFormat.Html => HtmlResult(snapshot, maxLength, origin),
            ClipboardFormat.Files => FilesResult(snapshot, maxLength, origin),
            _ => TextResult(snapshot, maxLength, origin)
        };
    }

    public SetClipboardResult SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var outcome = _clipboard.WriteText(text);
        if (!outcome.Opened)
        {
            return new SetClipboardResult(false, 0, Error: outcome.Error ??
                "Another application kept the clipboard open, so the text was not put on it. Try again in a moment.",
                ErrorCode: ClipboardErrorCode.ClipboardBusy);
        }

        if (!outcome.Success)
        {
            return new SetClipboardResult(false, 0, Error: $"Windows did not put the text on the clipboard: {outcome.Error}",
                ErrorCode: ClipboardErrorCode.ClipboardFailed);
        }

        _origin.ServerWrote(outcome.SequenceNumber, text);
        return new SetClipboardResult(true, text.Length, ExcludedFromHistory: outcome.ExcludedFromHistory);
    }

    public void UserDeclined(uint sequenceNumber)
    {
        lock (_declinedLock)
        {
            _declined = sequenceNumber == 0 ? null : sequenceNumber;
        }
    }

    public bool WasDeclined(uint sequenceNumber)
    {
        lock (_declinedLock)
        {
            return sequenceNumber != 0 && _declined == sequenceNumber;
        }
    }

    public IDisposable? TryOpenQuestion() =>
        Interlocked.CompareExchange(ref _questionOpen, 1, 0) == 0 ? new OpenQuestion(this) : null;

    /// <summary>The open question; disposing it (once) lets the next call ask.</summary>
    private sealed class OpenQuestion(ClipboardService owner) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                Volatile.Write(ref owner._questionOpen, 0);
            }
        }
    }

    /// <summary>
    /// True when the content read can be set_clipboard's: it holds no HTML or files (set_clipboard puts only text there),
    /// and text read as text is the server's text.
    /// </summary>
    private bool IsServerContent(ClipboardSnapshot snapshot, ClipboardFormat format) =>
        !snapshot.HasHtml && !snapshot.HasFiles &&
        (format != ClipboardFormat.Text || !snapshot.HasText || _origin.IsServerText(snapshot.Text, snapshot.TextLength));

    private static bool IsAgents(ClipboardOriginKind kind) =>
        kind is ClipboardOriginKind.SetClipboard or ClipboardOriginKind.AgentInput;

    private static GetClipboardResult NotFromAgent(ClipboardFormat format) => Failed(format,
        ClipboardErrorCode.ClipboardNotFromAgent,
        "The clipboard holds content the agent did not put there: the user or another application copied it. It is not " +
        "returned without the user's consent, because it can be a password or other private text. To read text from an " +
        "application, select it and copy it with send_keys '^c' (with windowHandle), then call get_clipboard: the first copy " +
        "the application the keys went to makes within 5 s (2 s after a click) counts as the agent's. A copy written by a " +
        "helper process of that application (as in apps built on WebView2) does not count. Otherwise ask the user.");

    private static GetClipboardResult Failed(ClipboardFormat format, ClipboardErrorCode code, string error) =>
        new(false, format, Error: error, ErrorCode: code);

    private static bool Holds(ClipboardSnapshot snapshot, ClipboardFormat format) => format switch
    {
        ClipboardFormat.Html => snapshot.HasHtml,
        ClipboardFormat.Files => snapshot.HasFiles,
        _ => snapshot.HasText
    };

    private static List<ClipboardFormat> Available(ClipboardSnapshot snapshot)
    {
        var formats = new List<ClipboardFormat>();
        if (snapshot.HasText)
        {
            formats.Add(ClipboardFormat.Text);
        }

        if (snapshot.HasHtml)
        {
            formats.Add(ClipboardFormat.Html);
        }

        if (snapshot.HasFiles)
        {
            formats.Add(ClipboardFormat.Files);
        }

        return formats;
    }

    private static string Name(ClipboardFormat format) => format switch
    {
        ClipboardFormat.Html => "HTML",
        ClipboardFormat.Files => "files",
        _ => "text"
    };

    private static string DescribeMarkers(ClipboardPrivacyMarkers markers)
    {
        var names = new List<string>();
        if (markers.HasFlag(ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing))
        {
            names.Add("ExcludeClipboardContentFromMonitorProcessing");
        }

        if (markers.HasFlag(ClipboardPrivacyMarkers.NotForClipboardHistory))
        {
            names.Add("CanIncludeInClipboardHistory = 0");
        }

        if (markers.HasFlag(ClipboardPrivacyMarkers.ClipboardViewerIgnore))
        {
            names.Add("Clipboard Viewer Ignore");
        }

        return string.Join(", ", names);
    }

    private static GetClipboardResult TextResult(ClipboardSnapshot snapshot, int maxLength, ClipboardContentOrigin origin)
    {
        var text = snapshot.Text ?? string.Empty;
        var length = Math.Max(snapshot.TextLength, text.Length);
        var cut = Cut(text, maxLength);
        if (cut.Length < length && cut.Length > 0 && char.IsHighSurrogate(cut[^1]))
        {
            // The wrapper read exactly maxLength characters, which can end inside a surrogate pair
            cut = cut[..^1];
        }

        return new GetClipboardResult(true, ClipboardFormat.Text, Text: cut, Length: length, Truncated: cut.Length < length, Origin: origin);
    }

    private static GetClipboardResult HtmlResult(ClipboardSnapshot snapshot, int maxLength, ClipboardContentOrigin origin)
    {
        var content = ClipboardHtml.Parse(snapshot.Html ?? []);
        var cut = Cut(content.Html, maxLength);
        return new GetClipboardResult(true, ClipboardFormat.Html, Text: cut, Length: content.Html.Length,
            Truncated: cut.Length < content.Html.Length, Origin: origin, SourceUrl: content.SourceUrl);
    }

    private static GetClipboardResult FilesResult(ClipboardSnapshot snapshot, int maxLength, ClipboardContentOrigin origin)
    {
        var all = snapshot.Files ?? [];
        var count = Math.Max(snapshot.FileCount, all.Count);
        var files = new List<string>();
        var characters = 0;
        foreach (var path in all)
        {
            if (characters + path.Length > maxLength)
            {
                break;
            }

            files.Add(path);
            characters += path.Length;
        }

        return new GetClipboardResult(true, ClipboardFormat.Files, Files: files, Length: count, Truncated: files.Count < count, Origin: origin);
    }

    /// <summary><paramref name="text"/> cut to at most <paramref name="maxLength"/> characters, never inside a surrogate pair.</summary>
    private static string Cut(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..length];
    }
}
