using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

// Phase 1 (resolve, read, validate, choose how to act) and the state reads and checks, all on the read worker's thread
public sealed partial class ElementActionService
{
    /// <summary>How many ancestors scroll looks through for a scrollable container.</summary>
    private const int MaxScrollAncestors = 16;

    /// <summary>How many ancestors of the focused element the focus check looks through for the target.</summary>
    private const int MaxFocusAncestors = 12;

    /// <summary>The actions that act on an element the user can see and use: refused for a disabled or offscreen one.</summary>
    private static bool NeedsUsableElement(ElementActionKind action) => action is not (ElementActionKind.ScrollIntoView
        or ElementActionKind.Scroll or ElementActionKind.Realize or ElementActionKind.WindowClose
        or ElementActionKind.WindowMinimize or ElementActionKind.WindowMaximize);

    /// <summary>Phase 1, on the worker: the hung check, the ref resolved, the state read, the checks, the plan.</summary>
    private ReadOutcome<PreparedElement> Prepare(ActionCall call, CancellationToken token)
    {
        if (UiaTargets.NotRespondingReason(_windows, call.Window, "its element was not acted on") is { } notResponding)
        {
            return Failed(call, nameof(UIElementErrorCode.NotResponding), notResponding);
        }

        if (!_tree.IsAvailable())
        {
            return Failed(call, nameof(UIElementErrorCode.AutomationUnavailable), "UI Automation is not available on this system.");
        }

        var resolution = _registry.Resolve(call.Ref!, token);
        if (resolution.Element is not { } resolved)
        {
            return Failed(call, resolution.ErrorCode.ToString(), resolution.Error ?? "The element ref could not be resolved.");
        }

        call.Ref = resolved.Ref;
        try
        {
            var node = resolved.Node;
            var element = resolved.Element;
            var identity = TryRead(() => _tree.ReadIdentity(node));
            var isPassword = identity?.IsPassword == true;
            var (before, fullValue) = ReadState(node, element, isPassword);
            call.Before = before;

            var prepared = new PreparedElement(resolved.Ref, node, element, identity, isPassword, before, fullValue);
            var request = call.Request;
            var properties = element.Properties;
            if (NeedsUsableElement(request.Action) && !properties.IsEnabled)
            {
                return Failed(call, nameof(ElementActionErrorCode.ElementNotEnabled),
                    $"{Name(element)} is disabled: it takes no input until the application enables it. Nothing was done. Check " +
                    "what it waits for (another field, a selection) and read it again.");
            }

            if (NeedsUsableElement(request.Action) && properties.IsOffscreen)
            {
                return Failed(call, nameof(ElementActionErrorCode.ElementOffscreen),
                    $"{Name(element)} is offscreen (scrolled out of view, or in a collapsed or hidden part). Nothing was done. Use " +
                    "action scroll_into_view first (realize for an item of a virtualized list), or open the part that holds it.");
            }

            return Choose(call, prepared) is { } refused ? ReadOutcome<PreparedElement>.Failed(refused) : ReadOutcome<PreparedElement>.Of(prepared);
        }
        catch (UiaElementUnavailableException ex)
        {
            return Failed(call, nameof(UIElementErrorCode.ElementStale),
                $"The element disappeared while it was read ({ex.Message}). Nothing was done. Find it again with find_ui_elements.");
        }
        catch (TimeoutException ex)
        {
            return Failed(call, nameof(UIElementErrorCode.OperationTimedOut),
                $"UI Automation did not answer in time ({ex.Message}). Nothing was done.");
        }
        catch (Exception ex)
        {
            return Failed(call, nameof(UIElementErrorCode.ElementAccessFailed), $"The element could not be read: {ex.Message} Nothing was done.");
        }
    }

