using System.Diagnostics;
using System.Windows.Automation;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for retrieving UI element trees from windows using Windows UI Automation.
/// </summary>
public class UIElementService : IUIElementService
{
    private const int MaxAllowedDepth = 10;
    private const int DefaultMaxDepth = 5;

    // Map ControlType to readable string names
    private static readonly Dictionary<ControlType, string> ControlTypeNames = new()
    {
        { ControlType.Button, "Button" },
        { ControlType.Calendar, "Calendar" },
        { ControlType.CheckBox, "CheckBox" },
        { ControlType.ComboBox, "ComboBox" },
        { ControlType.Custom, "Custom" },
        { ControlType.DataGrid, "DataGrid" },
        { ControlType.DataItem, "DataItem" },
        { ControlType.Document, "Document" },
        { ControlType.Edit, "Edit" },
        { ControlType.Group, "Group" },
        { ControlType.Header, "Header" },
        { ControlType.HeaderItem, "HeaderItem" },
        { ControlType.Hyperlink, "Hyperlink" },
        { ControlType.Image, "Image" },
        { ControlType.List, "List" },
        { ControlType.ListItem, "ListItem" },
        { ControlType.Menu, "Menu" },
        { ControlType.MenuBar, "MenuBar" },
        { ControlType.MenuItem, "MenuItem" },
        { ControlType.Pane, "Pane" },
        { ControlType.ProgressBar, "ProgressBar" },
        { ControlType.RadioButton, "RadioButton" },
        { ControlType.ScrollBar, "ScrollBar" },
        { ControlType.Separator, "Separator" },
        { ControlType.Slider, "Slider" },
        { ControlType.Spinner, "Spinner" },
        { ControlType.SplitButton, "SplitButton" },
        { ControlType.StatusBar, "StatusBar" },
        { ControlType.Tab, "Tab" },
        { ControlType.TabItem, "TabItem" },
        { ControlType.Table, "Table" },
        { ControlType.Text, "Text" },
        { ControlType.Thumb, "Thumb" },
        { ControlType.TitleBar, "TitleBar" },
        { ControlType.ToolBar, "ToolBar" },
        { ControlType.ToolTip, "ToolTip" },
        { ControlType.Tree, "Tree" },
        { ControlType.TreeItem, "TreeItem" },
        { ControlType.Window, "Window" }
    };

