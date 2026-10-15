using System.Text.Json;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The CDP side of the DOM bridge (roadmap M5.8): run a script, answer a JavaScript dialog, find an element's box, click
/// it and type into the page with CDP's own input (Input.dispatchMouseEvent, Input.insertText), and capture the page.
/// Every call opens its own connection to the tab and closes it before it returns (M5.2: no pool), and is bounded by its
/// timeout; failures come back as a failed <see cref="CdpPageResult{T}"/>, and only the caller's cancellation throws.
/// </summary>
/// <remarks>
/// <para>
/// CDP's input goes to the page through the browser, not through Windows: it needs no foreground window, works on a window
/// behind others, is not subject to UIPI and moves no real cursor. It is still an action on the user's browser, so the
/// commands that change the page are sent inside the input gate (<see cref="CdpGate"/>), which refuses while the user has
/// paused input and orders them with the input and window tools; the gate is let go once the browser answered, at most
/// about a second per command, never for a page's work after it.
/// </para>
/// <para>
/// The tab is the caller's to choose and check (the window's own debug port, the target policy, not one of the browser's
/// own pages): this service only talks to the tab it is given.
/// </para>
/// </remarks>
public interface ICdpPageService
{
    /// <summary>
    /// Runs <paramref name="expression"/> in the tab's page (Runtime.evaluate, returnByValue) and returns its value, or
    /// what it threw as data (<see cref="CdpEvaluation.Exception"/>). The command is sent inside the gate.
    /// </summary>
    Task<CdpPageResult<CdpEvaluation>> EvaluateAsync(
        CdpTarget target, string expression, bool awaitPromise, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts or dismisses the JavaScript dialog the tab has open (Page.handleJavaScriptDialog, sent while the dialog
    /// blocks the page), also one that was open before this connection: the browser may not report such a dialog to a new
    /// connection (Edge 155 does not), so the command is sent whether or not it was reported. DialogNotFound when the page
    /// has none open (it answers); CdpDialogOpen when it does not answer and the browser would not hand its dialog over.
    /// </summary>
    Task<CdpPageResult<CdpDialogOutcome>> HandleDialogAsync(
        CdpTarget target, bool accept, string? promptText, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>Finds the element <paramref name="locator"/> names and reads its box (DOM.getContentQuads); nothing is changed.</summary>
    Task<CdpPageResult<CdpElementBox>> LocateAsync(
        CdpTarget target, BrowserElementLocator locator, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clicks the element <paramref name="locator"/> names: scrolls it into view, checks that nothing covers its middle
    /// (ElementObscured) and that it is not disabled (ElementNotEnabled), then sends the mouse events there inside the gate.
    /// </summary>
    Task<CdpPageResult<CdpInputOutcome>> ClickAsync(
        CdpTarget target, BrowserElementLocator locator, MouseButton button, int clickCount, CdpGate gate, TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Types <see cref="BrowserTypeOptions.Text"/> into the element its selector names (focused first) or the page's
    /// focused element, which must take text (FocusNotOnElement otherwise), with Input.insertText inside the gate.
    /// </summary>
    Task<CdpPageResult<CdpInputOutcome>> TypeAsync(
        CdpTarget target, BrowserTypeOptions options, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the tab's viewport as PNG (Page.captureScreenshot). A tab whose page is not shown (another tab is in front,
    /// or its window is covered or minimized) produces no frames, so it is brought to the front first (Page.bringToFront,
    /// inside the gate; the browser may bring its window to the front).
    /// </summary>
    Task<CdpPageResult<CdpScreenshot>> CaptureAsync(
        CdpTarget target, CdpGate gate, TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a DOM bridge call (roadmap M5.8): the value, or an error code (a <see cref="BrowserErrorCode"/>'s name,
/// or one of the element codes the UI Automation tools use, such as ElementNotFound) and its message.
/// </summary>
public sealed record CdpPageResult<T>(T? Value, string? ErrorCode, string? Error)
{
    /// <summary>True when the call succeeded and <see cref="Value"/> is set.</summary>
    public bool Success => ErrorCode == null;

    public static CdpPageResult<T> Ok(T value) => new(value, null, null);

    public static CdpPageResult<T> Fail(string errorCode, string error) => new(default, errorCode, error);

    public static CdpPageResult<T> Fail(BrowserErrorCode errorCode, string error) => new(default, errorCode.ToString(), error);

    /// <summary>The failure of a failed CDP command or connection.</summary>
    public static CdpPageResult<T> From<TOther>(CdpResult<TOther> failed) => new(default, failed.ErrorCode.ToString(), failed.Error);

    /// <summary>Copies the error of this failed result into a result of another type.</summary>
    public CdpPageResult<TOther> FailAs<TOther>() => new(default, ErrorCode, Error);
}

/// <summary>What a script gave back: its value (JSON) and its type, or what it threw.</summary>
/// <param name="Value">The value as JSON; a value that is not JSON (NaN, Infinity, -0, a BigInt) as a string; null for undefined.</param>
/// <param name="ValueType">The value's type as CDP names it: its subtype (null, array, date ...) when it has one, else its type.</param>
public sealed record CdpEvaluation(JsonElement? Value, string ValueType, BrowserScriptException? Exception = null);

/// <summary>What browser_handle_dialog answered: the dialog, when the browser reported it to this connection.</summary>
public sealed record CdpDialogOutcome(CdpDialog? Dialog);

/// <summary>
/// An element's box: what it is, how many elements matched, its rectangle in CSS pixels of the viewport (all its boxes
/// together), and the page's devicePixelRatio, viewport size in CSS pixels, pinch-zoom scale and visibilityState.
/// </summary>
public sealed record CdpElementBox(
    BrowserElementInfo Element, int MatchCount, CssRect Rect, double DevicePixelRatio, double ViewportWidth, double ViewportHeight,
    double Scale, string Visibility, bool InMainDocument);

/// <summary>
/// What a click or a typing did: the element, how many matched, the point clicked (CSS pixels), the length typed, the
/// dialog it opened, and a warning when the page did not confirm the input in time.
/// </summary>
public sealed record CdpInputOutcome(
    BrowserElementInfo? Element = null, int? MatchCount = null, CssPoint? Point = null, int? TypedLength = null,
    bool? PressedEnter = null, CdpDialog? Dialog = null, string? Warning = null);

/// <summary>A capture of the tab's viewport: the PNG, the page's address and title, and whether the tab was brought to the front.</summary>
public sealed record CdpScreenshot(byte[] Png, string Url, string Title, bool Activated);
