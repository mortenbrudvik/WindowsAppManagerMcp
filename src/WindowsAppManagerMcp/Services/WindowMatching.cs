namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The process-name rule shared by find_windows, the window selectors (roadmap M2.4) and layout matching (roadmap M2.8):
/// a name matches a window's app process or, for a UWP frame window, its host process (ApplicationFrameHost), ignoring
/// case and an ".exe" suffix; as the whole name by default, or as a part of it when asked.
/// </summary>
public static class WindowMatching
{
    /// <summary>The process name as Windows reports it: trimmed, without an ".exe" suffix ("notepad.exe" is "notepad").</summary>
    public static string WithoutExe(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    /// <summary>
    /// Whether <paramref name="wanted"/> names the window's process (<paramref name="processName"/>) or its host process
    /// (<paramref name="hostProcessName"/>): the whole name, or with <paramref name="contains"/> a part of it.
    /// </summary>
    public static bool ProcessNameMatches(string? processName, string? hostProcessName, string wanted, bool contains)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        var name = WithoutExe(wanted);
        return Matches(processName, name, contains) || Matches(hostProcessName, name, contains);
    }

    private static bool Matches(string? processName, string wanted, bool contains) =>
        processName != null && (contains
            ? processName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            : string.Equals(WithoutExe(processName), wanted, StringComparison.OrdinalIgnoreCase));
}
