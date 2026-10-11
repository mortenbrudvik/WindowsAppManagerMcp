namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for MonitorService.
/// Tests T3.1, T3.2, T3.6 from BACKLOG-TECHNICAL.md.
///
/// Note: MonitorService uses P/Invoke for monitor enumeration.
/// The snap calculation is tested in SnapMathTests;
/// GetAllMonitors and GetPrimaryMonitor are integration tests
/// that run against the actual system (every Windows has monitors).
/// </summary>
public class MonitorServiceTests
{
    #region T3.1: GetAllMonitors Integration Tests

    /// <summary>
    /// T3.1.1: Test GetAllMonitors returns non-empty list.
    /// Every Windows system has at least one monitor.
    /// </summary>
    [Fact]
    public void GetAllMonitors_ReturnsNonEmptyList()
    {
        // Arrange
        var sut = new MonitorService();

        // Act
        var result = sut.GetAllMonitors();

        // Assert
        result.Should().NotBeNull();
        result.Should().NotBeEmpty("Every Windows system has at least one monitor");
    }

    [Fact]
    public void GetAllMonitors_ReturnsValidMonitorInfo()
    {
        // Arrange
        var sut = new MonitorService();

        // Act
        var result = sut.GetAllMonitors();

        // Assert
        foreach (var monitor in result)
        {
            monitor.DeviceName.Should().NotBeNullOrEmpty();
            monitor.Bounds.Width.Should().BeGreaterThan(0);
            monitor.Bounds.Height.Should().BeGreaterThan(0);
            monitor.WorkArea.Width.Should().BeGreaterThan(0);
            monitor.WorkArea.Height.Should().BeGreaterThan(0);
            monitor.ScaleFactor.Should().BeGreaterOrEqualTo(1.0);
        }
    }

    [Fact]
    public void GetAllMonitors_HasSequentialIndices()
    {
        // Arrange
        var sut = new MonitorService();

        // Act
        var result = sut.GetAllMonitors();

        // Assert
        for (int i = 0; i < result.Count; i++)
        {
            result[i].Index.Should().Be(i, "Monitor indices should be sequential starting at 0");
        }
    }

    #endregion

    #region T3.2: GetPrimaryMonitor Integration Tests

    /// <summary>
    /// T3.2.1: Test GetPrimaryMonitor returns monitor with IsPrimary: true.
    /// Every Windows system has exactly one primary monitor.
    /// </summary>
    [Fact]
    public void GetPrimaryMonitor_ReturnsPrimaryMonitor()
    {
        // Arrange
        var sut = new MonitorService();

        // Act
        var result = sut.GetPrimaryMonitor();

        // Assert
        result.Should().NotBeNull();
        result.IsPrimary.Should().BeTrue("GetPrimaryMonitor should return the primary monitor");
    }

    [Fact]
    public void GetPrimaryMonitor_ReturnsValidMonitorInfo()
    {
        // Arrange
        var sut = new MonitorService();

        // Act
        var result = sut.GetPrimaryMonitor();

        // Assert
        result.DeviceName.Should().NotBeNullOrEmpty();
        result.Bounds.Width.Should().BeGreaterThan(0);
        result.Bounds.Height.Should().BeGreaterThan(0);
        result.WorkArea.Width.Should().BeGreaterThan(0);
        result.WorkArea.Height.Should().BeGreaterThan(0);
    }

    [Fact]
    public void GetPrimaryMonitor_IsIncludedInGetAllMonitors()
    {
        // Arrange
        var sut = new MonitorService();

        // Act
        var primary = sut.GetPrimaryMonitor();
        var all = sut.GetAllMonitors();

        // Assert
        all.Should().Contain(m => m.IsPrimary && m.DeviceName == primary.DeviceName,
            "Primary monitor should be included in GetAllMonitors");
    }

    [Fact]
    public void GetAllMonitors_HasExactlyOnePrimary()
    {
        // Arrange
        var sut = new MonitorService();

        // Act
        var result = sut.GetAllMonitors();

        // Assert
        result.Count(m => m.IsPrimary).Should().Be(1,
            "There should be exactly one primary monitor");
    }

    #endregion
}
