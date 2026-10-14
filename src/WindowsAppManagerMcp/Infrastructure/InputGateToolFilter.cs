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
/// list. A contract test checks that every tool the server serves is in exactly one of the five lists. A name the server does not
/// serve passes straight through, so the SDK reports it as a protocol error at once: a call that runs nothing
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
/// </remarks>
public static class InputGateToolFilter
{
    /// <summary>The input tools. They hold the gate themselves (InputSimulationService), not through this filter.</summary>
    public static IReadOnlySet<string> InputTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "click", "right_click", "double_click", "mouse_click", "drag", "mouse_move", "scroll", "type_text", "send_keys"
    };

    /// <summary>
    /// Tools that can change which window is in front or at a point: they arrange windows or start processes (a new window
    /// can take the foreground). They run inside the gate. set_clipboard runs inside it too: it changes no window, but it
    /// replaces the user's clipboard, so it waits for a running input call (a paste) and is refused while the user has
    /// paused input. The single-window tools and their batches take the gate themselves (<see cref="SelfGatedTools"/>).
    /// </summary>
    public static IReadOnlySet<string> WindowChangingTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "apply_layout", "launch_application", "launch_applications_batch", "launch_browser_with_debug", "set_clipboard"
    };

    /// <summary>
    /// Tools that change windows or stop processes like <see cref="WindowChangingTools"/>, but do work first that must not
    /// hold the gate, and then take it themselves with <see cref="EnterAsync"/>, which waits, refuses and reports a pause
    /// exactly as this filter does. kill_process and the close tools may ask the user to confirm (elicitation, roadmap
    /// M2.10): the user may take minutes to answer, and every input and window call would wait for them. The close tools
    /// (roadmap M2.5) also send their close messages inside the gate and release it before they wait for the windows to
    /// close, and kill_process sends its close request or termination inside it and releases it before it waits for the
    /// process to exit: a slow app or a prompt must not hold up every input and window call for the whole wait. The window tools
    /// (move, resize, bounds, state, focus, snap, move to a monitor and the three window batches) resolve their window
    /// selector (roadmap M2.4) before they take the gate, and act inside it. element_action (roadmap M3.6) searches for its
    /// element before it takes the gate, acts on it inside the gate, and releases the gate before its mouse or keyboard path,
    /// which takes it again through the input service, and before it waits for the state after the action. respond_to_dialog
    /// and file_dialog (roadmap M3.5, M3.14) read the dialog without the gate and act only through element_action's service,
    /// which takes it for each action; they wait for the dialog to close without it.
    /// </summary>
    public static IReadOnlySet<string> SelfGatedTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "kill_process", "close_window", "close_windows_batch", "close_application",
        "move_window", "resize_window", "set_window_bounds", "set_window_state", "focus_window",
        "snap_window", "move_window_to_monitor", "set_windows_bounds_batch", "snap_windows_batch", "set_windows_state_batch",
        "element_action", "respond_to_dialog", "file_dialog"
    };

    /// <summary>
    /// Tools that only read windows, processes, monitors, the screen, UI Automation, a browser page or the clipboard, or
    /// that write only layout files. They run without the gate (get_clipboard may ask the user, which must never hold it).
    /// capture_window and capture_with_elements with restoreIfMinimized take it themselves (<see cref="EnterAsync"/>) while
    /// they restore a minimized window.
    /// </summary>
    public static IReadOnlySet<string> ReadOnlyTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "find_windows", "get_all_windows", "get_foreground_window", "get_monitors", "get_primary_monitor",
        "list_processes", "list_layouts", "get_layout", "save_layout", "delete_layout", "get_browser_url",
        "get_browser_content", "get_active_browser_url", "list_screens", "take_screenshot", "capture_region",
        "capture_window", "capture_with_elements", "get_ui_elements", "find_ui_elements", "get_focused_element", "get_element_at_point",
        "get_active_popups",
        "get_control_status", "get_server_info", "get_owned_windows", "get_clipboard", "get_window_info",
        // Wait up to a minute; holding the gate meanwhile would stall every window and input call (roadmap M3.7, M3.8)
        "wait_for_window", "wait_for_element",
        // Read an element's text and a table's cells (roadmap M3.9)
        "read_element_text", "read_table",
        // One observation of the desktop: windows, focus, popups, elements and an optional capture (roadmap M3.12)
        "get_desktop_state"
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
    public static async Task<IDisposable> EnterAsync(IInputSession? session, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ThrowIfPaused(session);
        var wait = timeout ?? GateWaitTimeout;

        IDisposable? gate;
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session?.PauseToken ?? CancellationToken.None))
        {
            try
            {
                gate = await InputGate.TryEnterAsync(wait, waiting.Token).ConfigureAwait(false);
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

    private static string PausedMessage(IInputSession session) =>
        $"Nothing was done: input is paused, the user pressed the pause hotkey{(session.PauseChord is { } chord ? $" ({chord})" : string.Empty)}. " +
        "While paused, tools that change windows or start or stop processes do nothing, like the input tools; tools that only read " +
        "still work. Stop and ask the user how to continue. Do not try to resume input yourself: only the user can, with the hotkey " +
        "or by confirming resume_input.";
}