    /// <summary>Chooses how to carry out the action and stores it in <paramref name="element"/>; the refusal, or null.</summary>
    private ElementActionResult? Choose(ActionCall call, PreparedElement element)
    {
        var request = call.Request;
        var node = element.Node;
        var before = element.Before;
        switch (request.Action)
        {
            case ElementActionKind.Invoke:
                if (Pattern<IUiaInvokePattern>(element, UiaPatterns.Invoke) is { } invoke)
                {
                    return element.Use(Plan.Pattern(ElementActionMethod.Invoke, invoke.Invoke));
                }

                if (DefaultAction(element) is { } legacy)
                {
                    return element.Use(Plan.Pattern(ElementActionMethod.LegacyIAccessible, legacy.DoDefaultAction));
                }

                return request.Fallback == ElementActionFallback.Mouse
                    ? element.Use(Plan.Click(MouseKind.Left))
                    : NotSupported(call, element, "invoke needs the Invoke pattern (or an MSAA default action under the UIA3 engine)",
                        "Try action click, which also uses Toggle, SelectionItem and ExpandCollapse, or pass fallback mouse for a guarded click.");

            case ElementActionKind.Click:
                if (Pattern<IUiaInvokePattern>(element, UiaPatterns.Invoke) is { } clickInvoke)
                {
                    return element.Use(Plan.Pattern(ElementActionMethod.Invoke, clickInvoke.Invoke));
                }

                if (Pattern<IUiaTogglePattern>(element, UiaPatterns.Toggle) is { } clickToggle)
                {
                    return element.Use(Plan.Pattern(ElementActionMethod.Toggle, clickToggle.Toggle));
                }

                if (Pattern<IUiaSelectionItemPattern>(element, UiaPatterns.SelectionItem) is { } clickSelect)
                {
                    return element.Use(Plan.Pattern(ElementActionMethod.SelectionItem, clickSelect.Select));
                }

                if (Pattern<IUiaExpandCollapsePattern>(element, UiaPatterns.ExpandCollapse) is { } clickExpand &&
                    before.ExpandCollapseState is { } expandState && expandState != UiaExpandCollapseState.LeafNode)
                {
                    return element.Use(Plan.Pattern(ElementActionMethod.ExpandCollapse,
                        expandState == UiaExpandCollapseState.Collapsed ? clickExpand.Expand : clickExpand.Collapse));
                }

                if (DefaultAction(element) is { } clickLegacy)
                {
                    return element.Use(Plan.Pattern(ElementActionMethod.LegacyIAccessible, clickLegacy.DoDefaultAction));
                }

                return request.Fallback == ElementActionFallback.Mouse
                    ? element.Use(Plan.Click(MouseKind.Left))
                    : NotSupported(call, element, "click needs one of the Invoke, Toggle, SelectionItem or ExpandCollapse patterns",
                        "Pass fallback mouse for a guarded mouse click at the element's click point.");

            case ElementActionKind.DoubleClick:
                return element.Use(Plan.Click(MouseKind.Double));

            case ElementActionKind.RightClick:
                return element.Use(Plan.Click(MouseKind.Right));

            case ElementActionKind.Focus:
                return element.Element.IsKeyboardFocusable
                    ? element.Use(new Plan(PlanKind.Focus, ElementActionMethod.Focus))
                    : NotSupported(call, element, "focus needs a keyboard-focusable element", "Focus the field or control inside it instead.");

            case ElementActionKind.SetValue:
            case ElementActionKind.AppendText:
                return ChooseValue(call, element);

            case ElementActionKind.Toggle:
            {
                if (Pattern<IUiaTogglePattern>(element, UiaPatterns.Toggle) is not { } toggle)
                {
                    return NotSupported(call, element, "toggle needs the Toggle pattern",
                        "Use action click (with fallback mouse when it has no pattern either).");
                }

                var goal = ParseToggleState(request.Value);
                if (goal != null && before.ToggleState == goal)
                {
                    return element.Use(new Plan(PlanKind.Nothing, ElementActionMethod.None));
                }

                var plan = new Plan(PlanKind.Pattern, ElementActionMethod.Toggle);
                plan.Act = () =>
                {
                    // Up to three toggles: a three-state check box can need two to reach a state
                    for (var i = 0; i < 3; i++)
                    {
                        toggle.Toggle();
                        plan.Toggles++;
                        if (goal == null || toggle.ToggleState == goal)
                        {
                            break;
                        }
                    }
                };
                return element.Use(plan);
            }

            case ElementActionKind.Select:
            case ElementActionKind.AddToSelection:
            case ElementActionKind.RemoveFromSelection:
            {
                if (Pattern<IUiaSelectionItemPattern>(element, UiaPatterns.SelectionItem) is not { } selection)
                {
                    return request.Fallback == ElementActionFallback.Mouse
                        ? element.Use(Plan.Click(MouseKind.Left))
                        : NotSupported(call, element, $"{call.Action} needs the SelectionItem pattern",
                            request.Action == ElementActionKind.Select
                                ? "Pass fallback mouse for a guarded click on the item."
                                : "Select it with a click instead (action click).");
                }

                Action act = request.Action switch
                {
                    ElementActionKind.AddToSelection => selection.AddToSelection,
                    ElementActionKind.RemoveFromSelection => selection.RemoveFromSelection,
                    _ => selection.Select
                };
                return element.Use(Plan.Pattern(ElementActionMethod.SelectionItem, act));
            }

            case ElementActionKind.Expand:
            case ElementActionKind.Collapse:
            {
                if (Pattern<IUiaExpandCollapsePattern>(element, UiaPatterns.ExpandCollapse) is not { } expandCollapse)
                {
                    return NotSupported(call, element, $"{call.Action} needs the ExpandCollapse pattern",
                        "Use action click (with fallback mouse when it has no pattern either).");
                }

                var expand = request.Action == ElementActionKind.Expand;
                if (before.ExpandCollapseState == UiaExpandCollapseState.LeafNode)
                {
                    return NotSupported(call, element, $"{Name(element.Element)} is a leaf node: it has nothing to {call.Action}", null);
                }

                if (before.ExpandCollapseState == (expand ? UiaExpandCollapseState.Expanded : UiaExpandCollapseState.Collapsed))
                {
                    return element.Use(new Plan(PlanKind.Nothing, ElementActionMethod.None));
                }

                return element.Use(Plan.Pattern(ElementActionMethod.ExpandCollapse, expand ? expandCollapse.Expand : expandCollapse.Collapse));
            }

            case ElementActionKind.ScrollIntoView:
                return Pattern<IUiaScrollItemPattern>(element, UiaPatterns.ScrollItem) is { } scrollItem
                    ? element.Use(Plan.Pattern(ElementActionMethod.ScrollItem, scrollItem.ScrollIntoView))
                    : NotSupported(call, element, "scroll_into_view needs the ScrollItem pattern",
                        "Scroll its container instead (action scroll on the element or the list), or realize a virtualized item.");

            case ElementActionKind.Scroll:
                return ChooseScroll(call, element, node);

            case ElementActionKind.SetRangeValue:
            {
                if (Pattern<IUiaRangeValuePattern>(element, UiaPatterns.RangeValue) is not { } range)
                {
                    return NotSupported(call, element, "set_range_value needs the RangeValue pattern", "Use set_value for a text box.");
                }

                var state = range.State;
                if (state.IsReadOnly)
                {
                    return Refuse(call, nameof(ElementActionErrorCode.ElementReadOnly), $"{Name(element.Element)} is read-only. Nothing was done.");
                }

                TryParseNumber(request.Value, out var number);
                if (number < state.Minimum || number > state.Maximum)
                {
                    return Refuse(call, ToolErrorReporting.InvalidArgumentsErrorCode,
                        $"{number} is outside the element's range {state.Minimum} to {state.Maximum}. Nothing was done.");
                }

                element.RangeTarget = number;
                return Math.Abs(state.Value - number) <= RangeTolerance(state)
                    ? element.Use(new Plan(PlanKind.Nothing, ElementActionMethod.None))
                    : element.Use(Plan.Pattern(ElementActionMethod.RangeValue, () => range.SetValue(number)));
            }

            case ElementActionKind.Realize:
                if (Pattern<IUiaVirtualizedItemPattern>(element, UiaPatterns.VirtualizedItem) is { } virtualized)
                {
                    return element.Use(Plan.Pattern(ElementActionMethod.VirtualizedItem, virtualized.Realize));
                }

                element.Warnings.Add("The element is not a virtualized item (it supports no VirtualizedItem pattern): it is real already.");
                return element.Use(new Plan(PlanKind.Nothing, ElementActionMethod.None));

            case ElementActionKind.WindowClose:
            case ElementActionKind.WindowMinimize:
            case ElementActionKind.WindowMaximize:
                return ChooseWindow(call, element);
        }

        return Refuse(call, ToolErrorReporting.InvalidArgumentsErrorCode, $"Unknown action {call.Action}. Nothing was done.");
    }

