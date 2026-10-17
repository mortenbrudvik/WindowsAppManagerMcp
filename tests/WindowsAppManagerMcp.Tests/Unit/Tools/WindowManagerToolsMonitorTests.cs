using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// The window tools' `monitor` parameter (roadmap M6.7, owner decision D13): snap_window, move_window_to_monitor and
/// snap_windows_batch resolve a name to an index before anything is changed. The services are mocks, so nothing real moves.
/// </summary>
public class WindowManagerToolsMonitorTests
{
    private static readonly nint Handle = 12345;

    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IMonitorService> _monitors = new(MockBehavior.Strict);

    /// <summary>Three monitors with ids, left to right; a test can change them while a call waits.</summary>
    private static readonly IReadOnlyList<MonitorInfo> Three =
    [
        TestDataFactory.CreateMonitorInfo(0) with { Id = "m-0000000a" },
        TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, 1920) with { Id = "m-0000000b" },
        TestDataFactory.CreateMonitorInfo(2, @"\\.\DISPLAY3", false, 3840) with { Id = "m-0000000c" }
    ];

    /// <summary>What GetAllMonitors lists now.</summary>
    private IReadOnlyList<MonitorInfo> _listed = Three;

    public WindowManagerToolsMonitorTests()
    {
        _monitors.Setup(m => m.GetAllMonitors()).Returns(() => _listed);
        _windows.Setup(w => w.SnapWindowsBatchAsync(It.IsAny<IReadOnlyList<(nint, SnapPosition, int?, bool)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<(nint Handle, SnapPosition, int?, bool)> p, int _, CancellationToken _) =>
                p.Select(x => WindowChangeResult.Done(x.Handle)).ToList());
        _windows.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns(TestDataFactory.CreateWindowInfo(handleValue: 12345));
        _windows.Setup(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()))
            .Returns((nint h, SnapPosition _, int? _, bool _) => WindowChangeResult.Done(h));
        _windows.Setup(w => w.MoveWindowToMonitor(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()))
            .Returns((nint h, int _, MonitorPositioning _, bool _) => WindowChangeResult.Done(h));
    }

    private WindowManagerTools CreateSut() => new(_windows.Object, Mock.Of<IForegroundService>(), monitorService: _monitors.Object);

    private void Resolves(string name, int index, Action? then = null) =>
        _monitors.Setup(m => m.ResolveMonitor(It.Is<MonitorSelector>(s => s.Name == name)))
            .Returns(() => MonitorResolution.Found(index, _listed[index]))
            .Callback(() => then?.Invoke());

    /// <summary>Docking renumbers the monitors after the name was resolved: the right one is enumerated first now.</summary>
    private void Redock() => _listed = [Three[2] with { Index = 0 }, Three[0] with { Index = 1 }, Three[1] with { Index = 2 }];

    /// <summary>The right monitor is unplugged after the name was resolved.</summary>
    private void Unplug() => _listed = [Three[0], Three[1]];

    #region snap_window

    [Fact]
    public async Task SnapWindow_WithAMonitorName_SnapsOnTheMonitorItNames()
    {
        // Arrange
        Resolves("left", 1);

        // Act
        var result = await CreateSut().SnapWindow(SnapPosition.LeftHalf, handle: Handle, monitor: MonitorSelector.Of("left"));

        // Assert
        result.Success.Should().BeTrue();
        _windows.Verify(w => w.SnapWindow(Handle, SnapPosition.LeftHalf, 1, true), Times.Once);
    }

    [Fact]
    public async Task SnapWindow_WithAMonitorIndex_PassesItOnWithoutResolvingAName()
    {
        // Act - the strict monitor mock would throw if a name were resolved
        await CreateSut().SnapWindow(SnapPosition.RightHalf, handle: Handle, monitor: MonitorSelector.OfIndex(2));

        // Assert - an index out of range stays the window service's InvalidMonitorIndex
        _windows.Verify(w => w.SnapWindow(Handle, SnapPosition.RightHalf, 2, true), Times.Once);
    }

    [Fact]
    public async Task SnapWindow_WhenTheMonitorsAreRenumberedWhileItWaits_SnapsOnTheMonitorTheNameNamed()
    {
        // Arrange - "right" is monitor 2 when the call starts; docking makes it monitor 0 before the call acts
        Resolves("right", 2, then: Redock);

        // Act
        await CreateSut().SnapWindow(SnapPosition.LeftHalf, handle: Handle, monitor: MonitorSelector.Of("right"));

        // Assert - found again by its id inside the gate, never by the stale index
        _windows.Verify(w => w.SnapWindow(Handle, SnapPosition.LeftHalf, 0, true), Times.Once);
    }

    [Fact]
    public async Task SnapWindow_WhenTheNamedMonitorIsGoneByTheTimeItActs_IsMonitorNotFound_AndNothingMoves()
    {
        // Arrange
        Resolves("right", 2, then: Unplug);

        // Act
        var act = () => CreateSut().SnapWindow(SnapPosition.LeftHalf, handle: Handle, monitor: MonitorSelector.Of("right"));

        // Assert
        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        error.Message.Should().Contain("m-0000000c").And.EndWith("Nothing was changed.");
        _windows.Verify(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task MoveWindowToMonitor_WhenTheMonitorsAreRenumberedWhileItWaits_MovesToTheMonitorTheNameNamed()
    {
        // Arrange
        Resolves("right", 2, then: Redock);

        // Act
        await CreateSut().MoveWindowToMonitor(handle: Handle, monitor: MonitorSelector.Of("right"));

        // Assert
        _windows.Verify(w => w.MoveWindowToMonitor(Handle, 0, MonitorPositioning.Center, true), Times.Once);
    }

    [Theory]
    [InlineData(nameof(WindowErrorCode.MonitorNotFound))]
    [InlineData(nameof(WindowErrorCode.AmbiguousMonitor))]
    public async Task SnapWindow_WithAMonitorNameThatNamesNoSingleMonitor_FailsWithItsCode_AndNothingMoves(string code)
    {
        // Arrange
        _monitors.Setup(m => m.ResolveMonitor(It.IsAny<MonitorSelector>()))
            .Returns(MonitorResolution.Failed(code, "monitor 'left' names none. Nothing was done."));

        // Act
        var act = () => CreateSut().SnapWindow(SnapPosition.LeftHalf, handle: Handle, monitor: MonitorSelector.Of("left"));

        // Assert
        var error = (await act.Should().ThrowAsync<ToolErrorException>()).Which;
        error.ErrorCode.Should().Be(code);
        error.Message.Should().EndWith("Nothing was changed.");
        _windows.Verify(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SnapWindow_WithMonitorAll_IsInvalidArguments()
    {
        // Arrange
        _monitors.Setup(m => m.ResolveMonitor(It.IsAny<MonitorSelector>())).Returns(MonitorResolution.Found(MonitorSelection.AllMonitors, null));

        // Act
        var act = () => CreateSut().SnapWindow(SnapPosition.LeftHalf, handle: Handle, monitor: MonitorSelector.Of("all"));

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    #endregion

    #region move_window_to_monitor

    [Fact]
    public async Task MoveWindowToMonitor_WithAMonitorName_MovesTheWindowThere()
    {
        // Arrange
        Resolves("primary", 0);

        // Act
        var result = await CreateSut().MoveWindowToMonitor(handle: Handle, monitor: MonitorSelector.Of("primary"));

        // Assert
        result.Success.Should().BeTrue();
        _windows.Verify(w => w.MoveWindowToMonitor(Handle, 0, MonitorPositioning.Center, true), Times.Once);
    }

    [Fact]
    public async Task MoveWindowToMonitor_WithAnIndex_MovesThere_WithoutResolvingAName()
    {
        // Act
        await CreateSut().MoveWindowToMonitor(MonitorSelector.OfIndex(1), handle: Handle);

        // Assert
        _windows.Verify(w => w.MoveWindowToMonitor(Handle, 1, MonitorPositioning.Center, true), Times.Once);
    }

    #endregion

    #region set_window_bounds in percent

    /// <summary>The primary, and a 2560-wide monitor right of it whose work area ends above a 40 px taskbar.</summary>
    private static readonly IReadOnlyList<MonitorInfo> Desk =
    [
        TestDataFactory.CreateMonitorInfo(0),
        TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, 1920, 0, 2560, 1440, 1920, 0, 2560, 1400)
    ];

    private void PercentSetup()
    {
        _listed = Desk;
        _monitors.Setup(m => m.GetMonitorForWindow(Handle)).Returns(Desk[1]);
        _windows.Setup(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint h, int _, int _, int _, int _, bool _) => WindowChangeResult.Done(h));
        _windows.Setup(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns((nint h, int _, int _, bool _) => WindowChangeResult.Done(h));
        _windows.Setup(w => w.ResizeWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((nint h, int _, int _) => WindowChangeResult.Done(h));
    }

    [Fact]
    public async Task SetWindowBounds_InPercent_IsAShareOfTheWindowsOwnMonitorsWorkArea()
    {
        // Arrange
        PercentSetup();

        // Act
        var result = await CreateSut().SetWindowBounds(50, 0, 50, 100, handle: Handle, unit: BoundsUnit.Percent);

        // Assert - the right half of 1920..4480 x 0..1400
        result.Success.Should().BeTrue();
        _windows.Verify(w => w.SetWindowFrame(Handle, 3200, 0, 1280, 1400, true), Times.Once);
    }

    [Theory]
    [InlineData(0, 1920, 853)]
    [InlineData(1, 2773, 853)]
    [InlineData(2, 3626, 854)]
    public async Task SetWindowBounds_InThirds_TilesTheWorkAreaAsSnapWindowDoes(int third, int left, int width)
    {
        // Arrange
        PercentSetup();

        // Act
        await CreateSut().SetWindowBounds(third * 100.0 / 3, 0, 100.0 / 3, 100, handle: Handle, unit: BoundsUnit.Percent);

        // Assert - 853, 853 and 854 px, as SnapMath's left_third, center_third and right_third
        _windows.Verify(w => w.SetWindowFrame(Handle, left, 0, width, 1400, true), Times.Once);
    }

    [Fact]
    public async Task SetWindowBounds_InPercent_OfAnotherMonitorsFullBounds()
    {
        // Arrange
        PercentSetup();
        Resolves("primary", 0);

        // Act
        await CreateSut().SetWindowBounds(25, 50, 50, 50, handle: Handle, unit: BoundsUnit.Percent, relativeTo: RelativeArea.Monitor,
            monitor: MonitorSelector.Of("primary"));

        // Assert - a quarter of 1920 x 1080, taskbar included
        _windows.Verify(w => w.SetWindowFrame(Handle, 480, 540, 960, 540, true), Times.Once);
    }

    [Fact]
    public async Task SetWindowBounds_APositionInPercent_MovesTheWindow_AndASizeInPercent_ResizesIt()
    {
        // Arrange
        PercentSetup();
        var sut = CreateSut();

        // Act
        await sut.SetWindowBounds(x: 50, y: 50, handle: Handle, unit: BoundsUnit.Percent);
        await sut.SetWindowBounds(width: 25, height: 100, handle: Handle, unit: BoundsUnit.Percent);

        // Assert
        _windows.Verify(w => w.MoveWindow(Handle, 3200, 700, true), Times.Once);
        _windows.Verify(w => w.ResizeWindow(Handle, 640, 1400), Times.Once);
    }

    [Theory]
    [InlineData(10.5, 0, 800, 600, null, "whole number of pixels")]
    [InlineData(50, 0, 60, 100, BoundsUnit.Percent, "x + width")]
    [InlineData(-1, 0, 50, 100, BoundsUnit.Percent, "x must be from 0 to 100")]
    [InlineData(0, 0, 0.001, 100, BoundsUnit.Percent, "less than one pixel")]
    public async Task SetWindowBounds_WithValuesTheUnitCannotTake_IsInvalidArguments_AndNothingMoves(
        double x, double y, double width, double height, BoundsUnit? unit, string message)
    {
        // Arrange
        PercentSetup();

        // Act
        var result = await CreateSut().SetWindowBounds(x, y, width, height, handle: Handle, unit: unit);

        // Assert
        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        result.Error.Should().Contain(message);
        _windows.Verify(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetWindowBounds_MonitorOrRelativeToInPixels_IsInvalidArguments()
    {
        // Act
        var withMonitor = await CreateSut().SetWindowBounds(0, 0, 800, 600, handle: Handle, monitor: MonitorSelector.OfIndex(1));
        var withRelativeTo = await CreateSut().SetWindowBounds(0, 0, 800, 600, handle: Handle, relativeTo: RelativeArea.Monitor);

        // Assert
        withMonitor.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        withMonitor.Error.Should().Contain("unit 'percent'");
        withRelativeTo.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task SetWindowBounds_InPercent_OnANamedMonitorThatIsGoneByTheTimeItActs_IsMonitorNotFound()
    {
        // Arrange
        PercentSetup();
        Resolves("primary", 0, then: () => _listed = [Desk[1] with { Index = 0 }]);

        // Act
        var act = () => CreateSut().SetWindowBounds(0, 0, 50, 50, handle: Handle, unit: BoundsUnit.Percent, monitor: MonitorSelector.Of("primary"));

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        _windows.Verify(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetWindowBounds_InPercent_OnAMonitorIndexThatIsNoMonitor_IsInvalidMonitorIndex()
    {
        // Arrange
        PercentSetup();

        // Act
        var result = await CreateSut().SetWindowBounds(0, 0, 50, 50, handle: Handle, unit: BoundsUnit.Percent, monitor: MonitorSelector.OfIndex(4));

        // Assert
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidMonitorIndex));
        result.Error.Should().Contain("valid indexes are 0 to 1");
    }

    #endregion

    #region -1 where every monitor is not a target

    public static TheoryData<string> OneMonitorTools => new() { "snap_window", "move_window_to_monitor", "set_window_bounds", "snap_windows_batch" };

    [Theory]
    [MemberData(nameof(OneMonitorTools))]
    public async Task MonitorMinusOne_WhereATool_TakesOneMonitor_IsInvalidMonitorIndex_AndNothingMoves(string tool)
    {
        // Arrange - -1 is every monitor, which only the captures take
        PercentSetup();
        var sut = CreateSut();
        var minusOne = MonitorSelector.OfIndex(-1);

        // Act
        string? code;
        if (tool == "snap_windows_batch")
        {
            var batch = await sut.SnapWindowsBatch([new WindowSnapPlacement("left_half", Handle: 111, Monitor: minusOne)], delayBetweenMs: 0);
            code = batch.Results.Single().ErrorCode;
        }
        else
        {
            Func<Task> act = tool switch
            {
                "snap_window" => () => sut.SnapWindow(SnapPosition.LeftHalf, handle: Handle, monitor: minusOne),
                "move_window_to_monitor" => () => sut.MoveWindowToMonitor(handle: Handle, monitor: minusOne),
                _ => () => sut.SetWindowBounds(0, 0, 50, 50, handle: Handle, unit: BoundsUnit.Percent, monitor: minusOne)
            };
            code = (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode;
        }

        // Assert
        code.Should().Be(nameof(WindowErrorCode.InvalidMonitorIndex));
        _windows.Verify(w => w.SnapWindow(It.IsAny<nint>(), It.IsAny<SnapPosition>(), It.IsAny<int?>(), It.IsAny<bool>()), Times.Never);
        _windows.Verify(w => w.MoveWindowToMonitor(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()), Times.Never);
        _windows.Verify(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region snap_windows_batch

    [Fact]
    public async Task SnapWindowsBatch_ResolvesEachItemsMonitor_AndAnItemWhoseMonitorIsNotFoundFailsAlone()
    {
        // Arrange
        Resolves("right", 2);
        _monitors.Setup(m => m.ResolveMonitor(It.Is<MonitorSelector>(s => s.Name == "m-00000000")))
            .Returns(MonitorResolution.Failed(nameof(WindowErrorCode.MonitorNotFound), "No monitor has the id 'm-00000000' now. Nothing was done."));
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)>? sent = null;
        _windows.Setup(w => w.SnapWindowsBatchAsync(It.IsAny<IReadOnlyList<(nint, SnapPosition, int?, bool)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<(nint, SnapPosition, int?, bool)> p, int _, CancellationToken _) => sent = p)
            .ReturnsAsync((IReadOnlyList<(nint Handle, SnapPosition, int?, bool)> p, int _, CancellationToken _) =>
                p.Select(x => WindowChangeResult.Done(x.Handle)).ToList());

        // Act
        var result = await CreateSut().SnapWindowsBatch(
        [
            new WindowSnapPlacement("left_half", Handle: 111, Monitor: MonitorSelector.Of("right")),
            new WindowSnapPlacement("left_half", Handle: 222, Monitor: MonitorSelector.Of("m-00000000")),
            new WindowSnapPlacement("left_half", Handle: 333, Monitor: MonitorSelector.OfIndex(-1))
        ], delayBetweenMs: 0);

        // Assert
        result.Results.Select(r => r.Success).Should().Equal(true, false, false);
        result.Results[1].ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        result.Results[1].Error.Should().StartWith("Item 1: placements[1].monitor:");
        result.Results[2].ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidMonitorIndex));
        sent!.Should().ContainSingle().Which.Should().Be(((nint)111, SnapPosition.LeftHalf, (int?)2, true));
    }

    [Fact]
    public async Task SnapWindowsBatch_AnItemWhoseNamedMonitorIsGoneByTheTimeItActs_FailsAlone()
    {
        // Arrange
        Resolves("right", 2, then: Unplug);
        Resolves("left", 0);
        IReadOnlyList<(nint Handle, SnapPosition Position, int? MonitorIndex, bool BringToFront)>? sent = null;
        _windows.Setup(w => w.SnapWindowsBatchAsync(It.IsAny<IReadOnlyList<(nint, SnapPosition, int?, bool)>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((IReadOnlyList<(nint, SnapPosition, int?, bool)> p, int _, CancellationToken _) => sent = p)
            .ReturnsAsync((IReadOnlyList<(nint Handle, SnapPosition, int?, bool)> p, int _, CancellationToken _) =>
                p.Select(x => WindowChangeResult.Done(x.Handle)).ToList());

        // Act
        var result = await CreateSut().SnapWindowsBatch(
        [
            new WindowSnapPlacement("left_half", Handle: 111, Monitor: MonitorSelector.Of("right")),
            new WindowSnapPlacement("left_half", Handle: 222, Monitor: MonitorSelector.Of("left"))
        ], delayBetweenMs: 0);

        // Assert
        result.Results.Select(r => r.Success).Should().Equal(false, true);
        result.Results[0].ErrorCode.Should().Be(nameof(WindowErrorCode.MonitorNotFound));
        sent!.Should().ContainSingle().Which.Should().Be(((nint)222, SnapPosition.LeftHalf, (int?)0, true));
    }

    #endregion
}
