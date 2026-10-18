using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Models.Results;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Workspaces (roadmap M7.5): a layout preset that also holds what its File Explorer and browser windows showed, and a restore
/// that opens what is missing before it arranges the windows. Best effort: a program that keeps one window, a browser without
/// a debug port (only its shown page is kept) and pages that need a sign-in cannot come back exactly.
/// </summary>
public interface IWorkspaceService
{
    /// <summary>
    /// Captures the windows as save_layout does (the same filters), then records each File Explorer window's folder and each
    /// browser window's pages in its placement, and saves the preset again.
    /// </summary>
    Task<WorkspaceSaveResult> SaveAsync(string name, string? description, string[]? includeProcesses, string[]? excludeProcesses,
        bool includeMinimized, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens each placement's folder or pages that no window shows (File Explorer, or the browser with its pages in a new
    /// window), then applies the preset as apply_layout with launchMissing does. A dry run opens and changes nothing.
    /// The caller holds the input gate for a restore that is not a dry run.
    /// </summary>
    Task<WorkspaceRestoreResult> RestoreAsync(string name, bool dryRun, IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class WorkspaceService : IWorkspaceService
{
    /// <summary>How long a restore waits for the window of a folder or of a browser it opened.</summary>
    public const int OpenWaitMs = 15_000;

    /// <summary>How long a save waits for a browser's tab list over its debug port.</summary>
    private static readonly TimeSpan TabListTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The Chromium browsers: they take --new-window and the pages as arguments.</summary>
    private static readonly HashSet<string> ChromiumBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "vivaldi", "opera", "chromium"
    };

    /// <summary>
    /// What the browsers put in a private window's title (Edge's [InPrivate], Chrome's Incognito, Firefox's Private Browsing,
    /// Brave's Private): such a window's pages are never kept, nor reopened in a window that is not private.
    /// </summary>
    private static readonly string[] PrivateWindowMarkers = ["InPrivate", "Incognito", "Private Browsing", "(Private)"];

    private readonly ILayoutService _layouts;
    private readonly IWindowService _windows;
    private readonly INativeWindowWrapper _native;
    private readonly IBrowserReadingService _browsers;
    private readonly ICdpTabService? _tabs;
    private readonly INativeShellWindowsWrapper _shell;
    private readonly IProcessService _processes;
    private readonly ITargetPolicy? _policy;
    private readonly ILogger<WorkspaceService>? _logger;

    public WorkspaceService(
        ILayoutService layouts,
        IWindowService windows,
        INativeWindowWrapper native,
        IBrowserReadingService browsers,
        INativeShellWindowsWrapper shell,
        IProcessService processes,
        ICdpTabService? tabs = null,
        ITargetPolicy? policy = null,
        ILogger<WorkspaceService>? logger = null)
    {
        _layouts = layouts;
        _windows = windows;
        _native = native;
        _browsers = browsers;
        _shell = shell;
        _processes = processes;
        _tabs = tabs;
        _policy = policy;
        _logger = logger;
    }

    // ---- Save ----

    public async Task<WorkspaceSaveResult> SaveAsync(string name, string? description, string[]? includeProcesses,
        string[]? excludeProcesses, bool includeMinimized, CancellationToken cancellationToken = default)
    {
        var saved = await _layouts.CaptureCurrentLayoutAsync(name, description, includeProcesses, excludeProcesses, includeMinimized,
            cancellationToken).ConfigureAwait(false);
        if (!saved.Success || _layouts.GetPreset(name) is not { } preset)
        {
            return new WorkspaceSaveResult(false, name, Error: saved.ErrorMessage ?? "The workspace could not be saved.",
                ErrorCode: saved.ErrorCode ?? nameof(LayoutErrorCode.SaveFailed));
        }

        // The windows as the capture saw them a moment ago: each placement is matched to its window by process, title and
        // its place among the process's windows
        var windows = _windows.GetAllWindows(includeMinimized);
        var warnings = new List<string>();
        var listing = _shell.GetExplorerFolders();
        var folders = listing?.Folders;
        if (listing == null)
        {
            warnings.Add("File Explorer did not answer, so no folder was kept; save the workspace again to keep them.");
        }
        else if (listing.SkippedWindows > 0)
        {
            // Their folders are unknown: the placements of those windows are saved without one
            warnings.Add($"{Windows(listing.SkippedWindows)} could not be read, so the folder each shows was not kept; save the " +
                         "workspace again to keep it.");
        }

        var assigned = new HashSet<nint>();
        var placements = new List<WindowPlacement>(preset.Placements.Count);
        int folderCount = 0, browserCount = 0, urlCount = 0, partialCount = 0;

        foreach (var placement in preset.Placements)
        {
            var window = WindowOf(placement, windows, assigned);
            WorkspaceContent? content = null;
            if (window != null && (_policy == null || _policy.Evaluate(window).Allowed))
            {
                if (folders?.FirstOrDefault(f => f.Window == window.Handle) is { Path: { } folder })
                {
                    content = new WorkspaceContent(Folder: folder);
                    folderCount++;
                }
                else if (IsPrivateWindow(window))
                {
                    warnings.Add($"A private browsing window of {window.ProcessName} was left out: its pages are not kept.");
                }
                else if (_browsers.IsBrowserWindow(window.Handle))
                {
                    var (urls, partial, warning) = await ReadPagesAsync(window, cancellationToken).ConfigureAwait(false);
                    if (warning != null)
                    {
                        warnings.Add(warning);
                    }

                    if (urls.Count > 0)
                    {
                        content = new WorkspaceContent(Urls: urls, UrlsPartial: partial ? true : null);
                        browserCount++;
                        urlCount += urls.Count;
                        partialCount += partial ? 1 : 0;
                    }
                }
            }

            placements.Add(content == null ? placement : placement with { Content = content });
        }

        if (folderCount + browserCount > 0)
        {
            var withContent = await _layouts.SavePresetAsync(preset with { Placements = placements, UpdatedAt = DateTime.UtcNow },
                cancellationToken).ConfigureAwait(false);
            if (!withContent.Success)
            {
                return new WorkspaceSaveResult(false, name, Error: withContent.ErrorMessage, ErrorCode: withContent.ErrorCode);
            }
        }

        if (partialCount > 0)
        {
            warnings.Add($"{partialCount} browser window(s) have no debug port, so only the page each showed was kept, not its other tabs.");
        }

        return new WorkspaceSaveResult(true, name, placements.Count, folderCount, browserCount, urlCount, partialCount,
            warnings.Count > 0 ? warnings : null);
    }

    /// <summary>The open window a captured placement came from, not one already matched to another placement.</summary>
    private static WindowInfo? WindowOf(WindowPlacement placement, IReadOnlyList<WindowInfo> windows, HashSet<nint> assigned)
    {
        var processName = placement.Matcher.ProcessName;
        if (string.IsNullOrEmpty(processName))
        {
            return null;
        }

        // The process's windows in z-order, as the capture numbered them (Ordinal)
        var sameProcess = windows.Where(w => string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase)).ToList();
        var window = sameProcess.FirstOrDefault(w => !assigned.Contains(w.Handle) && placement.Matcher.TitlePattern != null &&
                                                     string.Equals(w.Title, placement.Matcher.TitlePattern, StringComparison.Ordinal))
                     ?? (placement.Matcher.Ordinal is { } ordinal && ordinal < sameProcess.Count && !assigned.Contains(sameProcess[ordinal].Handle)
                         ? sameProcess[ordinal]
                         : sameProcess.FirstOrDefault(w => !assigned.Contains(w.Handle)));
        if (window != null)
        {
            assigned.Add(window.Handle);
        }

        return window;
    }

    /// <summary>
    /// A browser window's pages: every tab of the window over the browser's debug port when it has one, else the page it shows
    /// (partial). Cleaned (<see cref="WorkspaceUrls.Clean"/>), without repeats, at most <see cref="WorkspaceUrls.MaxUrlsPerWindow"/>.
    /// </summary>
    private async Task<(IReadOnlyList<string> Urls, bool Partial, string? Warning)> ReadPagesAsync(WindowInfo window,
        CancellationToken cancellationToken)
    {
        BrowserUrlResult shown;
        try
        {
            shown = await _browsers.GetBrowserUrlAsync(window.Handle, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], true, $"The pages of {window.ProcessName} window '{AppTextOf(window)}' could not be read: {ex.Message}");
        }

        var raw = new List<string>();
        var partial = true;
        if (shown.CdpPort is { } port && _tabs != null)
        {
            try
            {
                var describe = CdpWindowBinder.Describe(_native, window.Handle, (uint)window.ProcessId, window.Title);
                var listing = await _tabs.ListTabsAsync(port, window.ProcessId, describe, TabListTimeout, cancellationToken).ConfigureAwait(false);
                if (listing is { Success: true, Value: { WindowBound: true } tabs })
                {
                    raw.AddRange(tabs.Tabs.Where(t => t.InWindow).Select(t => t.Target.Url));
                    partial = false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "The tabs of browser window {Handle} could not be listed", window.Handle);
            }
        }

        if (partial && shown.Success && shown.Url != null)
        {
            raw.Add(shown.Url);
        }

        var urls = raw.Select(WorkspaceUrls.Clean).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        string? warning = null;
        if (urls.Count > WorkspaceUrls.MaxUrlsPerWindow)
        {
            warning = $"{window.ProcessName} window '{AppTextOf(window)}' has {urls.Count} pages; the first {WorkspaceUrls.MaxUrlsPerWindow} were kept.";
            urls = urls.Take(WorkspaceUrls.MaxUrlsPerWindow).ToList();
        }

        return (urls, partial, warning);
    }

