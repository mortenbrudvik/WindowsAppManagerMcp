using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Tools;

/// <summary>element_action (roadmap M3.6): pattern-first actions on a UI element, with its state before and after.</summary>
[McpServerToolType]
public partial class ElementActionTools
{
    private const int MinTimeoutMs = 1000;
    private const int MaxTimeoutMs = 30000;
    private const int DefaultTimeoutMs = 5000;

    private readonly IElementActionService _actions;
    private readonly IElementSearchService _search;
    private readonly IElementRegistry _registry;
    private readonly IWindowService? _windowService;
    private readonly IDesktopStateService? _desktopState;
    private readonly IMenuPathService? _menuPaths;

    /// <param name="actions">Acts on the element.</param>
    /// <param name="search">Resolves window and element criteria to one element, as find_ui_elements searches.</param>
    /// <param name="registry">Tells which window a ref belongs to.</param>
    /// <param name="windowService">Resolves the window selector; the server always passes it.</param>
    /// <param name="desktopState">get_desktop_state's observation, for observe (roadmap M3.12); the server always passes it.</param>
    /// <param name="menuPaths">Carries out menu_path (roadmap M3.5); the server always passes it.</param>
    public ElementActionTools(
        IElementActionService actions, IElementSearchService search, IElementRegistry registry, IWindowService? windowService = null,
        IDesktopStateService? desktopState = null, IMenuPathService? menuPaths = null)
    {
        _actions = actions;
        _search = search;
        _registry = registry;
        _windowService = windowService;
        _desktopState = desktopState;
        _menuPaths = menuPaths;
    }

