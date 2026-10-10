using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Pure rules for telling app windows from shell and helper windows, from native facts that
/// <see cref="Interfaces.INativeWindowWrapper"/> reads (class name, extended style).
/// </summary>
public static class WindowClassification
{
    /// <summary>The frame window of a UWP app; ApplicationFrameHost.exe owns it, not the app.</summary>
    public const string ApplicationFrameWindowClass = "ApplicationFrameWindow";

    /// <summary>The content window of a UWP app, in the app's own process.</summary>
    public const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// The desktop (Progman "Program Manager" and its WorkerW layers) and the taskbars
    /// (Shell_TrayWnd, Shell_SecondaryTrayWnd on other monitors).
    /// </summary>
    public static IReadOnlySet<string> ShellWindowClasses { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>Whether the class is the desktop or a taskbar (see <see cref="ShellWindowClasses"/>).</summary>
    public static bool IsShellWindowClass(string? className) =>
        !string.IsNullOrEmpty(className) && ShellWindowClasses.Contains(className);

    /// <summary>
    /// Whether the extended style (GWL_EXSTYLE) marks a tool window that is kept out of the taskbar
    /// and Alt+Tab: WS_EX_TOOLWINDOW without WS_EX_APPWINDOW. Floating palettes, tooltips, menus and
    /// helper windows of the shell are tool windows.
    /// </summary>
    public static bool IsToolWindow(nint extendedStyle)
    {
        var style = (long)extendedStyle;
        return (style & NativeEnums.WS_EX_TOOLWINDOW) != 0 && (style & NativeEnums.WS_EX_APPWINDOW) == 0;
    }

    /// <summary>Whether the class is the frame window of a UWP app.</summary>
    public static bool IsApplicationFrameWindow(string? className) =>
        string.Equals(className, ApplicationFrameWindowClass, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the class is the content window of a UWP app.</summary>
    public static bool IsCoreWindow(string? className) =>
        string.Equals(className, CoreWindowClass, StringComparison.OrdinalIgnoreCase);
}
