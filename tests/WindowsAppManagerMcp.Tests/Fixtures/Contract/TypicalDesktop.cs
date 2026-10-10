namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// A typical developer desktop, as the mocked services of the every-tool contract run report it: two monitors,
/// 15 app windows, 40 processes, a 120-element UI Automation tree, a 15,000-character article, five layout presets.
/// The payload budgets (ToolPayloadBudgetTests) are measured on these results, so the sizes here are what
/// "typical" means for them; change them only together with the budgets.
/// </summary>
public static class TypicalDesktop
{
    public const long NotepadHandle = 0x1A0B2C;
    public const long BrowserHandle = 0x2B0C3D;
    public const int NotepadProcessId = 10_244;

    /// <summary>Characters of page text get_browser_content returns for a typical article.</summary>
    public const int ArticleLength = 15_000;

    /// <summary>Elements of the typical UI Automation tree (get_ui_elements at the default depth).</summary>
    public const int UiTreeElements = 120;

    // Declared first: the static initializers below use it, and they run in declaration order
    private static readonly string[] ProcessNames =
    [
        "Code", "chrome", "msedge", "WindowsTerminal", "explorer", "notepad", "Teams", "OUTLOOK", "slack", "Spotify",
        "devenv", "rider64", "pwsh", "Docker Desktop"
    ];

