using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Models.Results;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// What a window of a workspace showed when it was saved (roadmap M7.5), beside where it was: a File Explorer window's folder,
/// or a browser window's pages. save_workspace records it in the layout preset's placement (<see cref="WindowPlacement.Content"/>),
/// and restore_workspace opens what is missing. Never a program's arguments (owner decision D15).
/// </summary>
/// <param name="Folder">A File Explorer window's folder: a drive or network path.</param>
/// <param name="Urls">A browser window's pages (http and https only), with values that look like secrets taken out.</param>
/// <param name="UrlsPartial">
/// True when only the page the window showed was read (the browser has no debug port, so its other tabs cannot be listed).
/// </param>
public sealed record WorkspaceContent(string? Folder = null, IReadOnlyList<string>? Urls = null, bool? UrlsPartial = null);

/// <summary>The result of save_workspace (roadmap M7.5).</summary>
/// <param name="Success">True when the workspace was saved.</param>
/// <param name="Name">The workspace's name: a layout preset's.</param>
/// <param name="Windows">How many windows it holds.</param>
/// <param name="Folders">How many File Explorer folders were recorded.</param>
/// <param name="BrowserWindows">How many browser windows had pages recorded.</param>
/// <param name="Urls">How many pages were recorded in all.</param>
/// <param name="PartialBrowsers">How many browser windows had only their shown page recorded (no debug port).</param>
/// <param name="Warnings">What could not be recorded, and why.</param>
/// <param name="Error">Why the workspace was not saved.</param>
/// <param name="ErrorCode">The layout's error code when it was not saved; null on success.</param>
public sealed record WorkspaceSaveResult(
    bool Success,
    string Name,
    int Windows = 0,
    int Folders = 0,
    int BrowserWindows = 0,
    int Urls = 0,
    int PartialBrowsers = 0,
    IReadOnlyList<string>? Warnings = null,
    string? Error = null,
    string? ErrorCode = null);

/// <summary>What restore_workspace did about one window's content (roadmap M7.5).</summary>
/// <param name="Placement">The placement's index in the workspace.</param>
/// <param name="Kind">"folder" or "pages".</param>
/// <param name="Status">
/// alreadyOpen (a window shows it), opened (started now), wouldOpen (dry run), missing (the folder is gone),
/// unsupported (no way to open these pages in that browser), or failed (the start failed; error says why).
/// </param>
/// <param name="Target">The folder, or the browser program that opens the pages.</param>
/// <param name="Urls">How many pages were (or would be) opened.</param>
/// <param name="WindowHandle">The window that shows it, when one was found or opened.</param>
/// <param name="Error">Why it could not be opened.</param>
public sealed record WorkspaceContentOutcome(
    int Placement,
    string Kind,
    string Status,
    string? Target = null,
    int? Urls = null,
    long? WindowHandle = null,
    string? Error = null);

/// <summary>The result of restore_workspace (roadmap M7.5): what was opened, then the layout applied over the windows.</summary>
/// <param name="Success">True when every placement was arranged (or, in a dry run, would be) and nothing failed to open.</param>
/// <param name="Name">The workspace.</param>
/// <param name="Status">restored, partial, failed or dryRun.</param>
/// <param name="Opened">What each window's content needed: opened, already open, or why not.</param>
/// <param name="Layout">The layout applied afterwards, as apply_layout with launchMissing reports it.</param>
/// <param name="Error">Why the workspace could not be restored at all.</param>
/// <param name="ErrorCode">PresetNotFound, or LaunchNotConfirmed for a preset an agent wrote; null when it ran.</param>
public sealed record WorkspaceRestoreResult(
    bool Success,
    string Name,
    string Status,
    IReadOnlyList<WorkspaceContentOutcome>? Opened = null,
    [property: JsonPropertyName("layout")] LayoutApplyResult? Layout = null,
    string? Error = null,
    string? ErrorCode = null);
