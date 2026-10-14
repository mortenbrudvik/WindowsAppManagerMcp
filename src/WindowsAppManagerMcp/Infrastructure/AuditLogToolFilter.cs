using System.Text.Json;
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
/// it never changes a call's result or fails it: the audit log swallows its own failures, and the filter swallows any
/// failure of a log that does not. The line is normally written before the result goes back (one small append); the call
/// waits for it at most <see cref="Services.AuditLog.MaxCallDelay"/>, so a slow or unreachable audit folder never holds
/// up a call for longer.
/// </remarks>
public static class AuditLogToolFilter
{
    /// <summary>
    /// The audited tools: every tool that injects input (keys, clicks, pointer moves, scrolling, key releases) or acts on a UI
    /// element through a UI Automation pattern (element_action, with its value's length only; respond_to_dialog with its
    /// button; file_dialog with its path's length only), closes a
    /// window, stops a process, starts a program, changes the windows or presets of a layout, or replaces the clipboard;
    /// and get_clipboard, the one read that can return the user's own clipboard (after their yes, or with
    /// WAM_ALLOW_USER_CLIPBOARD), recorded with its format, origin and length, never its content. Other read-only tools are
    /// never audited. The captures are left out: they are destructive only through overwrite:true of an image file, and
    /// are observation, called too often for their lines to help. A contract test checks that every other destructive
    /// tool, and every input tool, is here.
    /// </summary>
    public static IReadOnlySet<string> AuditedTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Input, and actions on UI elements through UI Automation patterns (roadmap M3.6)
        "click", "right_click", "double_click", "mouse_click", "drag", "mouse_move", "type_text", "send_keys", "scroll", "release_all_input",
        "element_action",
        // Dialogs answered and file dialogs completed through element_action (roadmap M3.5, M3.14)
        "respond_to_dialog", "file_dialog",
        // Close and kill
        "close_window", "close_windows_batch", "close_application", "kill_process",
        // Launch
        "launch_application", "launch_applications_batch", "launch_browser_with_debug",
        // Layouts: arrange (and launch) windows, replace or delete a preset
        "apply_layout", "save_layout", "delete_layout",
        // Clipboard: the write, and the read that can return the user's clipboard
        "set_clipboard", "get_clipboard"
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
            var call = audit == null ? null : Begin(audit, tool!, request);
            if (call == null)
            {
                return await next(request, cancellationToken).ConfigureAwait(false);
            }

            CallToolResult result;
            try
            {
                // The tool notes the windows it learns of while it runs (a selector's, close_application's) in this call's record
                using (AuditScope.Enter(call))
                {
                    result = await next(request, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await EndAsync(audit!, call, null, AuditOutcome.Cancelled).ConfigureAwait(false);
                throw;
            }
            catch (McpProtocolException)
            {
                await EndAsync(audit!, call, null, AuditOutcome.ProtocolError).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                // The error filter inside turns every other exception into an error result; recorded all the same
                await EndAsync(audit!, call, null, AuditOutcome.Error).ConfigureAwait(false);
                throw;
            }

            await EndAsync(audit!, call, result, result.IsError == true ? AuditOutcome.Error : AuditOutcome.Ok).ConfigureAwait(false);
            return result;
        };

    /// <summary>Starts the record, with the tool's declared input schema; null (the call goes unaudited) if the log fails.</summary>
    private static AuditCall? Begin(IAuditLog audit, string tool, RequestContext<CallToolRequestParams> request)
    {
        try
        {
            return audit.Begin(tool, request.Params?.Arguments, request.JsonRpcRequest?.Id.ToString(), InputSchemaOf(request, tool));
        }
        catch (Exception)
        {
            // An audit log that breaks its own contract never breaks the call
            return null;
        }
    }

    /// <summary>
    /// Finishes the record. The log bounds its own wait; this also bounds a log that breaks its contract, so that a call
    /// waits for its line at most <see cref="Services.AuditLog.MaxCallDelay"/>, without a thread.
    /// </summary>
    private static async Task EndAsync(IAuditLog audit, AuditCall call, CallToolResult? result, string outcome)
    {
        try
        {
            await audit.EndAsync(call, result, outcome).WaitAsync(Services.AuditLog.MaxCallDelay).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // As in Begin: the result goes back as it is
        }
    }

    /// <summary>The input schema the server declares for <paramref name="tool"/>, or null when the filter cannot see it.</summary>
    internal static JsonElement? InputSchemaOf(RequestContext<CallToolRequestParams> request, string tool) =>
        request.Server?.ServerOptions?.ToolCollection is { } tools && tools.TryGetPrimitive(tool, out var served)
            ? served.ProtocolTool.InputSchema
            : null;

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
