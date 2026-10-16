using System.Reflection;
using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The display configuration read behind <see cref="INativeMonitorWrapper.GetDisplayTargets"/> (roadmap M6.7, S0d): the
/// pure parsing and retry helpers, the layouts of the native structs it marshals, and one read of the real configuration.
/// QueryDisplayConfig and DisplayConfigGetDeviceInfo only read, so the real read runs in the default suite.
/// </summary>
public class NativeMonitorWrapperDisplayConfigTests
{
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorAccessDenied = 5;

    private static readonly IReadOnlyList<DisplayTarget> OneTarget =
        [new(@"\\.\DISPLAY1", "DELL U2720Q", @"\\?\DISPLAY#DEL40F2#1", DisplayOutputTechnology.DisplayPortExternal)];

    #region ReadTargets

    [Fact]
    public void ReadTargets_WhenTheQuerySucceeds_ReturnsItsTargets()
    {
        var calls = 0;

        var result = NativeMonitorWrapper.ReadTargets(() => { calls++; return (ErrorSuccess, OneTarget); });

        result.Should().Equal(OneTarget);
        calls.Should().Be(1);
    }

    [Fact]
    public void ReadTargets_WhenTheTopologyChangesBetweenSizingAndQuerying_TriesAgain()
    {
        // QueryDisplayConfig returns ERROR_INSUFFICIENT_BUFFER when paths were added after GetDisplayConfigBufferSizes
        var calls = 0;

        var result = NativeMonitorWrapper.ReadTargets(() => ++calls < 3 ? (ErrorInsufficientBuffer, []) : (ErrorSuccess, OneTarget));

        result.Should().Equal(OneTarget);
        calls.Should().Be(3);
    }

    [Fact]
    public void ReadTargets_WhenTheBufferIsTooSmallEveryTime_GivesUpWithNull()
    {
        var calls = 0;

        var result = NativeMonitorWrapper.ReadTargets(() => { calls++; return (ErrorInsufficientBuffer, []); });

        result.Should().BeNull();
        calls.Should().Be(NativeMonitorWrapper.DisplayConfigAttempts);
    }

    [Fact]
    public void ReadTargets_OnAnyOtherError_ReturnsNullWithoutRetrying()
    {
        var calls = 0;

        var result = NativeMonitorWrapper.ReadTargets(() => { calls++; return (ErrorAccessDenied, []); });

        result.Should().BeNull();
        calls.Should().Be(1);
    }

    #endregion

    #region ToDisplayTarget

    [Fact]
    public void ToDisplayTarget_KeepsWhatTheDeviceInfoReported()
    {
        var result = NativeMonitorWrapper.ToDisplayTarget(@"\\.\DISPLAY2", "LG ULTRAFINE", @"\\?\DISPLAY#GSM5B7F#5", 5);

        result.Should().Be(new DisplayTarget(@"\\.\DISPLAY2", "LG ULTRAFINE", @"\\?\DISPLAY#GSM5B7F#5", DisplayOutputTechnology.Hdmi));
    }

    [Fact]
    public void ToDisplayTarget_TurnsEmptyNamesAndPathsIntoNull_AndTrimsThem()
    {
        // The native buffers are fixed-size and zero-filled; a monitor without an EDID has an empty friendly name
        var result = NativeMonitorWrapper.ToDisplayTarget(@" \\.\DISPLAY1 ", "  ", "", 0x80000000);

        result.Should().Be(new DisplayTarget(@"\\.\DISPLAY1", null, null, DisplayOutputTechnology.Internal));
    }

