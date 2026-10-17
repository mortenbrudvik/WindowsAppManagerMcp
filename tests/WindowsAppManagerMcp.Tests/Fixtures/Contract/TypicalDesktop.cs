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

    /// <summary>A 2560 x 1440 primary at 125% and a 1080p monitor to its left, each with its identity (roadmap M6.7).</summary>
    public static IReadOnlyList<MonitorInfo> Monitors { get; } =
    [
        new(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 2560, 1440), new MonitorRect(0, 0, 2560, 1392), 1.25)
        {
            Id = "m-3f9a12c4", FriendlyName = "DELL U2720Q", IsInternal = false
        },
        new(1, @"\\.\DISPLAY2", false, new MonitorRect(-1920, 180, 1920, 1080), new MonitorRect(-1920, 180, 1920, 1040), 1.0)
        {
            Id = "m-81c07e5b", FriendlyName = "LG ULTRAFINE", IsInternal = false
        }
    ];

    public static IReadOnlyList<WindowInfo> Windows { get; } = CreateWindows();

    public static WindowInfo Notepad => Windows[0];

    /// <summary>
    /// 40 processes as list_processes reports them since roadmap M6.6: each with its parent, start time and integrity, and
    /// up to two windows (two thirds of them have one or two).
    /// </summary>
    public static IReadOnlyList<ProcessInfo> Processes { get; } = Enumerable.Range(0, 40)
        .Select(i => new ProcessInfo(
            ProcessId: 4_000 + i * 37,
            Name: ProcessNames[i % ProcessNames.Length],
            ExecutablePath: $@"C:\Program Files\{ProcessNames[i % ProcessNames.Length]}\{ProcessNames[i % ProcessNames.Length]}.exe",
            WindowCount: i % 3,
            MemoryUsageMB: 48.5 + i * 11.25,
            ParentProcessId: 5_120,
            StartTime: new DateTime(2026, 10, 16, 7, 0, 0, DateTimeKind.Utc).AddSeconds(i * 97),
            Windows: [.. Windows.Skip(i % Windows.Count).Concat(Windows).Take(i % 3)],
            Integrity: new ProcessIntegrity(4_000 + i * 37, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null)))
        .ToList();

    public static string Article { get; } = string.Concat(Enumerable.Repeat(
        "Windows App Manager lets an agent find, arrange and read the windows on a desktop. ", 200))[..ArticleLength];

    /// <summary>A paragraph of 300 characters the agent copied with send_keys '^c' (get_clipboard).</summary>
    public static string CopiedParagraph { get; } = Article[..300];

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
                // The visible frame (DWMWA_EXTENDED_FRAME_BOUNDS) without the invisible resize borders of Windows 10 and 11:
                // 7 pixels left, right and bottom at 100%. The service reads it for every window, minimized ones included
                VisibleBounds = new WindowRect((i % 2 == 0 ? 0 : 1280) + 7, i % 3 * 100, 1280 - 14, 1392 - i % 3 * 100 - 7),
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
        var element = 0;

        // Every element has a ref (roadmap M3.4), as in a real walk, numbered in the order the walk would register them
        UIElementInfo Leaf(string name, string type, string id) =>
            new(name, type, id, new BoundsDto(40, 60 + (y += 24) % 1200, 220, 22), IsEnabled: true, IsOffscreen: false,
                Ref: $"w4120035e{++element}");
        UIElementInfo Group(string name, string type, string id, IReadOnlyList<UIElementInfo> children) =>
            new(name, type, id, new BoundsDto(0, 40, 1280, 600), IsEnabled: true, IsOffscreen: false, Children: children,
                Ref: $"w4120035e{++element}");

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

    /// <summary>What find_ui_elements returns for "the Save button": two matches, at the default detail.</summary>
    public static IReadOnlyList<FoundElement> FoundButtons { get; } =
    [
        new("w4120035e9", "Save", "Button", "cmd.save", "Button", new BoundsDto(64, 88, 28, 28), new ScreenPoint(78, 102), true, false,
            ["Invoke"]),
        new("w4120035e31", "Save all", "Button", "cmd.saveAll", "Button", new BoundsDto(96, 88, 28, 28), new ScreenPoint(110, 102), true, false,
            ["Invoke"]),
    ];

    /// <summary>What get_active_popups returns for Notepad (roadmap M3.5): its open context menu and a Save As dialog.</summary>
    public static IReadOnlyList<ActivePopup> NotepadPopups { get; } =
    [
        new("w4120036e1", 2493112, PopupKind.Menu, null, "#32768", 4120, "Notepad", null, new BoundsDto(612, 240, 268, 410), "Menu"),
        new("w4120037e1", 3017452, PopupKind.Dialog, "Save As", "#32770", 4120, "Notepad", NotepadHandle,
            new BoundsDto(420, 180, 960, 640), "Window", IsBlocking: true),
    ];

    /// <summary>Characters of the reading pane's text that read_element_text returns for a typical e-mail (roadmap M3.9).</summary>
    public const int MessageLength = 3_000;

    /// <summary>What read_element_text returns for a reading pane: the first page of a 3,000-character e-mail.</summary>
    public static ElementTextResult MessageText { get; } = new(
        true, "w4120035e57", NotepadHandle, "Document", Article[..MessageLength], ElementTextSource.Text, 0, MessageLength, false, null, 18.25);

    /// <summary>Rows of the process table read_table returns (Task Manager's first page, roadmap M3.9).</summary>
    public const int TableRows = 50;

    /// <summary>What read_table returns for Task Manager's processes: 50 rows of 5 columns, with headers.</summary>
    public static ElementTableResult ProcessTable { get; } = new(
        Success: true,
        Ref: "w4120035e61",
        WindowHandle: NotepadHandle,
        Source: ElementTableSource.Grid,
        Headers: ["Name", "Status", "CPU", "Memory", "Disk"],
        Rows: Enumerable.Range(0, TableRows)
            .Select(i => (IReadOnlyList<string?>)
            [
                ProcessNames[i % ProcessNames.Length], i % 7 == 0 ? "Suspended" : "", $"{i % 9}.{i % 10}%", $"{48.5 + i * 11.25:0.0} MB",
                $"{i % 4}.1 MB/s"
            ])
            .ToList(),
        StartRow: 0,
        TotalRows: 212,
        TotalColumns: 5,
        Truncated: true,
        ElapsedMilliseconds: 412.5,
        TruncationReason: ElementTableTruncation.MaxRows,
        NextStartRow: TableRows);

    /// <summary>
    /// What element_action returns for a typical set_value through the Value pattern (roadmap M3.6): the state before and
    /// after, verified. The value set is not echoed; the state shows the field's value.
    /// </summary>
    public static ElementActionResult ElementActionDone(ElementActionRequest request) => new(
        Success: true,
        Action: "set_value",
        Ref: request.Ref,
        WindowHandle: NotepadHandle,
        Status: ElementActionStatus.Done,
        Method: ElementActionMethod.Value,
        Before: new ElementState("Search", IsEnabled: true, IsOffscreen: false, HasKeyboardFocus: false, Value: "window layout"),
        After: new ElementState("Search", IsEnabled: true, IsOffscreen: false, HasKeyboardFocus: false, Value: "window layout presets"),
        OutcomeVerified: true,
        ElapsedMilliseconds: 48.5);

    /// <summary>
    /// What element_action's menu_path returns for File>Save As in Notepad (roadmap M3.5): File expanded, its submenu window,
    /// Save As invoked, and the Save As dialog the press waits for.
    /// </summary>
    public static ElementActionResult MenuPathDone { get; } = new(
        Success: true,
        Action: "menu_path",
        Ref: "w4120037e4",
        WindowHandle: 0x1A0C40,
        Status: ElementActionStatus.InvokedPendingModal,
        Method: ElementActionMethod.Invoke,
        Before: new ElementState("Save As...", IsEnabled: true, IsOffscreen: false),
        OpenedPopup: new OpenedWindow(0x1A0C00, "Save As", "#32770", 10_244, "notepad"),
        ElapsedMilliseconds: 5_310.5,
        Steps:
        [
            new MenuPathStep("File", "w4120035e3", ElementActionMethod.ExpandCollapse, 0x1A0C40),
            new MenuPathStep("Save As", "w4120037e4", ElementActionMethod.Invoke, 0x1A0C00)
        ]);

    /// <summary>What respond_to_dialog returns for Notepad's save prompt answered "no" (roadmap M3.5).</summary>
    public static DialogResponseResult SavePromptAnswered { get; } = new(
        Success: true,
        DialogHandle: 0x1A0C10,
        Title: "Notepad",
        Text: ["Do you want to save changes to Untitled?"],
        Buttons:
        [
            new DialogButton("w4120036e1", "Save", "6", "yes", true),
            new DialogButton("w4120036e2", "Don't Save", "7", "no", true),
            new DialogButton("w4120036e3", "Cancel", "2", "cancel", true)
        ],
        Invoked: new DialogButton("w4120036e2", "Don't Save", "7", "no", true),
        Method: ElementActionMethod.Invoke,
        DialogClosed: true,
        ElapsedMilliseconds: 186.5);

    /// <summary>A file the contract run's file_dialog saves: in the temporary folder, which exists on every machine.</summary>
    public static string SavePath { get; } = Path.Combine(Path.GetTempPath(), "meeting-notes.txt");

    /// <summary>What file_dialog returns for Notepad's Save As dialog completed with a path (roadmap M3.14).</summary>
    public static FileDialogResult FileSaved(FileDialogRequest request) => new(
        Success: true,
        Action: "save",
        DialogHandle: 0x1A0C00,
        Completed: true,
        FinalPath: Path.GetFullPath(request.Path),
        PromptHandled: request.Overwrite,
        ElapsedMilliseconds: 742.5);

    public static int Count(IReadOnlyList<UIElementInfo> elements) =>
        elements.Sum(e => 1 + (e.Children == null ? 0 : Count(e.Children)));

    /// <summary>The first <paramref name="max"/> elements in reading order, as a walk stopped at maxElements keeps them (get_desktop_state asks for 80).</summary>
    public static IReadOnlyList<UIElementInfo> Take(IReadOnlyList<UIElementInfo> elements, int max)
    {
        var left = max;
        return Cut(elements);

        IReadOnlyList<UIElementInfo> Cut(IReadOnlyList<UIElementInfo> level)
        {
            var kept = new List<UIElementInfo>();
            foreach (var element in level)
            {
                if (left-- <= 0)
                {
                    break;
                }

                kept.Add(element.Children == null ? element : element with { Children = Cut(element.Children) });
            }

            return kept;
        }
    }

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

    // Presets as save_layout writes them (roadmap M2.8): the exact process name and the window's identity, plus a
    // launch command and focus the user added to the first placement by hand
    public static IReadOnlyList<LayoutPreset> Layouts { get; } = Enumerable.Range(1, 5)
        .Select(i => new LayoutPreset(
            Name: $"layout-{i}",
            Description: $"Preset {i}: editor left, browser right, terminal below",
            Placements: Enumerable.Range(0, 6).Select(p => new WindowPlacement(
                Matcher: new WindowMatcher(ProcessName: ProcessNames[p], ClassName: "Chrome_WidgetWin_1")
                {
                    ExactProcessName = true,
                    TitlePattern = $"README.md - WindowsAppManagerMcp - {ProcessNames[p]}",
                    ExePath = $@"C:\Program Files\{ProcessNames[p]}\{ProcessNames[p]}.exe",
                    Ordinal = 0
                },
                MonitorIndex: p % 2,
                Position: new RelativePosition(p % 2 * 0.5, 0, 0.5, 1),
                LaunchCommand: p == 0 ? "code" : null,
                Focus: p == 0,
                Order: p)
            {
                State = p == 0 ? WindowState.Maximized : WindowState.Normal
            }).ToList())
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
