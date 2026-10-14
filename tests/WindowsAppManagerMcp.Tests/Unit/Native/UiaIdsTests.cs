using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The raw UI Automation ids the UIA3 engine uses (roadmap M3.2), checked against the managed client's own
/// AutomationProperty, AutomationPattern and ControlType objects, which carry the same ids. Nothing here reads a window;
/// the managed client's id lookups only load UIAutomationCore.
/// </summary>
[Collection(RealUiAutomationCollection.Name)]
public class UiaIdsTests
{
    public static TheoryData<int, string> PropertyIds => new()
    {
        { UiaIds.RuntimeIdProperty, nameof(AutomationElement.RuntimeIdProperty) },
        { UiaIds.BoundingRectangleProperty, nameof(AutomationElement.BoundingRectangleProperty) },
        { UiaIds.ProcessIdProperty, nameof(AutomationElement.ProcessIdProperty) },
        { UiaIds.ControlTypeProperty, nameof(AutomationElement.ControlTypeProperty) },
        { UiaIds.LocalizedControlTypeProperty, nameof(AutomationElement.LocalizedControlTypeProperty) },
        { UiaIds.NameProperty, nameof(AutomationElement.NameProperty) },
        { UiaIds.AcceleratorKeyProperty, nameof(AutomationElement.AcceleratorKeyProperty) },
        { UiaIds.AccessKeyProperty, nameof(AutomationElement.AccessKeyProperty) },
        { UiaIds.HelpTextProperty, nameof(AutomationElement.HelpTextProperty) },
        { UiaIds.HasKeyboardFocusProperty, nameof(AutomationElement.HasKeyboardFocusProperty) },
        { UiaIds.IsKeyboardFocusableProperty, nameof(AutomationElement.IsKeyboardFocusableProperty) },
        { UiaIds.IsEnabledProperty, nameof(AutomationElement.IsEnabledProperty) },
        { UiaIds.AutomationIdProperty, nameof(AutomationElement.AutomationIdProperty) },
        { UiaIds.ClassNameProperty, nameof(AutomationElement.ClassNameProperty) },
        { UiaIds.IsPasswordProperty, nameof(AutomationElement.IsPasswordProperty) },
        { UiaIds.NativeWindowHandleProperty, nameof(AutomationElement.NativeWindowHandleProperty) },
        { UiaIds.IsOffscreenProperty, nameof(AutomationElement.IsOffscreenProperty) },
        { UiaIds.FrameworkIdProperty, nameof(AutomationElement.FrameworkIdProperty) },
        { UiaIds.IsExpandCollapsePatternAvailableProperty, nameof(AutomationElement.IsExpandCollapsePatternAvailableProperty) },
        { UiaIds.IsGridPatternAvailableProperty, nameof(AutomationElement.IsGridPatternAvailableProperty) },
        { UiaIds.IsInvokePatternAvailableProperty, nameof(AutomationElement.IsInvokePatternAvailableProperty) },
        { UiaIds.IsRangeValuePatternAvailableProperty, nameof(AutomationElement.IsRangeValuePatternAvailableProperty) },
        { UiaIds.IsScrollPatternAvailableProperty, nameof(AutomationElement.IsScrollPatternAvailableProperty) },
        { UiaIds.IsScrollItemPatternAvailableProperty, nameof(AutomationElement.IsScrollItemPatternAvailableProperty) },
        { UiaIds.IsSelectionItemPatternAvailableProperty, nameof(AutomationElement.IsSelectionItemPatternAvailableProperty) },
        { UiaIds.IsTablePatternAvailableProperty, nameof(AutomationElement.IsTablePatternAvailableProperty) },
        { UiaIds.IsTextPatternAvailableProperty, nameof(AutomationElement.IsTextPatternAvailableProperty) },
        { UiaIds.IsTogglePatternAvailableProperty, nameof(AutomationElement.IsTogglePatternAvailableProperty) },
        { UiaIds.IsValuePatternAvailableProperty, nameof(AutomationElement.IsValuePatternAvailableProperty) },
        { UiaIds.IsWindowPatternAvailableProperty, nameof(AutomationElement.IsWindowPatternAvailableProperty) },
        { UiaIds.IsItemContainerPatternAvailableProperty, nameof(AutomationElement.IsItemContainerPatternAvailableProperty) },
        { UiaIds.IsVirtualizedItemPatternAvailableProperty, nameof(AutomationElement.IsVirtualizedItemPatternAvailableProperty) },
    };