    private ElementActionResult? ChooseValue(ActionCall call, PreparedElement element)
    {
        var request = call.Request;
        var value = Pattern<IUiaValuePattern>(element, UiaPatterns.Value);
        if (value != null && value.IsReadOnly)
        {
            return Refuse(call, nameof(ElementActionErrorCode.ElementReadOnly),
                $"{Name(element.Element)} is read-only (its Value pattern says so). Nothing was done.");
        }

        if (request.Mode != ElementValueMode.Value)
        {
            return element.Use(new Plan(PlanKind.Keyboard, ElementActionMethod.Keyboard));
        }

        if (value == null)
        {
            return NotSupported(call, element, $"{call.Action} with mode value needs the Value pattern",
                "Use mode type: the server focuses the element and types the text, with every input check.");
        }

        var text = request.Value ?? string.Empty;
        if (request.Action == ElementActionKind.SetValue)
        {
            return element.Use(Plan.Pattern(ElementActionMethod.Value, () => value.SetValue(text)));
        }

        if (element.IsPassword)
        {
            return Refuse(call, ToolErrorReporting.InvalidArgumentsErrorCode,
                "append_text with mode value reads the field's value first, and a password field's value is never read. Nothing " +
                "was done. Use mode type, or set_value with the whole text.");
        }

        // The current value is read again on the action's thread, whole (the state shows at most 200 characters)
        return element.Use(Plan.Pattern(ElementActionMethod.Value, () => value.SetValue((value.Value ?? string.Empty) + text)));
    }

