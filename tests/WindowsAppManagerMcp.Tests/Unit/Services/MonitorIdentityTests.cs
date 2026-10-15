namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for how a monitor's identity (id, friendly name, internal or not) is derived from the display configuration's
/// targets (roadmap M6.7, S0d). Pure: no display is read.
/// </summary>
public class MonitorIdentityTests
{
    private const string DellPath = @"\\?\DISPLAY#DEL40F2#5&2a8e5b0f&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string LaptopPath = @"\\?\DISPLAY#BOE0A1C#4&1b2c3d4e&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string ProjectorPath = @"\\?\DISPLAY#EPS0001#5&99aa88bb&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static DisplayTarget Target(
        string gdiDeviceName = @"\\.\DISPLAY1",
        string? friendlyName = "DELL U2720Q",
        string? devicePath = DellPath,
        DisplayOutputTechnology technology = DisplayOutputTechnology.DisplayPortExternal) =>
        new(gdiDeviceName, friendlyName, devicePath, technology);

    #region IdOf

    [Fact]
    public void IdOf_ADevicePath_IsMDashAndEightLowercaseHexDigits()
    {
        MonitorIdentity.IdOf(DellPath).Should().MatchRegex("^m-[0-9a-f]{8}$");
    }

    [Fact]
    public void IdOf_TheSamePath_IsTheSameIdEveryTime()
    {
        // The id must survive restarts of the server, so it cannot depend on string.GetHashCode's per-process seed
        MonitorIdentity.IdOf(DellPath).Should().Be(MonitorIdentity.IdOf(new string(DellPath.ToCharArray())));
    }

    [Fact]
    public void IdOf_IsThePinnedHashOfTheUpperCasedPath()
    {
        // Pins the derivation with a literal: SHA-256 of the upper-cased path as UTF-8, first 4 bytes as lowercase hex.
        // Saved layouts (M6.8) keep these ids, so changing any step would give every monitor a new id and break them
        MonitorIdentity.IdOf(DellPath).Should().Be("m-c70b38ef");
        MonitorIdentity.IdOf(LaptopPath).Should().Be("m-aa8be114");
    }

    [Fact]
    public void IdOf_SeveralPaths_IsThePinnedHashOfTheSortedPathsJoinedWithLineFeeds()
    {
        // Pins the clone-mode derivation with a literal: the upper-cased paths sorted ordinally and joined with "\n".
        // Another separator, sort or trim would change the id of every cloned monitor
        MonitorIdentity.IdOf([ProjectorPath, LaptopPath]).Should().Be("m-ba81ed65");
    }

    [Fact]
    public void IdOf_IgnoresTheCaseOfThePath()
    {
        // Windows compares device paths case-insensitively and has reported the same path in different cases
        MonitorIdentity.IdOf(DellPath.ToLowerInvariant()).Should().Be(MonitorIdentity.IdOf(DellPath));
    }

    [Fact]
    public void IdOf_DifferentMonitors_AreDifferentIds()
    {
        MonitorIdentity.IdOf(DellPath).Should().NotBe(MonitorIdentity.IdOf(LaptopPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IdOf_NoPath_IsNull(string? devicePath)
    {
        MonitorIdentity.IdOf(devicePath).Should().BeNull();
    }

    [Fact]
    public void IdOf_SeveralPaths_DoesNotDependOnTheirOrder()
    {
        // Clone mode: QueryDisplayConfig does not promise an order of the paths that share a source
        MonitorIdentity.IdOf([LaptopPath, ProjectorPath]).Should().Be(MonitorIdentity.IdOf([ProjectorPath, LaptopPath]));
        MonitorIdentity.IdOf([LaptopPath, ProjectorPath]).Should().NotBe(MonitorIdentity.IdOf(LaptopPath));
    }

    [Fact]
    public void IdOf_SeveralPaths_SkipsMissingOnesAndDuplicates()
    {
        MonitorIdentity.IdOf([LaptopPath, null, "", LaptopPath.ToLowerInvariant()]).Should().Be(MonitorIdentity.IdOf(LaptopPath));
        MonitorIdentity.IdOf([null, ""]).Should().BeNull();
    }

    #endregion

    #region IsInternal

    [Theory]
    [InlineData(DisplayOutputTechnology.Internal, true)]
    [InlineData(DisplayOutputTechnology.DisplayPortEmbedded, true)]
    [InlineData(DisplayOutputTechnology.UdiEmbedded, true)]
    [InlineData(DisplayOutputTechnology.DisplayPortExternal, false)]
    [InlineData(DisplayOutputTechnology.Hdmi, false)]
    [InlineData(DisplayOutputTechnology.Lvds, false)]
    [InlineData(DisplayOutputTechnology.IndirectVirtual, false)]
    [InlineData(DisplayOutputTechnology.Other, false)]
    [InlineData((DisplayOutputTechnology)0, false)]
    public void IsInternalTechnology_IsTrueOnlyForTheBuiltInConnectors(DisplayOutputTechnology technology, bool expected)
    {
        MonitorIdentity.IsInternalTechnology(technology).Should().Be(expected);
    }

    #endregion

    #region Identify

    [Fact]
    public void Identify_JoinsTheTargetWithTheMonitorsGdiName()
    {
        // Arrange
        var monitor = TestDataFactory.CreateMonitorInfo(index: 1, deviceName: @"\\.\DISPLAY2", isPrimary: false);
        var targets = new[]
        {
            Target(@"\\.\DISPLAY1", "Built-in display", LaptopPath, DisplayOutputTechnology.Internal),
            Target(@"\\.\DISPLAY2", "DELL U2720Q", DellPath, DisplayOutputTechnology.DisplayPortExternal)
        };

        // Act
        var result = MonitorIdentity.Identify(monitor, targets);

        // Assert - the identity is added; everything EnumDisplayMonitors reported stays as it was
        result.Id.Should().Be(MonitorIdentity.IdOf(DellPath));
        result.FriendlyName.Should().Be("DELL U2720Q");
        result.IsInternal.Should().BeFalse();
        result.Should().Be(monitor with { Id = result.Id, FriendlyName = "DELL U2720Q", IsInternal = false });
    }

    [Fact]
    public void Identify_MatchesTheGdiNameIgnoringCase()
    {
        var monitor = TestDataFactory.CreateMonitorInfo(deviceName: @"\\.\DISPLAY1");

        var result = MonitorIdentity.Identify(monitor, [Target(@"\\.\display1")]);

        result.Id.Should().Be(MonitorIdentity.IdOf(DellPath));
    }

    [Fact]
    public void Identify_AnInternalPanel_IsInternal()
    {
        var monitor = TestDataFactory.CreateMonitorInfo();

        var result = MonitorIdentity.Identify(monitor, [Target(friendlyName: null, devicePath: LaptopPath, technology: DisplayOutputTechnology.Internal)]);

        result.IsInternal.Should().BeTrue();
        result.FriendlyName.Should().BeNull("an internal panel often has no EDID name");
        result.Id.Should().Be(MonitorIdentity.IdOf(LaptopPath));
    }

    [Fact]
    public void Identify_WithNoTargetForTheMonitor_LeavesTheIdentityUnknown()
    {
        // A topology change between EnumDisplayMonitors and QueryDisplayConfig, or a display the configuration does not list
        var monitor = TestDataFactory.CreateMonitorInfo(deviceName: @"\\.\DISPLAY3");

        var result = MonitorIdentity.Identify(monitor, [Target(@"\\.\DISPLAY1")]);

        result.Should().Be(monitor);
        result.Id.Should().BeNull();
        result.FriendlyName.Should().BeNull();
        result.IsInternal.Should().BeNull();
    }

    [Fact]
    public void Identify_ATargetWithoutPathOrName_GivesNoIdButKeepsTheConnector()
    {
        // A remote, virtual or EDID-less display: the id is null rather than a hash of nothing
        var monitor = TestDataFactory.CreateMonitorInfo();

        var result = MonitorIdentity.Identify(monitor, [Target(friendlyName: "", devicePath: null, technology: DisplayOutputTechnology.IndirectVirtual)]);

        result.Id.Should().BeNull();
        result.FriendlyName.Should().BeNull();
        result.IsInternal.Should().BeFalse();
    }

    [Fact]
    public void Identify_InCloneMode_JoinsTheNamesAndHashesEveryPath()
    {
        // Arrange - the laptop panel cloned to a projector: one source, two targets
        var monitor = TestDataFactory.CreateMonitorInfo();
        var targets = new[]
        {
            Target(@"\\.\DISPLAY1", null, LaptopPath, DisplayOutputTechnology.Internal),
            Target(@"\\.\DISPLAY1", "EPSON PJ", ProjectorPath, DisplayOutputTechnology.Hdmi),
            Target(@"\\.\DISPLAY1", "EPSON PJ", ProjectorPath, DisplayOutputTechnology.Hdmi)
        };

        // Act
        var result = MonitorIdentity.Identify(monitor, targets);

        // Assert
        result.Id.Should().Be(MonitorIdentity.IdOf([LaptopPath, ProjectorPath]));
        result.FriendlyName.Should().Be("EPSON PJ");
        result.IsInternal.Should().BeTrue("one of the cloned monitors is the built-in panel");
    }

    [Fact]
    public void Identify_InCloneMode_JoinsDistinctNamesInPathOrder()
    {
        var monitor = TestDataFactory.CreateMonitorInfo();
        var targets = new[]
        {
            Target(friendlyName: "DELL U2720Q", devicePath: DellPath),
            Target(friendlyName: "EPSON PJ", devicePath: ProjectorPath, technology: DisplayOutputTechnology.Hdmi)
        };

        var result = MonitorIdentity.Identify(monitor, targets);

        result.FriendlyName.Should().Be("DELL U2720Q + EPSON PJ");
        result.IsInternal.Should().BeFalse();
    }

    [Fact]
    public void Identify_KeepsTheDpiTheWrapperSet()
    {
        var monitor = TestDataFactory.CreateMonitorInfo(scaleFactor: 1.5) with { Dpi = 144 };

        MonitorIdentity.Identify(monitor, [Target()]).Dpi.Should().Be(144);
    }

    #endregion

    #region Covers

    [Fact]
    public void Covers_IsTrueOnlyWhenEveryMonitorHasATarget()
    {
        var monitors = new[]
        {
            TestDataFactory.CreateMonitorInfo(index: 0, deviceName: @"\\.\DISPLAY1"),
            TestDataFactory.CreateMonitorInfo(index: 1, deviceName: @"\\.\DISPLAY2", isPrimary: false)
        };

        MonitorIdentity.Covers(monitors, [Target(@"\\.\DISPLAY1"), Target(@"\\.\DISPLAY2", devicePath: LaptopPath)]).Should().BeTrue();
        MonitorIdentity.Covers(monitors, [Target(@"\\.\DISPLAY1")]).Should().BeFalse();
        MonitorIdentity.Covers(monitors, []).Should().BeFalse();
    }

    [Fact]
    public void IsComplete_IsTrueOnlyWhenEveryMonitorHasATarget_AndEveryOneOfThemWasRead()
    {
        var monitors = new[]
        {
            TestDataFactory.CreateMonitorInfo(index: 0, deviceName: @"\\.\DISPLAY1"),
            TestDataFactory.CreateMonitorInfo(index: 1, deviceName: @"\\.\DISPLAY2", isPrimary: false)
        };
        var unread = new DisplayTarget(@"\\.\DISPLAY2", null, null, DisplayOutputTechnology.Hdmi) { TargetRead = false };

        MonitorIdentity.IsComplete(monitors, [Target(@"\\.\DISPLAY1"), Target(@"\\.\DISPLAY2", devicePath: LaptopPath)]).Should().BeTrue();
        MonitorIdentity.IsComplete(monitors, [Target(@"\\.\DISPLAY1")]).Should().BeFalse();
        // DISPLAY2's GET_TARGET_NAME failed: it is covered, but its identity is unknown, not absent
        MonitorIdentity.IsComplete(monitors, [Target(@"\\.\DISPLAY1"), unread]).Should().BeFalse();
        // In clone mode one unread target of a monitor is enough to make its id partial
        MonitorIdentity.IsComplete(monitors,
            [Target(@"\\.\DISPLAY1"), Target(@"\\.\DISPLAY2", devicePath: LaptopPath), unread]).Should().BeFalse();
    }

    [Fact]
    public void IsComplete_IgnoresAnUnreadTargetOfASourceNoMonitorHas()
    {
        var monitors = new[] { TestDataFactory.CreateMonitorInfo(index: 0, deviceName: @"\\.\DISPLAY1") };
        var other = new DisplayTarget(@"\\.\DISPLAY9", null, null, DisplayOutputTechnology.Hdmi) { TargetRead = false };

        MonitorIdentity.IsComplete(monitors, [Target(@"\\.\DISPLAY1"), other]).Should().BeTrue();
    }

    #endregion
}
