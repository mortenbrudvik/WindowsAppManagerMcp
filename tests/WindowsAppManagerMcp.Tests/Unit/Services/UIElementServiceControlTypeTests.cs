using System.Windows.Automation;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for how UIElementService names UI Automation control types.
/// These tests do not read any real window.
/// </summary>
public class UIElementServiceControlTypeTests
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
    public void GetControlTypeName_WithKnownControlType_ReturnsItsName(ControlType controlType, string expected)
    {
        // Act
        var result = UIElementService.GetControlTypeName(controlType);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void GetControlTypeName_WithKnownControlType_IgnoresLocalizedName()
    {
        // Act
        var result = UIElementService.GetControlTypeName(ControlType.Button, "Schaltfläche");

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
    public void GetControlTypeName_WithNullControlTypeAndNoLocalizedName_ReturnsUnknown()
    {
        // Act
        var result = UIElementService.GetControlTypeName(null);

        // Assert
        result.Should().Be("Unknown");
    }

    [Theory]
    [InlineData("app bar", "AppBar")]
    [InlineData("App Bar", "AppBar")]
    [InlineData("semantic zoom", "SemanticZoom")]
    public void GetControlTypeName_WithNullControlTypeAndKnownLocalizedName_ReturnsCanonicalName(
        string localizedControlType, string expected)
    {
        // Act
        var result = UIElementService.GetControlTypeName(null, localizedControlType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("flip view", "FlipView")]
    [InlineData("  rating   control ", "RatingControl")]
    [InlineData("App-Leiste", "App-Leiste")]
    public void GetControlTypeName_WithNullControlTypeAndOtherLocalizedName_ReturnsPascalCasedName(
        string localizedControlType, string expected)
    {
        // Act
        var result = UIElementService.GetControlTypeName(null, localizedControlType);

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetControlTypeName_WithNullControlTypeAndBlankLocalizedName_ReturnsUnknown(string localizedControlType)
    {
        // Act
        var result = UIElementService.GetControlTypeName(null, localizedControlType);

        // Assert
        result.Should().Be("Unknown");
    }

    [Theory]
    [InlineData(AppBarControlTypeId, "app bar", "AppBar")]
    [InlineData(SemanticZoomControlTypeId, "semantic zoom", "SemanticZoom")]
    public void GetControlTypeName_WithControlTypeLookedUpFromNewerId_ReturnsCanonicalName(
        int controlTypeId, string localizedControlType, string expected)
    {
        // Arrange - what element.Current.ControlType yields for these elements
        _ = ControlType.Button;
        var controlType = ControlType.LookupById(controlTypeId);

        // Act
        var result = UIElementService.GetControlTypeName(controlType, localizedControlType);

        // Assert
        result.Should().Be(expected);
    }
}
