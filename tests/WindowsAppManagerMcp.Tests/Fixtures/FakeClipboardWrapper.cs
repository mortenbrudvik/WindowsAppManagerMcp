namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake clipboard: a sequence number, privacy markers and content the test sets, read and written the way
/// <see cref="NativeClipboardWrapper"/> reads and writes the real one (the sequence number changes with every write, and
/// private content is not read unless its number was given). The real clipboard is the user's, so no test may touch it.
/// </summary>
public sealed class FakeClipboardWrapper : INativeClipboardWrapper
{
    /// <summary>The process of the app <see cref="UserCopies"/> copies in: not one the agent's input goes to.</summary>
    public const int UserAppProcessId = 9000;

    /// <summary>The process that owns the clipboard after set_clipboard (the server's).</summary>
    public const int ServerProcessId = 4242;

    private readonly object _lock = new();

    /// <summary>The clipboard's sequence number.</summary>
    public uint Sequence { get; set; } = 100;

    /// <summary>The process of the window that owns the clipboard; 0: none.</summary>
    public int OwnerProcessId { get; set; }

    public ClipboardPrivacyMarkers Markers { get; set; }

    public string? Text { get; set; }

    public byte[]? Html { get; set; }

    public IReadOnlyList<string>? Files { get; set; }

    /// <summary>When false, the clipboard cannot be opened (another app holds it).</summary>
    public bool CanOpen { get; set; } = true;

    /// <summary>An error the read of the content reports, as when GetClipboardData fails.</summary>
    public string? ReadError { get; set; }

    /// <summary>The write fails with this error after the clipboard was opened.</summary>
    public string? WriteError { get; set; }

    /// <summary>Runs on every GetSequenceNumber call, before the number is returned (to copy "meanwhile").</summary>
    public Action? OnSequenceRead { get; set; }

    /// <summary>Runs at the start of every Read, before the clipboard is "opened" (to copy between the check and the read).</summary>
    public Action? OnRead { get; set; }

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    /// <summary>The privateContentSequence argument of the last read.</summary>
    public uint? LastPrivateContentSequence { get; private set; }

    /// <summary>True when the last read read content marked private (only with its sequence number).</summary>
    public bool? LastReadPrivate { get; private set; }

    /// <summary>The user copies text in an app of their own: the sequence number moves on.</summary>
    public void UserCopies(string text, ClipboardPrivacyMarkers markers = ClipboardPrivacyMarkers.None) =>
        AppCopies(UserAppProcessId, text, markers);

    /// <summary>The app of process <paramref name="processId"/> copies text: the sequence number moves on.</summary>
    public void AppCopies(int processId, string text, ClipboardPrivacyMarkers markers = ClipboardPrivacyMarkers.None)
    {
        lock (_lock)
        {
            Sequence++;
            OwnerProcessId = processId;
            Text = text;
            Html = null;
            Files = null;
            Markers = markers;
        }
    }

    /// <summary>Runs on every GetOwnerProcessId call, before the owner is read (to copy "meanwhile").</summary>
    public Action? OnOwnerRead { get; set; }

    public int GetOwnerProcessId()
    {
        OnOwnerRead?.Invoke();
        lock (_lock)
        {
            return OwnerProcessId;
        }
    }

    public uint GetSequenceNumber()
    {
        OnSequenceRead?.Invoke();
        lock (_lock)
        {
            return Sequence;
        }
    }

    public ClipboardSnapshot Read(ClipboardFormat format, int maxCharacters, uint privateContentSequence)
    {
        OnRead?.Invoke();
        lock (_lock)
        {
            Reads++;
            LastPrivateContentSequence = privateContentSequence;
            LastReadPrivate = false;
            if (!CanOpen)
            {
                return ClipboardSnapshot.NotOpened;
            }

            var snapshot = new ClipboardSnapshot(true, Sequence, Markers, Text != null, Html != null, Files != null);
            if (snapshot.IsPrivate && (privateContentSequence == 0 || privateContentSequence != Sequence))
            {
                return snapshot;
            }

            LastReadPrivate = snapshot.IsPrivate;

            if (ReadError != null)
            {
                return snapshot with { Error = ReadError };
            }

            return format switch
            {
                ClipboardFormat.Text when Text != null =>
                    snapshot with { Text = Text[..Math.Min(Text.Length, maxCharacters)], TextLength = Text.Length },
                ClipboardFormat.Html when Html != null => snapshot with { Html = Html },
                ClipboardFormat.Files when Files != null => snapshot with { Files = Files.Take(maxCharacters).ToList(), FileCount = Files.Count },
                _ => snapshot
            };
        }
    }

    public ClipboardWriteOutcome WriteText(string text)
    {
        lock (_lock)
        {
            Writes++;
            if (!CanOpen)
            {
                return new ClipboardWriteOutcome(false, false);
            }

            if (WriteError != null)
            {
                return new ClipboardWriteOutcome(true, false, Error: WriteError);
            }

            Sequence += 2; // EmptyClipboard and the data
            OwnerProcessId = ServerProcessId;
            Text = text;
            Html = null;
            Files = null;
            Markers = ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing | ClipboardPrivacyMarkers.NotForClipboardHistory;
            return new ClipboardWriteOutcome(true, true, Sequence, ExcludedFromHistory: true);
        }
    }
}