    private ElementActionResult? ChooseScroll(ActionCall call, PreparedElement element, IUiaNode node)
    {
        var request = call.Request;
        IUiaScrollPattern? scroll = null;
        IUiaNode? container = null;
        UiaElement? containerElement = null;
        if (Pattern<IUiaScrollPattern>(element, UiaPatterns.Scroll) is { } own)
        {
            (scroll, container) = (own, node);
        }
        else
        {
            var current = node;
            for (var level = 0; level < MaxScrollAncestors && _tree.GetParent(current) is { } parent; level++)
            {
                var read = _tree.ReadElement(parent);
                if ((read.Patterns & UiaPatterns.Scroll) != 0 && _tree.GetPattern<IUiaScrollPattern>(parent) is { } found)
                {
                    (scroll, container, containerElement) = (found, parent, read);
                    break;
                }

                current = parent;
            }
        }

        if (scroll == null || container == null)
        {
            return NotSupported(call, element, "scroll needs the Scroll pattern on the element or one of its ancestors",
                "Use the scroll tool (the mouse wheel) over the element's window instead.");
        }

        var state = scroll.State;
        element.ScrollContainer = container;
        element.ScrollBefore = state;
        if (containerElement != null)
        {
            element.ContainerRef = TryRead(() => _registry.Register(call.Window, containerElement, new ElementOrigin(_registry.NextGeneration())));
        }

        var direction = request.Direction!.Value;
        var vertical = direction is ElementScrollDirection.Up or ElementScrollDirection.Down;
        if (!(vertical ? state.VerticallyScrollable : state.HorizontallyScrollable))
        {
            element.Warnings.Add($"The container does not scroll {(vertical ? "vertically" : "horizontally")}: all of its content fits.");
            return element.Use(new Plan(PlanKind.Nothing, ElementActionMethod.None));
        }

        if (AtEnd(direction, state))
        {
            return element.Use(new Plan(PlanKind.Nothing, ElementActionMethod.None));
        }

        var forward = direction is ElementScrollDirection.Down or ElementScrollDirection.Right;
        var amount = request.Amount == ElementScrollAmount.Line
            ? forward ? UiaScrollAmount.SmallIncrement : UiaScrollAmount.SmallDecrement
            : forward ? UiaScrollAmount.LargeIncrement : UiaScrollAmount.LargeDecrement;
        return element.Use(Plan.Pattern(ElementActionMethod.Scroll, vertical
            ? () => scroll.Scroll(UiaScrollAmount.NoAmount, amount)
            : () => scroll.Scroll(amount, UiaScrollAmount.NoAmount)));
    }