    [Fact]
    public void ToDisplayTarget_KeepsAConnectorTheEnumDoesNotName()
    {
        NativeMonitorWrapper.ToDisplayTarget(@"\\.\DISPLAY1", null, null, 3)!.OutputTechnology.Should().Be((DisplayOutputTechnology)3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ToDisplayTarget_WithoutASourceName_IsNull(string? gdiDeviceName)
    {
        // Without the GDI name the target cannot be joined with a monitor
        NativeMonitorWrapper.ToDisplayTarget(gdiDeviceName, "DELL", @"\\?\DISPLAY#DEL", 10).Should().BeNull();
    }

    [Fact]
    public void ToDisplayTarget_IsMarkedAsRead()
    {
        NativeMonitorWrapper.ToDisplayTarget(@"\\.\DISPLAY1", "DELL", @"\\?\DISPLAY#DEL", 10)!.TargetRead.Should().BeTrue();
    }

    [Fact]
    public void ToUnreadTarget_KeepsTheSourceAndConnector_AndIsMarkedAsNotRead()
    {
        // GET_TARGET_NAME failed: the path still joins its monitor, but its identity is unknown, so it must not be cached
        var result = NativeMonitorWrapper.ToUnreadTarget(@" \\.\DISPLAY2 ", 5);

        result.Should().Be(new DisplayTarget(@"\\.\DISPLAY2", null, null, DisplayOutputTechnology.Hdmi) { TargetRead = false });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void ToUnreadTarget_WithoutASourceName_IsNull(string? gdiDeviceName)
    {
        NativeMonitorWrapper.ToUnreadTarget(gdiDeviceName, 5).Should().BeNull();
    }

    #endregion

    #region ReadPathTargets

    /// <summary>A path as the tests see it: its source (adapter and source id) and the device path of its target.</summary>
    private sealed record FakePath(int Source, string DevicePath);

    private static DisplayTarget? TargetOf(FakePath path, string gdiDeviceName) =>
        new(gdiDeviceName, null, path.DevicePath, DisplayOutputTechnology.Hdmi);

    [Fact]
    public void ReadPathTargets_JoinsEachPathWithItsSourcesName_ReadingEachSourceOnce()
    {
        // Arrange - source 1 is cloned to two targets, source 2 has one
        var paths = new[] { new FakePath(1, "A"), new FakePath(1, "B"), new FakePath(2, "C") };
        var reads = new List<int>();

        // Act
        var result = NativeMonitorWrapper.ReadPathTargets(paths, p => p.Source,
            p => { reads.Add(p.Source); return $@"\\.\DISPLAY{p.Source}"; }, TargetOf);

        // Assert
        result.Select(t => (t.GdiDeviceName, t.DevicePath)).Should().Equal(
            (@"\\.\DISPLAY1", "A"), (@"\\.\DISPLAY1", "B"), (@"\\.\DISPLAY2", "C"));
        reads.Should().Equal(1, 2);
    }

    [Fact]
    public void ReadPathTargets_WhenASourcesNameCannotBeRead_LeavesOutEveryPathOfIt()
    {
        // Arrange - GET_SOURCE_NAME fails for source 1. Its cloned paths are left out together, so its monitor's identity
        // is unknown; joining only some of them would hash the monitor's id from part of its clone set
        var paths = new[] { new FakePath(1, "A"), new FakePath(2, "C"), new FakePath(1, "B") };

        // Act
        var result = NativeMonitorWrapper.ReadPathTargets(paths, p => p.Source,
            p => p.Source == 1 ? null : $@"\\.\DISPLAY{p.Source}", TargetOf);

        // Assert
        result.Select(t => t.DevicePath).Should().Equal("C");
    }

    [Fact]
    public void ReadPathTargets_SkipsAPathWithNoTarget()
    {
        var paths = new[] { new FakePath(1, "A"), new FakePath(2, "C") };

        var result = NativeMonitorWrapper.ReadPathTargets(paths, p => p.Source, p => $@"\\.\DISPLAY{p.Source}",
            (p, name) => p.Source == 1 ? null : TargetOf(p, name));

        result.Select(t => t.DevicePath).Should().Equal("C");
    }

    #endregion

    #region Native Layouts

    /// <summary>The sizes wingdi.h gives these structs on x64; a wrong layout would make Windows read or write past them.</summary>
    [Theory]
    [InlineData("DISPLAYCONFIG_PATH_SOURCE_INFO", 20)]
    [InlineData("DISPLAYCONFIG_PATH_TARGET_INFO", 48)]
    [InlineData("DISPLAYCONFIG_PATH_INFO", 72)]
    [InlineData("DISPLAYCONFIG_MODE_INFO", 64)]
    [InlineData("DISPLAYCONFIG_DEVICE_INFO_HEADER", 20)]
    [InlineData("DISPLAYCONFIG_SOURCE_DEVICE_NAME", 84)]
    [InlineData("DISPLAYCONFIG_TARGET_DEVICE_NAME", 420)]
    public void DisplayConfigStructs_HaveTheirNativeSize(string name, int size)
    {
        var type = typeof(NativeMonitorWrapper).Assembly.GetType($"WindowsAppManagerMcp.Native.{name}", throwOnError: true)!;

        Marshal.SizeOf(type).Should().Be(size);
    }

    [Theory]
    [InlineData("DISPLAYCONFIG_TARGET_DEVICE_NAME", "outputTechnology", 24)]
    [InlineData("DISPLAYCONFIG_TARGET_DEVICE_NAME", "monitorFriendlyDeviceName", 36)]
    [InlineData("DISPLAYCONFIG_TARGET_DEVICE_NAME", "monitorDevicePath", 164)]
    [InlineData("DISPLAYCONFIG_SOURCE_DEVICE_NAME", "viewGdiDeviceName", 20)]
    [InlineData("DISPLAYCONFIG_PATH_INFO", "targetInfo", 20)]
    [InlineData("DISPLAYCONFIG_PATH_TARGET_INFO", "outputTechnology", 16)]
    public void DisplayConfigStructFields_AreAtTheirNativeOffsets(string name, string field, int offset)
    {
        var type = typeof(NativeMonitorWrapper).Assembly.GetType($"WindowsAppManagerMcp.Native.{name}", throwOnError: true)!;
        type.GetField(field, BindingFlags.Public | BindingFlags.Instance).Should().NotBeNull();

        Marshal.OffsetOf(type, field).ToInt32().Should().Be(offset);
    }

    #endregion

    #region Real Read

    [Fact]
    public void GetDisplayTargets_OnThisMachine_ReadsWithoutThrowing()
    {
        // Read-only. A machine without an interactive display (a CI runner, a service session) may read null or nothing
        var targets = new NativeMonitorWrapper().GetDisplayTargets();

        if (targets is not null)
        {
            targets.Should().AllSatisfy(t => t.GdiDeviceName.Should().StartWith(@"\\.\"));
        }
    }

    [Fact]
    public void GetDisplayTargets_OnThisMachine_NamesTheMonitorsEnumDisplayMonitorsReports()
    {
        // Read-only. Every target's source is a monitor EnumDisplayMonitors lists (a topology change between the two reads
        // would break this, which a test run does not cause)
        var wrapper = new NativeMonitorWrapper();
        var monitors = wrapper.EnumerateMonitors().Select(m => m.Info.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = wrapper.GetDisplayTargets();

        if (targets is { Count: > 0 } && monitors.Count > 0)
        {
            targets.Select(t => t.GdiDeviceName).Should().OnlyContain(name => monitors.Contains(name));
        }
    }

    #endregion
}
