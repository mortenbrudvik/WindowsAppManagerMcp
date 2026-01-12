using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// Unit tests for model records.
/// Tests T6.1-T6.3 from BACKLOG-TECHNICAL.md.
/// </summary>
public class ModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    #region T6.1: WindowMatcher Tests

    [Fact]
    public void WindowMatcher_WithAllNullProperties_IsValid()
    {
        // Arrange & Act
        var matcher = new WindowMatcher();

        // Assert
        matcher.TitleContains.Should().BeNull();
        matcher.ProcessName.Should().BeNull();
        matcher.ClassName.Should().BeNull();
    }

    [Fact]
    public void WindowMatcher_WithProcessName_SetsProperty()
    {
        // Arrange & Act
        var matcher = new WindowMatcher(ProcessName: "notepad");

        // Assert
        matcher.ProcessName.Should().Be("notepad");
        matcher.TitleContains.Should().BeNull();
    }

    [Fact]
    public void WindowMatcher_WithTitleContains_SetsProperty()
    {
        // Arrange & Act
        var matcher = new WindowMatcher(TitleContains: "Document");

        // Assert
        matcher.TitleContains.Should().Be("Document");
        matcher.ProcessName.Should().BeNull();
    }

    [Fact]
    public void WindowMatcher_Equality_WhenPropertiesMatch_AreEqual()
    {
        // Arrange
        var matcher1 = new WindowMatcher(TitleContains: "Test", ProcessName: "app");
        var matcher2 = new WindowMatcher(TitleContains: "Test", ProcessName: "app");

        // Assert
        matcher1.Should().Be(matcher2);
    }

    [Fact]
    public void WindowMatcher_Equality_WhenPropertiesDiffer_AreNotEqual()
    {
        // Arrange
        var matcher1 = new WindowMatcher(ProcessName: "notepad");
        var matcher2 = new WindowMatcher(ProcessName: "chrome");

        // Assert
        matcher1.Should().NotBe(matcher2);
    }

    [Fact]
    public void WindowMatcher_Serialization_RoundTrips()
    {
        // Arrange
        var original = new WindowMatcher(
            TitleContains: "Document",
            ProcessName: "notepad",
            ClassName: "Notepad");

        // Act
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<WindowMatcher>(json, JsonOptions);

        // Assert
        deserialized.Should().Be(original);
    }

    #endregion

    #region T6.2: RelativePosition Tests

    [Fact]
    public void RelativePosition_WithValidValues_CreatesRecord()
    {
        // Arrange & Act
        var position = new RelativePosition(0.0, 0.0, 0.5, 1.0);

        // Assert
        position.X.Should().Be(0.0);
        position.Y.Should().Be(0.0);
        position.Width.Should().Be(0.5);
        position.Height.Should().Be(1.0);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.5, 0.5)]   // Top-left quarter
    [InlineData(0.5, 0.0, 0.5, 0.5)]   // Top-right quarter
    [InlineData(0.0, 0.5, 0.5, 0.5)]   // Bottom-left quarter
    [InlineData(0.5, 0.5, 0.5, 0.5)]   // Bottom-right quarter
    [InlineData(0.0, 0.0, 1.0, 1.0)]   // Fullscreen
    [InlineData(0.0, 0.0, 0.5, 1.0)]   // Left half
    [InlineData(0.5, 0.0, 0.5, 1.0)]   // Right half
    public void RelativePosition_CommonSnapPositions_AreValid(
        double x, double y, double width, double height)
    {
        // Act
        var position = new RelativePosition(x, y, width, height);

        // Assert
        position.X.Should().BeGreaterThanOrEqualTo(0);
        position.Y.Should().BeGreaterThanOrEqualTo(0);
        position.Width.Should().BeGreaterThan(0);
        position.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RelativePosition_Equality_WhenValuesMatch_AreEqual()
    {
        // Arrange
        var pos1 = new RelativePosition(0.25, 0.25, 0.5, 0.5);
        var pos2 = new RelativePosition(0.25, 0.25, 0.5, 0.5);

        // Assert
        pos1.Should().Be(pos2);
    }

    [Fact]
    public void RelativePosition_Equality_WhenValuesDiffer_AreNotEqual()
    {
        // Arrange
        var pos1 = new RelativePosition(0.0, 0.0, 0.5, 1.0);
        var pos2 = new RelativePosition(0.5, 0.0, 0.5, 1.0);

        // Assert
        pos1.Should().NotBe(pos2);
    }

    [Fact]
    public void RelativePosition_Serialization_RoundTrips()
    {
        // Arrange
        var original = new RelativePosition(0.333, 0.0, 0.333, 1.0);

        // Act
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<RelativePosition>(json, JsonOptions);

        // Assert
        deserialized.Should().Be(original);
    }

    #endregion

    #region T6.3: WindowPlacement Serialization Tests

    [Fact]
    public void WindowPlacement_WithAllProperties_CreatesRecord()
    {
        // Arrange
        var matcher = new WindowMatcher(ProcessName: "code");
        var position = new RelativePosition(0.0, 0.0, 0.5, 1.0);

        // Act
        var placement = new WindowPlacement(
            Matcher: matcher,
            MonitorIndex: 0,
            Position: position,
            LaunchCommand: "code.exe",
            Focus: true,
            Order: 1);

        // Assert
        placement.Matcher.Should().Be(matcher);
        placement.MonitorIndex.Should().Be(0);
        placement.Position.Should().Be(position);
        placement.LaunchCommand.Should().Be("code.exe");
        placement.Focus.Should().BeTrue();
        placement.Order.Should().Be(1);
    }

    [Fact]
    public void WindowPlacement_WithDefaults_HasCorrectDefaultValues()
    {
        // Arrange
        var matcher = new WindowMatcher(ProcessName: "notepad");
        var position = new RelativePosition(0, 0, 1, 1);

        // Act
        var placement = new WindowPlacement(matcher, 0, position);

        // Assert
        placement.LaunchCommand.Should().BeNull();
        placement.Focus.Should().BeFalse();
        placement.Order.Should().Be(0);
    }

    [Fact]
    public void WindowPlacement_Serialization_RoundTrips()
    {
        // Arrange
        var original = new WindowPlacement(
            Matcher: new WindowMatcher(ProcessName: "chrome", TitleContains: "Google"),
            MonitorIndex: 1,
            Position: new RelativePosition(0.5, 0.0, 0.5, 1.0),
            LaunchCommand: "chrome.exe --new-window",
            Focus: true,
            Order: 2);

        // Act
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<WindowPlacement>(json, JsonOptions);

        // Assert
        deserialized.Should().Be(original);
    }

    #endregion

    #region T6.3: LayoutPreset Serialization Tests

    [Fact]
    public void LayoutPreset_WithPlacements_CreatesRecord()
    {
        // Arrange
        var placements = new List<WindowPlacement>
        {
            new(new WindowMatcher(ProcessName: "code"), 0, new RelativePosition(0, 0, 0.5, 1)),
            new(new WindowMatcher(ProcessName: "chrome"), 0, new RelativePosition(0.5, 0, 0.5, 1))
        };

        // Act
        var preset = new LayoutPreset("Development", "Dev setup", placements);

        // Assert
        preset.Name.Should().Be("Development");
        preset.Description.Should().Be("Dev setup");
        preset.Placements.Should().HaveCount(2);
    }

    [Fact]
    public void LayoutPreset_SetsTimestamps()
    {
        // Arrange & Act
        var beforeCreation = DateTime.UtcNow;
        var preset = new LayoutPreset("Test", null, new List<WindowPlacement>());
        var afterCreation = DateTime.UtcNow;

        // Assert
        preset.CreatedAt.Should().BeOnOrAfter(beforeCreation);
        preset.CreatedAt.Should().BeOnOrBefore(afterCreation);
        preset.UpdatedAt.Should().BeOnOrAfter(beforeCreation);
    }

    [Fact]
    public void LayoutPreset_Serialization_RoundTrips()
    {
        // Arrange
        var original = new LayoutPreset(
            Name: "WorkSetup",
            Description: "My work layout",
            Placements: new List<WindowPlacement>
            {
                new(new WindowMatcher(ProcessName: "code"), 0, new RelativePosition(0, 0, 0.6, 1)),
                new(new WindowMatcher(ProcessName: "terminal"), 0, new RelativePosition(0.6, 0, 0.4, 0.5)),
                new(new WindowMatcher(ProcessName: "chrome"), 1, new RelativePosition(0, 0, 1, 1))
            });

        // Act
        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<LayoutPreset>(json, JsonOptions);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Name.Should().Be(original.Name);
        deserialized.Description.Should().Be(original.Description);
        deserialized.Placements.Should().HaveCount(3);
    }

    [Fact]
    public void LayoutPreset_Serialization_PreservesPlacementDetails()
    {
        // Arrange
        var placement = new WindowPlacement(
            Matcher: new WindowMatcher(ProcessName: "notepad", TitleContains: "untitled"),
            MonitorIndex: 0,
            Position: new RelativePosition(0.25, 0.25, 0.5, 0.5),
            LaunchCommand: "notepad.exe",
            Focus: true,
            Order: 5);
        var preset = new LayoutPreset("Test", null, new List<WindowPlacement> { placement });

        // Act
        var json = JsonSerializer.Serialize(preset, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<LayoutPreset>(json, JsonOptions);

        // Assert
        var deserializedPlacement = deserialized!.Placements[0];
        deserializedPlacement.Matcher.ProcessName.Should().Be("notepad");
        deserializedPlacement.Matcher.TitleContains.Should().Be("untitled");
        deserializedPlacement.MonitorIndex.Should().Be(0);
        deserializedPlacement.Position.X.Should().Be(0.25);
        deserializedPlacement.LaunchCommand.Should().Be("notepad.exe");
        deserializedPlacement.Focus.Should().BeTrue();
        deserializedPlacement.Order.Should().Be(5);
    }

    [Fact]
    public void LayoutPreset_Serialization_HandlesEmptyPlacements()
    {
        // Arrange
        var preset = new LayoutPreset("Empty", "No placements", new List<WindowPlacement>());

        // Act
        var json = JsonSerializer.Serialize(preset, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<LayoutPreset>(json, JsonOptions);

        // Assert
        deserialized!.Placements.Should().BeEmpty();
    }

    #endregion

    #region MonitorRect and WindowRect Tests

    [Fact]
    public void MonitorRect_Equality_WhenValuesMatch_AreEqual()
    {
        // Arrange
        var rect1 = new MonitorRect(0, 0, 1920, 1080);
        var rect2 = new MonitorRect(0, 0, 1920, 1080);

        // Assert
        rect1.Should().Be(rect2);
    }

    [Fact]
    public void WindowRect_Equality_WhenValuesMatch_AreEqual()
    {
        // Arrange
        var rect1 = new WindowRect(100, 100, 800, 600);
        var rect2 = new WindowRect(100, 100, 800, 600);

        // Assert
        rect1.Should().Be(rect2);
    }

    [Fact]
    public void MonitorInfo_CreatesWithAllProperties()
    {
        // Arrange & Act
        var info = new MonitorInfo(
            Index: 0,
            DeviceName: @"\\.\DISPLAY1",
            IsPrimary: true,
            Bounds: new MonitorRect(0, 0, 1920, 1080),
            WorkArea: new MonitorRect(0, 0, 1920, 1040),
            ScaleFactor: 1.25);

        // Assert
        info.Index.Should().Be(0);
        info.DeviceName.Should().Be(@"\\.\DISPLAY1");
        info.IsPrimary.Should().BeTrue();
        info.ScaleFactor.Should().Be(1.25);
    }

    [Fact]
    public void WindowInfo_CreatesWithAllProperties()
    {
        // Arrange & Act
        var info = new WindowInfo(
            Handle: new nint(12345),
            Title: "Test Window",
            ProcessName: "testapp",
            ProcessId: 1234,
            Bounds: new WindowRect(100, 100, 800, 600),
            State: WindowState.Normal,
            IsVisible: true,
            MonitorIndex: 0);

        // Assert
        info.Handle.Should().Be(new nint(12345));
        info.Title.Should().Be("Test Window");
        info.ProcessName.Should().Be("testapp");
        info.ProcessId.Should().Be(1234);
        info.State.Should().Be(WindowState.Normal);
        info.IsVisible.Should().BeTrue();
    }

    #endregion
}