    private ElementActionResult? ChooseWindow(ActionCall call, PreparedElement element)
    {
        var request = call.Request;
        if (Pattern<IUiaWindowPattern>(element, UiaPatterns.Window) is not { } window)
        {
            return NotSupported(call, element, $"{call.Action} needs the Window pattern", "Act on the window element itself.");
        }

        // A top-level window has tools of its own, with the close guards (shell and host windows, confirmation, verified close)
        // and verified state changes: those must not be bypassed through its Window pattern
        var handle = element.Identity?.NativeWindowHandle ?? nint.Zero;
        if (handle != nint.Zero && (handle == call.Window || _windows.GetAncestor(handle, WindowAncestor.Root) == handle))
        {
            return Refuse(call, ToolErrorReporting.InvalidArgumentsErrorCode,
                $"The element is the top-level window {(long)handle}. " + (request.Action == ElementActionKind.WindowClose
                    ? $"Close it with close_window (handle {(long)handle}), which refuses the shell and your own host window, asks the user " +
                      "where needed and verifies the close."
                    : $"Use set_window_state (handle {(long)handle}), which checks the policy and verifies the new state.") +
                " window_* actions are for windows drawn inside an application (an MDI child, an in-app dialog). Nothing was done.");
        }

        var state = window.State;
        switch (request.Action)
        {
            case ElementActionKind.WindowClose:
                return element.Use(Plan.Pattern(ElementActionMethod.Window, window.Close));
            case ElementActionKind.WindowMinimize when !state.CanMinimize:
            case ElementActionKind.WindowMaximize when !state.CanMaximize:
                return NotSupported(call, element, $"the window cannot be {(request.Action == ElementActionKind.WindowMinimize ? "minimized" : "maximized")}", null);
        }

        var goal = request.Action == ElementActionKind.WindowMinimize ? UiaWindowVisualState.Minimized : UiaWindowVisualState.Maximized;
        return state.VisualState == goal
            ? element.Use(new Plan(PlanKind.Nothing, ElementActionMethod.None))
            : element.Use(Plan.Pattern(ElementActionMethod.Window, () => window.SetVisualState(goal)));
    }

    /// <summary>The element's state, read through its patterns, and its whole value (null for a password field).</summary>
    private (ElementState State, string? FullValue) ReadState(IUiaNode node, UiaElement element, bool isPassword)
    {
        var properties = element.Properties;
        var patterns = element.Patterns;
        var interaction = TryRead(() => _tree.ReadInteraction(node));

        // A password field's value is never read, not even to mask it
        var fullValue = !isPassword && (patterns & UiaPatterns.Value) != 0 ? TryRead(() => _tree.GetPattern<IUiaValuePattern>(node)?.Value) : null;
        var (value, truncated) = ElementInspectionService.Truncate(fullValue);
        var scroll = (patterns & UiaPatterns.Scroll) != 0 ? TryRead(() => _tree.GetPattern<IUiaScrollPattern>(node)?.State) : null;
        var state = new ElementState(
            Name: string.IsNullOrEmpty(properties.Name) ? null : properties.Name,
            IsEnabled: properties.IsEnabled,
            IsOffscreen: properties.IsOffscreen,
            HasKeyboardFocus: interaction?.HasKeyboardFocus,
            Value: value,
            ValueTruncated: truncated ? true : null,
            IsPassword: isPassword ? true : null,
            ToggleState: (patterns & UiaPatterns.Toggle) != 0 ? TryState(() => _tree.GetPattern<IUiaTogglePattern>(node)?.ToggleState) : null,
            ExpandCollapseState: (patterns & UiaPatterns.ExpandCollapse) != 0
                ? TryState(() => _tree.GetPattern<IUiaExpandCollapsePattern>(node)?.ExpandCollapseState)
                : null,
            IsSelected: (patterns & UiaPatterns.SelectionItem) != 0 ? TryState(() => _tree.GetPattern<IUiaSelectionItemPattern>(node)?.IsSelected) : null,
            RangeValue: (patterns & UiaPatterns.RangeValue) != 0 ? TryState(() => _tree.GetPattern<IUiaRangeValuePattern>(node)?.State.Value) : null,
            HorizontalScrollPercent: Percent(scroll?.HorizontallyScrollable, scroll?.HorizontalPercent),
            VerticalScrollPercent: Percent(scroll?.VerticallyScrollable, scroll?.VerticalPercent),
            WindowVisualState: (patterns & UiaPatterns.Window) != 0 ? TryState(() => _tree.GetPattern<IUiaWindowPattern>(node)?.State.VisualState) : null);
        return (state, fullValue);
    }

