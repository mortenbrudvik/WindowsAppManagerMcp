using System.Text;
using System.Windows.Automation;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Wrapper around Windows UI Automation API for browser reading operations.
/// Provides browser-specific strategies for finding URL bars and extracting content.
/// </summary>
public class UIAutomationWrapper : IUIAutomationWrapper
{
    /// <inheritdoc />
    public bool IsAvailable()
    {
        try
        {
            // Try to access the root element to verify UI Automation is working
            var root = AutomationElement.RootElement;
            return root != null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryGetElementFromHandle(nint hwnd, out object? element)
    {
        element = null;
        try
        {
            element = AutomationElement.FromHandle(hwnd);
            return element != null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string? GetWindowTitle(nint hwnd)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            return element?.Current.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string? GetBrowserUrl(nint browserHandle, string browserType)
    {
        try
        {
            var browserElement = AutomationElement.FromHandle(browserHandle);
            if (browserElement == null)
                return null;

            return browserType.ToLowerInvariant() switch
            {
                "firefox" => GetFirefoxUrl(browserElement),
                "chrome" or "edge" or "brave" or "vivaldi" or "opera" or "chromium" => GetChromiumUrl(browserElement),
                _ => GetChromiumUrl(browserElement) ?? GetFirefoxUrl(browserElement) // Try both strategies
            };
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string? GetBrowserContent(nint browserHandle, string browserType, int? maxLength = null)
    {
        try
        {
            var browserElement = AutomationElement.FromHandle(browserHandle);
            if (browserElement == null)
                return null;

            // Try to find the document/content area
            var content = ExtractDocumentContent(browserElement, browserType);

            if (content != null && maxLength.HasValue && content.Length > maxLength.Value)
            {
                content = content[..maxLength.Value];
            }

            return content;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets URL from Chromium-based browsers (Chrome, Edge, Brave, Vivaldi, Opera).
    /// These browsers use an Edit control with Name="Address and search bar" or similar.
    /// </summary>
    private static string? GetChromiumUrl(AutomationElement browserElement)
    {
        // Strategy 1: Find by Name pattern (most reliable for Chrome/Edge)
        var urlBar = FindElementByNameContains(browserElement, "Address and search bar");
        if (urlBar == null)
        {
            // Strategy 2: Try "Address bar" (Edge variation)
            urlBar = FindElementByNameContains(browserElement, "Address bar");
        }
        if (urlBar == null)
        {
            // Strategy 3: Find Edit control in toolbar area
            urlBar = FindEditControlInToolbar(browserElement);
        }

        return GetElementValue(urlBar);
    }

    /// <summary>
    /// Gets URL from Firefox browser.
    /// Firefox uses an element with AutomationId="urlbar-input".
    /// </summary>
    private static string? GetFirefoxUrl(AutomationElement browserElement)
    {
        // Strategy 1: Find by AutomationId (Firefox specific)
        var urlBar = FindElementByAutomationId(browserElement, "urlbar-input");
        if (urlBar == null)
        {
            // Strategy 2: Try urlbar (parent element)
            urlBar = FindElementByAutomationId(browserElement, "urlbar");
        }
        if (urlBar == null)
        {
            // Strategy 3: Find by Name pattern
            urlBar = FindElementByNameContains(browserElement, "Search or enter address");
        }

        return GetElementValue(urlBar);
    }

    /// <summary>
    /// Finds an element by partial name match (case-insensitive).
    /// </summary>
    private static AutomationElement? FindElementByNameContains(AutomationElement root, string nameContains)
    {
        try
        {
            // Use TreeWalker for more flexible searching
            var walker = TreeWalker.ControlViewWalker;
            return FindInTreeByNameContains(root, walker, nameContains, maxDepth: 10);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively searches the automation tree for an element with matching name.
    /// </summary>
    private static AutomationElement? FindInTreeByNameContains(
        AutomationElement element,
        TreeWalker walker,
        string nameContains,
        int maxDepth,
        int currentDepth = 0)
    {
        if (currentDepth > maxDepth)
            return null;

        try
        {
            var name = element.Current.Name;
            if (!string.IsNullOrEmpty(name) &&
                name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return element;
            }

            // Search children
            var child = walker.GetFirstChild(element);
            while (child != null)
            {
                var found = FindInTreeByNameContains(child, walker, nameContains, maxDepth, currentDepth + 1);
                if (found != null)
                    return found;

                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
            // Element may have become invalid
        }

        return null;
    }

    /// <summary>
    /// Finds an element by AutomationId.
    /// </summary>
    private static AutomationElement? FindElementByAutomationId(AutomationElement root, string automationId)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
            return root.FindFirst(TreeScope.Descendants, condition);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds an Edit control that looks like a URL bar in the toolbar area.
    /// Used as a fallback strategy.
    /// </summary>
    private static AutomationElement? FindEditControlInToolbar(AutomationElement browserElement)
    {
        try
        {
            // Find all Edit controls
            var editCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);
            var edits = browserElement.FindAll(TreeScope.Descendants, editCondition);

            foreach (AutomationElement edit in edits)
            {
                try
                {
                    var rect = edit.Current.BoundingRectangle;
                    // URL bars are typically wide and near the top of the window
                    // Skip very small or very large edit boxes
                    if (rect.Width > 200 && rect.Width < 2000 && rect.Height > 15 && rect.Height < 60)
                    {
                        // Check if it looks like a URL (starts with http, has a dot, etc.)
                        var value = GetElementValue(edit);
                        if (value != null && (value.Contains("://") || value.Contains(".")))
                        {
                            return edit;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }

    /// <summary>
    /// Gets the value from an automation element using ValuePattern or TextPattern.
    /// </summary>
    private static string? GetElementValue(AutomationElement? element)
    {
        if (element == null)
            return null;

        try
        {
            // Try ValuePattern first (most common for edit controls)
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) &&
                valuePatternObj is ValuePattern valuePattern)
            {
                return valuePattern.Current.Value;
            }

            // Try TextPattern as fallback
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj) &&
                textPatternObj is TextPattern textPattern)
            {
                return textPattern.DocumentRange.GetText(-1);
            }

            // Last resort: try the Name property (sometimes contains the value)
            return element.Current.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts text content from the browser's document/content area.
    /// </summary>
    private static string? ExtractDocumentContent(AutomationElement browserElement, string browserType)
    {
        try
        {
            // Find the document element (the web page content area)
            var documentElement = FindDocumentElement(browserElement, browserType);
            if (documentElement == null)
                return null;

            // Try to extract text using TextPattern
            if (documentElement.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj) &&
                textPatternObj is TextPattern textPattern)
            {
                return textPattern.DocumentRange.GetText(-1);
            }

            // Fallback: Walk the tree and collect text
            return CollectTextFromTree(documentElement);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the document/content element in a browser window.
    /// </summary>
    private static AutomationElement? FindDocumentElement(AutomationElement browserElement, string browserType)
    {
        try
        {
            // Try to find Document control type
            var documentCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);
            var document = browserElement.FindFirst(TreeScope.Descendants, documentCondition);
            if (document != null)
                return document;

            // Try Pane control type (some browsers use this)
            var paneCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane);
            var panes = browserElement.FindAll(TreeScope.Descendants, paneCondition);

            // Find the largest pane (likely the content area)
            AutomationElement? largestPane = null;
            double largestArea = 0;

            foreach (AutomationElement pane in panes)
            {
                try
                {
                    var rect = pane.Current.BoundingRectangle;
                    var area = rect.Width * rect.Height;
                    if (area > largestArea && rect.Width > 200 && rect.Height > 200)
                    {
                        largestArea = area;
                        largestPane = pane;
                    }
                }
                catch
                {
                    continue;
                }
            }

            return largestPane;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Walks the automation tree and collects text from all elements.
    /// </summary>
    private static string? CollectTextFromTree(AutomationElement element)
    {
        var sb = new StringBuilder();
        var walker = TreeWalker.ControlViewWalker;

        CollectTextRecursive(element, walker, sb, maxDepth: 20);

        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? null : result;
    }

    /// <summary>
    /// Recursively collects text from automation elements.
    /// </summary>
    private static void CollectTextRecursive(
        AutomationElement element,
        TreeWalker walker,
        StringBuilder sb,
        int maxDepth,
        int currentDepth = 0)
    {
        if (currentDepth > maxDepth)
            return;

        try
        {
            // Get text from this element
            var name = element.Current.Name;
            var controlType = element.Current.ControlType;

            // Only collect text from certain control types
            if (controlType == ControlType.Text ||
                controlType == ControlType.Edit ||
                controlType == ControlType.Hyperlink)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    sb.AppendLine(name);
                }
            }

            // Try to get value from ValuePattern
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) &&
                valuePatternObj is ValuePattern valuePattern)
            {
                var value = valuePattern.Current.Value;
                if (!string.IsNullOrWhiteSpace(value) && value != name)
                {
                    sb.AppendLine(value);
                }
            }

            // Recurse into children
            var child = walker.GetFirstChild(element);
            while (child != null)
            {
                CollectTextRecursive(child, walker, sb, maxDepth, currentDepth + 1);
                child = walker.GetNextSibling(child);
            }
        }
        catch
        {
            // Element may have become invalid
        }
    }
}
