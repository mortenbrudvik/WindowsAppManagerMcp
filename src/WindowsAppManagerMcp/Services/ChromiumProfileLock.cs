namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Whether a Chromium browser is running with a profile folder (roadmap M5.1). On Windows, Chromium's process singleton
/// creates &lt;userDataDir&gt;\lockfile when it takes the profile, and keeps it open for writing, shared for reading only and
/// deleted when it is closed. A second start of that profile hands its command line, url included, to the running browser
/// and exits, so launch_browser_with_debug checks the lock before it deletes or starts anything. A lockfile that opens for
/// writing was left behind (a copied profile), and no browser holds it.
/// </summary>
public static class ChromiumProfileLock
{
    public const string FileName = "lockfile";

    /// <summary>Why the lockfile in <paramref name="userDataDir"/> cannot be opened for writing (a browser holds it), or null.</summary>
    public static string? HeldReason(string userDataDir)
    {
        var path = Path.Combine(userDataDir, FileName);
        try
        {
            if (!File.Exists(path))
                return null;

            // Opens the file as it is, without changing it, and closes it at once
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Not a path a browser could use either; the start reports it
            return null;
        }
    }
}
