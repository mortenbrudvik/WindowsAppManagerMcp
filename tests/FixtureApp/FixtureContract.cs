using System.Globalization;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// The contract between the fixture app (tests/FixtureApp) and the end-to-end tests: process name,
/// window titles, AutomationIds, the event log format and the swatch colors. The E2E project compiles
/// this file too (a linked source file), so both sides always agree.
/// </summary>
/// <remarks>
/// Every window title carries the instance token the harness passes with <c>--instance</c>, so a test
/// finds only the fixture it started, even when another fixture or an unrelated window has a similar title.
/// </remarks>
public static class FixtureContract
{
    /// <summary>The fixture's process name (its AssemblyName).</summary>
    public const string ProcessName = "WamFixtureApp";

    /// <summary>Start of the main window's title.</summary>
    public const string MainTitlePrefix = "WAM Fixture Main";

    /// <summary>Start of the modal dialog's title.</summary>
    public const string ModalTitlePrefix = "WAM Fixture Modal";

    /// <summary>Start of the document window's title.</summary>
    public const string DocumentTitlePrefix = "WAM Fixture Document";

    /// <summary>Start of the title of the save prompt the document shows when it is closed with unsaved changes.</summary>
    public const string SavePromptTitlePrefix = "WAM Fixture Save Prompt";

    /// <summary>Start of the title of the Save As dialog the main window's Save As button shows.</summary>
    public const string SaveAsTitlePrefix = "WAM Fixture Save As";

    /// <summary>Start of the title of the task dialog the main window's Task dialog button shows.</summary>
    public const string TaskDialogTitlePrefix = "WAM Fixture Task Dialog";

    /// <summary>The task dialog's main instruction.</summary>
    public const string TaskDialogInstruction = "Keep the fixture's draft?";

    /// <summary>What the fixture writes into the file its Save As dialog returns.</summary>
    public const string SavedFileText = "Saved by the WAM fixture's Save As dialog.";

    /// <summary>Start of the table window's title.</summary>
    public const string TableTitlePrefix = "WAM Fixture Table";

    /// <summary>Start of the menu window's title.</summary>
    public const string MenuTitlePrefix = "WAM Fixture Menu";

    /// <summary>Start of the WebView2 window's title.</summary>
    public const string WebTitlePrefix = "WAM Fixture Web";

    /// <summary>The heading of the WebView2 pane's page.</summary>
    public const string WebHeading = "WAM fixture web content";

    /// <summary>The text of the button on the WebView2 pane's page (its HTML id is <see cref="Ids.WebButton"/>).</summary>
    public const string WebButtonName = "Web button";

    /// <summary>Start of the canvas window's title (roadmap M6.3).</summary>
    public const string CanvasTitlePrefix = "WAM Fixture Canvas";

    /// <summary>Start of the overlay's title (roadmap M6.7).</summary>
    public const string OverlayTitlePrefix = "WAM Fixture Overlay";

    /// <summary>Number of items in the virtualized list.</summary>
    public const int VirtualListItemCount = 10_000;

    /// <summary>Number of rows of the table window's data grid: far more than it realizes.</summary>
    public const int TableRowCount = 1_000;

    /// <summary>Title of the main window of fixture instance <paramref name="instance"/>.</summary>
    public static string MainTitle(string instance) => $"{MainTitlePrefix} {instance}";

    /// <summary>Title of the modal dialog of fixture instance <paramref name="instance"/>.</summary>
    public static string ModalTitle(string instance) => $"{ModalTitlePrefix} {instance}";

    /// <summary>Title of the document window; it ends with '*' while the document has unsaved changes.</summary>
    public static string DocumentTitle(string instance, bool dirty) =>
        $"{DocumentTitlePrefix} {instance} - Untitled{(dirty ? "*" : "")}";

    /// <summary>Title of the save prompt of fixture instance <paramref name="instance"/>.</summary>
    public static string SavePromptTitle(string instance) => $"{SavePromptTitlePrefix} {instance}";

    /// <summary>Title of the Save As dialog of fixture instance <paramref name="instance"/>.</summary>
    public static string SaveAsTitle(string instance) => $"{SaveAsTitlePrefix} {instance}";

    /// <summary>Title of the task dialog of fixture instance <paramref name="instance"/>.</summary>
    public static string TaskDialogTitle(string instance) => $"{TaskDialogTitlePrefix} {instance}";

    /// <summary>Title of the table window of fixture instance <paramref name="instance"/>.</summary>
    public static string TableTitle(string instance) => $"{TableTitlePrefix} {instance}";

