using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// How the monitors are enumerated (NativeMonitorWrapper.ReadMonitors, a pure helper over one EnumDisplayMonitors pass):
/// a monitor that cannot be read, or an enumeration that fails, never yields a shorter list whose later monitors are
/// renumbered (a "monitor: 2" would then reach another physical monitor); a monitor whose DPI cannot be read says so,
/// rather than report 96 as read. Then get_monitors' primaryIndex is null without a primary, and a failed monitor name
/// resolves to no index, never to -1 (every monitor).
/// </summary>
public class NativeMonitorWrapperEnumerationTests
{
    private static MonitorInfo Monitor(string device, bool primary = false) =>
        NativeMonitorWrapper.ToMonitorInfo(device, primary, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 96);

    private static MonitorsResult GetMonitors(params MonitorInfo[] monitors)
    {
        var service = new Mock<IMonitorService>();
        service.Setup(m => m.GetAllMonitors()).Returns(monitors);
        return new MonitorInfoTools(service.Object, Mock.Of<IScreenCaptureWrapper>()).GetMonitors();
    }

    private static NativeMonitorWrapper.MonitorEnumeration Pass(bool completed, params MonitorInfo?[] monitors) =>
        new(completed, monitors.Select((m, i) => ((nint)(0x10 + i), m)).ToList());

    #region ReadMonitors

    [Fact]
    public void ReadMonitors_NumbersTheMonitorsFromZero_InEnumerationOrder()
    {
        var result = NativeMonitorWrapper.ReadMonitors(() => Pass(true, Monitor(@"\\.\DISPLAY2", primary: true), Monitor(@"\\.\DISPLAY1")));

        result.Select(m => (m.Handle, m.Info.Index, m.Info.DeviceName)).Should().Equal(
            ((nint)0x10, 0, @"\\.\DISPLAY2"), ((nint)0x11, 1, @"\\.\DISPLAY1"));
    }

    [Fact]
    public void ReadMonitors_AMonitorThatCannotBeRead_IsNoShorterRenumberedList_ButEmpty()
    {
        // Arrange - the second of three monitors fails GetMonitorInfoW on every pass
        var passes = 0;

        // Act
        var result = NativeMonitorWrapper.ReadMonitors(() =>
        {
            passes++;
            return Pass(true, Monitor(@"\\.\DISPLAY1", true), null, Monitor(@"\\.\DISPLAY3"));
        });

        // Assert - the third monitor must never become monitor 1
        result.Should().BeEmpty();
        passes.Should().Be(NativeMonitorWrapper.EnumerationAttempts);
    }

    [Fact]
    public void ReadMonitors_AnEnumerationThatFails_IsEmpty_NotWhatItListedSoFar()
    {
        var result = NativeMonitorWrapper.ReadMonitors(() => Pass(false, Monitor(@"\\.\DISPLAY1", true)));

        result.Should().BeEmpty();
    }

    [Fact]
    public void ReadMonitors_AFailedPass_IsTriedAgain_AndTheFirstWholePassIsKept()
    {
        // Arrange - the displays change during the first pass
        var passes = 0;

        // Act
        var result = NativeMonitorWrapper.ReadMonitors(() => ++passes == 1
            ? Pass(true, Monitor(@"\\.\DISPLAY1", true), null)
            : Pass(true, Monitor(@"\\.\DISPLAY1", true), Monitor(@"\\.\DISPLAY2")));

        // Assert
        result.Select(m => m.Info.DeviceName).Should().Equal(@"\\.\DISPLAY1", @"\\.\DISPLAY2");
        passes.Should().Be(2);
    }

    #endregion

    #region DPI

    [Fact]
    public void ToMonitorInfo_WithItsDpi_HasThatDpiAndScale()
    {
        var monitor = NativeMonitorWrapper.ToMonitorInfo(@"\\.\DISPLAY1", true, new MonitorRect(0, 0, 3840, 2160),
            new MonitorRect(0, 0, 3840, 2100), 144);

        monitor.Dpi.Should().Be(144);
        monitor.ScaleFactor.Should().Be(1.5);
        monitor.DpiUnknown.Should().BeFalse();
    }

    [Fact]
    public void ToMonitorInfo_WithoutItsDpi_SaysTheDpiIsUnknown()
    {
        var monitor = NativeMonitorWrapper.ToMonitorInfo(@"\\.\DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080),
            new MonitorRect(0, 0, 1920, 1040), null);

        monitor.DpiUnknown.Should().BeTrue();
        monitor.ScaleFactor.Should().Be(1, "arithmetic that needs a scale still gets one");
    }

    [Fact]
    public void GetMonitors_AMonitorWhoseDpiIsUnknown_LeavesOutItsDpiAndScale()
    {
        // Arrange
        var unknown = TestDataFactory.CreateMonitorInfo() with { DpiUnknown = true };

        // Act
        var dto = GetMonitors(unknown).Monitors.Single();

        // Assert
        dto.Dpi.Should().BeNull();
        dto.ScaleFactor.Should().BeNull();
    }

    [Fact]
    public void GetMonitors_AMonitorWhoseDpiWasRead_ReportsIt()
    {
        var dto = GetMonitors(TestDataFactory.CreateMonitorInfo(scaleFactor: 1.25)).Monitors.Single();

        dto.Dpi.Should().Be(120);
        dto.ScaleFactor.Should().Be(1.25);
    }

    #endregion

    #region primaryIndex and failed resolutions

    [Fact]
    public void GetMonitors_WithoutMonitors_HasNoPrimaryIndex()
    {
        var result = GetMonitors();

        result.PrimaryIndex.Should().BeNull();
    }

    [Fact]
    public void GetMonitors_WithoutAPrimaryMonitor_HasNoPrimaryIndex()
    {
        var result = GetMonitors(TestDataFactory.CreateMonitorInfo(0, isPrimary: false));

        result.PrimaryIndex.Should().BeNull();
    }

    [Fact]
    public void AFailedResolution_HasNoIndex_SoItIsNeverEveryMonitor()
    {
        var resolution = MonitorSelection.Resolve([TestDataFactory.CreateMonitorInfo()], MonitorSelector.Of("m-00000000"), () => null);

        resolution.Succeeded.Should().BeFalse();
        resolution.Index.Should().BeNull();
        resolution.Index.Should().NotBe(MonitorSelection.AllMonitors);
    }

    #endregion
}