    public static IReadOnlyList<MonitorInfo> Monitors { get; } =
    [
        new(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 2560, 1440), new MonitorRect(0, 0, 2560, 1392), 1.25),
        new(1, @"\\.\DISPLAY2", false, new MonitorRect(-1920, 180, 1920, 1080), new MonitorRect(-1920, 180, 1920, 1040), 1.0)
    ];

    public static IReadOnlyList<WindowInfo> Windows { get; } = CreateWindows();

    public static WindowInfo Notepad => Windows[0];

    public static IReadOnlyList<ProcessInfo> Processes { get; } = Enumerable.Range(0, 40)
        .Select(i => new ProcessInfo(
            ProcessId: 4_000 + i * 37,
            Name: ProcessNames[i % ProcessNames.Length],
            ExecutablePath: $@"C:\Program Files\{ProcessNames[i % ProcessNames.Length]}\{ProcessNames[i % ProcessNames.Length]}.exe",
            WindowCount: i % 3,
            MemoryUsageMB: 48.5 + i * 11.25))
        .ToList();

    public static string Article { get; } = string.Concat(Enumerable.Repeat(
        "Windows App Manager lets an agent find, arrange and read the windows on a desktop. ", 200))[..ArticleLength];

    private static IReadOnlyList<WindowInfo> CreateWindows()
    {
        (string Title, string Process, string Class)[] apps =
        [
            ("notes.txt - Notepad", "notepad", "Notepad"),
            ("Windows App Manager - GitHub - Google Chrome", "chrome", "Chrome_WidgetWin_1"),
            ("README.md - WindowsAppManagerMcp - Visual Studio Code", "Code", "Chrome_WidgetWin_1"),
            ("pwsh in WindowsAppManagerMcp", "WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS"),
            ("Documents - File Explorer", "explorer", "CabinetWClass"),
            ("Inbox - user@example.com - Outlook", "OUTLOOK", "rctrl_renwnd32"),
            ("Microsoft Teams", "Teams", "TeamsWebView"),
            ("Slack | general | Example Workspace", "slack", "Chrome_WidgetWin_1"),
            ("Spotify Premium", "Spotify", "Chrome_WidgetWin_1"),
            ("WindowsAppManagerMcp - Microsoft Visual Studio", "devenv", "HwndWrapper[DefaultDomain;;]"),
            ("Calculator", "CalculatorApp", "ApplicationFrameWindow"),
            ("Settings", "SystemSettings", "ApplicationFrameWindow"),
            ("Docker Desktop", "Docker Desktop", "Chrome_WidgetWin_1"),
            ("Task Manager", "Taskmgr", "TaskManagerWindow"),
            ("Release notes - Microsoft Edge", "msedge", "Chrome_WidgetWin_1"),
        ];

        return apps.Select((app, i) => new WindowInfo(
                Handle: (nint)(i == 0 ? NotepadHandle : i == 1 ? BrowserHandle : 0x300000 + i * 0x1F4),
                Title: app.Title,
                ProcessName: app.Process,
                ProcessId: i == 0 ? NotepadProcessId : 12_000 + i * 113,
                Bounds: new WindowRect(i % 2 == 0 ? 0 : 1280, i % 3 * 100, 1280, 1392 - i % 3 * 100),
                State: i % 5 == 4 ? WindowState.Minimized : i % 4 == 3 ? WindowState.Maximized : WindowState.Normal,
                IsVisible: true,
                MonitorIndex: i % 4 == 1 ? 1 : 0)
            {
                ClassName = app.Class,
                HostProcessName = app.Class == "ApplicationFrameWindow" ? "ApplicationFrameHost" : null,
                HostProcessId = app.Class == "ApplicationFrameWindow" ? 9_120 : null
            })
            .ToList();
    }

    /// <summary>A UI Automation tree of <see cref="UiTreeElements"/> elements: a menu bar, a toolbar, a tree and a list.</summary>
    public static IReadOnlyList<UIElementInfo> UiTree()
    {
        var y = 0;
        UIElementInfo Leaf(string name, string type, string id) =>
            new(name, type, id, new BoundsDto(40, 60 + (y += 24) % 1200, 220, 22), IsEnabled: true, IsOffscreen: false);
        UIElementInfo Group(string name, string type, string id, IReadOnlyList<UIElementInfo> children) =>
            new(name, type, id, new BoundsDto(0, 40, 1280, 600), IsEnabled: true, IsOffscreen: false, Children: children);

        var menu = Group("Application", "MenuBar", "MenuBar",
            new[] { "File", "Edit", "Selection", "View", "Go", "Run", "Terminal", "Help" }
                .Select(m => Leaf(m, "MenuItem", string.Empty)).ToList());
        var toolbar = Group("Toolbar", "ToolBar", "toolbar",
            Enumerable.Range(1, 14).Select(i => Leaf($"Command {i}", "Button", $"cmd{i}")).ToList());
        var tree = Group("Explorer", "Tree", "explorerTree",
            Enumerable.Range(1, 40).Select(i => Leaf($"src/WindowsAppManagerMcp/File{i:00}.cs", "TreeItem", string.Empty)).ToList());
        var list = Group("Problems", "List", "problemsList",
            Enumerable.Range(1, 50).Select(i => Leaf($"Warning CS{8600 + i}: Possible null reference in line {i * 7}", "ListItem", string.Empty)).ToList());
        var status = Group("Status bar", "StatusBar", "statusbar",
            Enumerable.Range(1, 1).Select(i => Leaf("Ln 42, Col 7", "Text", "status.position")).ToList());

        // 5 groups + 8 + 14 + 40 + 50 + 1 leaves = 118, plus the pane and the document
        var pane = Group("Main", "Pane", "workbench", [menu, toolbar, tree, list, status]);
        var document = Leaf("README.md", "Document", "editor");
        return [pane, document];
    }

    public static int Count(IReadOnlyList<UIElementInfo> elements) =>
        elements.Sum(e => 1 + (e.Children == null ? 0 : Count(e.Children)));

    public static UIElementDetails FocusedEdit { get; } = new(
        Name: "Search",
        ControlType: "Edit",
        AutomationId: "searchBox",
        ClassName: "TextBox",
        FrameworkId: "WPF",
        Bounds: new BoundsDto(320, 96, 480, 28),
        IsEnabled: true,
        IsOffscreen: false,
        IsKeyboardFocusable: true,
        HasKeyboardFocus: true,
        IsPassword: false,
        Patterns: ["Value", "Text", "ScrollItem"],
        Value: "window layout presets for a dual monitor setup");

    public static IReadOnlyList<LayoutPreset> Layouts { get; } = Enumerable.Range(1, 5)
        .Select(i => new LayoutPreset(
            Name: $"layout-{i}",
            Description: $"Preset {i}: editor left, browser right, terminal below",
            Placements: Enumerable.Range(0, 6).Select(p => new WindowPlacement(
                Matcher: new WindowMatcher(TitleContains: p % 2 == 0 ? "WindowsAppManagerMcp" : null, ProcessName: ProcessNames[p]),
                MonitorIndex: p % 2,
                Position: new RelativePosition(p % 2 * 0.5, 0, 0.5, 1),
                LaunchCommand: p == 0 ? "code" : null,
                Focus: p == 0,
                Order: p)).ToList())
        {
            CreatedAt = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 10, 1, 17, 5, 0, DateTimeKind.Utc)
        })
        .ToList();

    public static WindowSummary NotepadSummary { get; } = new(NotepadHandle, Notepad.Title, Notepad.ProcessName, NotepadProcessId);

    public static FocusResult NotepadFocused { get; } = FocusResult.From(new FocusOutcome
    {
        Target = (nint)NotepadHandle,
        IsForeground = true,
        Method = FocusMethod.SetForeground,
        ForegroundHandle = (nint)NotepadHandle,
        ForegroundTitle = Notepad.Title,
        ForegroundProcess = Notepad.ProcessName,
        ForegroundProcessId = NotepadProcessId
    });
}
