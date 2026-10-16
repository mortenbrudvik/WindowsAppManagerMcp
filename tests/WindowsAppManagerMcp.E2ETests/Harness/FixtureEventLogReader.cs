using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>A rectangle in screen coordinates.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int CenterX => X + Width / 2;

    public int CenterY => Y + Height / 2;

    public override string ToString() => $"({X}, {Y}, {Width}x{Height})";

    internal static ScreenRect From(JsonElement element) => new(
        element.GetProperty("x").GetInt32(),
        element.GetProperty("y").GetInt32(),
        element.GetProperty("width").GetInt32(),
        element.GetProperty("height").GetInt32());
}

/// <summary>One line of the fixture's event log (see <see cref="FixtureContract.Events"/>).</summary>
public sealed record FixtureEvent(long Sequence, string Name, JsonElement Data)
{
    public string? GetString(string property) =>
        Data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public long? GetInt64(string property) =>
        Data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    public bool? GetBoolean(string property) =>
        Data.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>The window handle in field <paramref name="property"/>, or 0.</summary>
    public nint GetHandle(string property) => (nint)(GetInt64(property) ?? 0);

    public static FixtureEvent Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement.Clone();
        return new FixtureEvent(
            root.GetProperty("seq").GetInt64(),
            root.GetProperty("event").GetString() ?? "",
            root);
    }

    public override string ToString() => Data.GetRawText();
}

/// <summary>
/// The fixture's ready event: what the fixture itself reports about its main window, as ground truth
/// for the server's answers. Coordinates are the fixture's screen coordinates (physical pixels unless the
/// fixture runs DPI unaware).
/// </summary>
public sealed record FixtureReady(
    int ProcessId,
    nint MainWindowHandle,
    string Title,
    int Dpi,
    string DpiAwareness,
    ScreenRect WindowRect,
    ScreenRect ClientRect,
    IReadOnlyDictionary<string, ScreenRect> Elements)
{
    public static FixtureReady From(FixtureEvent ready)
    {
        if (ready.Name != FixtureContract.Events.Ready)
        {
            throw new ArgumentException($"Expected a '{FixtureContract.Events.Ready}' event, got '{ready.Name}'.", nameof(ready));
        }

        var data = ready.Data;
        return new FixtureReady(
            data.GetProperty("pid").GetInt32(),
            (nint)data.GetProperty("hwnd").GetInt64(),
            data.GetProperty("title").GetString() ?? "",
            data.GetProperty("dpi").GetInt32(),
            data.GetProperty("dpiAwareness").GetString() ?? "",
            ScreenRect.From(data.GetProperty("windowRect")),
            ScreenRect.From(data.GetProperty("clientRect")),
            data.GetProperty("elements").EnumerateObject().ToDictionary(p => p.Name, p => ScreenRect.From(p.Value)));
    }

    /// <summary>The screen rectangle of the element with AutomationId <paramref name="id"/>.</summary>
    public ScreenRect Element(string id) =>
        Elements.TryGetValue(id, out var rect)
            ? rect
            : throw new KeyNotFoundException($"The ready event reports no element '{id}'.");
}

/// <summary>
/// One line the canvas window drew, and its words, with their rectangles in the fixture's screen coordinates: cell boxes (the
/// line's full height), not ink, so an OCR box lies inside one rather than equal to it.
/// </summary>
public sealed record FixtureCanvasLine(string Text, ScreenRect Rect, IReadOnlyList<FixtureCanvasWord> Words);

/// <summary>One word of a canvas line and its rectangle.</summary>
public sealed record FixtureCanvasWord(string Text, ScreenRect Rect);