    /// <summary>Phase 3, on the worker: the element read again (or gone), with its scroll container's position.</summary>
    private ReadOutcome<AfterRead> ReadAfter(ActionCall call, PreparedElement element)
    {
        try
        {
            UiaElement fresh;
            try
            {
                fresh = _tree.ReadElement(element.Node);
            }
            catch (UiaElementUnavailableException)
            {
                return ReadOutcome<AfterRead>.Of(new AfterRead(null, null, null, Gone: true, Warning: null));
            }

            var (state, fullValue) = ReadState(element.Node, fresh, element.IsPassword);
            var scroll = element.ScrollContainer is { } container
                ? TryRead(() => _tree.GetPattern<IUiaScrollPattern>(container)?.State)
                : null;
            return ReadOutcome<AfterRead>.Of(new AfterRead(state, fullValue, scroll, Gone: false, Warning: null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadOutcome<AfterRead>.Failed(call.Fail(nameof(UIElementErrorCode.ElementAccessFailed),
                $"The action was done, but the element's state could not be read afterwards: {ex.Message}"));
        }
    }

    /// <summary>
    /// Whether the state after the action shows its effect: true, false, or null when the action has no state to check (invoke,
    /// a mouse click) or the state could not be read (a password field's value).
    /// </summary>
    private static bool? Verify(ElementActionRequest request, PreparedElement element, AfterRead after)
    {
        if (after.Gone)
        {
            return request.Action == ElementActionKind.WindowClose ? true : null;
        }

        if (after.State is not { } state)
        {
            return null;
        }

        var before = element.Before;
        var method = element.Plan.Method;
        return request.Action switch
        {
            ElementActionKind.Click => method switch
            {
                ElementActionMethod.Toggle => state.ToggleState != before.ToggleState,
                ElementActionMethod.SelectionItem => state.IsSelected == true,
                ElementActionMethod.ExpandCollapse => state.ExpandCollapseState != before.ExpandCollapseState,
                _ => null
            },
            ElementActionKind.Focus => state.HasKeyboardFocus,
            ElementActionKind.SetValue => element.IsPassword || after.FullValue == null ? null : after.FullValue == (request.Value ?? string.Empty),
            ElementActionKind.AppendText => element.IsPassword || after.FullValue == null || element.FullValue == null
                ? null
                : after.FullValue == element.FullValue + request.Value,
            ElementActionKind.Toggle => ParseToggleState(request.Value) is { } goal
                ? state.ToggleState == goal
                : state.ToggleState != before.ToggleState,
            ElementActionKind.Select or ElementActionKind.AddToSelection => method == ElementActionMethod.Mouse ? null : state.IsSelected == true,
            ElementActionKind.RemoveFromSelection => state.IsSelected == false,
            ElementActionKind.Expand => state.ExpandCollapseState is UiaExpandCollapseState.Expanded or UiaExpandCollapseState.PartiallyExpanded,
            ElementActionKind.Collapse => state.ExpandCollapseState == UiaExpandCollapseState.Collapsed,
            ElementActionKind.ScrollIntoView => !state.IsOffscreen,
            ElementActionKind.Scroll => ScrollVerified(request, element, after),
            ElementActionKind.SetRangeValue => state.RangeValue is { } value && element.RangeTarget is { } target
                ? Math.Abs(value - target) <= RangeTolerance(target)
                : null,
            ElementActionKind.WindowMinimize => state.WindowVisualState == UiaWindowVisualState.Minimized,
            ElementActionKind.WindowMaximize => state.WindowVisualState == UiaWindowVisualState.Maximized,
            ElementActionKind.WindowClose => false,
            _ => null
        };
    }

    private static bool? ScrollVerified(ElementActionRequest request, PreparedElement element, AfterRead after)
    {
        if (after.Scroll is not { } now || element.ScrollBefore is not { } then || request.Direction is not { } direction)
        {
            return null;
        }

        var vertical = direction is ElementScrollDirection.Up or ElementScrollDirection.Down;
        var moved = vertical ? now.VerticalPercent != then.VerticalPercent : now.HorizontalPercent != then.HorizontalPercent;
        return moved || AtEnd(direction, now);
    }

    private static ElementScrollPosition? ScrollPosition(ElementActionRequest request, PreparedElement element, AfterRead after)
    {
        if ((after.Scroll ?? element.ScrollBefore) is not { } state || request.Direction is not { } direction)
        {
            return null;
        }

        return new ElementScrollPosition(
            Percent(state.HorizontallyScrollable, state.HorizontalPercent),
            Percent(state.VerticallyScrollable, state.VerticalPercent),
            AtEnd(direction, state),
            element.ContainerRef);
    }

    /// <summary>True when the container cannot scroll further in <paramref name="direction"/> (or not on that axis at all).</summary>
    private static bool AtEnd(ElementScrollDirection direction, UiaScrollState state) => direction switch
    {
        ElementScrollDirection.Down => !state.VerticallyScrollable || state.VerticalPercent >= 99.99,
        ElementScrollDirection.Up => !state.VerticallyScrollable || state.VerticalPercent <= 0.01,
        ElementScrollDirection.Right => !state.HorizontallyScrollable || state.HorizontalPercent >= 99.99,
        _ => !state.HorizontallyScrollable || state.HorizontalPercent <= 0.01
    };

    private static double? Percent(bool? scrollable, double? percent) =>
        scrollable == true && percent is { } value && value >= 0 ? Math.Round(value, 2) : null;

    private static double RangeTolerance(UiaRangeValueState state) => Math.Max(1e-9, (state.Maximum - state.Minimum) * 1e-6);

    private static double RangeTolerance(double goal) => Math.Max(1e-9, Math.Abs(goal) * 1e-6);

    private static string UnverifiedWarning(ElementActionRequest request, PreparedElement element) => request.Action switch
    {
        ElementActionKind.SetValue or ElementActionKind.AppendText when request.Mode == ElementValueMode.Value =>
            "The value read back is not the value set: the application may reformat it, or ignore the Value pattern (some web " +
            "and React inputs do). Read it again, or set it with mode type.",
        ElementActionKind.SetValue or ElementActionKind.AppendText =>
            "The value read back is not the text typed: the field may limit or reformat its input. Read it again before you go on.",
        ElementActionKind.Focus => "Keyboard focus is not on the element after SetFocus. Click it first (action click with fallback mouse).",
        _ => $"The state read after {ToolEnum<ElementActionKind>.Name(request.Action)} does not show its effect yet; the application " +
             "may still be updating. Read the element again before you repeat the action."
    };

    /// <summary>The click point now, on the worker: UI Automation's, else the centre of the bounds; null when offscreen.</summary>
    private ReadOutcome<UiaPoint?> ClickPointOf(IUiaNode node)
    {
        try
        {
            var fresh = _tree.ReadElement(node);
            if (fresh.Properties.IsOffscreen || fresh.Properties.Bounds.ToBounds() is not { Width: > 0, Height: > 0 } bounds)
            {
                return ReadOutcome<UiaPoint?>.Of(null);
            }

            var point = TryState(() => _tree.GetClickablePoint(node));
            return ReadOutcome<UiaPoint?>.Of(point ?? new UiaPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        }
        catch (UiaElementUnavailableException)
        {
            return ReadOutcome<UiaPoint?>.Of(null);
        }
    }

    /// <summary>On the worker: whether keyboard focus is on <paramref name="node"/> or on an element inside it.</summary>
    private ReadOutcome<bool> HasFocus(IUiaNode node)
    {
        try
        {
            var focused = _tree.GetFocusedElement();
            for (var level = 0; focused != null && level <= MaxFocusAncestors; level++)
            {
                if (_tree.AreSameElement(focused, node))
                {
                    return ReadOutcome<bool>.Of(true);
                }

                focused = _tree.GetParent(focused);
            }

            return ReadOutcome<bool>.Of(false);
        }
        catch (Exception ex) when (ex is UiaElementUnavailableException or InvalidOperationException)
        {
            return ReadOutcome<bool>.Of(false);
        }
    }

    private TPattern? Pattern<TPattern>(PreparedElement element, UiaPatterns flag) where TPattern : class, IUiaPattern =>
        (element.Element.Patterns & flag) != 0 ? _tree.GetPattern<TPattern>(element.Node) : null;

    /// <summary>The MSAA default action, when the element has one (UIA3 engine only).</summary>
    private IUiaLegacyIAccessiblePattern? DefaultAction(PreparedElement element)
    {
        if (Pattern<IUiaLegacyIAccessiblePattern>(element, UiaPatterns.LegacyIAccessible) is not { } legacy ||
            TryRead(() => legacy.State) is not { DefaultAction: { Length: > 0 } name })
        {
            return null;
        }

        element.Warnings.Add($"The element has no Invoke pattern; its MSAA default action ('{name}') was used.");
        return legacy;
    }

    private static ElementActionResult NotSupported(ActionCall call, PreparedElement element, string what, string? hint)
    {
        var patterns = UiaPatternNames.All(element.Element.Patterns);
        return call.Fail(nameof(ElementActionErrorCode.PatternNotSupported),
            $"{what}, and {Name(element.Element)} supports {(patterns.Count == 0 ? "no pattern" : string.Join(", ", patterns))}. " +
            $"Nothing was done.{(hint == null ? string.Empty : " " + hint)}");
    }

    private static ElementActionResult Refuse(ActionCall call, string code, string error) => call.Fail(code, error);

    private static ReadOutcome<PreparedElement> Failed(ActionCall call, string code, string error) =>
        ReadOutcome<PreparedElement>.Failed(call.Fail(code, error));

    private static string Name(UiaElement element) =>
        string.IsNullOrEmpty(element.Properties.Name)
            ? $"The {element.Properties.ControlType}"
            : $"{element.Properties.ControlType} '{element.Properties.Name}'";

    /// <summary>A read that may fail without failing the action: null then. A provider timeout is thrown.</summary>
    private static T? TryRead<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return null;
        }
    }

    /// <inheritdoc cref="TryRead{T}"/>
    private static T? TryState<T>(Func<T?> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            return null;
        }
    }

