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

        var wait = request.Timeout is { } timeout && timeout < SubmenuWait ? timeout : SubmenuWait;
        ElementActionResult? last = null;
        for (var index = 0; index < request.Items.Count; index++)
        {
            var item = request.Items[index];
            var found = await FindItemAsync(run, index, cancellationToken).ConfigureAwait(false);
            if (found.Failure != null)
            {
                return found.Failure;
            }

            if (index < request.Items.Count - 1)
            {
                var before = _popups.GetPopups(request.Window).Select(p => p.Handle).ToHashSet();
                var expanded = await ActAsync(run, found.Ref!, ElementActionKind.Expand, ElementActionKind.Invoke, cancellationToken)
                    .ConfigureAwait(false);
                if (!expanded.Success)
                {
                    return run.Fail(expanded, item);
                }

                var submenu = expanded.OpenedPopup is { } popup ? (nint)popup.Handle : nint.Zero;
                if (submenu == nint.Zero)
                {
                    submenu = (await _popups.WaitForNewPopupAsync(request.Window, before, wait, cancellationToken).ConfigureAwait(false))?.Handle
                              ?? nint.Zero;
                }

                run.Opened(new MenuPathStep(item, expanded.Ref ?? found.Ref, expanded.Method, submenu == nint.Zero ? null : (long)submenu),
                    found.Window, submenu, expanded.Warnings);
                continue;
            }

            last = await ActAsync(run, found.Ref!, ElementActionKind.Invoke, ElementActionKind.Click, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The action, or <paramref name="instead"/> when the item does not support it (PatternNotSupported).</summary>
    private async Task<ElementActionResult> ActAsync(
        MenuRun run, string itemRef, ElementActionKind action, ElementActionKind instead, CancellationToken cancellationToken)
    {
        var request = new ElementActionRequest(itemRef, action, AllowHostTarget: run.Request.AllowHostTarget, Timeout: run.Request.Timeout);
        var result = await _actions.ActAsync(request, cancellationToken).ConfigureAwait(false);
        return result.ErrorCode == nameof(UIElementErrorCode.PatternNotSupported)
            ? await _actions.ActAsync(request with { Action = instead }, cancellationToken).ConfigureAwait(false)
            : result;
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
            if (run.Submenu != nint.Zero)
            {
                places.Add((run.Submenu, null, ElementSearchScope.Window));
            }

            places.Add((run.PreviousWindow, run.PreviousRef, ElementSearchScope.Window));
            places.Add((request.Window, null, ElementSearchScope.WindowAndPopups));
        }

        FindElementsResult? failed = null;
        var incomplete = false;
        foreach (var (window, within, scope) in places)
        {
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
                    Timeout: request.Timeout,
                    Scope: scope), cancellationToken).ConfigureAwait(false);
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
                var refs = string.Join(", ", several.Select(e => $"{e.Ref} '{e.Name}'"));
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
            : $"in the menu '{request.Items[index - 1]}' opened";
        return FoundItem.Failed(run.Fail(
            incomplete ? nameof(UIElementErrorCode.OperationTimedOut) : nameof(UIElementErrorCode.ElementNotFound),
            $"No visible menu item '{item}' (item {index + 1} of {request.Items.Count}) was found {where}" +
            (incomplete ? " before the search ran out of time" : string.Empty) +
            ". Check the names with find_ui_elements (controlType MenuItem) or get_active_popups."));
    }

    /// <summary>A found item: its ref and the top-level window it is registered under, or the failure.</summary>
    private sealed record FoundItem(string? Ref, nint Window, ElementActionResult? Failure)
    {
        public static FoundItem Failed(ElementActionResult failure) => new(null, nint.Zero, failure);
    }

    /// <summary>One run of a path: what was opened so far.</summary>
    private sealed class MenuRun(MenuPathRequest request, Stopwatch stopwatch)
    {
        private const string Action = "menu_path";

        public MenuPathRequest Request { get; } = request;

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
            false, Action, WindowHandle: (long)Request.Window, Error: error + StillOpen(), ErrorCode: code, ElapsedMilliseconds: Elapsed)
        {
            Steps = Steps.Count == 0 ? null : [.. Steps]
        };

        /// <summary>The failure of an item's action, keeping what element_action reported.</summary>
        public ElementActionResult Fail(ElementActionResult failed, string item) => failed with
        {
            Action = Action,
            Error = $"Menu item '{item}' (item {Steps.Count + 1} of {Request.Items.Count}): {failed.Error}{StillOpen()}",
            Steps = Steps.Count == 0 ? null : [.. Steps],
            ElapsedMilliseconds = Elapsed
        };

        public ElementActionResult Succeed(ElementActionResult last) => last with
        {
            Action = Action,
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
