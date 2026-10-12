using System.Diagnostics;
using System.Text.RegularExpressions;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>The menu path element_action's menu_path takes as its value (roadmap M3.5): "File>Save As".</summary>
public static class MenuPaths
{
    /// <summary>What separates the items: menu names hardly ever hold it.</summary>
    public const char Separator = '>';

    /// <summary>The most items one path may have: menus are rarely deeper than four.</summary>
    public const int MaxItems = 8;

    /// <summary>The longest item name.</summary>
    public const int MaxItemLength = 200;

    /// <summary>The items of <paramref name="value"/>, trimmed, or null with the reason.</summary>
    public static IReadOnlyList<string>? Parse(string? value, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "menu_path needs value: the menu items from the top down, separated by '>', such as 'File>Save As'.";
            return null;
        }

        var items = value.Split(Separator).Select(item => item.Trim()).ToList();
        if (items.Any(item => item.Length == 0))
        {
            error = "menu_path's value has an empty item (two '>' in a row, or one at an end); name every item, such as 'File>Save As'.";
            return null;
        }

        if (items.Count > MaxItems)
        {
            error = $"menu_path's value has {items.Count} items; at most {MaxItems} are followed.";
            return null;
        }

        if (items.Any(item => item.Length > MaxItemLength))
        {
            error = $"A menu_path item is longer than {MaxItemLength} characters.";
            return null;
        }

        return items;
    }

    /// <summary>
    /// The name pattern of an item: its name, ignoring case and the blanks around it, with an optional trailing "..." or
    /// "…" (Win32 and WinForms menus name "Save As..." so) and an optional accelerator after a tab.
    /// </summary>
    public static string NamePattern(string item) => $@"^\s*{Regex.Escape(item)}\s*(\.\.\.|…)?\s*(\t.*)?$";
}

/// <summary>element_action's menu_path (roadmap M3.5); see <see cref="IMenuPathService"/> for the rules.</summary>
public sealed class MenuPathService : IMenuPathService
{
    private static readonly string[] MenuItemType = ["MenuItem"];

    /// <summary>The bound of each search and action when the request names none (element_action's default).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The longest a whole path may take: its timeout times its items, at most this.</summary>
    public static readonly TimeSpan MaxTotalTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The longest element name quoted in a message (the names are the application's text).</summary>
    private const int MaxQuotedName = 100;

    private readonly IElementSearchService _search;
    private readonly IElementActionService _actions;
    private readonly IPopupService _popups;

    /// <param name="search">Finds each item, as find_ui_elements.</param>
    /// <param name="actions">Expands and invokes the items, with every guard of element_action.</param>
    /// <param name="popups">The submenu window an item opened.</param>
    public MenuPathService(IElementSearchService search, IElementActionService actions, IPopupService popups)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _popups = popups ?? throw new ArgumentNullException(nameof(popups));
    }

    /// <summary>
    /// How long to wait for the submenu window of an expanded item when element_action did not report one (bounded by the
    /// timeout). WPF and WinForms submenus are found below their item as well, so this only delays a path whose submenu
    /// opens late.
    /// </summary>
    public TimeSpan SubmenuWait { get; init; } = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    public async Task<ElementActionResult> RunAsync(MenuPathRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = new MenuRun(request, Stopwatch.StartNew());
        if (request.Items.Count is 0 or > MenuPaths.MaxItems)
        {
            return run.Fail(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"menu_path needs 1 to {MenuPaths.MaxItems} items, such as 'File>Save As'. Nothing was done.");
        }

        ElementActionResult? last = null;
        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index];
            if (run.OutOfTime)
            {
                return run.TimedOut(index);
            }

            var found = await FindItemAsync(run, index, cancellationToken).ConfigureAwait(false);
            if (found.Failure != null)
            {
                return found.Failure;
            }

            if (index < request.Items.Count - 1)
            {
                var before = _popups.GetPopups(request.Window).Select(p => p.Handle).ToHashSet();
                var expanded = await _actions.ActAsync(run.Action(found.Ref!, ElementActionKind.Expand), cancellationToken).ConfigureAwait(false);
                if (expanded.ErrorCode == nameof(UIElementErrorCode.PatternNotSupported))
                {
                    // An item without ExpandCollapse has no submenu: it is a command, and invoking it on the way to the next
                    // item would run it (a wrong path such as 'Edit>Delete>All' would delete)
                    return run.Fail(nameof(UIElementErrorCode.PatternNotSupported),
                        $"Menu item '{item}' (item {index + 1} of {request.Items.Count}) has no submenu to open (no ExpandCollapse " +
                        "pattern): it is a command, and only the last item of a path is invoked, so it was not. Check the path " +
                        "(find_ui_elements controlType MenuItem, get_active_popups), or invoke the item with element_action.");
                }

                if (!expanded.Success)
                {
                    return run.Fail(expanded, item);
                }

                var submenu = expanded.OpenedPopup is { } popup ? (nint)popup.Handle : nint.Zero;
                if (submenu == nint.Zero)
                {
                    submenu = (await _popups.WaitForNewPopupAsync(request.Window, before, run.Bound(SubmenuWait), cancellationToken)
                        .ConfigureAwait(false))?.Handle ?? nint.Zero;
                }

                run.Opened(new MenuPathStep(item, expanded.Ref ?? found.Ref, expanded.Method, submenu == nint.Zero ? null : (long)submenu),
                    found.Window, submenu, expanded.Warnings);
                continue;
            }

