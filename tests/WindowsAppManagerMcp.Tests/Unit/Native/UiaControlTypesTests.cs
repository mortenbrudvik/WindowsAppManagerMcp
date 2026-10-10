using System.Windows.Automation;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// Unit tests for how UiaControlTypes names UI Automation control types and checks filter names.
/// These tests do not read any real window.
/// </summary>
public class UiaControlTypesTests
{
    // UIA control type ids added in Windows 8 that the managed UI Automation client does not know
    private const int SemanticZoomControlTypeId = 50039;
    private const int AppBarControlTypeId = 50040;

    public static TheoryData<ControlType, string> KnownControlTypes => new()
    {
        { ControlType.Button, "Button" },
        { ControlType.Edit, "Edit" },
        { ControlType.ListItem, "ListItem" },
        { ControlType.SplitButton, "SplitButton" },
        { ControlType.Window, "Window" }
    };

    [Theory]
    [MemberData(nameof(KnownControlTypes))]
    public void GetName_WithKnownControlType_ReturnsItsName(ControlType controlType, string expected)
    {
        // Act
        var result = UiaControlTypes.GetName(controlType);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GetName_WithKnownControlType_IgnoresLocalizedName()
    {
        // Act
        var result = UiaControlTypes.GetName(ControlType.Button, "Schaltfläche");

        // Assert
        result.Should().Be("Button");
    }

    [Theory]
    [InlineData(AppBarControlTypeId)]
    [InlineData(SemanticZoomControlTypeId)]
    public void ManagedClient_ReturnsNullForNewerControlTypes(int controlTypeId)
    {
        // Arrange - make sure the built-in control types are registered first
        _ = ControlType.Button;

        // Act
        var controlType = ControlType.LookupById(controlTypeId);

        // Assert - this is why GetControlTypeName must accept null
        controlType.Should().BeNull();
    }

    [Fact]
    public void GetName_WithNullControlTypeAndNoLocalizedName_ReturnsUnknown()
    {
        // Act
        var result = UiaControlTypes.GetName(null);

        // Assert
        result.Should().Be("Unknown");
    }

    [Theory]
    [InlineData("app bar", "AppBar")]
    [InlineData("App Bar", "AppBar")]
    [InlineData("semantic zoom", "SemanticZoom")]
    public void GetName_WithNullControlTypeAndKnownLocalizedName_ReturnsCanonicalName(
        string localizedControlType, string expected)
    {
        // Act
        var result = UiaControlTypes.GetName(null, localizedControlType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("flip view", "FlipView")]
    [InlineData("  rating   control ", "RatingControl")]
    [InlineData("App-Leiste", "App-Leiste")]
    public void GetName_WithNullControlTypeAndOtherLocalizedName_ReturnsPascalCasedName(
        string localizedControlType, string expected)
    {
        // Act
        var result = UiaControlTypes.GetName(null, localizedControlType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetName_WithNullControlTypeAndBlankLocalizedName_ReturnsUnknown(string localizedControlType)
    {
        // Act
        var result = UiaControlTypes.GetName(null, localizedControlType);

        // Assert
        result.Should().Be("Unknown");
    }

    [Theory]
    [InlineData(AppBarControlTypeId, "app bar", "AppBar")]
    [InlineData(SemanticZoomControlTypeId, "semantic zoom", "SemanticZoom")]
    public void GetName_WithControlTypeLookedUpFromNewerId_ReturnsCanonicalName(
        int controlTypeId, string localizedControlType, string expected)
    {
        // Arrange - what element.Current.ControlType yields for these elements
        _ = ControlType.Button;
        var controlType = ControlType.LookupById(controlTypeId);

        // Act
        var result = UiaControlTypes.GetName(controlType, localizedControlType);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Names_AreTheStandardTypesPlusTheFallbackNames_InAlphabeticalOrder()
    {
        // Assert
        UiaControlTypes.Names.Should().HaveCount(39 + 3);
        UiaControlTypes.Names.Should().Contain(["Button", "Edit", "MenuItem", "Window", "AppBar", "SemanticZoom", "Unknown"]);
        UiaControlTypes.Names.Should().BeInAscendingOrder(StringComparer.Ordinal).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void Names_IncludeEveryNameGetNameReturnsForAStandardControlType()
    {
        // Arrange - every ControlType the managed client defines
        var standard = typeof(ControlType)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ControlType))
            .Select(f => (ControlType)f.GetValue(null)!)
            .ToList();

        // Act
        var names = standard.Select(t => UiaControlTypes.GetName(t)).ToList();

        // Assert - otherwise the controlTypes filter would reject a type that get_ui_elements reports
        standard.Should().HaveCount(39);
        UiaControlTypes.Names.Should().Contain(names);
    }

    [Theory]
    [InlineData("Button", "Button")]
    [InlineData("button", "Button")]
    [InlineData(" MENUITEM ", "MenuItem")]
    [InlineData("appbar", "AppBar")]
    [InlineData("unknown", "Unknown")]
    public void Canonicalize_KnownName_ReturnsItsCanonicalSpelling(string name, string expected)
    {
        UiaControlTypes.Canonicalize(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("Buton")]
    [InlineData("Textbox")]
    [InlineData("App-Leiste")]
    [InlineData("")]
    public void Canonicalize_UnknownName_ReturnsNull(string name)
    {
        UiaControlTypes.Canonicalize(name).Should().BeNull();
    }
}
