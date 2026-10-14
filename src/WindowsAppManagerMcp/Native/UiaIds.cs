using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// The UI Automation property, pattern and control type ids of UIAutomationClient.h (UIA_*PropertyId, UIA_*PatternId,
/// UIA_*ControlTypeId) that the UIA3 engine uses. The managed client's AutomationProperty, AutomationPattern and
/// ControlType objects carry the same ids, which UiaIdsTests checks.
/// </summary>
public static class UiaIds
{
    // Properties
    public const int RuntimeIdProperty = 30000;
    public const int BoundingRectangleProperty = 30001;
    public const int ProcessIdProperty = 30002;
    public const int ControlTypeProperty = 30003;
    public const int LocalizedControlTypeProperty = 30004;
    public const int NameProperty = 30005;
    public const int AcceleratorKeyProperty = 30006;
    public const int AccessKeyProperty = 30007;
    public const int HasKeyboardFocusProperty = 30008;
    public const int IsKeyboardFocusableProperty = 30009;
    public const int IsEnabledProperty = 30010;
    public const int AutomationIdProperty = 30011;
    public const int ClassNameProperty = 30012;
    public const int HelpTextProperty = 30013;
    public const int IsPasswordProperty = 30019;
    public const int NativeWindowHandleProperty = 30020;
    public const int IsOffscreenProperty = 30022;
    public const int FrameworkIdProperty = 30024;
    public const int IsExpandCollapsePatternAvailableProperty = 30028;
    public const int IsGridPatternAvailableProperty = 30030;
    public const int IsInvokePatternAvailableProperty = 30031;
    public const int IsRangeValuePatternAvailableProperty = 30033;
    public const int IsScrollPatternAvailableProperty = 30034;
    public const int IsScrollItemPatternAvailableProperty = 30035;
    public const int IsSelectionItemPatternAvailableProperty = 30036;
    public const int IsTablePatternAvailableProperty = 30038;
    public const int IsTextPatternAvailableProperty = 30040;
    public const int IsTogglePatternAvailableProperty = 30041;
    public const int IsValuePatternAvailableProperty = 30043;
    public const int IsWindowPatternAvailableProperty = 30044;
    public const int IsLegacyIAccessiblePatternAvailableProperty = 30090;
    public const int IsItemContainerPatternAvailableProperty = 30108;
    public const int IsVirtualizedItemPatternAvailableProperty = 30109;
    public const int ValueValueProperty = 30045;
    public const int ToggleToggleStateProperty = 30086;

    // Patterns
    public const int InvokePattern = 10000;
    public const int ValuePattern = 10002;
    public const int RangeValuePattern = 10003;
    public const int ScrollPattern = 10004;
    public const int ExpandCollapsePattern = 10005;
    public const int GridPattern = 10006;
    public const int GridItemPattern = 10007;
    public const int WindowPattern = 10009;
    public const int SelectionItemPattern = 10010;
    public const int TablePattern = 10012;
    public const int TextPattern = 10014;
    public const int TogglePattern = 10015;
    public const int ScrollItemPattern = 10017;
    public const int LegacyIAccessiblePattern = 10018;
    public const int ItemContainerPattern = 10019;
    public const int VirtualizedItemPattern = 10020;

    /// <summary>The pattern availability properties, with the pattern each one reports.</summary>
    public static IReadOnlyList<(int Property, UiaPatterns Pattern)> PatternAvailability { get; } =
    [
        (IsInvokePatternAvailableProperty, UiaPatterns.Invoke),
        (IsTogglePatternAvailableProperty, UiaPatterns.Toggle),
        (IsValuePatternAvailableProperty, UiaPatterns.Value),
        (IsSelectionItemPatternAvailableProperty, UiaPatterns.SelectionItem),
        (IsExpandCollapsePatternAvailableProperty, UiaPatterns.ExpandCollapse),
        (IsRangeValuePatternAvailableProperty, UiaPatterns.RangeValue),
        (IsScrollItemPatternAvailableProperty, UiaPatterns.ScrollItem),
        (IsScrollPatternAvailableProperty, UiaPatterns.Scroll),
        (IsWindowPatternAvailableProperty, UiaPatterns.Window),
        (IsTextPatternAvailableProperty, UiaPatterns.Text),
        (IsGridPatternAvailableProperty, UiaPatterns.Grid),
        (IsTablePatternAvailableProperty, UiaPatterns.Table),
        (IsItemContainerPatternAvailableProperty, UiaPatterns.ItemContainer),
        (IsVirtualizedItemPatternAvailableProperty, UiaPatterns.VirtualizedItem),
        (IsLegacyIAccessiblePatternAvailableProperty, UiaPatterns.LegacyIAccessible),
    ];

    /// <summary>The pattern id of a pattern interface of the seam, or null for one this engine does not map.</summary>
    public static int? PatternId(Type patternInterface) => patternInterface.Name switch
    {
        nameof(IUiaInvokePattern) => InvokePattern,
        nameof(IUiaTogglePattern) => TogglePattern,
        nameof(IUiaValuePattern) => ValuePattern,
        nameof(IUiaExpandCollapsePattern) => ExpandCollapsePattern,
        nameof(IUiaSelectionItemPattern) => SelectionItemPattern,
        nameof(IUiaScrollItemPattern) => ScrollItemPattern,
        nameof(IUiaScrollPattern) => ScrollPattern,
        nameof(IUiaRangeValuePattern) => RangeValuePattern,
        nameof(IUiaWindowPattern) => WindowPattern,
        nameof(IUiaTextPattern) => TextPattern,
        nameof(IUiaGridPattern) => GridPattern,
        nameof(IUiaGridItemPattern) => GridItemPattern,
        nameof(IUiaTablePattern) => TablePattern,
        nameof(IUiaItemContainerPattern) => ItemContainerPattern,
        nameof(IUiaVirtualizedItemPattern) => VirtualizedItemPattern,
        nameof(IUiaLegacyIAccessiblePattern) => LegacyIAccessiblePattern,
        _ => null
    };

    /// <summary>The property id of a property a <see cref="UiaCondition"/> can compare.</summary>
    public static int PropertyId(UiaProperty property) => property switch
    {
        UiaProperty.Name => NameProperty,
        UiaProperty.AutomationId => AutomationIdProperty,
        UiaProperty.ClassName => ClassNameProperty,
        UiaProperty.ControlType => ControlTypeProperty,
        UiaProperty.FrameworkId => FrameworkIdProperty,
        UiaProperty.ProcessId => ProcessIdProperty,
        UiaProperty.IsEnabled => IsEnabledProperty,
        UiaProperty.IsOffscreen => IsOffscreenProperty,
        UiaProperty.IsKeyboardFocusable => IsKeyboardFocusableProperty,
        UiaProperty.RuntimeId => RuntimeIdProperty,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "Not a property a condition can compare.")
    };
}
