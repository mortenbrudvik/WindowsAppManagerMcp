using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Resources;

/// <summary>
/// The server's read-only resources (roadmap M6.9): the JSON that get_monitors, find_windows with no filter and get_layout
/// return, through the same mappers, so a change to a tool's result changes its resource too. Clients show them to the user
/// (Claude Code as @-mentions); an agent reads the same data with the tools. No resource keeps pixels (owner decisions D8
/// and D9): a screenshot is only ever in the result of the call that took it.
/// </summary>
[McpServerResourceType]
public sealed class DesktopResources
{
    public const string MonitorsUri = ResourcesAndPrompts.Scheme + "://monitors";
    public const string WindowsUri = ResourcesAndPrompts.Scheme + "://windows";
    public const string LayoutUriTemplate = ResourcesAndPrompts.Scheme + "://layouts/{name}";

    private const string JsonMimeType = "application/json";

    private readonly IMonitorService _monitors;
    private readonly IScreenCaptureWrapper _screen;
    private readonly IWindowService _windows;
    private readonly ILayoutService _layouts;

    public DesktopResources(IMonitorService monitors, IScreenCaptureWrapper screen, IWindowService windows, ILayoutService layouts)
    {
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
    }

    [McpServerResource(UriTemplate = MonitorsUri, Name = "monitors", Title = "Monitors", MimeType = JsonMimeType)]
    [Description("The monitors as get_monitors returns them: bounds, work areas, scale factors, the primary monitor and the virtual screen.")]
    public string Monitors() => Json(MonitorInfoTools.ReadMonitors(_monitors, _screen));

    /// <summary>
    /// find_windows with no argument. A window of an app the target policy denies is listed, as find_windows and the other
    /// window lists list it (its title, process and bounds); the policy refuses reading its content and acting on it.
    /// </summary>
    [McpServerResource(UriTemplate = WindowsUri, Name = "windows", Title = "Windows", MimeType = JsonMimeType)]
    [Description("The user-facing app windows, front to back, as find_windows with no filter returns them: handle, title, process, " +
                 "class, bounds, visible frame and state.")]
    public string Windows() => Json(_windows.FindWindows(new WindowQuery()).Select(WindowFinderTools.ToDto).ToList());

    [McpServerResource(UriTemplate = LayoutUriTemplate, Name = "layout", Title = "Layout preset", MimeType = JsonMimeType)]
    [Description("A saved layout preset as get_layout returns it: each placement's window, monitor and position. A name no " +
                 "preset has is an error.")]
    public string Layout(McpServer server, string name)
    {
        ArgumentNullException.ThrowIfNull(server);

        // As get_layout: an unusable layouts folder is an error with its message, not an empty preset
        if (_layouts.GetStorageError() is { } storageError)
        {
            throw new McpException(storageError);
        }

        return _layouts.GetPreset(name) is { } preset
            ? Json(LayoutPresetTools.ToDetailDto(preset))
            : throw new McpProtocolException(LayoutPresetTools.PresetNotFoundMessage(name), MissingResourceCode(server.NegotiatedProtocolVersion));
    }

    /// <summary>
    /// The error code the SDK sends for a URI no resource matches, on the revision the session negotiated: -32002
    /// (ResourceNotFound) up to 2025-11-25, and -32602 (InvalidParams) from 2026-07-28 on. A name no preset has gets the same
    /// code, so a client tells both apart from a failed read the same way (ResourceContractTests compares them).
    /// </summary>
    internal static McpErrorCode MissingResourceCode(string? protocolVersion) =>
        string.CompareOrdinal(protocolVersion ?? string.Empty, FirstRevisionWithInvalidParams) >= 0
            ? McpErrorCode.InvalidParams
            : McpErrorCode.ResourceNotFound;

    private const string FirstRevisionWithInvalidParams = "2026-07-28";

    /// <summary>The JSON a tool returning <paramref name="value"/> would send: the server's serializer options.</summary>
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, ToolJson.SerializerOptions);
}
