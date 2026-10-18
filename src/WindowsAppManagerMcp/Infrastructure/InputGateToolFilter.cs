using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Call-tool filter that runs every tool that can change which window is in front, or which window is at a
/// point, inside the <see cref="InputGate"/>. The MCP SDK runs tool calls concurrently, so without it an agent
/// could call focus_window (or close the window in front, or launch an app) in parallel with type_text and
/// bring its own terminal to the front between the input guard's last check and the input. The input tools
/// take the gate themselves, around their checks and batches; read-only tools run without it.
/// </summary>
/// <remarks>
/// A tool that is in none of <see cref="InputTools"/>, <see cref="ReadOnlyTools"/>, <see cref="ControlTools"/> and
/// <see cref="SelfGatedTools"/> runs inside the gate, so a new tool is serialized until it is reviewed onto another
/// list. A contract test checks that every tool the server serves is in exactly one of the five lists. The geometry
/// batches roadmap M6.1 moved to windows-batch keep their places on the lists: a toolset that is off by default serves
/// them, and when they are served they are gated exactly as before. A name the server does not serve (a tool 3.0.0
/// removed is refused before this filter, RemovedTools) passes straight through, so the SDK reports it as a protocol error at once: a call that runs nothing
/// cannot change which window is in front.
/// <para>
/// While the user has paused input (roadmap M1.10), the same tools return PausedByUser without running: they
/// take the foreground from the user, close windows, or start programs that could type. Read-only tools keep
/// working, so the agent can still look.
/// </para>
/// <para>
/// Every single wait is bounded. A tool waits for the gate at most <see cref="GateWaitTimeout"/>, as an input call
/// does, then returns InputBusy without running; a pause while it waits returns PausedByUser at once. And a tool
/// that holds the gate does not wait on a window without a deadline: SetWindowPos and ShowWindow go through
/// <see cref="Services.Interfaces.IWindowCallRunner"/>, so a hung application cannot hold the gate until it recovers.
/// A batch's total time inside the gate still grows with its length: up to the clamped waitTimeoutMs per launched
/// application, the clamped delayBetweenMs per window, and one call deadline per window of a slow but live
/// application. The agent chooses that length; a tool that waits for the gate meanwhile gets InputBusy after
/// <see cref="GateWaitTimeout"/>.
/// </para>
/// <para>
/// launch_browser_with_debug (roadmap M5.1) also keeps the gate after its window wait (10 seconds), while it waits for
/// the browser's DevToolsActivePort file (up to ProcessService.DevToolsPortTimeout, 10 seconds, for port 0) and for its
/// DevTools endpoint to answer (up to ProcessService.DebugReadinessTimeout, 10 seconds). Those waits only poll a file and
/// HTTP, but unlike the close tools' waits they cannot be moved outside the gate: a browser whose window wait timed out
/// is still starting, and its first window can take the foreground at any moment, in the middle of an input call. So a
/// slow launch can hold the gate about 30 seconds, and an input or window call queued at its start can get InputBusy
/// (and sends nothing). A browser that exits ends the readiness wait at once. A launch that fails, or is cancelled, after
/// the start also ends the browser it started and deletes its temporary profile inside the gate (ProcessService.AbandonTimeout
/// and DebugBrowserLifecycle.AbandonTimeout: up to about 5 seconds for the exit and 5 for the delete), and a launch with a
/// new temporary profile first deletes the ones earlier browsers left; so the longest hold is about 40 seconds.
/// </para>
/// <para>
/// run_steps (roadmap M6.2) holds the gate longer on purpose: for its whole run of steps, so no other call's input or
/// window change lands between two of them. The run is bounded by its maxDurationMs (30 seconds by default, 60 at most),
/// its wait steps are cut to what is left of it, and its steps pass the gate it holds through an
/// <see cref="InputGate.Lease"/> bound to the run's flow; the gate is reentrant for nothing else.
/// </para>
/// </remarks>
public static class InputGateToolFilter
{
    /// <summary>
    /// The input tools. They hold the gate themselves (InputSimulationService), not through this filter.
    /// </summary>
    public static IReadOnlySet<string> InputTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "mouse_click", "drag", "mouse_move", "scroll", "type_text", "send_keys", "press_keys"
    };

    /// <summary>
    /// Tools that can change which window is in front or at a point: they arrange windows or start processes (a new window
    /// can take the foreground). They run inside the gate. set_clipboard runs inside it too: it changes no window, but it
    /// replaces the user's clipboard, so it waits for a running input call (a paste) and is refused while the user has
    /// paused input. The single-window tools and their batches take the gate themselves (<see cref="SelfGatedTools"/>).
    /// </summary>
    public static IReadOnlySet<string> WindowChangingTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "launch_applications_batch", "launch_browser_with_debug", "set_clipboard"
    };

    /// <summary>
    /// Tools that change windows or stop processes like <see cref="WindowChangingTools"/>, but do work first that must not
    /// hold the gate, and then take it themselves with <see cref="EnterAsync"/>, which waits, refuses and reports a pause
    /// exactly as this filter does. kill_process and the close tools may ask the user to confirm (elicitation, roadmap
    /// M2.10): the user may take minutes to answer, and every input and window call would wait for them. The close tools
    /// (roadmap M2.5) also send their close messages inside the gate and release it before they wait for the windows to
    /// close, and kill_process sends its close request or termination inside it and releases it before it waits for the
    /// process to exit: a slow app or a prompt must not hold up every input and window call for the whole wait. The window tools
    /// (bounds, state, focus, snap, move to a monitor, z-order and the three window batches) resolve their window
    /// selector (roadmap M2.4) before they take the gate, and act inside it. element_action (roadmap M3.6) searches for its
    /// element before it takes the gate, acts on it inside the gate, and releases the gate before its mouse or keyboard path,
    /// which takes it again through the input service, and before it waits for the state after the action. respond_to_dialog
    /// and file_dialog (roadmap M3.5, M3.14) read the dialog without the gate and act only through element_action's service,
    /// which takes it for each action; they wait for the dialog to close without it. close_debug_browser (roadmap M5.4) asks
    /// the user first like kill_process, then takes the gate for its Browser.close and again for the end of a browser that
    /// did not exit, and waits for the browser to exit without it. activate_tab and browser_navigate (roadmap M5.5) find the
    /// browser and the tab without it, take it to send the command that shows the tab (the browser may bring its window to
    /// the front) or navigates it, and release it once the browser answers, at most a second later: a page's load wait never
    /// holds it. They refuse at once while input is paused. The DOM bridge's actions (roadmap M5.8: browser_click,
    /// browser_type, browser_evaluate) find the tab and the element without it, and take it for the commands that change
    /// the page: a click's scroll into view, hit test and mouse events, a typing's focus, selection, text and Enter, a
    /// script (a dialog the action opens is answered while it holds it). browser_handle_dialog (roadmap M5.8, dialogs) reads
    /// the dialog without it and sets a prompt's text and presses the button through element_action's service, which takes
    /// it. Each command is held until the browser answers, at most a second or two;
    /// a script's or a page's work after that never holds it. A pause while a click or a typing holds it stops the call
    /// between its commands. browser_screenshot takes it only to bring a tab that is not shown to the front. The actions
    /// refuse at once while input is paused; browser_screenshot only when it would bring a tab to the front. run_steps
    /// (roadmap M6.2) checks its steps and resolves its window without the gate, then holds it for the whole run with
    /// <see cref="HoldAsync"/> (at most its maxDurationMs, 60 seconds): its own steps pass the gate it holds, through this
    /// filter and through the input services, and every other call waits, or gets InputBusy after
    /// <see cref="GateWaitTimeout"/>. launch_application (roadmap M6.5) asks the user first when its runAsAdmin needs a
    /// confirmation Windows itself would not ask for, then holds the gate for the launch, its window wait and its placement,
    /// as this filter did before. apply_layout (roadmap M6.8) takes no gate for a dry run, which only reads; otherwise it asks
    /// the user first when an agent-written layout would start programs, then holds the gate for the arrangement and its
    /// launches, as this filter did before.
    /// </summary>
    public static IReadOnlySet<string> SelfGatedTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "kill_process", "close_window", "close_windows_batch", "close_application", "close_debug_browser",
        "set_window_bounds", "set_window_state", "focus_window",
        "snap_window", "move_window_to_monitor", "set_windows_bounds_batch", "snap_windows_batch", "set_windows_state_batch",
        "set_window_zorder",
        "element_action", "respond_to_dialog", "file_dialog",
        // Find the tab without the gate and take it only to send the command that changes the browser, until the browser
        // answers (at most a second): not for the page's load (roadmap M5.5)
        "activate_tab", "browser_navigate",
        // Find the tab and the element without the gate; take it only for the commands that change the page, or that bring a
        // tab to the front for a capture (roadmap M5.8)
        "browser_click", "browser_type", "browser_handle_dialog", "browser_evaluate", "browser_screenshot",
        // Holds the gate for its whole run through a lease its own steps pass (roadmap M6.2)
        "run_steps",
        // Asks the user before an elevation Windows would not ask about (runAsAdmin, roadmap M6.5), then holds the gate for the
        // launch, its window wait and its placement, as the filter did
        "launch_application",
        // Plans without the gate (a dry run takes none), asks the user before an agent-written layout starts programs, then
        // holds the gate for the arrangement and its launches (roadmap M6.8)
        "apply_layout",
        // A dry run takes no gate; a restore holds it while it opens folders and pages and arranges (roadmap M7.5)
        "restore_workspace"
    };

    /// <summary>
    /// Tools that only read windows, processes, monitors, the screen, UI Automation, a browser page or the clipboard, or
    /// that write only layout files. They run without the gate (get_clipboard may ask the user, which must never hold it).
    /// capture_window and capture_with_elements with restoreIfMinimized take it themselves (<see cref="EnterAsync"/>) while
    /// they restore a minimized window.
    /// </summary>
    public static IReadOnlySet<string> ReadOnlyTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "find_windows", "get_foreground_window", "get_monitors",
        "list_processes", "list_installed_apps", "list_layouts", "get_layout", "save_layout", "delete_layout", "save_workspace", "get_browser_url",
        "get_browser_content", "take_screenshot", "capture_region",
        "capture_window", "capture_with_elements", "get_ui_elements", "find_ui_elements", "get_focused_element", "get_element_at_point",
        "get_active_popups",
        "get_control_status", "get_server_info", "get_owned_windows", "get_clipboard", "get_window_info",
        // Wait up to a minute; holding the gate meanwhile would stall every window and input call (roadmap M3.7, M3.8)
        "wait_for_window", "wait_for_element",
        // The window event journal: reads what it recorded (roadmap M7.2)
        "get_window_events",
        // Read an element's text and a table's cells (roadmap M3.9)
        "read_element_text", "read_table",
        // One observation of the desktop: windows, focus, popups, elements and an optional capture (roadmap M3.12)
        "get_desktop_state",
        // A debug browser's tabs, and a wait on a page of up to a minute (roadmap M5.5)
        "list_tabs", "wait_for_browser",
        // An element's place on the screen: a DOM query and the window's UI Automation tree, read only (roadmap M5.8)
        "browser_element_rect",
        // Read the screen's text with Windows OCR; a minimized window is refused, never restored (roadmap M6.3)
        "read_screen_text", "find_text_on_screen",
        // A wait of up to a minute on a window's, region's or monitor's pixels; returns rectangles only (roadmap M6.4)
        "wait_for_visual"
    };

    /// <summary>
    /// The tools that control the input session (roadmap M1.10). They run without the gate, so resuming and
    /// releasing stuck keys are never queued behind a long input call, and resume_input's wait for the user does
    /// not hold up other tools. Their own input (key-ups) is serialized by the input session's lock.
    /// </summary>
    public static IReadOnlySet<string> ControlTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "resume_input", "release_all_input"
    };

    /// <summary>
    /// How long a tool waits for the input gate before it gives up with InputBusy and does nothing: as long as an
    /// input call waits (<see cref="InputGate.InputWaitTimeout"/>).
    /// </summary>
    public static TimeSpan GateWaitTimeout => InputGate.InputWaitTimeout;

    /// <summary>Registers <see cref="Filter"/> in the call-tool pipeline of the MCP server.</summary>
    public static IMcpServerBuilder AddInputGateSerialization(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(Filter));
    }

    /// <summary>True when the tool runs inside the gate through this filter.</summary>
    public static bool RunsInsideTheGate(string? toolName) =>
        toolName == null || (!InputTools.Contains(toolName) && !ReadOnlyTools.Contains(toolName) && !ControlTools.Contains(toolName) &&
                             !SelfGatedTools.Contains(toolName));

    /// <summary>
    /// For the <see cref="SelfGatedTools"/>: refuses with a PausedByUser tool error while the user has paused input,
    /// before the tool asks the user anything.
    /// </summary>
    /// <exception cref="ToolErrorException">Input is paused.</exception>
    public static void ThrowIfPaused(IInputSession? session)
    {
        if (session?.IsPaused == true)
        {
            throw new ToolErrorException(nameof(WindowErrorCode.PausedByUser), PausedMessage(session));
        }
    }

    /// <summary>
    /// For the <see cref="SelfGatedTools"/>, after any question to the user: waits for the input gate as this filter
    /// does and returns it, to be disposed when the tool is done. A pause before or during the wait, or just as the gate
    /// is taken, is a PausedByUser tool error; a gate that is not free within <paramref name="timeout"/> (default
    /// <see cref="GateWaitTimeout"/>) is InputBusy. Either way nothing has been done.
    /// </summary>
    /// <exception cref="ToolErrorException">Input is paused, or the gate is busy.</exception>
    public static Task<IDisposable> EnterAsync(IInputSession? session, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        WaitForGateAsync(session, cancellationToken, timeout, InputGate.TryEnterAsync);

    /// <summary>
    /// For run_steps (roadmap M6.2): waits for the input gate as <see cref="EnterAsync"/> does, with the same refusals, and
    /// holds it for the whole run through a <see cref="InputGate.Lease"/>, which the caller binds to its own flow
    /// (<see cref="InputGate.Lease.Bind"/>) so that the run's steps pass the gate it holds. Disposing the lease releases it.
    /// </summary>
    /// <exception cref="ToolErrorException">Input is paused, or the gate is busy.</exception>
    public static Task<InputGate.Lease> HoldAsync(IInputSession? session, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        WaitForGateAsync(session, cancellationToken, timeout, InputGate.TryHoldAsync);

    private static async Task<TGate> WaitForGateAsync<TGate>(
        IInputSession? session, CancellationToken cancellationToken, TimeSpan? timeout, Func<TimeSpan, CancellationToken, Task<TGate?>> take)
        where TGate : class, IDisposable
    {
        ThrowIfPaused(session);
        var wait = timeout ?? GateWaitTimeout;

        TGate? gate;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session?.PauseToken ?? CancellationToken.None))
        {
            try
            {
                gate = await take(wait, waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ToolErrorException(nameof(WindowErrorCode.PausedByUser), PausedMessage(session!));
            }
        }

        if (gate == null)
        {
            throw new ToolErrorException(nameof(InputErrorCode.InputBusy), BusyMessage(wait));
        }

        if (session?.IsPaused == true)
        {
            gate.Dispose();
            throw new ToolErrorException(nameof(WindowErrorCode.PausedByUser), PausedMessage(session));
        }

        return gate;
    }

    /// <summary>
    /// Call-tool filter: a tool that <see cref="RunsInsideTheGate"/> waits for the input gate and holds it
    /// until it returns, or returns PausedByUser while the user has paused input; every other tool, and a name
    /// the server does not serve, passes straight through.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        CreateFilter(GateWaitTimeout)(next);

    /// <summary>
    /// Creates the filter with a different wait for the gate. <see cref="Filter"/> is this filter with
    /// <see cref="GateWaitTimeout"/>; tests pass a short one to check InputBusy without waiting.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CreateFilter(TimeSpan gateWaitTimeout) =>
        next => async (request, cancellationToken) =>
        {
            var name = request.Params?.Name;
            if (!RunsInsideTheGate(name) || IsUnknownTool(request, name))
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            var session = request.Services?.GetService<IInputSession>();
            if (session?.IsPaused == true)
            {
                return PausedResult(name, session);
            }

            IDisposable? gate;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken, session?.PauseToken ?? CancellationToken.None))
            {
                try
                {
                    gate = await InputGate.TryEnterAsync(gateWaitTimeout, wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The user paused while this call waited for the gate
                    return PausedResult(name, session!);
                }
            }

            if (gate == null)
            {
                return BusyResult(name, gateWaitTimeout);
            }

            using (gate)
            {
                // The user may have paused just as this call got the gate
                return session?.IsPaused == true
                    ? PausedResult(name, session)
                    : await next(request, cancellationToken).ConfigureAwait(false);
            }
        };

    /// <summary>
    /// True when the server serves no tool named <paramref name="name"/>: the SDK answers the call with a protocol
    /// error ("Unknown tool"). False when the filter cannot see the server's tools, so such a call fails safe.
    /// </summary>
    private static bool IsUnknownTool(RequestContext<CallToolRequestParams> request, string? name) =>
        request.Server?.ServerOptions?.ToolCollection is { } tools &&
        (name == null || !tools.TryGetPrimitive(name, out _));

    private static CallToolResult BusyResult(string? toolName, TimeSpan waited) =>
        ToolErrorReporting.CreateToolErrorResult(toolName ?? "(unknown)", nameof(InputErrorCode.InputBusy), BusyMessage(waited));

    private static CallToolResult PausedResult(string? toolName, IInputSession session) =>
        ToolErrorReporting.CreateToolErrorResult(toolName ?? "(unknown)", nameof(WindowErrorCode.PausedByUser), PausedMessage(session));

    private static string BusyMessage(TimeSpan waited) =>
        $"Nothing was done: another input or window call held the input gate for {waited.TotalSeconds:0.#} s, so this call " +
        "did not start. Retry when your other calls have finished.";

    private static string PausedMessage(IInputSession session)
    {
        var pausedBy = session.PauseState?.PausedBy;
        return $"Nothing was done: input is paused, {InputControlSources.PauseCause(pausedBy, session.PauseChord)}. While paused, " +
               "tools that change windows or start or stop processes do nothing, like the input tools; tools that only read still " +
               $"work. Stop and ask the user how to continue. Do not try to resume input yourself: {InputControlSources.WhoResumes(pausedBy)}.";
    }
}
