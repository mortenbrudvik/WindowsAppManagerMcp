using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// The fixed-value tool parameters (roadmap M2.2): each [ToolEnum] enum names every member, its names and aliases do
/// not collide, parsing ignores case and separators but never accepts numbers or unknown words, and an unknown value
/// fails with the enum's errorCode and a message that lists the allowed values.
/// </summary>
public class ToolEnumTests
{
    public static TheoryData<Type> ToolEnumTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(ToolEnumInfo).Assembly.GetTypes().Where(ToolEnumInfo.IsToolEnum).OrderBy(t => t.Name))
        {
            data.Add(type);
        }

        return data;
    }

    [Fact]
    public void TheServer_HasTheToolEnumsOfItsFixedValueParameters()
    {
        ToolEnumTypes().Select(row => (Type)row[0]).Should().BeEquivalentTo(new[]
        {
            typeof(WindowStateRequest), typeof(MonitorPositioning), typeof(SnapPosition), typeof(LayoutMatchBy),
            typeof(CaptureFormat), typeof(CaptureOutputMode), typeof(DebugBrowser), typeof(ClipboardFormat),
            typeof(WindowSort), typeof(WindowDetail), typeof(UIElementFormat), typeof(WindowWaitCondition), typeof(LaunchWindowState),
            typeof(ElementNameMatch), typeof(ElementDetail)
        });
    }

    [Theory]
    [MemberData(nameof(ToolEnumTypes))]
    public void EveryToolEnum_NamesEveryMember_WithAnErrorCodeTheServerDeclares(Type enumType)
    {
        // Act - building the info fails for a member without a name or two members with the same name
        var info = ToolEnumInfo.Get(enumType);

        // Assert
        info.Names.Should().HaveCount(Enum.GetValues(enumType).Length).And.OnlyHaveUniqueItems();
        info.SchemaValues.Should().StartWith(info.Names);
        // A parameter's own code, or InvalidArguments for a parameter that has none (find_windows sort, get_window_info detail)
        var declaredCodes = typeof(ToolEnumInfo).Assembly.GetTypes()
            .Where(t => t.IsEnum && t.Name.EndsWith("ErrorCode", StringComparison.Ordinal))
            .SelectMany(Enum.GetNames)
            .Append(ToolErrorReporting.InvalidArgumentsErrorCode);
        declaredCodes.Should().Contain(info.ErrorCode);
        foreach (var value in Enum.GetValues(enumType))
        {
            info.TryParse(info.Name(value), out var parsed).Should().BeTrue();
            parsed.Should().Be(value);
        }
    }

    [Theory]
    [InlineData("minimize", WindowStateRequest.Minimize)]
    [InlineData("minimized", WindowStateRequest.Minimize)]
    [InlineData("MINIMIZE", WindowStateRequest.Minimize)]
    [InlineData(" Maximized ", WindowStateRequest.Maximize)]
    [InlineData("restore", WindowStateRequest.Restore)]
    [InlineData("normal", WindowStateRequest.Restore)]
    public void WindowStateRequest_AcceptsTheDocumentedNamesAndAliases_InAnyCase(string text, WindowStateRequest expected)
    {
        ToolEnum<WindowStateRequest>.TryParse(text, out var state, out var error).Should().BeTrue(error);
        state.Should().Be(expected);
    }

    [Theory]
    [InlineData("left_half", SnapPosition.LeftHalf)]
    [InlineData("Left-Half", SnapPosition.LeftHalf)]
    [InlineData("lefthalf", SnapPosition.LeftHalf)]
    [InlineData("LeftHalf", SnapPosition.LeftHalf)]
    [InlineData("right_two_thirds", SnapPosition.RightTwoThirds)]
    [InlineData("fullscreen", SnapPosition.Fullscreen)]
    [InlineData("full", SnapPosition.Fullscreen)]
    public void SnapPosition_AcceptsEverySpellingTheStringParameterAccepted(string text, SnapPosition expected)
    {
        ToolEnum<SnapPosition>.TryParse(text, out var position, out var error).Should().BeTrue(error);
        position.Should().Be(expected);
    }

    [Theory]
    [InlineData("topleft", MonitorPositioning.TopLeft)]
    [InlineData("top_left", MonitorPositioning.TopLeft)]
    [InlineData("bottomright", MonitorPositioning.BottomRight)]
    [InlineData("maximize", MonitorPositioning.Maximize)]
    public void MonitorPositioning_AcceptsTheDocumentedAndTheUnderscoredNames(string text, MonitorPositioning expected)
    {
        ToolEnum<MonitorPositioning>.TryParse(text, out var positioning, out var error).Should().BeTrue(error);
        positioning.Should().Be(expected);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("minimise")]
    [InlineData("fullscreen")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownValues_AreRefused_WithTheAllowedValues(string? text)
    {
        // Act
        var parsed = ToolEnum<WindowStateRequest>.TryParse(text, out _, out var error);

        // Assert - never a silent fallback to restore (roadmap M2.2)
        parsed.Should().BeFalse();
        error.Should().Contain("Use one of: minimize, maximize, restore (also accepted: minimized, maximized, normal)");
    }

    [Fact]
    public void TheSchemaValues_ListTheNamesThenTheListedAliases_ButNotTheUnlistedOnes()
    {
        ToolEnumInfo.Get(typeof(WindowStateRequest)).SchemaValues.Should()
            .Equal("minimize", "maximize", "restore", "minimized", "maximized", "normal");
        ToolEnumInfo.Get(typeof(CaptureFormat)).SchemaValues.Should().Equal("auto", "png", "jpeg", "jpg");
        ToolEnumInfo.Get(typeof(SnapPosition)).SchemaValues.Should().NotContain("full").And.Contain("fullscreen");
    }

    [Theory]
    [InlineData("\"maximized\"", WindowStateRequest.Maximize)]
    [InlineData("\"Restore\"", WindowStateRequest.Restore)]
    public void TheConverter_BindsNamesAndAliases(string json, WindowStateRequest expected)
    {
        JsonSerializer.Deserialize<WindowStateRequest>(json, ToolJson.SerializerOptions).Should().Be(expected);
    }

    [Theory]
    [InlineData("\"hide\"", "'hide' is not a valid window state.")]
    [InlineData("1", "'1' is not a valid window state.")]
    [InlineData("null", "'null' is not a valid window state.")]
    [InlineData("true", "'true' is not a valid window state.")]
    public void TheConverter_RefusesUnknownValuesAndNumbers_WithTheEnumsErrorCode(string json, string message)
    {
        // Act
        var act = () => JsonSerializer.Deserialize<WindowStateRequest>(json, ToolJson.SerializerOptions);

        // Assert
        act.Should().Throw<ToolArgumentException>()
            .Where(e => e.ErrorCode == nameof(WindowErrorCode.InvalidState))
            .WithMessage(message + " Use one of: minimize, maximize, restore*");
    }

    [Fact]
    public void ANull_BindsToTheDefaultItMeant_ForTheOptionalParameters_AndIsRefusedForTheRequiredOnes()
    {
        // format = null used to mean auto; state never had a default
        JsonSerializer.Deserialize<CaptureFormat>("null", ToolJson.SerializerOptions).Should().Be(CaptureFormat.Auto);
        JsonSerializer.Deserialize<CaptureOutputMode>("null", ToolJson.SerializerOptions).Should().Be(CaptureOutputMode.Image);
        JsonSerializer.Deserialize<MonitorPositioning>("null", ToolJson.SerializerOptions).Should().Be(MonitorPositioning.Center);
        JsonSerializer.Deserialize<LayoutMatchBy>("null", ToolJson.SerializerOptions).Should().Be(LayoutMatchBy.ProcessAndTitle);
        JsonSerializer.Deserialize<DebugBrowser>("null", ToolJson.SerializerOptions).Should().Be(DebugBrowser.Chrome);
        var state = () => JsonSerializer.Deserialize<WindowStateRequest>("null", ToolJson.SerializerOptions);
        var position = () => JsonSerializer.Deserialize<SnapPosition>("null", ToolJson.SerializerOptions);
        state.Should().Throw<ToolArgumentException>();
        position.Should().Throw<ToolArgumentException>();
    }

    [Fact]
    public void TheConverter_LeavesANullableEnumNull_AndWritesTheCanonicalName()
    {
        JsonSerializer.Deserialize<SnapPosition?>("null", ToolJson.SerializerOptions).Should().BeNull();
        JsonSerializer.Serialize(SnapPosition.TopLeftQuarter, ToolJson.SerializerOptions).Should().Be("\"top_left_quarter\"");
        JsonSerializer.Serialize(MonitorPositioning.BottomRight, ToolJson.SerializerOptions).Should().Be("\"bottomright\"");
    }

    [Fact]
    public void OtherEnums_KeepTheSdksSerialization()
    {
        // The result enums (error codes and the like) are not tool arguments: they serialize as before
        JsonSerializer.Serialize(WindowState.Minimized, ToolJson.SerializerOptions).Should().Be("\"Minimized\"");
        JsonSerializer.Serialize(InputErrorCode.ModifiersHeld, ToolJson.SerializerOptions).Should().Be("\"ModifiersHeld\"");
    }

    [Fact]
    public void AToolEnumMember_WithoutAName_IsRefused()
    {
        var act = () => ToolEnumInfo.Get(typeof(Unnamed));

        act.Should().Throw<ArgumentException>().WithMessage("*JsonStringEnumMemberName*");
    }

    [Fact]
    public void ToolEnumNames_ThatCollide_AreRefused()
    {
        var act = () => ToolEnumInfo.Get(typeof(Colliding));

        act.Should().Throw<ArgumentException>().WithMessage("*names both*");
    }

    [ToolEnum("InvalidState", "thing")]
    private enum Unnamed
    {
        [JsonStringEnumMemberName("named")] Named,
        Bare
    }

    [ToolEnum("InvalidState", "thing")]
    private enum Colliding
    {
        [JsonStringEnumMemberName("left_half")] One,
        [JsonStringEnumMemberName("lefthalf")] Two
    }
}
