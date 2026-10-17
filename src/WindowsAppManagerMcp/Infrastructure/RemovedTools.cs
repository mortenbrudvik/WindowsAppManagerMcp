using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// The tools 3.0.0 removed (owner decision D1): the nine that 2.2.0 replaced and served in the opt-in deprecated toolset
/// until then. A call to one is refused with <see cref="ErrorCode"/> and the tool to call instead, so a client or prompt
/// written for 2.1 learns what changed; it is never forwarded to the replacement, whose arguments and result differ.
/// </summary>
public static class RemovedTools
{
    /// <summary>The opt-in toolset that served the nine tools until 3.0.0 removed it with them.</summary>
    public const string Toolset = "deprecated";

    /// <summary>The errorCode of a call to a removed tool.</summary>
    public const string ErrorCode = "ToolRemoved";

    /// <summary>Each removed tool and what to call instead.</summary>
    public static IReadOnlyDictionary<string, string> Replacements { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["click"] = "mouse_click",
        ["right_click"] = "mouse_click with button 'secondary'",
        ["double_click"] = "mouse_click with clickCount 2",
        ["move_window"] = "set_window_bounds with x and y",
        ["resize_window"] = "set_window_bounds with width and height",
        ["get_all_windows"] = "find_windows",
        ["get_primary_monitor"] = "get_monitors (the monitor with isPrimary)",
        ["list_screens"] = "get_monitors (its virtualScreen)",
        ["get_active_browser_url"] = "get_browser_url with window {\"foreground\": true}"
    };

    /// <summary>Registers <see cref="Filter"/> in the call-tool pipeline of the MCP server.</summary>
    public static IMcpServerBuilder AddRemovedToolRefusal(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(Filter));
    }

    /// <summary>Call-tool filter: refuses a call to a removed tool with <see cref="ErrorCode"/>; every other call passes through.</summary>
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        (request, cancellationToken) =>
        {
            if (request.Params?.Name is { } tool && Replacements.TryGetValue(tool, out var replacement))
            {
                throw new ToolErrorException(ErrorCode,
                    $"The tool '{tool}' was removed in 3.0.0; call {replacement} instead. Nothing was done.");
            }

            return next(request, cancellationToken);
        };
}
