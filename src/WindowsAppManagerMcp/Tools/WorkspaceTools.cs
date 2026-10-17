using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// Workspaces (roadmap M7.5): save_workspace and restore_workspace, in the workspaces toolset, off by default. A workspace is
/// a layout preset that also holds what its File Explorer and browser windows showed.
/// </summary>
[McpServerToolType]
public class WorkspaceTools
{
    private readonly IWorkspaceService _workspaces;
    private readonly IInputSession? _session;
    private readonly TimeSpan? _gateWaitTimeout;

    public WorkspaceTools(IWorkspaceService workspaces, IInputSession? session = null, TimeSpan? gateWaitTimeout = null)
    {
        _workspaces = workspaces;
        _session = session;
        _gateWaitTimeout = gateWaitTimeout;
    }

    [McpServerTool(Name = "save_workspace", Title = "Save Workspace", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Save the open windows as a workspace: save_layout's preset (same filters), plus each File Explorer window's folder " +
                 "and each browser window's pages (every tab when the browser has a debug port, else only the page it shows: " +
                 "partialBrowsers). Pages are http/https only, with secret-looking query values removed; no program arguments are " +
                 "kept. Replaces a layout or workspace of the same name. restore_workspace brings it back; apply_layout arranges it as a layout.")]
    public async Task<WorkspaceSaveResult> SaveWorkspace(
        [Description("Name of the workspace (a layout preset's name)")]
        string name,
        [Description("Optional description")]
        string? description = null,
        [Description("Only windows of these processes (as save_layout)")]
        string[]? includeProcesses = null,
        [Description("Leave out windows of these processes (as save_layout)")]
        string[]? excludeProcesses = null,
        [Description("Also save minimized windows (default: false)")]
        bool includeMinimized = false,
        CancellationToken cancellationToken = default) =>
        await _workspaces.SaveAsync(name, description, includeProcesses, excludeProcesses, includeMinimized, cancellationToken)
            .ConfigureAwait(false);

    [McpServerTool(Name = "restore_workspace", Title = "Restore Workspace", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description("Restore a workspace save_workspace saved, best effort: open each folder and browser window no window shows (File " +
                 "Explorer; Chrome, Edge, Brave, Vivaldi, Opera or Firefox with its pages in a new window), then arrange the windows " +
                 "as apply_layout with launchMissing (starting missing programs, without arguments). opened says what each window " +
                 "needed (alreadyOpen, opened, missing, unsupported, failed), layout what apply_layout did. dryRun opens and changes " +
                 "nothing. A preset an agent wrote is refused (LaunchNotConfirmed): use apply_layout, which asks the user. " +
                 "PausedByUser while input is paused.")]
    public async Task<WorkspaceRestoreResult> RestoreWorkspace(
        [Description("Name of the workspace")]
        string name,
        [Description("Report what would be opened and arranged, and change nothing (default: false)")]
        bool dryRun = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // A dry run only reads, so it neither waits for the input gate nor stops while input is paused (as apply_layout)
        if (dryRun)
        {
            return await _workspaces.RestoreAsync(name, dryRun: true, ToolProgress.ForServices(progress), cancellationToken).ConfigureAwait(false);
        }

        InputGateToolFilter.ThrowIfPaused(_session);
        using (await InputGateToolFilter.EnterAsync(_session, cancellationToken, _gateWaitTimeout).ConfigureAwait(false))
        {
            return await _workspaces.RestoreAsync(name, dryRun: false, ToolProgress.ForServices(progress), cancellationToken).ConfigureAwait(false);
        }
    }
}