    /// <summary>Title of the menu window of fixture instance <paramref name="instance"/>.</summary>
    public static string MenuTitle(string instance) => $"{MenuTitlePrefix} {instance}";

    /// <summary>Title of the WebView2 window of fixture instance <paramref name="instance"/>.</summary>
    public static string WebTitle(string instance) => $"{WebTitlePrefix} {instance}";

    /// <summary>Title of the canvas window of fixture instance <paramref name="instance"/>.</summary>
    public static string CanvasTitle(string instance) => $"{CanvasTitlePrefix} {instance}";

    /// <summary>Title of the overlay of fixture instance <paramref name="instance"/>.</summary>
    public static string OverlayTitle(string instance) => $"{OverlayTitlePrefix} {instance}";

    /// <summary>
    /// The lines the canvas window draws (roadmap M6.3), top to bottom, in Segoe UI: pixels only, which UI Automation cannot
    /// read (the canvas's automation peer has no name, value or text, and no children). Every word appears once, and in no
    /// title or control, so an OCR hit can only come from the canvas. The <c>canvasOpened</c> event reports where each line
    /// and word landed on the screen.
    /// </summary>
    public static readonly IReadOnlyList<CanvasLine> CanvasLines =
    [
        new("Glyph quartz 4711", 28, LightOnDark: false),
        new("Harbor lantern 2938", 20, LightOnDark: false),
        new("Inverted meadow 0815", 20, LightOnDark: true),
        new("Small print 3141", 12, LightOnDark: false)
    ];

    /// <summary>
    /// One line of the canvas: its text, its font size in device-independent units (12 is 9 pt) and whether it is light
    /// text on a dark band instead of dark text on the white canvas.
    /// </summary>
    public sealed record CanvasLine(string Text, double FontSize, bool LightOnDark);

    /// <summary>The words of a canvas line, as the canvas reports them: the runs between spaces.</summary>
    public static IReadOnlyList<string> CanvasWords(string line) =>
        CanvasWordSpans(line).Select(span => line.Substring(span.Start, span.Length)).ToList();

    /// <summary>Where each word of <paramref name="line"/> starts, and its length.</summary>
    public static IReadOnlyList<(int Start, int Length)> CanvasWordSpans(string line)
    {
        var spans = new List<(int, int)>();
        var start = -1;
        for (var i = 0; i <= line.Length; i++)
        {
            var isSpace = i == line.Length || line[i] == ' ';
            if (!isSpace && start < 0)
            {
                start = i;
            }
            else if (isSpace && start >= 0)
            {
                spans.Add((start, i - start));
                start = -1;
            }
        }

        return spans;
    }

    /// <summary>The change panel's color and label before its one change (roadmap M6.4, <c>--change-after-ms</c>).</summary>
    public static readonly Swatch ChangePanelBefore = new(Ids.ChangePanel, 0xF0, 0xC0, 0x20);

    /// <summary>The change panel's color and label after it.</summary>
    public static readonly Swatch ChangePanelAfter = new(Ids.ChangePanel, 0x60, 0x20, 0xA0);

    public const string ChangePanelBeforeText = "Old";

    public const string ChangePanelAfterText = "New";

    /// <summary>The size of the change panel and of the spinner, in device-independent units.</summary>
    public const int TimedPanelSize = 40;

    /// <summary>How often the spinner turns while it animates.</summary>
    public const int SpinnerIntervalMs = 50;

    /// <summary>Text of item <paramref name="index"/> of the virtualized list.</summary>
    public static string VirtualListItem(int index) => $"Item {index:D5}";

    /// <summary>The column headers of the table window's data grid, in order.</summary>
    public static readonly IReadOnlyList<string> TableColumns = ["Name", "Quantity", "Price"];

    /// <summary>The cells of row <paramref name="index"/> of the data grid, one per column of <see cref="TableColumns"/>.</summary>
    public static IReadOnlyList<string> TableRow(int index) =>
    [
        $"Row {index:D4}",
        (index % 7 + 1).ToString(CultureInfo.InvariantCulture),
        (index * 0.25m + 1m).ToString("0.00", CultureInfo.InvariantCulture)
    ];

    /// <summary>The row header of row <paramref name="index"/> of the data grid.</summary>
    public static string TableRowHeader(int index) => $"#{index + 1}";

