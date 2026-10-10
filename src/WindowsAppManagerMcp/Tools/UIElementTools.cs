using System.ComponentModel;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

[McpServerToolType]
public class UIElementTools
{
    private readonly IUIElementService _uiElementService;

    private const int MaxTimeoutMs = 30000;
    private const int DefaultTimeoutMs = 10000;
    private const int MaxDepth = 10;
    private const int DefaultDepth = 5;

    // The filters are applied per element, and an element that fails one is dropped together with its
    // children (UIElementService.ExtractElementInfo). The descriptions must not present them as a safe
    // way to narrow the result. Shared with capture_with_elements (ScreenshotTools).
    internal const string CompleteResultsHint =
        "For complete results leave it unset and filter the returned tree yourself.";

    internal const string ControlTypesDescription =
        "Comma-separated control types to keep (e.g., 'Button,Edit'). Leave empty for all types. " +
        "Caution: this filter currently drops whole subtrees: an element is returned only if every container above it " +
        "also matches, so a Button inside a Pane is left out unless 'Pane' is listed too. " + CompleteResultsHint +
        " Common types: Button, Edit, CheckBox, ComboBox, ListItem, MenuItem, Text, Hyperlink, Image, Pane, Group.";

    internal const string InteractableOnlyCaution =
        "Caution: this filter currently drops whole subtrees: everything inside a disabled container is left out too. " +
        CompleteResultsHint;

    internal const string VisibleOnlyCaution =
        "This filter drops whole subtrees: everything inside an element reported as offscreen is left out too. " +
        "Pass false if the control may be scrolled out of view.";

    public UIElementTools(IUIElementService uiElementService)
    {
        _uiElementService = uiElementService;
    }

    [McpServerTool(Name = "get_ui_elements")]
    [Description("Get UI element tree with bounding boxes for a window. Returns control names, types, automation IDs, and coordinates for programmatic interaction planning. Use this as a lightweight alternative to screenshots when only element coordinates are needed.")]
    public UIElementResult GetUIElements(
        [Description("Window handle (as integer) from find_windows or get_all_windows")]
        long handle,
        [Description("Maximum tree depth to traverse (default: 5, max: 10). Higher values return more nested elements but take longer.")]
        int maxDepth = DefaultDepth,
        [Description(ControlTypesDescription)]
        string? controlTypes = null,
        [Description("Only return enabled elements (default: false). " + InteractableOnlyCaution)]
        bool interactableOnly = false,
        [Description("Only return elements that are not offscreen (default: true). " + VisibleOnlyCaution)]
        bool visibleOnly = true,
        [Description("Minimum element width in pixels (optional). Caution: this filter currently drops whole subtrees: everything inside a narrower container is left out too, and layout containers often report small or empty bounds. " + CompleteResultsHint)]
        int? minWidth = null,
        [Description("Minimum element height in pixels (optional). Caution: this filter currently drops whole subtrees: everything inside a shorter container is left out too, and layout containers often report small or empty bounds. " + CompleteResultsHint)]
        int? minHeight = null,
        [Description("Timeout in milliseconds (default: 10000, max: 30000)")]
        int timeoutMs = DefaultTimeoutMs)
    {
        // Clamp timeout to valid range
        timeoutMs = Math.Clamp(timeoutMs, 1000, MaxTimeoutMs);

        // Clamp max depth
        maxDepth = Math.Clamp(maxDepth, 1, MaxDepth);

        // Parse control types
        string[]? controlTypeArray = null;
        if (!string.IsNullOrWhiteSpace(controlTypes))
        {
            controlTypeArray = controlTypes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();

            if (controlTypeArray.Length == 0)
            {
                controlTypeArray = null;
            }
        }

        // Create filter
        var filter = new UIElementFilter(
            ControlTypes: controlTypeArray,
            InteractableOnly: interactableOnly,
            VisibleOnly: visibleOnly,
            MinWidth: minWidth,
            MinHeight: minHeight);

        // Create cancellation token with timeout
        using var cts = new CancellationTokenSource(timeoutMs);

        return _uiElementService.GetUIElements(
            (nint)handle,
            maxDepth,
            filter,
            cts.Token);
    }
}