    [Theory]
    [MemberData(nameof(PropertyIds))]
    public void PropertyIds_AreTheManagedClientsIds(int id, string managedField)
    {
        var property = (AutomationProperty)typeof(AutomationElement).GetField(managedField, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        property.Id.Should().Be(id);
    }

    public static TheoryData<int, string> PatternIds => new()
    {
        { UiaIds.InvokePattern, nameof(InvokePattern) },
        { UiaIds.ValuePattern, nameof(ValuePattern) },
        { UiaIds.RangeValuePattern, nameof(RangeValuePattern) },
        { UiaIds.ScrollPattern, nameof(ScrollPattern) },
        { UiaIds.ExpandCollapsePattern, nameof(ExpandCollapsePattern) },
        { UiaIds.GridPattern, nameof(GridPattern) },
        { UiaIds.GridItemPattern, nameof(GridItemPattern) },
        { UiaIds.WindowPattern, nameof(WindowPattern) },
        { UiaIds.SelectionItemPattern, nameof(SelectionItemPattern) },
        { UiaIds.TablePattern, nameof(TablePattern) },
        { UiaIds.TextPattern, nameof(System.Windows.Automation.TextPattern) },
        { UiaIds.TogglePattern, nameof(TogglePattern) },
        { UiaIds.ScrollItemPattern, nameof(ScrollItemPattern) },
        { UiaIds.ItemContainerPattern, nameof(ItemContainerPattern) },
        { UiaIds.VirtualizedItemPattern, nameof(VirtualizedItemPattern) },
    };

    [Theory]
    [MemberData(nameof(PatternIds))]
    public void PatternIds_AreTheManagedClientsIds(int id, string managedPattern)
    {
        var type = typeof(AutomationElement).Assembly.GetType($"System.Windows.Automation.{managedPattern}")!;
        var pattern = (AutomationPattern)type.GetField("Pattern", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        pattern.Id.Should().Be(id);
    }

    [Fact]
    public void ThePatternPropertiesTheWaitsWatch_AreTheManagedClientsIds()
    {
        // wait_for_element's wake-ups (roadmap M3.F22) watch a value and a toggle state
        UiaIds.ValueValueProperty.Should().Be(ValuePattern.ValueProperty.Id);
        UiaIds.ToggleToggleStateProperty.Should().Be(TogglePattern.ToggleStateProperty.Id);
    }

    [Fact]
    public void LegacyIAccessiblePattern_IsTheOneTheManagedClientLacks()
    {
        // UIA_LegacyIAccessiblePatternId: the managed client has no pattern object for it
        UiaIds.LegacyIAccessiblePattern.Should().Be(10018);
        UiaIds.IsLegacyIAccessiblePatternAvailableProperty.Should().Be(30090);
    }

    [Fact]
    public void EveryPatternOfTheSeam_HasAnId_AndEveryPatternFlagAnAvailabilityProperty()
    {
        var patternInterfaces = typeof(IUiaPattern).Assembly.GetTypes()
            .Where(t => t.IsInterface && t != typeof(IUiaPattern) && typeof(IUiaPattern).IsAssignableFrom(t))
            .ToList();

        patternInterfaces.Should().HaveCount(16);
        patternInterfaces.Should().OnlyContain(t => UiaIds.PatternId(t) != null);
        // GridItem has no flag in UiaPatterns: read_table asks a cell for it, and find_ui_elements would list it on every cell
        UiaIds.PatternAvailability.Select(p => p.Pattern).Should().OnlyHaveUniqueItems().And.HaveCount(15);
        UiaIds.PatternAvailability.Aggregate(UiaPatterns.None, (all, p) => all | p.Pattern)
            .Should().HaveFlag(UiaPatterns.Actions);
    }

    [Fact]
    public void ControlTypeIds_AreTheManagedClientsIds_ForEveryTypeItKnows()
    {
        foreach (var name in UiaControlTypes.Names.Except(["AppBar", "SemanticZoom", UiaControlTypes.Unknown]))
        {
            var id = UiaControlTypes.GetId(name);
            id.Should().NotBeNull(name);
            ControlType.LookupById(id!.Value)!.ProgrammaticName.Should().Be($"ControlType.{name}");
            UiaControlTypes.GetName(id.Value).Should().Be(name);
        }
    }

    [Theory]
    [InlineData(50039, "SemanticZoom")]
    [InlineData(50040, "AppBar")]
    public void TheNewerControlTypeIds_HaveNames_ThatTheManagedClientCannotGive(int id, string name)
    {
        UiaControlTypes.GetName(id, "localized text").Should().Be(name);
        UiaControlTypes.GetId(name.ToLowerInvariant()).Should().Be(id);
        ControlType.LookupById(id).Should().BeNull("the managed client has no ControlType for it");
    }

    [Theory]
    [InlineData(49999)]
    [InlineData(50041)]
    public void AnIdOutsideTheTable_IsNamedFromTheLocalizedType(int id)
    {
        UiaControlTypes.GetName(id, "drop zone").Should().Be("DropZone");
        UiaControlTypes.GetName(id).Should().Be(UiaControlTypes.Unknown);
    }

    [Fact]
    public void Unknown_HasNoId()
    {
        UiaControlTypes.GetId(UiaControlTypes.Unknown).Should().BeNull();
        UiaControlTypes.GetId("Gizmo").Should().BeNull();
    }

    [Theory]
    [InlineData(UiaComErrors.ElementNotAvailable, typeof(UiaElementUnavailableException))]
    [InlineData(UiaComErrors.Disconnected, typeof(UiaElementUnavailableException))]
    [InlineData(UiaComErrors.ServerUnavailable, typeof(UiaElementUnavailableException))]
    [InlineData(UiaComErrors.ObjectNotConnected, typeof(UiaElementUnavailableException))]
    [InlineData(UiaComErrors.InvalidWindowHandle, typeof(UiaElementUnavailableException))]
    [InlineData(UiaComErrors.ElementNotEnabled, typeof(InvalidOperationException))]
    [InlineData(UiaComErrors.NotSupported, typeof(NotSupportedException))]
    public void ComErrors_BecomeTheSeamsExceptions(int hresult, Type expected)
    {
        var translated = UiaComErrors.Translate(new COMException("x", hresult));

        translated.Should().BeOfType(expected);
        translated!.InnerException.Should().BeOfType<COMException>();
    }

    [Fact]
    public void OtherComErrors_AreRethrownUnchanged()
    {
        UiaComErrors.Translate(new COMException("E_FAIL", unchecked((int)0x80004005))).Should().BeNull();
        FluentActions.Invoking(() => UiaComErrors.Run<int>(() => throw new COMException("E_FAIL", unchecked((int)0x80004005))))
            .Should().Throw<COMException>();
        FluentActions.Invoking(() => UiaComErrors.Run<int>(() => throw new TimeoutException("UIA_E_TIMEOUT")))
            .Should().Throw<TimeoutException>("the runtime already maps UIA_E_TIMEOUT to a TimeoutException, as the seam reports it");
    }
}
