using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// Writes the fixture's event log: one JSON object per line, flushed as it is written, so the harness
/// can read it while the fixture runs. See <see cref="FixtureContract.Events"/> for the format.
/// Without a path every write is a no-op, for starting the fixture by hand.
/// </summary>
internal sealed class FixtureEventLog : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter? _writer;
    private long _sequence;

    private FixtureEventLog(StreamWriter? writer)
    {
        _writer = writer;
    }

    public static FixtureEventLog Open(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new FixtureEventLog(null);
        }

        // Readers open the file while the fixture still writes to it, and the harness deletes it
        // afterwards, so allow both.
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new FixtureEventLog(new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        });
    }

    /// <summary>Writes event <paramref name="name"/> with <paramref name="fields"/>.</summary>
    public void Write(string name, params (string Key, JsonNode? Value)[] fields)
    {
        if (_writer == null)
        {
            return;
        }

        lock (_lock)
        {
            var entry = new JsonObject
            {
                ["seq"] = ++_sequence,
                ["event"] = name,
                ["time"] = DateTimeOffset.Now.ToString("O")
            };
            foreach (var (key, value) in fields)
            {
                entry[key] = value;
            }

            try
            {
                _writer.WriteLine(entry.ToJsonString());
            }
            catch (IOException)
            {
                // The log is a diagnostic channel; a full disk must not crash the fixture.
            }
            catch (ObjectDisposedException)
            {
                // Written after shutdown started.
            }
        }
    }

    /// <summary>A screen rectangle in the event log format.</summary>
    public static JsonObject Rect(int x, int y, int width, int height) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["width"] = width,
        ["height"] = height
    };

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
        }
    }
}