    private static bool IsPrivateWindow(WindowInfo window) =>
        PrivateWindowMarkers.Any(marker => window.Title.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string AppTextOf(WindowInfo window) => Infrastructure.AppText.Cut(window.Title, 60) ?? string.Empty;

    // ---- Restore ----

    public async Task<WorkspaceRestoreResult> RestoreAsync(string name, bool dryRun, IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_layouts.GetPreset(name) is not { } preset)
        {
            // A file that is no valid preset and may hold it is named (review finding F1)
            return new WorkspaceRestoreResult(false, name, "failed",
                Error: $"No layout or workspace named '{name}'. list_layouts lists them." + LayoutService.NotFoundNote(name, _layouts.GetInvalidFiles()),
                ErrorCode: nameof(LayoutErrorCode.PresetNotFound));
        }

        // An agent wrote this preset: its launches need the user's yes, which apply_layout asks for (owner decision D15)
        if (LayoutPresetSchema.IsAgentAuthored(preset))
        {
            return new WorkspaceRestoreResult(false, name, "failed",
                Error: $"'{name}' was written by an agent, not saved by save_workspace, so restore_workspace does not open anything for it. " +
                       "Use apply_layout with launchMissing, which asks the user before it starts programs.",
                ErrorCode: nameof(LayoutErrorCode.LaunchNotConfirmed));
        }

        var outcomes = new List<WorkspaceContentOutcome>();
        var listing = preset.Placements.Any(p => p.Content?.Folder != null) ? _shell.GetExplorerFolders() : new ExplorerFolderListing([], 0);
        var explorer = listing?.Folders;
        if (explorer == null)
        {
            // Not knowing which folders are open, opening them could open every one a second time
            explorer = [];
            foreach (var (placement, i) in preset.Placements.Select((p, i) => (p, i)).Where(t => t.p.Content?.Folder != null))
            {
                outcomes.Add(new WorkspaceContentOutcome(i, "folder", "failed", placement.Content!.Folder,
                    Error: "File Explorer did not answer, so whether the folder is open is unknown; it was not opened. Try again."));
            }
        }

        var windows = preset.Placements.Any(p => p.Content?.Urls is { Count: > 0 }) ? _windows.GetAllWindows(includeMinimized: true) : [];

        for (var i = 0; i < preset.Placements.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placement = preset.Placements[i];
            if (placement.Content is not { } content)
            {
                continue;
            }

            if (content.Folder is { } folder)
            {
                if (outcomes.Any(o => o.Placement == i))
                {
                    continue;
                }

                outcomes.Add(await RestoreFolderAsync(i, folder, explorer, dryRun, cancellationToken).ConfigureAwait(false));
            }
            else if (content.Urls is { Count: > 0 } urls)
            {
                outcomes.Add(await RestorePagesAsync(i, placement, urls, windows, dryRun, cancellationToken).ConfigureAwait(false));
            }
        }

        // A File Explorer window the shell could not read may show a folder that was opened (or would be) as missing
        List<string>? warnings = null;
        if (listing is { SkippedWindows: > 0 } &&
            outcomes.Any(o => o.Kind == "folder" && o.Status is "opened" or "wouldOpen"))
        {
            warnings = [$"{Windows(listing.SkippedWindows)} could not be read, so a folder one of them shows looked closed: it " +
                        $"{(dryRun ? "would be" : "may have been")} opened a second time. Check the File Explorer windows with find_windows."];
        }

        // The windows are there now (or will be launched): the layout puts them in place, as apply_layout with launchMissing
        var layout = await _layouts.ApplyPresetAsync(name, LayoutMatchBy.ProcessAndTitle, launchMissing: true,
            new LayoutApplyOptions(dryRun, null), progress, cancellationToken).ConfigureAwait(false);
        var openFailed = outcomes.Any(o => o.Status is "failed" or "missing" or "unsupported");
        var status = dryRun ? "dryRun" : layout.Success && !openFailed ? "restored" : layout.WindowsArranged > 0 || outcomes.Any(o => o.Status == "opened") ? "partial" : "failed";
        return new WorkspaceRestoreResult(layout.Success && !openFailed, name, status, outcomes, layout,
            Error: !dryRun && status != "restored" ? "Not everything of the workspace came back; see opened and layout." : null,
            Warnings: warnings);
    }

