using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Call-tool filter that runs every tool that can change which window is in front, or which window is at a
/// point, inside the <see cref="InputGate"/>. The MCP SDK runs tool calls concurrently, so without it an agent
/// could call focus_window (or close the window in front, or launch an app) in parallel with type_text and
/// bring its own terminal to the front between the input guard's last check and the input. The input tools
/// take the gate themselves, around their checks and batches; read-only tools run without it.
/// </summary>
/// <remarks>
/// A tool that is in none of <see cref="InputTools"/>, <see cref="ReadOnlyTools"/> and <see cref="ControlTools"/>
/// runs inside the gate, so a new tool is serialized until it is reviewed onto another list. A contract test
/// checks that every tool the server serves is in exactly one of the four lists.
/// </remarks>
public static class InputGateToolFilter
{
    /// <summary>The input tools. They hold the gate themselves (InputSimulationService), not through this filter.</summary>
    public static IReadOnlySet<string> InputTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "click", "right_click", "double_click", "mouse_move", "type_text", "send_keys"
    };

    /// <summary>
    /// Tools that can change which window is in front or at a point: they activate, move, resize, restore,
    /// minimize or close windows, or start or stop processes (a new window can take the foreground, and closing
    /// the window in front activates another). They run inside the gate.
    /// </summary>
    public static IReadOnlySet<string> WindowChangingTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "move_window", "resize_window", "set_window_bounds", "set_window_state", "focus_window", "close_window",
        "close_windows_batch", "snap_window", "move_window_to_monitor", "set_windows_bounds_batch",
        "snap_windows_batch", "set_windows_state_batch", "apply_layout", "launch_application",
        "launch_applications_batch", "kill_process", "launch_browser_with_debug"
    };

    /// <summary>
    /// Tools that only read windows, processes, monitors, the screen, UI Automation or a browser page, or
    /// that write only layout files. They run without the gate.
    /// </summary>
    public static IReadOnlySet<string> ReadOnlyTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "find_windows", "get_all_windows", "get_foreground_window", "get_monitors", "get_primary_monitor",
        "list_processes", "list_layouts", "get_layout", "save_layout", "delete_layout", "get_browser_url",
        "get_browser_content", "get_active_browser_url", "list_screens", "take_screenshot", "capture_region",
        "capture_window", "capture_with_elements", "get_ui_elements", "get_focused_element", "get_element_at_point",
        "get_control_status"
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

    /// <summary>Registers <see cref="Filter"/> in the call-tool pipeline of the MCP server.</summary>
    public static IMcpServerBuilder AddInputGateSerialization(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCallToolFilter(Filter);
    }

    /// <summary>True when the tool runs inside the gate through this filter.</summary>
    public static bool RunsInsideTheGate(string? toolName) =>
        toolName == null || (!InputTools.Contains(toolName) && !ReadOnlyTools.Contains(toolName) && !ControlTools.Contains(toolName));

    /// <summary>
    /// Call-tool filter: a tool that <see cref="RunsInsideTheGate"/> waits for the input gate and holds it
    /// until it returns; every other tool passes straight through.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            if (!RunsInsideTheGate(request.Params?.Name))
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            using (await InputGate.EnterAsync(cancellationToken).ConfigureAwait(false))
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }
        };
}
