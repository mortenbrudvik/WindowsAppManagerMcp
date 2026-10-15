namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The DevToolsActivePort file a Chromium browser started with --remote-debugging-port=0 writes into its profile folder
/// once its DevTools server listens (roadmap M5.1): the port on the first line, the browser's WebSocket path
/// (/devtools/browser/&lt;id&gt;) on the second. A file left by an earlier run of the profile would name a port the
/// browser no longer has, so it is deleted before each launch; a file found after the start is therefore the started
/// browser's own word for its port and its browser-level URL.
/// </summary>
public static class DevToolsActivePortFile
{
    public const string FileName = "DevToolsActivePort";

    public static string PathIn(string userDataDir) => Path.Combine(userDataDir, FileName);

    /// <summary>The port on the file's first line, or null when it holds no port from 1 to 65535 (yet).</summary>
    public static int? ParsePort(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        var firstLine = content.Split('\n', 2)[0].Trim();
        return int.TryParse(firstLine, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) &&
               port is >= 1 and <= 65535
            ? port
            : null;
    }

    /// <summary>The browser WebSocket path on the file's second line (/devtools/browser/&lt;id&gt;), or null when there is none.</summary>
    public static string? ParseBrowserPath(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        var lines = content.Split('\n', 3);
        var path = lines.Length > 1 ? lines[1].Trim() : string.Empty;
        return path.StartsWith("/devtools/browser/", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>The port the file names, or null when it is missing, cannot be read (being written) or holds no port.</summary>
    public static int? TryReadPort(string userDataDir) => ParsePort(TryReadText(userDataDir));

    /// <summary>
    /// True when the file names <paramref name="port"/> and <paramref name="browserPath"/> (the path of the browser
    /// WebSocket URL an endpoint listed); false when it is missing, cannot be read, or names something else.
    /// </summary>
    public static bool Names(string userDataDir, int port, string browserPath)
    {
        var content = TryReadText(userDataDir);
        return ParsePort(content) == port && string.Equals(ParseBrowserPath(content), browserPath, StringComparison.Ordinal);
    }

    /// <summary>The most of the file that is read: its two short lines fit many times over.</summary>
    private const int MaxBytes = 1024;

    private static string? TryReadText(string userDataDir)
    {
        try
        {
            // Bounded, and only a seekable (regular) file: never a whole large file, and never a pipe or a device that
            // could keep the read waiting
            using var stream = new FileStream(
                PathIn(userDataDir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            if (!stream.CanSeek)
                return null;

            var buffer = new byte[MaxBytes];
            var read = 0;
            while (read < buffer.Length && stream.Read(buffer, read, buffer.Length - read) is var n and > 0)
                read += n;

            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Deletes a file left by an earlier run; returns why it could not be deleted, or null.</summary>
    public static string? DeleteStale(string userDataDir)
    {
        var path = PathIn(userDataDir);
        try
        {
            if (File.Exists(path))
                File.Delete(path);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ex.Message;
        }
    }
}
