using WindowsAppManagerMcp.E2ETests.Harness;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>The rule of <see cref="MonitorAtAnotherScale"/>, checked without reading this machine's monitors.</summary>
public class MonitorAtAnotherScaleTests
{
    [Fact]
    public void MissingReasonFor_AMonitorAtAnotherScale_IsNull()
    {
        MonitorAtAnotherScale.MissingReasonFor(144, [144, 96]).Should().BeNull();
    }

    [Theory]
    [InlineData(new uint[] { 144 })]
    [InlineData(new uint[] { 144, 144 })]
    [InlineData(new uint[] { 144, 0 })] // a monitor whose DPI could not be read does not count
    public void MissingReasonFor_OnlyMonitorsAtThePrimarysScale_SaysWhatIsMissing(uint[] monitorDpis)
    {
        MonitorAtAnotherScale.MissingReasonFor(144, monitorDpis).Should().Contain("150%").And.Contain("second monitor");
    }
}