    private enum PlanKind
    {
        /// <summary>The element is in the asked-for state already.</summary>
        Nothing,

        /// <summary>A pattern call on the action runner.</summary>
        Pattern,

        /// <summary>Activation, then SetFocus on the action runner.</summary>
        Focus,

        /// <summary>Activation, then a guarded click.</summary>
        Mouse,

        /// <summary>Activation, focus, then guarded keys.</summary>
        Keyboard
    }

    private enum MouseKind
    {
        Left,
        Right,
        Double
    }

    /// <summary>How the action is carried out.</summary>
    private sealed class Plan(PlanKind kind, ElementActionMethod method)
    {
        public PlanKind Kind { get; } = kind;

        public ElementActionMethod Method { get; } = method;

        /// <summary>The pattern call, for <see cref="PlanKind.Pattern"/>.</summary>
        public Action? Act { get; set; }

        public MouseKind Mouse { get; private init; }

        /// <summary>How many toggles the toggle loop made (set on the action's thread).</summary>
        public int Toggles { get; set; }

        public static Plan Pattern(ElementActionMethod method, Action act) => new(PlanKind.Pattern, method) { Act = act };

        public static Plan Click(MouseKind mouse) => new(PlanKind.Mouse, ElementActionMethod.Mouse) { Mouse = mouse };
    }

