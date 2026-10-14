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

    /// <summary>Start of the table window's title.</summary>
    public const string TableTitlePrefix = "WAM Fixture Table";

    /// <summary>Start of the menu window's title.</summary>
    public const string MenuTitlePrefix = "WAM Fixture Menu";

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

    /// <summary>Title of the table window of fixture instance <paramref name="instance"/>.</summary>
    public static string TableTitle(string instance) => $"{TableTitlePrefix} {instance}";

    /// <summary>Title of the menu window of fixture instance <paramref name="instance"/>.</summary>
    public static string MenuTitle(string instance) => $"{MenuTitlePrefix} {instance}";

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

        // Modal dialog
        public const string ModalTextBox = "modalTextBox";
        public const string ModalOkButton = "modalOkButton";
        public const string ModalCancelButton = "modalCancelButton";

        // Document window
        public const string DocumentTextBox = "documentTextBox";
        public const string DocumentSaveButton = "documentSaveButton";

        // Table window
        public const string DataGrid = "dataGrid";
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

        /// <summary>The table window is shown. Fields: <c>hwnd</c>.</summary>
        public const string TableOpened = "tableOpened";

        /// <summary>The menu window is shown, with its Win32 menu bar. Fields: <c>hwnd</c>.</summary>
        public const string MenuWindowOpened = "menuWindowOpened";

        /// <summary>
        /// A command of the menu window's Win32 menu bar ran (WM_COMMAND). Fields: <c>command</c> (its id, see
        /// <see cref="Win32Menu"/>) and <c>path</c> (its menu path, such as File&gt;Export&gt;As Text).
        /// </summary>
        public const string MenuCommand = "menuCommand";

        /// <summary>The context menu opened.</summary>
        public const string ContextMenuOpened = "contextMenuOpened";

        /// <summary>A context menu item was clicked or invoked. Fields: <c>id</c>.</summary>
        public const string ContextMenuItemClicked = "contextMenuItemClicked";

        /// <summary>The fixture is exiting. Fields: <c>reason</c> (closed, parentExited or maxLifetime).</summary>
        public const string Exiting = "exiting";

        /// <summary>An exception reached the dispatcher. Fields: <c>message</c>.</summary>
        public const string UnhandledException = "unhandledException";
    }
}
