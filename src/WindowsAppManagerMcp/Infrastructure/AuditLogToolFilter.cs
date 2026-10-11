using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Call-tool filter that writes one audit log line (roadmap M2.17, <see cref="IAuditLog"/>) for every call of an
/// <see cref="AuditedTools">audited tool</see>: what it was aimed at, where its input went, and how it ended. It is
/// the outermost filter, so it records the result the agent got: an exception the error filter turned into an error
/// result, and a call the input gate filter refused while input was paused, are recorded with their errorCode.
/// </summary>
/// <remarks>
/// The filter does nothing when no <see cref="IAuditLog"/> is registered (tests that register their own services), and
/// it never changes a call's result or fails it: the audit log swallows its own failures.
/// </remarks>
public static class AuditLogToolFilter
{
    /// <summary>
    /// The audited tools: every tool that injects input (keys, clicks, pointer moves, scrolling, key releases), closes a
    /// window, stops a process, starts a program, changes the windows or presets of a layout, or replaces the clipboard.
    /// Read-only tools are never audited. The captures are left out: they are destructive only through overwrite:true
    /// of an image file, and are observation, called too often for their lines to help. A contract test checks that every
    /// other destructive tool, and every input tool, is here.
    /// </summary>
    public static IReadOnlySet<string> AuditedTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Input
        "click", "right_click", "double_click", "mouse_move", "type_text", "send_keys", "scroll", "release_all_input",
        // Close and kill
        "close_window", "close_windows_batch", "close_application", "kill_process",
        // Launch
        "launch_application", "launch_applications_batch", "launch_browser_with_debug",
        // Layouts: arrange (and launch) windows, replace or delete a preset
        "apply_layout", "save_layout", "delete_layout",
        // Clipboard writes
        "set_clipboard"
    };

    /// <summary>
    /// The audited tools that other M2 items add: scroll (M2.15), set_clipboard (M2.16) and close_application (M2.5).
    /// They are audited as soon as they are served.
    /// </summary>
    public static IReadOnlySet<string> ToolsOfOtherItems { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "scroll", "set_clipboard", "close_application"
    };

    /// <summary>Registers <see cref="Filter"/> in the call-tool pipeline of the MCP server.</summary>
    public static IMcpServerBuilder AddAuditLog(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(Filter));
    }

    /// <summary>
    /// Call-tool filter: starts the audit record of an audited tool's call before the rest of the pipeline runs (the
    /// targets are read then, before a close or kill makes them unreadable) and finishes it with the result, or with
    /// <c>cancelled</c> or <c>protocolError</c> when the call ends with an exception. Other tools pass straight through.
    /// </summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (request, cancellationToken) =>
        {
            var tool = request.Params?.Name;
            var audit = tool != null && AuditedTools.Contains(tool) ? AuditLogOf(request.Services) : null;
            if (audit == null)
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            var call = audit.Begin(tool!, request.Params?.Arguments, request.JsonRpcRequest?.Id.ToString());
            try
            {
                var result = await next(request, cancellationToken).ConfigureAwait(false);
                audit.End(call, result, result.IsError == true ? AuditOutcome.Error : AuditOutcome.Ok);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                audit.End(call, null, AuditOutcome.Cancelled);
                throw;
            }
            catch (McpProtocolException)
            {
                audit.End(call, null, AuditOutcome.ProtocolError);
                throw;
            }
            catch (Exception)
            {
                // The error filter inside turns every other exception into an error result; recorded all the same
                audit.End(call, null, AuditOutcome.Error);
                throw;
            }
        };

    private static IAuditLog? AuditLogOf(IServiceProvider? services)
    {
        try
        {
            return services?.GetService<IAuditLog>();
        }
        catch (Exception)
        {
            // A log that cannot even be created never breaks the call
            return null;
        }
    }
}