    /// <summary>The combo box items, in order. The first one is selected at start.</summary>
    public static readonly IReadOnlyList<string> ComboBoxItems = ["Alpha", "Bravo", "Charlie", "Delta"];

    /// <summary>Solid color swatches on the main window, for pixel checks in captures.</summary>
    public static readonly IReadOnlyList<Swatch> Swatches =
    [
        new(Ids.SwatchRed, 0xE0, 0x20, 0x30),
        new(Ids.SwatchGreen, 0x20, 0xB0, 0x40),
        new(Ids.SwatchBlue, 0x20, 0x40, 0xD0)
    ];

    /// <summary>A solid color swatch: its AutomationId and color.</summary>
    public sealed record Swatch(string Id, byte R, byte G, byte B);

    /// <summary>
    /// The menu window's Win32 menu bar (a real HMENU, roadmap M3.F4): File (New, Export with As Text and As CSV) and Edit
    /// (Select All). Each menu that opens is a #32768 popup window of its own, unlike a WPF menu.
    /// </summary>
    public static class Win32Menu
    {
        public const string File = "File";
        public const string New = "New";
        public const string Export = "Export";
        public const string ExportAsText = "As Text";
        public const string ExportAsCsv = "As CSV";
        public const string Edit = "Edit";
        public const string SelectAll = "Select All";

        public const int NewCommand = 101;
        public const int ExportAsTextCommand = 111;
        public const int ExportAsCsvCommand = 112;
        public const int SelectAllCommand = 201;

        /// <summary>The menu path of each command id, as element_action's menu_path takes it.</summary>
        public static readonly IReadOnlyDictionary<int, string> CommandPaths = new Dictionary<int, string>
        {
            [NewCommand] = $"{File}>{New}",
            [ExportAsTextCommand] = $"{File}>{Export}>{ExportAsText}",
            [ExportAsCsvCommand] = $"{File}>{Export}>{ExportAsCsv}",
            [SelectAllCommand] = $"{Edit}>{SelectAll}"
        };
    }

    /// <summary>
    /// The task dialog's buttons (roadmap M3.F4): two command links, each with its text and the id TaskDialogIndirect
    /// returns, and the common Cancel button (IDCANCEL), whose AutomationId is CommandButton_2.
    /// </summary>
    public static class TaskDialogLinks
    {
        public const string Keep = "Keep the draft";
        public const string Discard = "Discard the draft";

        public const int KeepCommand = 1001;
        public const int DiscardCommand = 1002;
        public const int CancelCommand = 2;
    }

    /// <summary>UI Automation AutomationIds of the fixture's elements.</summary>
    public static class Ids
    {
        // Main window
        public const string EditBox = "editBox";
        public const string PasswordBox = "passwordBox";
        public const string CheckBox = "checkBox";
        public const string ComboBox = "comboBox";
        public const string OpenModalButton = "openModalButton";
        public const string OpenDocumentButton = "openDocumentButton";
        public const string SaveAsButton = "saveAsButton";
        public const string TaskDialogButton = "taskDialogButton";
        public const string ContextMenuTarget = "contextMenuTarget";
        public const string ContextMenu = "contextMenu";
        public const string ContextMenuCopy = "contextMenuCopy";
        public const string ContextMenuPaste = "contextMenuPaste";
        public const string ContextMenuDelete = "contextMenuDelete";
        public const string VirtualList = "virtualList";
        public const string StatusText = "statusText";
        public const string SwatchRed = "swatchRed";
        public const string SwatchGreen = "swatchGreen";
        public const string SwatchBlue = "swatchBlue";

        // Main window, only with --change-after-ms and --animate-for-ms (roadmap M6.4), right of the swatches
        public const string ChangePanel = "changePanel";
        public const string Spinner = "spinner";

        // Canvas window (roadmap M6.3): the element that draws the text, with no text of its own
        public const string CanvasSurface = "canvasSurface";

        // Modal dialog
        public const string ModalTextBox = "modalTextBox";
        public const string ModalOkButton = "modalOkButton";
        public const string ModalCancelButton = "modalCancelButton";

        // Document window
        public const string DocumentTextBox = "documentTextBox";
        public const string DocumentSaveButton = "documentSaveButton";

        // Table window
        public const string DataGrid = "dataGrid";

        // WebView2 window: the WPF control, and the HTML id of the page's button (Chromium reports it as the AutomationId)
        public const string WebView = "webView";
        public const string WebButton = "webButton";
    }

