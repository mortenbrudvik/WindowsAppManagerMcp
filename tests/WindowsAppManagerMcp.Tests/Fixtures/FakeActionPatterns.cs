namespace WindowsAppManagerMcp.Tests.Fixtures;

// Pattern objects for the element_action tests (roadmap M3.6): their actions change their own state the way the application
// would, count their calls, and can fail or block on request. Nothing acts on a real element.

/// <summary>An Invoke pattern: counts invokes; <see cref="OnInvoke"/> runs inside (it can throw or block).</summary>
public sealed class FakeInvokePattern : IUiaInvokePattern
{
    public int Invokes { get; private set; }

    public Action? OnInvoke { get; set; }

    public void Invoke()
    {
        Invokes++;
        OnInvoke?.Invoke();
    }
}

/// <summary>A Toggle pattern that cycles Off, On (and Indeterminate when three-state) on every toggle.</summary>
public sealed class FakeCyclingTogglePattern(UiaToggleState state, bool threeState = false) : IUiaTogglePattern
{
    private UiaToggleState _state = state;
    private UiaToggleState _reported;
    private int _staleLeft;

    /// <summary>The state; after a toggle, the old one for the next <see cref="StaleReads"/> reads.</summary>
    public UiaToggleState ToggleState
    {
        get
        {
            if (_staleLeft > 0)
            {
                _staleLeft--;
                return _reported;
            }

            return _state;
        }
        set => _state = value;
    }

    public int Toggles { get; private set; }

    /// <summary>When set, toggling does nothing to the state (an application that refuses it silently).</summary>
    public bool Stuck { get; set; }

    /// <summary>
    /// How many reads after a toggle still report the state before it: a provider that toggles asynchronously (Chromium,
    /// Electron, WebView2) and updates its state later.
    /// </summary>
    public int StaleReads { get; set; }

    public void Toggle()
    {
        Toggles++;
        if (Stuck)
        {
            return;
        }

        var before = _state;
        _state = _state switch
        {
            UiaToggleState.Off => UiaToggleState.On,
            UiaToggleState.On => threeState ? UiaToggleState.Indeterminate : UiaToggleState.Off,
            _ => UiaToggleState.Off
        };
        if (StaleReads > 0)
        {
            _reported = before;
            _staleLeft = StaleReads;
        }
    }
}

/// <summary>A Value pattern: SetValue stores the value unless <see cref="Transform"/> changes it (a reformatting app).</summary>
public sealed class FakeValuePattern(string? value, bool isReadOnly = false) : IUiaValuePattern
{
    public string? Value { get; set; } = value;

    public bool IsReadOnly { get; set; } = isReadOnly;

    public List<string> SetValues { get; } = [];

    public Func<string, string?>? Transform { get; set; }

    public void SetValue(string value)
    {
        SetValues.Add(value);
        Value = Transform == null ? value : Transform(value);
    }
}

/// <summary>A RangeValue pattern.</summary>
public sealed class FakeRangeValuePattern(double value, double minimum = 0, double maximum = 100, bool isReadOnly = false) : IUiaRangeValuePattern
{
    public double Value { get; set; } = value;

    public UiaRangeValueState State => new(Value, minimum, maximum, 1, 10, isReadOnly);

    public void SetValue(double value) => Value = value;
}

/// <summary>A Scroll pattern: a page is 25 percent and a line 5 percent, clamped to 0-100.</summary>
public sealed class FakeScrollPattern(double vertical, bool verticallyScrollable = true, double horizontal = -1) : IUiaScrollPattern
{
    public double Vertical { get; set; } = vertical;

    public double Horizontal { get; set; } = horizontal;

    public List<(UiaScrollAmount Horizontal, UiaScrollAmount Vertical)> Scrolls { get; } = [];

    public UiaScrollState State => new(Horizontal, verticallyScrollable ? Vertical : -1, 100, 25, Horizontal >= 0, verticallyScrollable);

    public void Scroll(UiaScrollAmount horizontal, UiaScrollAmount vertical)
    {
        Scrolls.Add((horizontal, vertical));
        Vertical = Math.Clamp(Vertical + Step(vertical), 0, 100);
        if (Horizontal >= 0)
        {
            Horizontal = Math.Clamp(Horizontal + Step(horizontal), 0, 100);
        }
    }

    public void SetScrollPercent(double horizontalPercent, double verticalPercent) => Vertical = verticalPercent;

    private static double Step(UiaScrollAmount amount) => amount switch
    {
        UiaScrollAmount.LargeIncrement => 25,
        UiaScrollAmount.LargeDecrement => -25,
        UiaScrollAmount.SmallIncrement => 5,
        UiaScrollAmount.SmallDecrement => -5,
        _ => 0
    };
}

/// <summary>A ScrollItem pattern: <see cref="OnScrollIntoView"/> runs inside (the test makes the node visible).</summary>
public sealed class FakeScrollItemPattern : IUiaScrollItemPattern
{
    public int Calls { get; private set; }

    public Action? OnScrollIntoView { get; set; }

    public void ScrollIntoView()
    {
        Calls++;
        OnScrollIntoView?.Invoke();
    }
}

/// <summary>A Window pattern.</summary>
public sealed class FakeWindowPattern(UiaWindowVisualState state = UiaWindowVisualState.Normal) : IUiaWindowPattern
{
    public UiaWindowVisualState VisualState { get; set; } = state;

    public int Closes { get; private set; }

    public Action? OnClose { get; set; }

    public UiaWindowState State => new(false, false, true, true, VisualState, UiaWindowInteractionState.ReadyForUserInteraction);

    public void SetVisualState(UiaWindowVisualState state) => VisualState = state;

    public void Close()
    {
        Closes++;
        OnClose?.Invoke();
    }

    public bool WaitForInputIdle(int milliseconds) => true;
}

/// <summary>A LegacyIAccessible pattern with a default action.</summary>
public sealed class FakeLegacyPattern(string? defaultAction) : IUiaLegacyIAccessiblePattern
{
    public int DefaultActions { get; private set; }

    public string? DefaultAction => defaultAction;

    public void DoDefaultAction() => DefaultActions++;
}

/// <summary>A VirtualizedItem pattern: <see cref="OnRealize"/> runs inside (a virtual list puts the item in the tree).</summary>
public sealed class FakeVirtualizedItemPattern : IUiaVirtualizedItemPattern
{
    public int Realizes { get; private set; }

    public Action? OnRealize { get; set; }

    public void Realize()
    {
        Realizes++;
        OnRealize?.Invoke();
    }
}

/// <summary>An <see cref="IUiaActionRunner"/> that runs the action on the calling thread, or reports it pending or refused.</summary>
public sealed class InlineActionRunner : IUiaActionRunner
{
    public int Runs { get; private set; }

    /// <summary>When set, the action is not run and this status is returned (Pending: as if it blocked past the timeout).</summary>
    public UiaActionStatus? Outcome { get; set; }

    public Task<UiaActionOutcome> RunAsync(int targetProcessId, Action action, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Runs++;
        switch (Outcome)
        {
            case UiaActionStatus.Pending:
                return Task.FromResult(new UiaActionOutcome(UiaActionStatus.Pending));
            case UiaActionStatus.Refused:
                return Task.FromResult(new UiaActionOutcome(UiaActionStatus.Refused, Message: "Two earlier actions have not returned."));
        }

        try
        {
            action();
            return Task.FromResult(UiaActionOutcome.Returned);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new UiaActionOutcome(UiaActionStatus.Completed, ex));
        }
    }
}
