using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;
using static WindowsAppManagerMcp.Tools.BrowserTabTools;

namespace WindowsAppManagerMcp.Tools;

/// <summary>
/// The thin DOM bridge (roadmap M5.8): browser_element_rect, browser_click, browser_type and browser_screenshot (toolset
/// browser-actions, off by default) and browser_evaluate (toolset browser-script, off by default) on a debug browser's tab
/// through CDP only, and browser_handle_dialog (browser-actions) on a Chrome or Edge window through UI Automation. Anything
/// richer (snapshots of the page, forms, file uploads,
/// network) is Playwright MCP's, attached to the debug browser with --cdp-endpoint (docs/TOOLS.md, SKILL.md).
/// </summary>
/// <remarks>
/// The tools that change the page are refused while the user has paused input, take the input gate only for the commands
/// that change it, and are audited with the lengths of what they were given (an expression, a text, a selector), never
/// the values. CDP's input reaches the page without Windows' input: no foreground change, no real cursor, no UIPI.
/// </remarks>
[McpServerToolType]
public class BrowserPageTools
{
    /// <summary>browser_evaluate's default time limit.</summary>
    public const int DefaultEvaluateTimeoutMs = 5_000;

    /// <summary>The shortest time limit browser_evaluate takes.</summary>
    public const int MinEvaluateTimeoutMs = 100;

    /// <summary>The longest expression browser_evaluate takes.</summary>
    public const int MaxExpressionLength = 100_000;

    /// <summary>browser_screenshot's default long edge, as the capture tools'.</summary>
    public const int DefaultMaxDimension = CaptureOptions.DefaultMaxDimension;

    /// <summary>The longest edge browser_screenshot scales to.</summary>
    public const int MaxDimensionLimit = 8_192;

    private const string NothingDone = "Nothing was done.";

    private const string SelectorDescription = "CSS selector of the element.";

    private const string TextDescription = "Or: text the element shows (ignoring case; exact matches win).";

    private const string RoleDescription = "Or: its ARIA role, such as button or link (alone or with name).";

    private const string NameDescription = "Its accessible name, exactly.";

    private const string IndexDescription = "Which match, from 0, when several match (else AmbiguousElement).";

    /// <summary>The dialog option's description (browser_click, browser_type, browser_navigate; roadmap M5.8, dialogs).</summary>
    internal const string DialogDescription =
        "Answer a JavaScript dialog the action opens: accept or dismiss. Default: report it and leave it open.";

    /// <summary>The prompt text's description.</summary>
    internal const string PromptTextDescription = "The answer to a prompt dialog it opens (with dialog accept).";

    private readonly IBrowserPageService _pages;
    private readonly IWindowService? _windowService;
    private readonly IInputSession? _session;
    private readonly IBrowserDialogService? _dialogs;

    /// <param name="pages">The DOM bridge service.</param>
    /// <param name="windowService">Resolves the window selector; the server always passes it.</param>
    /// <param name="session">Refuses the tools that change the page while input is paused; the server always passes it.</param>
    /// <param name="dialogs">Reads and answers a browser window's JavaScript dialog; the server always passes it.</param>
    public BrowserPageTools(
        IBrowserPageService pages, IWindowService? windowService = null, IInputSession? session = null, IBrowserDialogService? dialogs = null)
    {
        _pages = pages;
        _windowService = windowService;
        _session = session;
        _dialogs = dialogs;
    }

    [McpServerTool(Name = "browser_evaluate", Title = "Run Script in Browser Page", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Run a JavaScript expression in a debug browser tab's page; returns its JSON value (cut at 20,000 characters) or " +
                 "what it threw. Runs with the page's rights; the result is untrusted page data." + CdpOnly)]
    public async Task<BrowserEvaluateResult> BrowserEvaluate(
        [Description("JavaScript expression.")] string expression,
        [Description("Wait for a promise it returns.")] bool awaitPromise = true,
        [Description("Time limit in ms (100-60000).")] int timeoutMs = DefaultEvaluateTimeoutMs,
        [Description(HandleDescription)] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        [Description(TargetIdDescription + ShownTabDefault)] string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        InputGateToolFilter.ThrowIfPaused(_session);
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > MaxExpressionLength)
            throw Invalid($"expression must be 1-{MaxExpressionLength} characters of JavaScript. {NothingDone}");

        if (timeoutMs is < MinEvaluateTimeoutMs or > MaxTimeoutMs)
            throw Invalid($"timeoutMs must be {MinEvaluateTimeoutMs}-{MaxTimeoutMs}, but it is {timeoutMs}. {NothingDone}");

