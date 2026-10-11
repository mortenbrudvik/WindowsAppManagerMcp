namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A fake clipboard: a sequence number, privacy markers and content the test sets, read and written the way
/// <see cref="NativeClipboardWrapper"/> reads and writes the real one (the sequence number changes with every write, and
/// private content is not read unless asked for). The real clipboard is the user's, so no test may touch it.
/// </summary>
public sealed class FakeClipboardWrapper : INativeClipboardWrapper
{
    private readonly object _lock = new();

    /// <summary>The clipboard's sequence number.</summary>
    public uint Sequence { get; set; } = 100;

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

    /// <summary>The readPrivateContent argument of the last read.</summary>
    public bool? LastReadPrivate { get; private set; }

    /// <summary>The user (or another app) copies text: the sequence number moves on.</summary>
    public void UserCopies(string text, ClipboardPrivacyMarkers markers = ClipboardPrivacyMarkers.None)
    {
        lock (_lock)
        {
            Sequence++;
            Text = text;
            Html = null;
            Files = null;
            Markers = markers;
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

    public ClipboardSnapshot Read(ClipboardFormat format, int maxCharacters, bool readPrivateContent)
    {
        OnRead?.Invoke();
        lock (_lock)
        {
            Reads++;
            LastReadPrivate = readPrivateContent;
            if (!CanOpen)
            {
                return ClipboardSnapshot.NotOpened;
            }

            var snapshot = new ClipboardSnapshot(true, Sequence, Markers, Text != null, Html != null, Files != null);
            if (snapshot.IsPrivate && !readPrivateContent)
            {
                return snapshot;
            }

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
            Text = text;
            Html = null;
            Files = null;
            Markers = ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing | ClipboardPrivacyMarkers.NotForClipboardHistory;
            return new ClipboardWriteOutcome(true, true, Sequence, ExcludedFromHistory: true);
        }
    }
}