    /// <summary>
    /// The fixture writes one JSON object per line to the event log (<c>--event-log</c>). Every object has
    /// <c>seq</c> (1, 2, ...), <c>event</c> (one of these names) and <c>time</c>, plus the fields listed here.
    /// The log is the fixture's own record of what it received, independent of what the server reports.
    /// </summary>
    public static class Events
    {
        /// <summary>
        /// The main window has rendered for the first time. Fields: <c>pid</c>, <c>hwnd</c>, <c>title</c>,
        /// <c>dpi</c> (GetDpiForWindow), <c>dpiAwareness</c> (unaware, system, perMonitor or perMonitorV2),
        /// <c>windowRect</c> (GetWindowRect), <c>clientRect</c> (the client area in screen coordinates) and
        /// <c>elements</c> (AutomationId to screen rectangle). Rectangles are objects with x, y, width and
        /// height, in the fixture's own screen coordinates: physical pixels unless it runs DPI unaware.
        /// </summary>
        public const string Ready = "ready";

        /// <summary>A text box changed. Fields: <c>id</c>, <c>text</c>.</summary>
        public const string TextChanged = "textChanged";

        /// <summary>The check box changed. Fields: <c>id</c>, <c>isChecked</c>.</summary>
        public const string CheckedChanged = "checkedChanged";

        /// <summary>A selection changed. Fields: <c>id</c>, <c>index</c>, <c>value</c>.</summary>
        public const string SelectionChanged = "selectionChanged";

        /// <summary>A button was clicked or invoked. Fields: <c>id</c>.</summary>
        public const string ButtonClicked = "buttonClicked";

        /// <summary>An element got keyboard focus. Fields: <c>id</c> (empty when it has no AutomationId).</summary>
        public const string KeyboardFocus = "keyboardFocus";

        /// <summary>The modal dialog is shown and the main window is disabled. Fields: <c>hwnd</c>, <c>ownerHwnd</c>.</summary>
        public const string ModalOpened = "modalOpened";

        /// <summary>The modal dialog closed. Fields: <c>result</c> (ok, cancel or none).</summary>
        public const string ModalClosed = "modalClosed";

        /// <summary>The document window is shown. Fields: <c>hwnd</c>.</summary>
        public const string DocumentOpened = "documentOpened";

        /// <summary>The document was saved (its unsaved-changes flag cleared).</summary>
        public const string DocumentSaved = "documentSaved";

        /// <summary>Closing the document with unsaved changes showed the save prompt. Fields: <c>hwnd</c> (the document).</summary>
        public const string DocumentClosePrompted = "documentClosePrompted";

        /// <summary>The document window closed. Fields: <c>result</c> (saved, discarded or clean).</summary>
        public const string DocumentClosed = "documentClosed";

        /// <summary>
        /// The Save As dialog closed. Fields: <c>result</c> (saved: the file was written; refused: the path was outside the
        /// fixture's save folder, see <see cref="FixtureSaveFolder"/>, and nothing was written; failed: writing it failed;
        /// cancel), and <c>path</c> (the path the dialog returned, unless cancelled) and <c>message</c> (when it failed).
        /// </summary>
        public const string SaveDialogClosed = "saveDialogClosed";

        /// <summary>The task dialog was created (TDN_CREATED); it is shown right after. Fields: <c>hwnd</c>.</summary>
        public const string TaskDialogOpened = "taskDialogOpened";

        /// <summary>
        /// The task dialog closed. Fields: <c>button</c> (the id of the button that closed it, see
        /// <see cref="TaskDialogLinks"/>; 0 when it could not be shown) and <c>message</c> (why, when it could not be shown).
        /// </summary>
        public const string TaskDialogClosed = "taskDialogClosed";

        /// <summary>The table window is shown. Fields: <c>hwnd</c>.</summary>
        public const string TableOpened = "tableOpened";

        /// <summary>The menu window is shown, with its Win32 menu bar. Fields: <c>hwnd</c>.</summary>
        public const string MenuWindowOpened = "menuWindowOpened";

        /// <summary>
        /// A command of the menu window's Win32 menu bar ran (WM_COMMAND). Fields: <c>command</c> (its id, see
        /// <see cref="Win32Menu"/>) and <c>path</c> (its menu path, such as File&gt;Export&gt;As Text).
        /// </summary>
        public const string MenuCommand = "menuCommand";

        /// <summary>
        /// The WebView2 window's page finished loading. Fields: <c>hwnd</c> (the window) and <c>runtimeVersion</c> (the
        /// WebView2 runtime's version).
        /// </summary>
        public const string WebViewReady = "webViewReady";