    [McpServerTool(Name = "element_action", Title = "Act on UI Element", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Act on one UI element through UI Automation patterns, without moving the mouse or needing the window in front: " +
                 "invoke, click, toggle, set_value, append_text, select, add_to_selection, remove_from_selection, expand, collapse, " +
                 "scroll_into_view, scroll, set_range_value, realize, focus; double_click and right_click are guarded mouse clicks; " +
                 "window_close, window_minimize and window_maximize are for windows inside an application (a top-level window: " +
                 "close_window, set_window_state); menu_path opens the menu path in value, such as 'File>Save As' (each item " +
                 "expanded, the last invoked, through the submenus they open), from the window or a menu bar's or open menu's ref. " +
                 "Name the element by ref (from find_ui_elements or get_ui_elements), or by handle " +
                 "or window plus name, automationId, controlType or className (in its open menus and dialogs too while it is in front: " +
                 "scope), which must match exactly one element (else " +
                 "AmbiguousElement or ElementNotFound). Returns method, before and after (value, toggle, expand and selection " +
                 "state, name, enabled), outcomeVerified and openedPopup (a window of the app that appeared). A disabled or " +
                 "offscreen element is refused (ElementNotEnabled, ElementOffscreen: use scroll_into_view); an element without the " +
                 "pattern is PatternNotSupported unless fallback mouse allows a guarded click. An action that waits for a dialog it " +
                 "opened returns status InvokedPendingModal with openedPopup: answer the dialog. realize makes an item of a virtualized " +
                 "list real and scrolls it into view; by criteria it also finds items the list has not realized (maxScrolls: by " +
                 "scrolling the list). A password field's value is never " +
                 "read or returned. Refused, with nothing done, for apps the user's policy denies (TargetDeniedByPolicy), the " +
                 "agent's own host window (TargetIsAgentHost), elevated apps (TargetElevated), a window a modal dialog blocks " +
                 "(BlockedByModal), and while the user has paused input (PausedByUser: stop and ask the user, never try to resume). " +
                 "observe 'state' adds get_desktop_state's observation of the window after the action (observed), 'screenshot' " +
                 "also a capture of it as image content.")]
    [ToolResultPayload(typeof(ElementActionResult))]
    public async Task<CallToolResult> ElementAction(
        [Description("What to do: invoke, click, double_click, right_click, focus, set_value, append_text, toggle, select, " +
                     "add_to_selection, remove_from_selection, expand, collapse, scroll_into_view, scroll, set_range_value, realize, " +
                     "window_close, window_minimize, window_maximize, menu_path.")]
        ElementActionKind action,
        [Description("The element: a ref from an earlier read, such as 'w4120035e17'. Or leave it out and pass handle or window " +
                     "with element criteria (menu_path: or with none, to start from the window).")]
        string? @ref = null,
        [Description("Window handle (as integer) whose element the criteria name. Pass handle or window, with criteria, instead of ref.")]
        long? handle = null,
        [Description(WindowTargeting.WindowDescription)]
        WindowSelector? window = null,
        [Description("Element criteria, as find_ui_elements: the element's name, compared as nameMatch says.")]
        string? name = null,
        [Description("How name is compared: 'exact' (default), 'contains' or 'regex', as find_ui_elements.")]
        ElementNameMatch nameMatch = ElementNameMatch.Exact,
        [Description("Element criteria: the element's AutomationId.")]
        string? automationId = null,
        [Description("Element criteria: control type, or several separated by commas, e.g. 'Button' or 'Edit,ComboBox'.")]
        string? controlType = null,
        [Description("Element criteria: the element's class name.")]
        string? className = null,
        [Description(UIElementTools.CriteriaScopeDescription)]
        ElementSearchScope? scope = null,
        [Description("set_value and append_text: the text. set_range_value: the number (e.g. '42'). toggle: the state to reach, " +
                     "'on', 'off' or 'indeterminate' (up to two toggles); leave it out to toggle once. menu_path: the menu items " +
                     "separated by '>', e.g. 'File>Save As' (names ignore case and a trailing '...'). Logged as its length only; a password " +
                     "field's value is never read or returned.")]
        string? value = null,
        [Description("set_value and append_text: 'value' (default: the Value pattern, no keyboard input), 'type' (text fields " +
                     "only: focus the field, select all or go to the end, type the text, with every input check; for web and React " +
                     "inputs that ignore the Value pattern) or 'paste' (as type, but pasted from the clipboard, which it replaces; " +
                     "not for password fields).")]
        ElementValueMode mode = ElementValueMode.Value,
        [Description("scroll: 'up', 'down', 'left' or 'right'. The element's Scroll pattern, or its nearest scrollable ancestor's, " +
                     "is used; the result's scroll has the percent and atEnd.")]
        ElementScrollDirection? direction = null,
        [Description("scroll: 'page' (default) or 'line'.")]
        ElementScrollAmount amount = ElementScrollAmount.Page,
        [Description("When no pattern does invoke, click or select: 'none' (default: PatternNotSupported, nothing is done) or " +
                     "'mouse' (bring the window to the front and send a guarded click at the element's click point, with every " +
                     "input check; it moves the pointer and takes the foreground).")]
        ElementActionFallback fallback = ElementActionFallback.None,
        [Description("Allow acting on the agent's own host window (its terminal, editor or chat app). Works only with ref or " +
                     "handle (or window.handle); set it only when the user explicitly asked for it. Default: false")]
        bool allowHostTarget = false,
        [Description(MaxScrollsDescription)]
        int maxScrolls = 0,
        [Description("How long the action may block, for example on a dialog it opened, and the bound of each read, in " +
                     "milliseconds (default: 5000, min: 1000, max: 30000). realize with maxScrolls: the whole search.")]
        int timeoutMs = DefaultTimeoutMs,
        [Description("What to return about the desktop after the action: 'none' (default), 'state' (get_desktop_state's default " +
                     "observation of the element's window, or of the window in front once that closed, as observed; needs " +
                     "get_desktop_state to be served) or 'screenshot' (as state, plus a capture of that window as image content with " +
                     "a captureId; needs capture_window to be served too).")]
        ObserveMode observe = ObserveMode.None,
        CancellationToken cancellationToken = default)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, MinTimeoutMs, MaxTimeoutMs));
        var request = new ElementActionRequest(@ref ?? string.Empty, action, value, mode, direction, amount, fallback, allowHostTarget, timeout);
        if (ElementActionService.ArgumentError(request) is { } argumentError)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, argumentError + " Nothing was done.");
        }

        if (maxScrolls != 0 && (action != ElementActionKind.Realize || @ref != null))
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "maxScrolls is for realize with element criteria (it scrolls the list to find the item), not for " +
                $"{(@ref != null ? "a ref, which names its element already" : ToolEnum<ElementActionKind>.Name(action))}. Nothing was done.");
        }

        if (ObserveError(observe) is { } observeError)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, observeError + " Nothing was done.");
        }

        var controlTypes = UIElementTools.ParseControlTypes(controlType);
        var hasCriteria = !string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(automationId) || controlTypes != null ||
                          !string.IsNullOrEmpty(className);
        if (UIElementTools.CriteriaScopeError(scope, @ref != null, hasWithinRef: false) is { } scopeError)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, scopeError + " Nothing was done.");
        }

        if (@ref != null)
        {
            if (hasCriteria || handle != null || window != null)
            {
                throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                    "Pass either ref, or handle or window with element criteria (name, automationId, controlType, className), not " +
                    "both. Nothing was done.");
            }

            var normalized = ElementRefs.Normalize(@ref) ?? throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"ref '{@ref}' is not an element ref: refs look like '{ElementRefs.Example}' and come from find_ui_elements, " +
                "get_ui_elements, get_focused_element, get_element_at_point or capture_with_elements. Nothing was done.");
            var lookup = _registry.Lookup(normalized);
            NoteWindow(lookup.WindowHandle);
            if (action == ElementActionKind.MenuPath)
            {
                // The ref's own window holds the path's first item; a ref the registry no longer knows names no window
                return await ObserveAsync(lookup.Success
                    ? await MenuPathAsync(lookup.WindowHandle, normalized, request, scope: null, cancellationToken).ConfigureAwait(false)
                    : new ElementActionResult(false, "menu_path", normalized, Error: lookup.Error, ErrorCode: lookup.ErrorCode.ToString()),
                    observe, cancellationToken).ConfigureAwait(false);
            }

            return await ObserveAsync(await _actions.ActAsync(request with { Ref = normalized }, cancellationToken).ConfigureAwait(false),
                observe, cancellationToken).ConfigureAwait(false);
        }

        var menuFromWindow = action == ElementActionKind.MenuPath && !hasCriteria && (handle != null || window != null);
        if (!hasCriteria && !menuFromWindow)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "Name the element: pass ref, or handle or window with at least one of name, automationId, controlType and " +
                "className" + (action == ElementActionKind.MenuPath ? " (menu_path: or handle or window alone)" : string.Empty) +
                ". Nothing was done.");
        }

        if (allowHostTarget && handle == null && window is not { Handle: not null })
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                "allowHostTarget lifts the guard on the agent's own terminal or chat window only for a window named by its handle " +
                "(handle, or window.handle) or an element ref, not for a selector that finds windows by name. Nothing was done.");
        }

        if (menuFromWindow)
        {
            var menuWindow = WindowTargeting.Require(_windowService, handle, window);
            NoteWindow(menuWindow);
            return await ObserveAsync(await MenuPathAsync(menuWindow, startRef: null, request, scope, cancellationToken).ConfigureAwait(false),
                observe, cancellationToken).ConfigureAwait(false);
        }

        if (nameMatch == ElementNameMatch.Regex && !string.IsNullOrEmpty(name) &&
            ElementCriteria.CompileNameRegex(name, out var regexError) == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, regexError + " Nothing was done.");
        }

        var windowHandle = WindowTargeting.Require(_windowService, handle, window);
        var query = new ElementQuery(
            WindowHandle: windowHandle,
            Name: name,
            NameMatch: nameMatch,
            AutomationId: automationId,
            ControlTypes: controlTypes,
            ClassName: className,
            // An offscreen or disabled element is found, so the action can say what is wrong with it (or scroll it into view)
            VisibleOnly: false,
            EnabledOnly: false,
            MaxResults: 2,
            Detail: ElementDetail.Minimal,
            Timeout: timeout,
            Scope: scope)
        {
            // realize (roadmap M3.10): items the list has not realized are found too, through its ItemContainer pattern
            SearchVirtualized = action == ElementActionKind.Realize && FitsVirtualizedSearch(name, nameMatch, automationId),
            // The input guard checks the found element's window, and allowHostTarget may name the host's own popups
            IncludeHostPopups = true
        };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var found = await FindOneAsync(query, action, cancellationToken).ConfigureAwait(false);
        if (found.Failure is { ErrorCode: nameof(UIElementErrorCode.ElementNotFound) } && action == ElementActionKind.Realize && maxScrolls > 0)
        {
            NoteWindow(windowHandle);
            return await ObserveAsync(
                await RealizeByScrollingAsync(query, request, Math.Min(maxScrolls, MaxScrollsLimit), stopwatch, found.Failure, cancellationToken)
                    .ConfigureAwait(false),
                observe, cancellationToken).ConfigureAwait(false);
        }

        if (found.Failure is { } failure)
        {
            return await ObserveAsync(failure, observe, cancellationToken).ConfigureAwait(false);
        }

        NoteWindow(windowHandle);
        if (action == ElementActionKind.MenuPath)
        {
            // The element the criteria found holds the path's first item (a menu bar, an open menu), in its own window
            var registered = _registry.Lookup(found.Ref!).WindowHandle;
            return await ObserveAsync(
                await MenuPathAsync(registered != nint.Zero ? registered : windowHandle, found.Ref, request, scope: null, cancellationToken)
                    .ConfigureAwait(false),
                observe, cancellationToken).ConfigureAwait(false);
        }

        return await ObserveAsync(await _actions.ActAsync(request with { Ref = found.Ref! }, cancellationToken).ConfigureAwait(false),
            observe, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Why observe cannot be honoured (checked before anything is done), or null: the observation is get_desktop_state's
    /// read, so it follows get_desktop_state being served, as the screenshot follows capture_window.
    /// </summary>
    private string? ObserveError(ObserveMode observe) => observe switch
    {
        ObserveMode.None => null,
        _ when _desktopState == null => throw new InvalidOperationException("observe needs the desktop state service; the server always registers it."),
        _ when _desktopState.StateUnavailable() is { } stateUnavailable => stateUnavailable,
        ObserveMode.Screenshot => _desktopState.ScreenshotUnavailable() is { } unavailable
            ? $"observe 'screenshot': {unavailable} Use observe 'state'."
            : null,
        _ => null
    };

    /// <summary>
    /// The tool result: the action's result, with observe (roadmap M3.12) get_desktop_state's observation of the element's window
    /// (of the window in front once that closed) after the action, and with 'screenshot' its capture as image content first.
    /// </summary>
    private async Task<CallToolResult> ObserveAsync(ElementActionResult result, ObserveMode observe, CancellationToken cancellationToken)
    {
        if (observe == ObserveMode.None || _desktopState == null)
        {
            return ToolImages.Create(result, null, isError: !result.Success, ToolJson.SerializerOptions);
        }

        var include = observe == ObserveMode.Screenshot
            ? new HashSet<DesktopStateSection>(DesktopStateRequest.DefaultInclude) { DesktopStateSection.Screenshot }
            : DesktopStateRequest.DefaultInclude;
        // A window the action was refused for (a denied app, the agent's own host) is not read by name: the agent asked for an
        // action there, not a read, so the observation follows the default rules for the window in front
        var refusedWindow = result.ErrorCode is nameof(InputErrorCode.TargetDeniedByPolicy) or nameof(InputErrorCode.TargetIsAgentHost);
        nint? window = !refusedWindow && result.WindowHandle != 0 && _windowService?.IsValidWindow((nint)result.WindowHandle) != false
            ? (nint)result.WindowHandle
            : null;
        var snapshot = await _desktopState.GetStateAsync(DesktopStateTools.Request(window, include), cancellationToken).ConfigureAwait(false);
        return ToolImages.Create(result with { Observed = snapshot.State }, snapshot.Image, isError: !result.Success, DesktopStateTools.StateJsonOptions);
    }

    /// <summary>menu_path (roadmap M3.5): the path's items, each acted on through element_action by the menu path service.</summary>
    private async Task<ElementActionResult> MenuPathAsync(
        nint window, string? startRef, ElementActionRequest request, ElementSearchScope? scope, CancellationToken cancellationToken)
    {
        if (_menuPaths == null)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, "menu_path is not available in this server. Nothing was done.");
        }

        var items = MenuPaths.Parse(request.Value, out var error) ??
                    throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode, error + " Nothing was done.");
        return await _menuPaths.RunAsync(new MenuPathRequest(window, startRef, items, scope, request.AllowHostTarget, request.Timeout),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The ref of the one element the query matches, or the failure: none, several, or a search that did not finish.</summary>
    private async Task<(string? Ref, ElementActionResult? Failure)> FindOneAsync(ElementQuery query, ElementActionKind action, CancellationToken cancellationToken)
    {
        var actionName = ToolEnum<ElementActionKind>.Name(action);
        ElementActionResult Fail(string code, string error) =>
            new(false, actionName, WindowHandle: (long)query.WindowHandle, Error: error, ErrorCode: code);

        FindElementsResult result;
        try
        {
            result = await _search.FindAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"name '{query.Name}' (nameMatch regex) took longer than {ElementCriteria.RegexTimeout.TotalMilliseconds:0} ms on an " +
                "element's name, so the search was stopped. Nothing was done. Use a simpler pattern, or nameMatch contains.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            return (null, Fail(result.ErrorCode.ToString(), $"{result.Error} Nothing was done."));
        }

        if (result.TruncationReason is ElementSearchTruncation.Timeout or ElementSearchTruncation.SearchLimit or
            ElementSearchTruncation.PartsSkipped or ElementSearchTruncation.WindowLimit)
        {
            return (null, Fail(nameof(UIElementErrorCode.OperationTimedOut),
                $"The search for the element did not cover {Covered(result)} ({result.TruncationReason}), so it is not known that " +
                $"only one element matches ({result.MatchCount} found so far). Nothing was done. Narrow the criteria (automationId, " +
                "controlType), or find the element with find_ui_elements and pass its ref."));
        }

        if (result.MatchCount == 0 || result.Elements is not { Count: > 0 } elements)
        {
            return (null, Fail(nameof(UIElementErrorCode.ElementNotFound),
                $"No element of {Searched(result)} matches the criteria (offscreen and disabled elements included). Nothing was done. " +
                "Check them with find_ui_elements, or get_ui_elements format text."));
        }

        if (result.MatchCount > 1)
        {
            var refs = string.Join(", ", elements.Select(e => $"{e.Ref} {e.ControlType} '{e.Name}'"));
            return (null, Fail(nameof(UIElementErrorCode.AmbiguousElement),
                $"{result.MatchCount} elements match the criteria (first: {refs}), so none was acted on. Narrow the criteria " +
                "(automationId, controlType), or find them with find_ui_elements and pass the ref of the one you mean."));
        }

        var element = elements[0];
        return element.Ref is { } elementRef
            ? (elementRef, null)
            : (null, Fail(nameof(UIElementErrorCode.ElementAccessFailed),
                "The element has no runtime id, so it cannot be named for the action. Nothing was done."));
    }

    /// <summary>What the search had to cover: the window, or with a wider scope the windows of that scope.</summary>
    private static string Covered(FindElementsResult result) =>
        result.Scope is ElementSearchScope.WindowAndPopups or ElementSearchScope.Desktop ? "every window of its scope" : "the whole window";

    /// <summary>What was searched, for a search that found nothing.</summary>
    private static string Searched(FindElementsResult result) => result.Scope switch
    {
        ElementSearchScope.WindowAndPopups => "the window or its open popups",
        ElementSearchScope.Desktop => "the windows searched",
        _ => "the window"
    };

    /// <summary>Notes the element's window for the audit log line, as it is before the action.</summary>
    private void NoteWindow(nint handle)
    {
        if (handle != nint.Zero && _windowService != null)
        {
            try
            {
                AuditScope.NoteWindow(_windowService.GetWindowInfo(handle));
            }
            catch (Exception)
            {
                // Only the audit line's title and process depend on it
            }
        }
    }
}