    /// <summary>The element as phase 1 found it, and how to act on it.</summary>
    private sealed class PreparedElement(
        string elementRef, IUiaNode node, UiaElement element, UiaNodeIdentity? identity, bool isPassword, ElementState before,
        string? fullValue)
    {
        public string Ref { get; } = elementRef;

        public IUiaNode Node { get; } = node;

        public UiaElement Element { get; } = element;

        public UiaNodeIdentity? Identity { get; } = identity;

        public bool IsPassword { get; } = isPassword;

        public ElementState Before { get; } = before;

        /// <summary>The whole value before the action (null for a password field, or without a Value pattern).</summary>
        public string? FullValue { get; } = fullValue;

        public int ProcessId => Identity?.ProcessId ?? 0;

        public Plan Plan { get; private set; } = new(PlanKind.Nothing, ElementActionMethod.None);

        public IUiaNode? ScrollContainer { get; set; }

        public UiaScrollState? ScrollBefore { get; set; }

        public string? ContainerRef { get; set; }

        public double? RangeTarget { get; set; }

        public List<string> Warnings { get; } = [];

        /// <summary>Stores the plan; null, for <see cref="Choose"/>'s "no refusal".</summary>
        public ElementActionResult? Use(Plan plan)
        {
            Plan = plan;
            return null;
        }
    }

    /// <summary>The state after the action.</summary>
    /// <param name="Gone">The element no longer exists.</param>
    /// <param name="Warning">Why it could not be read, when it could not.</param>
    private sealed record AfterRead(ElementState? State, string? FullValue, UiaScrollState? Scroll, bool Gone, string? Warning);
}
