using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Models;

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

    /// <summary>
    /// The filters of <paramref name="selector"/> that <paramref name="window"/> does not match, in words; empty when it
    /// matches them all (roadmap M2.4). Used to check a handle the caller passed with what it must still be, the window in
    /// the foreground, and a resolved window again inside the input gate.
    /// </summary>
    /// <exception cref="RegexMatchTimeoutException">titleRegex took longer than <see cref="WindowTitles.RegexTimeout"/>.</exception>
    public static List<string> Mismatches(WindowInfo window, WindowSelector selector)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(selector);

        var differences = new List<string>();
        if (selector.ProcessId is { } processId && window.ProcessId != processId && window.HostProcessId != processId)
            differences.Add($"its process is PID {window.ProcessId}, not {processId}");

        if (!string.IsNullOrEmpty(selector.ProcessName) &&
            !ProcessNameMatches(window.ProcessName, window.HostProcessName, selector.ProcessName, selector.ProcessNameContains))
            differences.Add($"its process is '{window.ProcessName}', not '{selector.ProcessName}'");

        if (!string.IsNullOrEmpty(selector.ClassName) &&
            !string.Equals(window.ClassName, selector.ClassName, StringComparison.OrdinalIgnoreCase))
            differences.Add($"its class is '{window.ClassName}', not '{selector.ClassName}'");

        if (!string.IsNullOrEmpty(selector.TitleContains) && !WindowTitles.Contains(window.Title, selector.TitleContains))
            differences.Add($"its title '{window.Title}' does not contain '{selector.TitleContains}'");

        if (!string.IsNullOrEmpty(selector.TitleEquals) && !WindowTitles.AreEqual(window.Title, selector.TitleEquals))
            differences.Add($"its title is '{window.Title}', not '{selector.TitleEquals}'");

        // A pattern that runs too long throws: the caller reports it as the pattern's fault, never as a different window
        if (selector.TitleRegex is { Length: > 0 } pattern && WindowTitles.CompileTitleRegex(pattern, out _) is { } regex &&
            !WindowTitles.Matches(window.Title, regex))
            differences.Add($"its title '{window.Title}' does not match titleRegex '{pattern}'");

        return differences;
    }
}