        /// <summary>
        /// The WebView2 window could not show its page. Fields: <c>runtimeMissing</c> (true when no WebView2 runtime is
        /// installed) and <c>message</c>.
        /// </summary>
        public const string WebViewFailed = "webViewFailed";

        /// <summary>The context menu opened.</summary>
        public const string ContextMenuOpened = "contextMenuOpened";

        /// <summary>A context menu item was clicked or invoked. Fields: <c>id</c>.</summary>
        public const string ContextMenuItemClicked = "contextMenuItemClicked";

        /// <summary>
        /// A mouse button went down in the main window (roadmap M4.2). Fields: <c>button</c> (left, right, middle, x1 or x2:
        /// the button as the window gets it, after Windows applied the user's button swap), <c>clickCount</c> (WPF's: 2 for
        /// the second press of a double click, 3 for a triple click), <c>x</c> and <c>y</c> (where the press was, in screen
        /// coordinates: GetMessagePos of the button message, physical pixels unless the fixture runs DPI unaware) and
        /// <c>id</c> (the AutomationId of the element under it, or of the nearest element above that has one; empty when
        /// none has), and <c>modifiers</c> (roadmap M4.3: the modifier keys down as the window handles the press, "ctrl",
        /// "shift", "alt" and "win" joined by '+', empty when none is).
        /// </summary>
        public const string MouseDown = "mouseDown";

        /// <summary>
        /// A mouse button came up in the main window (roadmap M4.3: a drag's drop). Fields as <see cref="MouseDown"/>'s, without
        /// <c>clickCount</c>.
        /// </summary>
        public const string MouseUp = "mouseUp";

        /// <summary>
        /// The canvas window has rendered (roadmap M6.3). Fields: <c>hwnd</c>, <c>title</c>, <c>dpi</c> (GetDpiForWindow) and
        /// <c>lines</c>: one object per line of <see cref="CanvasLines"/>, with <c>text</c>, <c>rect</c> (the line's ink box,
        /// a rectangle as in the ready event) and <c>words</c> (objects with <c>text</c> and <c>rect</c>).
        /// </summary>
        public const string CanvasOpened = "canvasOpened";

        /// <summary>
        /// A mouse button went down on the canvas (roadmap M6.3: a click on a word OCR found). Fields: <c>button</c>, <c>x</c>
        /// and <c>y</c> as in <see cref="MouseDown"/>, and <c>word</c> (the canvas word under the press; empty when none is).
        /// </summary>
        public const string CanvasMouseDown = "canvasMouseDown";

        /// <summary>
        /// The overlay has rendered (roadmap M6.7). Fields: <c>hwnd</c>, <c>topmost</c> and <c>toolWindow</c> (whether its
        /// extended style has WS_EX_TOPMOST and WS_EX_TOOLWINDOW), and <c>ownerHwnd</c> (GetWindow GW_OWNER: 0, it is
        /// unowned, so the z-order of the main window and of the overlay are independent).
        /// </summary>
        public const string OverlayOpened = "overlayOpened";

        /// <summary>
        /// The change panel changed color and label, once, <c>--change-after-ms</c> after the ready event (roadmap M6.4).
        /// Fields: <c>id</c> (<see cref="Ids.ChangePanel"/>) and <c>rect</c> (where it is on the screen).
        /// </summary>
        public const string PanelChanged = "panelChanged";

        /// <summary>
        /// The spinner started turning, right after the ready event (roadmap M6.4). Fields: <c>id</c>
        /// (<see cref="Ids.Spinner"/>), <c>rect</c> and <c>durationMs</c>.
        /// </summary>
        public const string AnimationStarted = "animationStarted";

        /// <summary>The spinner stopped, <c>--animate-for-ms</c> after it started, and stays still. Fields: <c>id</c>.</summary>
        public const string AnimationStopped = "animationStopped";

        /// <summary>
        /// The command line had arguments after <see cref="FixtureOptions.EndOfOptions"/>, which the fixture ignored (roadmap
        /// M6.6). Fields: <c>count</c>. Their text is not logged.
        /// </summary>
        public const string ArgumentsIgnored = "argumentsIgnored";

        /// <summary>The fixture is exiting. Fields: <c>reason</c> (closed, parentExited or maxLifetime).</summary>
        public const string Exiting = "exiting";

        /// <summary>An exception reached the dispatcher. Fields: <c>message</c>.</summary>
        public const string UnhandledException = "unhandledException";
    }
}