    /// <inheritdoc />
    public bool IsAvailable()
    {
        try
        {
            var root = AutomationElement.RootElement;
            return root != null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public UIElementResult GetUIElements(
        nint windowHandle,
        int maxDepth = DefaultMaxDepth,
        UIElementFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // Validate handle
        if (windowHandle == nint.Zero)
        {
            return CreateErrorResult(
                windowHandle,
                stopwatch.Elapsed.TotalMilliseconds,
                "Invalid window handle (zero)",
                UIElementErrorCode.InvalidHandle);
        }

        // Clamp maxDepth to valid range
        maxDepth = Math.Clamp(maxDepth, 1, MaxAllowedDepth);

        try
        {
            // Check UI Automation availability
            if (!IsAvailable())
            {
                return CreateErrorResult(
                    windowHandle,
                    stopwatch.Elapsed.TotalMilliseconds,
                    "UI Automation is not available on this system",
                    UIElementErrorCode.AutomationUnavailable);
            }

            // Get the automation element for the window
            AutomationElement windowElement;
            try
            {
                windowElement = AutomationElement.FromHandle(windowHandle);
                if (windowElement == null)
                {
                    return CreateErrorResult(
                        windowHandle,
                        stopwatch.Elapsed.TotalMilliseconds,
                        "Window not found or handle is invalid",
                        UIElementErrorCode.WindowNotFound);
                }
            }
            catch (ElementNotAvailableException)
            {
                return CreateErrorResult(
                    windowHandle,
                    stopwatch.Elapsed.TotalMilliseconds,
                    "Window is no longer available",
                    UIElementErrorCode.WindowNotFound);
            }

            // Get window title
            string? windowTitle = null;
            try
            {
                windowTitle = windowElement.Current.Name;
            }
            catch
            {
                // Ignore - window title is optional
            }

            // Parse filter control types
            HashSet<string>? filterControlTypes = null;
            if (filter?.ControlTypes != null && filter.ControlTypes.Length > 0)
            {
                filterControlTypes = new HashSet<string>(
                    filter.ControlTypes,
                    StringComparer.OrdinalIgnoreCase);
            }

            // Traverse the element tree
            var elementCount = 0;
            var walker = TreeWalker.ControlViewWalker;
            var elements = TraverseElements(
                windowElement,
                walker,
                maxDepth,
                currentDepth: 0,
                filter,
                filterControlTypes,
                ref elementCount,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return CreateErrorResult(
                    windowHandle,
                    stopwatch.Elapsed.TotalMilliseconds,
                    "Operation was cancelled due to timeout",
                    UIElementErrorCode.OperationTimedOut,
                    windowTitle);
            }

            stopwatch.Stop();

            return new UIElementResult(
                Success: true,
                WindowHandle: (long)windowHandle,
                WindowTitle: windowTitle,
                Elements: elements,
                TotalElementCount: elementCount,
                Depth: maxDepth,
                ElapsedMilliseconds: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));
        }
        catch (ElementNotAvailableException ex)
        {
            return CreateErrorResult(
                windowHandle,
                stopwatch.Elapsed.TotalMilliseconds,
                $"Element is no longer available: {ex.Message}",
                UIElementErrorCode.ElementAccessFailed);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(
                windowHandle,
                stopwatch.Elapsed.TotalMilliseconds,
                $"Error accessing UI elements: {ex.Message}",
                UIElementErrorCode.OperationException);
        }
    }

    private List<UIElementInfo> TraverseElements(
        AutomationElement element,
        TreeWalker walker,
        int maxDepth,
        int currentDepth,
        UIElementFilter? filter,
        HashSet<string>? filterControlTypes,
        ref int elementCount,
        CancellationToken cancellationToken)
    {
        var elements = new List<UIElementInfo>();

        if (cancellationToken.IsCancellationRequested || currentDepth > maxDepth)
        {
            return elements;
        }

        try
        {
            var child = walker.GetFirstChild(element);
            while (child != null && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var elementInfo = ExtractElementInfo(
                        child,
                        walker,
                        maxDepth,
                        currentDepth,
                        filter,
                        filterControlTypes,
                        ref elementCount,
                        cancellationToken);

                    if (elementInfo != null)
                    {
                        elements.Add(elementInfo);
                    }

                    child = walker.GetNextSibling(child);
                }
                catch (ElementNotAvailableException)
                {
                    // Element became unavailable, skip to next sibling
                    try
                    {
                        child = walker.GetNextSibling(child);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }
        catch (ElementNotAvailableException)
        {
            // Parent element became unavailable
        }

        return elements;
    }

    private UIElementInfo? ExtractElementInfo(
        AutomationElement element,
        TreeWalker walker,
        int maxDepth,
        int currentDepth,
        UIElementFilter? filter,
        HashSet<string>? filterControlTypes,
        ref int elementCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = element.Current;

            // Get control type string
            var controlTypeName = GetControlTypeName(current.ControlType);

            // Apply control type filter
            if (filterControlTypes != null && !filterControlTypes.Contains(controlTypeName))
            {
                // Still traverse children to find matching descendants
                if (currentDepth < maxDepth)
                {
                    var childElements = TraverseElements(
                        element, walker, maxDepth, currentDepth + 1,
                        filter, filterControlTypes, ref elementCount, cancellationToken);

                    // Return children as top-level elements if parent was filtered
                    foreach (var child in childElements)
                    {
                        elementCount++; // Count will be incremented in recursive call
                    }
                }
                return null;
            }

            // Get bounds
            var rect = current.BoundingRectangle;
            var bounds = new BoundsDto(
                (int)rect.X,
                (int)rect.Y,
                (int)rect.Width,
                (int)rect.Height);

            var isEnabled = current.IsEnabled;
            var isOffscreen = current.IsOffscreen;

            // Apply interactable filter
            if (filter?.InteractableOnly == true && !isEnabled)
            {
                return null;
            }

            // Apply visible filter
            if (filter?.VisibleOnly == true && isOffscreen)
            {
                return null;
            }

            // Apply size filters
            if (filter?.MinWidth.HasValue == true && bounds.Width < filter.MinWidth.Value)
            {
                return null;
            }

            if (filter?.MinHeight.HasValue == true && bounds.Height < filter.MinHeight.Value)
            {
                return null;
            }

            elementCount++;

            // Get children if not at max depth
            IReadOnlyList<UIElementInfo>? children = null;
            if (currentDepth < maxDepth)
            {
                var childList = TraverseElements(
                    element, walker, maxDepth, currentDepth + 1,
                    filter, filterControlTypes, ref elementCount, cancellationToken);

                if (childList.Count > 0)
                {
                    children = childList;
                }
            }

            return new UIElementInfo(
                Name: string.IsNullOrEmpty(current.Name) ? null : current.Name,
                ControlType: controlTypeName,
                AutomationId: string.IsNullOrEmpty(current.AutomationId) ? null : current.AutomationId,
                Bounds: bounds,
                IsEnabled: isEnabled,
                IsOffscreen: isOffscreen,
                Children: children);
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string GetControlTypeName(ControlType controlType)
    {
        if (ControlTypeNames.TryGetValue(controlType, out var name))
        {
            return name;
        }

        // Fallback: extract name from ControlType's ProgrammaticName
        var programmaticName = controlType.ProgrammaticName;
        if (!string.IsNullOrEmpty(programmaticName))
        {
            var lastDot = programmaticName.LastIndexOf('.');
            if (lastDot >= 0)
            {
                return programmaticName[(lastDot + 1)..];
            }
            return programmaticName;
        }

        return "Unknown";
    }

    private static UIElementResult CreateErrorResult(
        nint windowHandle,
        double elapsedMs,
        string error,
        UIElementErrorCode errorCode,
        string? windowTitle = null)
    {
        return new UIElementResult(
            Success: false,
            WindowHandle: (long)windowHandle,
            WindowTitle: windowTitle,
            Elements: null,
            TotalElementCount: 0,
            Depth: 0,
            ElapsedMilliseconds: Math.Round(elapsedMs, 2),
            Error: error,
            ErrorCode: errorCode);
    }
}