/// <summary>
/// The canvas window's <see cref="FixtureContract.Events.CanvasOpened"/> event (roadmap M6.3): where the fixture drew
/// <see cref="FixtureContract.CanvasLines"/>, as ground truth for what OCR reads there.
/// </summary>
public sealed record FixtureCanvas(nint WindowHandle, string Title, int Dpi, IReadOnlyList<FixtureCanvasLine> Lines)
{
    public static FixtureCanvas From(FixtureEvent opened)
    {
        if (opened.Name != FixtureContract.Events.CanvasOpened)
        {
            throw new ArgumentException(
                $"Expected a '{FixtureContract.Events.CanvasOpened}' event, got '{opened.Name}'.", nameof(opened));
        }

        var data = opened.Data;
        return new FixtureCanvas(
            (nint)data.GetProperty("hwnd").GetInt64(),
            data.GetProperty("title").GetString() ?? "",
            data.GetProperty("dpi").GetInt32(),
            data.GetProperty("lines").EnumerateArray().Select(line => new FixtureCanvasLine(
                line.GetProperty("text").GetString() ?? "",
                ScreenRect.From(line.GetProperty("rect")),
                line.GetProperty("words").EnumerateArray().Select(word => new FixtureCanvasWord(
                    word.GetProperty("text").GetString() ?? "",
                    ScreenRect.From(word.GetProperty("rect")))).ToList())).ToList());
    }

    /// <summary>The screen rectangle of the canvas word <paramref name="text"/> (each word appears once).</summary>
    public ScreenRect Word(string text) =>
        Lines.SelectMany(l => l.Words).FirstOrDefault(w => w.Text == text)?.Rect
            ?? throw new KeyNotFoundException($"The canvas reports no word '{text}'.");
}

/// <summary>
/// Reads the fixture's event log while the fixture writes it. Only complete lines are parsed, so a line
/// the fixture is still writing is picked up on a later read.
/// </summary>
public sealed class FixtureEventLogReader
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly object _lock = new();
    private readonly List<FixtureEvent> _events = [];
    private long _offset;

    public FixtureEventLogReader(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>Every event written so far.</summary>
    public IReadOnlyList<FixtureEvent> ReadAll()
    {
        lock (_lock)
        {
            ReadNewLines();
            return _events.ToList();
        }
    }

    /// <summary>
    /// Waits until an event matching <paramref name="match"/> is in the log and returns the first one.
    /// </summary>
    /// <param name="match">The event to wait for.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="description">What is awaited, for the error message.</param>
    /// <param name="abortReason">Checked while waiting; a non-null reason (such as "the fixture exited")
    /// stops the wait early.</param>
    /// <exception cref="TimeoutException">No such event within <paramref name="timeout"/>, or the wait was aborted.</exception>
    public FixtureEvent WaitFor(
        Func<FixtureEvent, bool> match,
        TimeSpan timeout,
        string description,
        Func<string?>? abortReason = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var found = ReadAll().FirstOrDefault(match);
            if (found != null)
            {
                return found;
            }

            var abort = abortReason?.Invoke();
            if (abort != null || stopwatch.Elapsed >= timeout)
            {
                // One last read: the event may have been written just before the process exited.
                found = ReadAll().FirstOrDefault(match);
                if (found != null)
                {
                    return found;
                }

                throw new TimeoutException(
                    $"The fixture did not report {description} within {timeout.TotalSeconds:0.#} s" +
                    (abort != null ? $" ({abort})" : "") + $". Event log {Path}:{Environment.NewLine}{Describe()}");
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>The event log as text, for failure messages and test output.</summary>
    public string Describe()
    {
        var events = ReadAll();
        return events.Count == 0
            ? "(empty)"
            : string.Join(Environment.NewLine, events.Select(e => e.ToString()));
    }

    private void ReadNewLines()
    {
        if (!File.Exists(Path))
        {
            return;
        }

        byte[] bytes;
        using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length <= _offset)
            {
                return;
            }
            stream.Seek(_offset, SeekOrigin.Begin);
            bytes = new byte[stream.Length - _offset];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0)
                {
                    break;
                }
                read += count;
            }
            if (read < bytes.Length)
            {
                Array.Resize(ref bytes, read);
            }
        }

        // Only whole lines: a line the fixture is still writing (or a UTF-8 sequence cut in half)
        // stays in the file for the next read.
        var lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        if (lastNewline < 0)
        {
            return;
        }

        var text = Encoding.UTF8.GetString(bytes, 0, lastNewline + 1);
        _offset += lastNewline + 1;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                _events.Add(FixtureEvent.Parse(trimmed));
            }
        }
    }
}
