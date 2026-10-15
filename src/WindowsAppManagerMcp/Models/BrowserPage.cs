using System.Text.Json;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// Which element of a page a DOM bridge tool works on (roadmap M5.8): a CSS selector, the element's visible text, or its
/// ARIA role and accessible name; exactly one way. Several matches need <see cref="Index"/> (from 0, in document order),
/// so a click never lands on the first of several look-alikes by accident.
/// </summary>
/// <param name="Selector">A CSS selector (querySelectorAll in the page's main document).</param>
/// <param name="Text">Text the element shows (innerText, or a button input's value): ignoring case and runs of
/// whitespace; the innermost elements that contain it, and only the exact matches when there are any.</param>
/// <param name="Role">An ARIA role (Accessibility.queryAXTree), alone or with <paramref name="Name"/>.</param>
/// <param name="Name">An accessible name, matched exactly as the browser computes it (with or without a role).</param>
/// <param name="Index">Which match, from 0; null when exactly one must match.</param>
public sealed record BrowserElementLocator(string? Selector, string? Text, string? Role, string? Name, int? Index = null)
{
    /// <summary>The most characters a selector, text, role or name may have.</summary>
    public const int MaxLength = 1_000;

    /// <summary>True when the element is found through the accessibility tree (a role or a name).</summary>
    public bool ByAccessibility => Role != null || Name != null;

    /// <summary>Why the locator cannot be used, or null: one of selector, text and role/name, each 1-1000 characters.</summary>
    public string? Refusal()
    {
        var ways = (Selector != null ? 1 : 0) + (Text != null ? 1 : 0) + (ByAccessibility ? 1 : 0);
        if (ways != 1)
        {
            return ways == 0
                ? "Name the element: pass selector, text, or role (with or without name)."
                : "Name the element one way only: selector, text, or role/name.";
        }

        foreach (var (value, name) in new[] { (Selector, "selector"), (Text, "text"), (Role, "role"), (Name, "name") })
        {
            if (value != null && (value.Trim().Length == 0 || value.Length > MaxLength))
                return $"{name} must be 1-{MaxLength} characters and not only whitespace.";
        }

        return Index is < 0 ? $"index must be 0 or more, but it is {Index}." : null;
    }

    /// <summary>The locator in words, for messages: "selector '#save'", "text 'Save'", "role button named 'Save'".</summary>
    public string Describe() =>
        Selector != null ? $"selector '{Selector}'"
        : Text != null ? $"text '{Text}'"
        : Role != null && Name != null ? $"role {Role} named '{Name}'"
        : Role != null ? $"role {Role}"
        : $"name '{Name}'";
}

/// <summary>An element a DOM bridge tool found (roadmap M5.8): its tag and the start of its text, from the page (untrusted).</summary>
/// <param name="Tag">The tag name, lower case, such as "button".</param>
/// <param name="Text">The start of its visible text (or a button input's value), at most 80 characters; null when it has none.</param>
public sealed record BrowserElementInfo(string Tag, string? Text = null);

/// <summary>A rectangle in CSS pixels of the page's viewport (roadmap M5.8), as DOM.getContentQuads reports it.</summary>
public sealed record CssRect(double X, double Y, double Width, double Height);

/// <summary>A point in CSS pixels of the page's viewport (roadmap M5.8): where Input.dispatchMouseEvent clicked.</summary>
public sealed record CssPoint(double X, double Y);

/// <summary>
/// browser_evaluate's result (roadmap M5.8). Value is the expression's value as JSON (returnByValue): a value that is not
/// JSON (NaN, Infinity, -0, a BigInt) as the string CDP gives it, undefined as no Value with ValueType "undefined". A value
/// whose JSON is longer than <see cref="MaxValueLength"/> characters is the start of that JSON as a string, with Truncated.
/// When the expression threw (or its promise was rejected), the result is CdpEvaluationFailed with Exception.
/// </summary>
public sealed record BrowserEvaluateResult(
    bool Success,
    string? TargetId = null,
    JsonElement? Value = null,
    string? ValueType = null,
    int? ValueLength = null,
    bool? Truncated = null,
    BrowserScriptException? Exception = null,
    int? CdpPort = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>The longest JSON text of a value returned as it is.</summary>
    public const int MaxValueLength = 20_000;

    /// <summary>A failed result.</summary>
    public static BrowserEvaluateResult Failed(string errorCode, string error, int? cdpPort = null, string? targetId = null) =>
        new(false, targetId, CdpPort: cdpPort, Error: error, ErrorCode: errorCode);
}

/// <summary>What a script threw (Runtime.evaluate's exceptionDetails): its text (cut) and where, 0-based as CDP gives it.</summary>
public sealed record BrowserScriptException(string Text, int? LineNumber = null, int? ColumnNumber = null);