    /// <summary>"1 File Explorer window" or "3 File Explorer windows".</summary>
    private static string Windows(int count) => $"{count} File Explorer window{(count == 1 ? string.Empty : "s")}";

    private async Task<WorkspaceContentOutcome> RestoreFolderAsync(int index, string folder, IReadOnlyList<ExplorerFolder> explorer,
        bool dryRun, CancellationToken cancellationToken)
    {
        if (explorer.FirstOrDefault(f => f.Path != null && SamePath(f.Path, folder)) is { } open)
        {
            return new WorkspaceContentOutcome(index, "folder", "alreadyOpen", folder, WindowHandle: open.Window.ToInt64());
        }

        if (!Path.IsPathFullyQualified(folder))
        {
            return new WorkspaceContentOutcome(index, "folder", "failed", folder, Error: "The folder is not a full path, so it was not opened.");
        }

        if (!Directory.Exists(folder))
        {
            return new WorkspaceContentOutcome(index, "folder", "missing", folder, Error: "The folder does not exist any more.");
        }

        if (dryRun)
        {
            return new WorkspaceContentOutcome(index, "folder", "wouldOpen", folder);
        }

        var launched = await _processes.LaunchApplicationAsync("explorer.exe", [folder], waitForWindow: true, waitTimeoutMs: OpenWaitMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return launched.Success
            ? new WorkspaceContentOutcome(index, "folder", "opened", folder, WindowHandle: launched.WindowHandle?.ToInt64())
            : new WorkspaceContentOutcome(index, "folder", "failed", folder, Error: launched.ErrorMessage);
    }

    private async Task<WorkspaceContentOutcome> RestorePagesAsync(int index, WindowPlacement placement, IReadOnlyList<string> urls,
        IReadOnlyList<WindowInfo> windows, bool dryRun, CancellationToken cancellationToken)
    {
        var program = placement.Launch?.Executable ?? placement.Matcher.ExePath ?? placement.Matcher.ProcessName;
        var processName = placement.Matcher.ProcessName ?? Path.GetFileNameWithoutExtension(program) ?? string.Empty;

        // Only pages a workspace keeps are opened, whatever the file says: http and https, without secrets
        var pages = urls.Select(WorkspaceUrls.Clean).OfType<string>().Take(WorkspaceUrls.MaxUrlsPerWindow).ToList();

        // The window is open when a window of its browser has the title it had (the page it showed), or shows one of its
        // pages (a window an earlier restore opened shows its first page, under that page's own title)
        if (await OpenBrowserWindowAsync(processName, placement.Matcher.TitlePattern, pages, windows, cancellationToken).ConfigureAwait(false)
            is { } open)
        {
            return new WorkspaceContentOutcome(index, "pages", "alreadyOpen", program, urls.Count, open.ToInt64());
        }
        var arguments = NewWindowArguments(processName, pages);

        // The program is the browser the window was: a file edited by hand must not start another program with these arguments
        var isThatBrowser = program != null &&
                            string.Equals(Path.GetFileNameWithoutExtension(program), processName, StringComparison.OrdinalIgnoreCase);
        if (program == null || arguments == null || pages.Count == 0 || !isThatBrowser)
        {
            return new WorkspaceContentOutcome(index, "pages", "unsupported", program, urls.Count,
                Error: $"Pages cannot be opened in a new window of '{processName}': Chromium browsers and Firefox only.");
        }

        if (dryRun)
        {
            return new WorkspaceContentOutcome(index, "pages", "wouldOpen", program, pages.Count);
        }

        var launched = await _processes.LaunchApplicationAsync(program, arguments, waitForWindow: true, waitTimeoutMs: OpenWaitMs,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return launched.Success
            ? new WorkspaceContentOutcome(index, "pages", "opened", program, pages.Count, launched.WindowHandle?.ToInt64())
            : new WorkspaceContentOutcome(index, "pages", "failed", program, pages.Count, Error: launched.ErrorMessage);
    }

    /// <summary>A window of the browser that has <paramref name="title"/> or shows one of <paramref name="pages"/>; null when none does.</summary>
    private async Task<nint?> OpenBrowserWindowAsync(string processName, string? title, IReadOnlyList<string> pages,
        IReadOnlyList<WindowInfo> windows, CancellationToken cancellationToken)
    {
        foreach (var window in windows.Where(w => string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase) &&
                                                  !IsPrivateWindow(w)))
        {
            if (title != null && string.Equals(window.Title, title, StringComparison.Ordinal))
            {
                return window.Handle;
            }

            try
            {
                var shown = await _browsers.GetBrowserUrlAsync(window.Handle, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (shown.Success && WorkspaceUrls.Clean(shown.Url) is { } url && pages.Contains(url, StringComparer.Ordinal))
                {
                    return window.Handle;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A window whose page cannot be read counts as not showing the workspace's pages
            }
        }

        return null;
    }

    /// <summary>The arguments that open <paramref name="pages"/> in a new window of the browser; null for a browser it does not know.</summary>
    public static string[]? NewWindowArguments(string processName, IReadOnlyList<string> pages)
    {
        if (ChromiumBrowsers.Contains(processName))
        {
            return ["--new-window", .. pages];
        }

        return string.Equals(processName, "firefox", StringComparison.OrdinalIgnoreCase) ? ["-new-window", .. pages] : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