        var target = TabTarget(handle, window, cdpPort, targetId);
        return await _pages.EvaluateAsync(target, expression, awaitPromise, timeoutMs, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "browser_handle_dialog", Title = "Answer Browser Dialog", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Read, or accept or dismiss, the JavaScript dialog (alert, confirm, prompt, beforeunload) a Chrome or Edge window " +
                 "shows, through UI Automation without keys: also one DevTools cannot answer (CdpDialogOpen, CdpTimeout). A confirm or " +
                 "prompt is accepted only once its text was read (DialogNotRead).")]
    public async Task<BrowserDialogResult> BrowserHandleDialog(
        [Description("true accepts (OK, Leave), false dismisses (Cancel, Stay); leave it out to only read the dialog.")] bool? accept = null,
        [Description("The answer to a prompt dialog (with accept true).")] string? promptText = null,
        [Description("Browser window handle. Pass handle or window.")] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        CancellationToken cancellationToken = default)
    {
        if (promptText != null && (accept != true || promptText.Length > BrowserTypeOptions.MaxTextLength))
        {
            throw Invalid(accept == true
                ? $"promptText may be at most {BrowserTypeOptions.MaxTextLength} characters. {NothingDone}"
                : $"promptText answers a prompt dialog, so it needs accept true. {NothingDone}");
        }

        // Reading the dialog is allowed while input is paused, as respond_to_dialog's read is; answering it is not
        if (accept != null)
            InputGateToolFilter.ThrowIfPaused(_session);

        var windowHandle = WindowTargeting.Optional(_windowService, handle, window)
            ?? throw Invalid($"Pass the browser window (handle or window): the dialog is read in it. {NothingDone}");
        if (_dialogs == null)
            throw new InvalidOperationException("browser_handle_dialog needs the browser dialog service.");

        return await _dialogs.HandleAsync(windowHandle, accept, promptText, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "browser_element_rect", Title = "Locate Browser Element", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Find a debug browser page's element; returns its screenRect and clickPoint in screen pixels (for the input and " +
                 "capture tools) and cssRect. Needs the window, showing the tab." + CdpOnly)]
    public async Task<BrowserElementRectResult> BrowserElementRect(
        [Description(SelectorDescription)] string? selector = null,
        [Description(TextDescription)] string? text = null,
        [Description(RoleDescription)] string? role = null,
        [Description(NameDescription)] string? name = null,
        [Description(IndexDescription)] int? index = null,
        [Description("Browser window handle. Pass handle or window.")] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        [Description(TargetIdDescription + ShownTabDefault)] string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        var locator = Locator(selector, text, role, name, index, "Nothing was read.");
        if (handle == null && window == null)
            throw Invalid("Pass the browser window (handle or window): the rect is placed where it shows the page. Nothing was read.");

        var target = TabTarget(handle, window, cdpPort, targetId);
        return await _pages.GetElementRectAsync(target, locator, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "browser_click", Title = "Click Browser Element", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Click a debug browser page's element with CDP mouse events: no real cursor or focus change, also behind other " +
                 "windows. Scrolls it into view; refuses a covered (ElementObscured) or disabled one." + CdpOnly)]
    public async Task<BrowserInputResult> BrowserClick(
        [Description(SelectorDescription)] string? selector = null,
        [Description(TextDescription)] string? text = null,
        [Description(RoleDescription)] string? role = null,
        [Description(NameDescription)] string? name = null,
        [Description(IndexDescription)] int? index = null,
        [Description("The button; primary is the page's left button.")] MouseButton button = MouseButton.Primary,
        [Description("1, 2 (double click) or 3.")] int clickCount = 1,
        [Description(DialogDescription)] BrowserDialogChoice? dialog = null,
        [Description(PromptTextDescription)] string? promptText = null,
        [Description(HandleDescription)] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        [Description(TargetIdDescription + ShownTabDefault)] string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        InputGateToolFilter.ThrowIfPaused(_session);
        var locator = Locator(selector, text, role, name, index, NothingDone);
        if (clickCount is < 1 or > 3)
            throw Invalid($"clickCount must be 1, 2 or 3, but it is {clickCount}. {NothingDone}");

        var answer = DialogAnswer(dialog, promptText);
        var target = TabTarget(handle, window, cdpPort, targetId);
        return await _pages.ClickAsync(target, locator, button, clickCount, cancellationToken, answer).ConfigureAwait(false);
    }

    [McpServerTool(Name = "browser_type", Title = "Type into Browser Page", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Type text into a debug browser page's focused field, or the one selector names, with CDP: no keystrokes reach " +
                 "Windows; also behind other windows." + CdpOnly)]
    public async Task<BrowserInputResult> BrowserType(
        [Description("The text (at most 10000 characters).")] string text,
        [Description("CSS selector of the field to focus first. Default: the focused element.")] string? selector = null,
        [Description("Select the field's content first, so the text replaces it.")] bool replace = false,
        [Description("Press Enter after the text.")] bool pressEnter = false,
        [Description(DialogDescription)] BrowserDialogChoice? dialog = null,
        [Description(PromptTextDescription)] string? promptText = null,
        [Description(HandleDescription)] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        [Description(TargetIdDescription + ShownTabDefault)] string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        InputGateToolFilter.ThrowIfPaused(_session);
        if (text == null || text.Length > BrowserTypeOptions.MaxTextLength || (text.Length == 0 && !pressEnter && !replace))
        {
            throw Invalid($"text must be 1-{BrowserTypeOptions.MaxTextLength} characters (empty only with replace or pressEnter). " +
                          NothingDone);
        }

        if (selector != null && new BrowserElementLocator(selector, null, null, null).Refusal() is { } badSelector)
            throw Invalid($"{badSelector} {NothingDone}");

        var answer = DialogAnswer(dialog, promptText);
        var target = TabTarget(handle, window, cdpPort, targetId);
        return await _pages.TypeAsync(target, new BrowserTypeOptions(text, selector, replace, pressEnter), cancellationToken, answer)
            .ConfigureAwait(false);
    }

    [McpServerTool(Name = "browser_screenshot", Title = "Capture Browser Page", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Capture a debug browser tab's page (its viewport, no browser frame) as an image. A tab not shown is first brought " +
                 "to the front of its window; the browser may bring the window to the front." + CdpOnly)]
    [ToolResultPayload(typeof(BrowserScreenshotResult))]
    public async Task<CallToolResult> BrowserScreenshot(
        [Description("Longest edge of the image in pixels; 0 keeps full size.")] int maxDimension = DefaultMaxDimension,
        [Description(HandleDescription)] long? handle = null,
        [Description(WindowDescription)] WindowSelector? window = null,
        [Description(CdpPortDescription)] int? cdpPort = null,
        [Description(TargetIdDescription + ShownTabDefault)] string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        if (maxDimension is < 0 or > MaxDimensionLimit or (> 0 and < 64))
            throw Invalid($"maxDimension must be 0 (full size) or 64-{MaxDimensionLimit}, but it is {maxDimension}. Nothing was captured.");

        var target = TabTarget(handle, window, cdpPort, targetId);
        var capture = await _pages.CaptureAsync(target, maxDimension, cancellationToken).ConfigureAwait(false);
        return ToolImages.Create(capture.Metadata, capture.Image, isError: !capture.Metadata.Success);
    }

    /// <summary>The browser and tab named: a window, or cdpPort with a targetId (no window names a tab then).</summary>
    private BrowserTabTarget TabTarget(long? handle, WindowSelector? window, int? cdpPort, string? targetId) =>
        Target(_windowService, handle, window, cdpPort, targetId == null ? null : RequireTargetId(targetId), NothingDone, needsTab: true);

    private static BrowserElementLocator Locator(string? selector, string? text, string? role, string? name, int? index, string nothingDone)
    {
        var locator = new BrowserElementLocator(Given(selector), Given(text), Given(role), Given(name), index);
        return locator.Refusal() is { } refusal ? throw Invalid($"{refusal} {nothingDone}") : locator;
    }

    private static string? Given(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// How the action answers a dialog it opens (roadmap M5.8, dialogs): null without the dialog option. promptText needs
    /// dialog accept, and is at most <see cref="BrowserTypeOptions.MaxTextLength"/> characters.
    /// </summary>
    internal static BrowserDialogAnswer? DialogAnswer(BrowserDialogChoice? dialog, string? promptText)
    {
        if (promptText != null && dialog != BrowserDialogChoice.Accept)
            throw Invalid($"promptText answers a prompt dialog the action opens, so it needs dialog accept. {NothingDone}");

        if (promptText is { Length: > BrowserTypeOptions.MaxTextLength })
            throw Invalid($"promptText may be at most {BrowserTypeOptions.MaxTextLength} characters. {NothingDone}");

        return dialog is { } choice ? new BrowserDialogAnswer(choice == BrowserDialogChoice.Accept, promptText) : null;
    }

    private static ToolErrorException Invalid(string message) => new(ToolErrorReporting.InvalidArgumentsErrorCode, message);
}