/// <summary>
/// browser_handle_dialog's result (roadmap M5.8). DialogType and DialogMessage are the dialog's when the browser reported it
/// to this connection (Chrome does after Page.enable; Edge 155 does not for a dialog that was open before), else null. The
/// message is written by the page: untrusted.
/// </summary>
public sealed record BrowserDialogResult(
    bool Success,
    string? TargetId = null,
    bool? Accepted = null,
    string? DialogType = null,
    string? DialogMessage = null,
    int? CdpPort = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>A failed result.</summary>
    public static BrowserDialogResult Failed(string errorCode, string error, int? cdpPort = null, string? targetId = null) =>
        new(false, targetId, CdpPort: cdpPort, Error: error, ErrorCode: errorCode);
}

/// <summary>
/// browser_element_rect's result (roadmap M5.8). ScreenRect is the element's box in physical virtual-screen pixels (the
/// space of the input and capture tools): the page's viewport on the screen (the tab's Document through UI Automation, or
/// its render widget's window) plus the CSS rectangle times devicePixelRatio (browser zoom and display scale).
/// ClickPoint is the middle of the part of the element inside the viewport, null when none of it is (scroll first).
/// </summary>
public sealed record BrowserElementRectResult(
    bool Success,
    string? TargetId = null,
    BrowserElementInfo? Element = null,
    int? MatchCount = null,
    BoundsDto? ScreenRect = null,
    ScreenPoint? ClickPoint = null,
    CssRect? CssRect = null,
    double? DevicePixelRatio = null,
    string? Anchor = null,
    int? CdpPort = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>A failed result.</summary>
    public static BrowserElementRectResult Failed(string errorCode, string error, int? cdpPort = null, string? targetId = null) =>
        new(false, targetId, CdpPort: cdpPort, Error: error, ErrorCode: errorCode);
}

/// <summary>
/// browser_click's and browser_type's result (roadmap M5.8). Dialog is the JavaScript dialog the action opened (its type and
/// the start of its message, from the page), to answer with browser_handle_dialog. Warning says the page did not confirm the
/// input in time (busy): it was sent.
/// </summary>
public sealed record BrowserInputResult(
    bool Success,
    string? TargetId = null,
    BrowserElementInfo? Element = null,
    int? MatchCount = null,
    CssPoint? Point = null,
    int? TypedLength = null,
    bool? PressedEnter = null,
    BrowserOpenedDialog? Dialog = null,
    string? Warning = null,
    int? CdpPort = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>A failed result.</summary>
    public static BrowserInputResult Failed(string errorCode, string error, int? cdpPort = null, string? targetId = null) =>
        new(false, targetId, CdpPort: cdpPort, Error: error, ErrorCode: errorCode);
}

/// <summary>A JavaScript dialog an action opened: its type and the start of its message, written by the page (untrusted).</summary>
public sealed record BrowserOpenedDialog(string Type, string Message);

/// <summary>
/// browser_screenshot's metadata (roadmap M5.8), next to the image: its size, the page's viewport size in device pixels
/// (OriginalWidth, OriginalHeight), and Activated when the tab was not shown and was brought to the front first.
/// </summary>
public sealed record BrowserScreenshotResult(
    bool Success,
    string? TargetId = null,
    int? Width = null,
    int? Height = null,
    int? OriginalWidth = null,
    int? OriginalHeight = null,
    string? Format = null,
    bool? Activated = null,
    string? Url = null,
    string? Title = null,
    int? CdpPort = null,
    string? Error = null,
    string? ErrorCode = null)
{
    /// <summary>A failed result.</summary>
    public static BrowserScreenshotResult Failed(string errorCode, string error, int? cdpPort = null, string? targetId = null) =>
        new(false, targetId, CdpPort: cdpPort, Error: error, ErrorCode: errorCode);
}

/// <summary>browser_screenshot's metadata and, on success, the image.</summary>
public sealed record BrowserScreenshot(BrowserScreenshotResult Metadata, EncodedImage? Image);

/// <summary>What browser_type does (roadmap M5.8).</summary>
/// <param name="Text">The text, inserted as one piece (Input.insertText): no key events per character.</param>
/// <param name="Selector">The element to focus first (CSS), or null for the page's focused element.</param>
/// <param name="Replace">Select the field's content first, so the text replaces it.</param>
/// <param name="PressEnter">Press Enter after the text (a keyDown and keyUp of Enter).</param>
public sealed record BrowserTypeOptions(string Text, string? Selector, bool Replace, bool PressEnter)
{
    /// <summary>The most characters one call types.</summary>
    public const int MaxTextLength = 10_000;
}

/// <summary>Where a page's viewport is on the screen, in physical virtual-screen pixels, and how it was found.</summary>
/// <param name="Source">"document" (the tab's Document through UI Automation) or "renderWidget" (its render widget's window).</param>
public sealed record BrowserViewport(double X, double Y, double Width, double Height, string Source)
{
    /// <summary>The Document of the tab, read through UI Automation (its BoundingRectangle).</summary>
    public const string DocumentSource = "document";

    /// <summary>The tab's Chrome_RenderWidgetHostHWND window (GetWindowRect).</summary>
    public const string RenderWidgetSource = "renderWidget";
}
