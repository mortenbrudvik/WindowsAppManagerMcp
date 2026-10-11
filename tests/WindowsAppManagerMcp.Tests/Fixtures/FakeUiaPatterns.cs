namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Pattern objects for <see cref="FakeUiaNode.WithPattern{TPattern}"/>: their state is what the test sets, and their
/// actions only count calls. Nothing acts on a real element.
/// </summary>
public sealed class FakeTogglePattern(UiaToggleState state) : IUiaTogglePattern
{
    public UiaToggleState ToggleState { get; set; } = state;

    public int Toggles { get; private set; }

    public void Toggle() => Toggles++;
}

/// <inheritdoc cref="FakeTogglePattern"/>
public sealed class FakeExpandCollapsePattern(UiaExpandCollapseState state) : IUiaExpandCollapsePattern
{
    public UiaExpandCollapseState ExpandCollapseState { get; set; } = state;

    public void Expand() => ExpandCollapseState = UiaExpandCollapseState.Expanded;

    public void Collapse() => ExpandCollapseState = UiaExpandCollapseState.Collapsed;
}

/// <inheritdoc cref="FakeTogglePattern"/>
public sealed class FakeSelectionItemPattern(bool isSelected) : IUiaSelectionItemPattern
{
    public bool IsSelected { get; set; } = isSelected;

    public void Select() => IsSelected = true;

    public void AddToSelection() => IsSelected = true;

    public void RemoveFromSelection() => IsSelected = false;
}