            last = await _actions.ActAsync(run.Action(found.Ref!, ElementActionKind.Invoke), cancellationToken).ConfigureAwait(false);
            if (last.ErrorCode == nameof(UIElementErrorCode.PatternNotSupported))
            {
                // A checkable item without Invoke: element_action's click goes through its pattern (Toggle)
                last = await _actions.ActAsync(run.Action(found.Ref!, ElementActionKind.Click), cancellationToken).ConfigureAwait(false);
            }

            if (!last.Success)
            {
                return run.Fail(last, item);
            }

            run.Steps.Add(new MenuPathStep(item, last.Ref ?? found.Ref, last.Method,
                last.OpenedPopup is { } opened ? opened.Handle : null));
            run.Warnings.AddRange(last.Warnings ?? []);
        }

        return run.Succeed(last!);
    }

    /// <summary>The one MenuItem named by the item at <paramref name="index"/>, searched where it can be (see the remarks).</summary>
    private async Task<FoundItem> FindItemAsync(MenuRun run, int index, CancellationToken cancellationToken)
    {
        var request = run.Request;
        var item = request.Items[index];
        var places = new List<(nint Window, string? Within, ElementSearchScope? Scope)>();
        if (index == 0)
        {
            places.Add((request.Window, request.StartRef, request.StartRef != null ? ElementSearchScope.Window : request.Scope));
        }
        else
        {
            // The submenu the previous item opened, else below that item (WPF, WinForms); the whole window and its popups
            // only when no submenu window was seen, so an item missing from the submenu never matches another menu
            if (run.Submenu != nint.Zero)
            {
                places.Add((run.Submenu, null, ElementSearchScope.Window));
            }

            places.Add((run.PreviousWindow, run.PreviousRef, ElementSearchScope.Window));
            if (run.Submenu == nint.Zero)
            {
                places.Add((request.Window, null, ElementSearchScope.WindowAndPopups));
            }
        }

        FindElementsResult? failed = null;
        var incomplete = false;
        foreach (var (window, within, scope) in places)
        {
            if (run.OutOfTime)
            {
                incomplete = true;
                break;
            }

            FindElementsResult result;
            try
            {
                result = await _search.FindAsync(new ElementQuery(
                    WindowHandle: window,
                    WithinRef: within,
                    Name: MenuPaths.NamePattern(item),
                    NameMatch: ElementNameMatch.Regex,
                    ControlTypes: MenuItemType,
                    VisibleOnly: true,
                    EnabledOnly: false,
                    MaxResults: 2,
                    Detail: ElementDetail.Minimal,
                    Timeout: run.StepTimeout,
                    Scope: scope)
                {
                    // The menus are popups; each item is acted on through element_action, whose guard checks its window
                    IncludeHostPopups = true
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (RegexMatchTimeoutException)
            {
                incomplete = true;
                continue;
            }

            if (!result.Success)
            {
                failed ??= result;
                continue;
            }

            if (result.MatchCount > 1 && result.Elements is { } several)
            {
                var refs = string.Join(", ", several.Select(e => $"{e.Ref} '{Cut(e.Name)}'"));
                return FoundItem.Failed(run.Fail(nameof(UIElementErrorCode.AmbiguousElement),
                    $"{result.MatchCount} menu items match '{item}' (item {index + 1} of {request.Items.Count}; first: {refs}), so none " +
                    "was opened. Name them one at a time with find_ui_elements and element_action."));
            }

            if (result.Elements is [{ Ref: { } itemRef } element])
            {
                return new FoundItem(itemRef, element.WindowHandle is { } own ? (nint)own : (nint)result.WindowHandle, null);
            }

            incomplete |= result.TruncationReason is ElementSearchTruncation.Timeout or ElementSearchTruncation.SearchLimit
                or ElementSearchTruncation.PartsSkipped or ElementSearchTruncation.WindowLimit;
        }

        if (failed != null && index == 0)
        {
            return FoundItem.Failed(run.Fail(failed.ErrorCode.ToString(), $"The menu could not be searched: {failed.Error}"));
        }

        var where = index == 0
            ? request.StartRef != null ? $"below {request.StartRef}" : "in the window (or its open popups)"
            : run.Submenu != nint.Zero
                ? $"in the submenu '{request.Items[index - 1]}' opened (window {(long)run.Submenu}) or below that item"
                : $"in the menu '{request.Items[index - 1]}' opened";
        return FoundItem.Failed(run.Fail(
            incomplete ? nameof(UIElementErrorCode.OperationTimedOut) : nameof(UIElementErrorCode.ElementNotFound),
            $"No visible menu item '{item}' (item {index + 1} of {request.Items.Count}) was found {where}" +
            (incomplete ? " before the search ran out of time" : string.Empty) +
            ". Check the names with find_ui_elements (controlType MenuItem) or get_active_popups."));
    }

    private static string? Cut(string? name) => name is { Length: > MaxQuotedName } ? name[..MaxQuotedName] : name;

    /// <summary>A found item: its ref and the top-level window it is registered under, or the failure.</summary>
    private sealed record FoundItem(string? Ref, nint Window, ElementActionResult? Failure)
    {
        public static FoundItem Failed(ElementActionResult failure) => new(null, nint.Zero, failure);
    }

    /// <summary>One run of a path: what was opened so far, and its deadline.</summary>
    private sealed class MenuRun(MenuPathRequest request, Stopwatch stopwatch)
    {
        private const string ActionName = "menu_path";

        private static readonly TimeSpan MinStep = TimeSpan.FromSeconds(1);

        public MenuPathRequest Request { get; } = request;

        /// <summary>The bound of each search and action: the request's timeout.</summary>
        public TimeSpan Timeout { get; } = request.Timeout ?? DefaultTimeout;

        /// <summary>The bound of the whole path: <see cref="Timeout"/> per item, at most <see cref="MaxTotalTimeout"/>.</summary>
        public TimeSpan Budget
        {
            get
            {
                var total = Timeout * Math.Max(1, Request.Items.Count);
                return total < MaxTotalTimeout ? total : MaxTotalTimeout;
            }
        }

        private TimeSpan Remaining => Budget - stopwatch.Elapsed;

        public bool OutOfTime => Remaining <= TimeSpan.Zero;

        /// <summary>
        /// The bound of the next search or action: the timeout, or what is left of the path's time when less, but at least a
        /// second (or the timeout when shorter), so a step begun just before the deadline is not cut off as it starts.
        /// </summary>
        public TimeSpan StepTimeout
        {
            get
            {
                var floor = Timeout < MinStep ? Timeout : MinStep;
                var bound = Bound(Timeout);
                return bound < floor ? floor : bound;
            }
        }

        /// <summary><paramref name="wait"/>, or what is left of the path's time when less (never below zero).</summary>
        public TimeSpan Bound(TimeSpan wait)
        {
            var remaining = Remaining;
            return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining < wait ? remaining : wait;
        }

        /// <summary>element_action's request for an item, within the path's time.</summary>
        public ElementActionRequest Action(string itemRef, ElementActionKind action) =>
            new(itemRef, action, AllowHostTarget: Request.AllowHostTarget, Timeout: StepTimeout);

        /// <summary>The path ran out of its time before the item at <paramref name="index"/>.</summary>
        public ElementActionResult TimedOut(int index) => Fail(nameof(UIElementErrorCode.OperationTimedOut),
            $"The menu path ran out of its time ({Budget.TotalMilliseconds:0} ms: timeoutMs per item) before item {index + 1} of " +
            $"{Request.Items.Count} ('{Request.Items[index]}'); nothing more was done.");

        public List<MenuPathStep> Steps { get; } = [];

        public List<string> Warnings { get; } = [];

        public string? PreviousRef { get; private set; }

        public nint PreviousWindow { get; private set; }

        /// <summary>The submenu window the previous item opened, or zero.</summary>
        public nint Submenu { get; private set; }

        public void Opened(MenuPathStep step, nint itemWindow, nint submenu, IReadOnlyList<string>? warnings)
        {
            Steps.Add(step);
            PreviousRef = step.Ref;
            PreviousWindow = itemWindow;
            Submenu = submenu;
            Warnings.AddRange(warnings ?? []);
        }

        public ElementActionResult Fail(string code, string error) => new(
            false, ActionName, WindowHandle: (long)Request.Window, Error: error + StillOpen(), ErrorCode: code, ElapsedMilliseconds: Elapsed)
        {
            Steps = Steps.Count == 0 ? null : [.. Steps]
        };

        /// <summary>The failure of an item's action, keeping what element_action reported.</summary>
        public ElementActionResult Fail(ElementActionResult failed, string item) => failed with
        {
            Action = ActionName,
            Error = $"Menu item '{item}' (item {Steps.Count + 1} of {Request.Items.Count}): {failed.Error}{StillOpen()}",
            Steps = Steps.Count == 0 ? null : [.. Steps],
            ElapsedMilliseconds = Elapsed
        };

        public ElementActionResult Succeed(ElementActionResult last) => last with
        {
            Action = ActionName,
            Steps = [.. Steps],
            Warnings = Warnings.Count == 0 ? null : Warnings.Distinct().ToList(),
            ElapsedMilliseconds = Elapsed
        };

        /// <summary>What the earlier items left open, for a failure.</summary>
        private string StillOpen() => Steps.Count == 0
            ? string.Empty
            : $" The menus opened by '{string.Join(">", Steps.Select(s => s.Item))}' may still be open: close them (element_action " +
              $"collapse on {Steps[0].Ref}, or send_keys {{ESC}}) or go on from there.";

        private double Elapsed => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
    }
}
