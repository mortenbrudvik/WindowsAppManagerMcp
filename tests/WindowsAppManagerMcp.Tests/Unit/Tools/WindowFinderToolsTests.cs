namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// Unit tests for WindowFinderTools.
/// Tests the MCP tool layer that exposes window finding functionality.
/// </summary>
public class WindowFinderToolsTests
{
    private readonly Mock<IWindowService> _mockWindowService;

    public WindowFinderToolsTests()
    {
        _mockWindowService = new Mock<IWindowService>();
    }

    private WindowFinderTools CreateSut() => new WindowFinderTools(_mockWindowService.Object);

    #region FindWindows Tests

    [Fact]
    public void FindWindows_CallsServiceWithCorrectParameters()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.FindWindows(
            titleContains: "Test",
            processName: "notepad",
            processId: 1234,
            handle: 5678,
            visibleOnly: false,
            className: "Shell_TrayWnd",
            includeCloaked: true,
            includeToolWindows: true,
            includeShell: true);

        // Assert
        _mockWindowService.Verify(w => w.FindWindows(new WindowQuery
        {
            TitleContains = "Test",
            ProcessName = "notepad",
            ProcessId = 1234,
            Handle = new nint(5678),
            VisibleOnly = false,
            ClassName = "Shell_TrayWnd",
            IncludeCloaked = true,
            IncludeToolWindows = true,
            IncludeShell = true
        }), Times.Once);
    }

    [Fact]
    public void FindWindows_ReportsTheVisibleFrame_BesideTheWindowRect()
    {
        // Arrange - the window rect has the invisible resize borders (7 px left, right and bottom); set_window_bounds
        // takes the visible frame, so an agent that copies a rect must be able to copy that one
        var window = TestDataFactory.CreateWindowInfo(0x100, x: 93, y: 100, width: 814, height: 607) with
        {
            VisibleBounds = new WindowRect(100, 100, 800, 600)
        };
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([window, window with { Handle = 0x200, VisibleBounds = null }]);

        // Act
        var result = CreateSut().FindWindows();

        // Assert
        result[0].Bounds.Should().Be(new BoundsDto(93, 100, 814, 607));
        result[0].VisibleBounds.Should().Be(new BoundsDto(100, 100, 800, 600));
        result[1].VisibleBounds.Should().BeNull("DWM could not report it");
    }

    [Fact]
    public void FindWindows_ByDefault_AsksForVisibleUserFacingWindows()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.FindWindows();

        // Assert - the default query keeps the user-facing filter
        _mockWindowService.Verify(w => w.FindWindows(It.Is<WindowQuery>(q =>
            q == new WindowQuery() && q.VisibleOnly && !q.SkipsUserFacingFilter)), Times.Once);
    }

    [Fact]
    public void FindWindows_MapsWindowIdentityToDto()
    {
        // Arrange - a UWP frame owned by another window, reported under the app's process
        var windowInfo = TestDataFactory.CreateWindowInfo(processName: "CalculatorApp", processId: 600) with
        {
            ClassName = "ApplicationFrameWindow",
            IsCloaked = true,
            OwnerHandle = new nint(777),
            HostProcessName = "ApplicationFrameHost",
            HostProcessId = 500
        };
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo> { windowInfo });
        var sut = CreateSut();

        // Act
        var dto = sut.FindWindows().Should().ContainSingle().Which;

        // Assert
        dto.ClassName.Should().Be("ApplicationFrameWindow");
        dto.IsCloaked.Should().BeTrue();
        dto.OwnerHandle.Should().Be(777);
        dto.ProcessName.Should().Be("CalculatorApp");
        dto.HostProcessName.Should().Be("ApplicationFrameHost");
        dto.HostProcessId.Should().Be(500);
    }

    [Fact]
    public void FindWindows_ReportsIsTopmost_OnlyForATopmostWindow()
    {
        // Roadmap M6.7: true or left out, so a typical listing costs nothing more
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns(new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(handleValue: 1) with { IsTopmost = true },
            TestDataFactory.CreateWindowInfo(handleValue: 2)
        });

        var rows = CreateSut().FindWindows();

        rows.Select(r => r.IsTopmost).Should().Equal(true, null);
    }

    [Fact]
    public void FindWindows_LeavesOwnerAndHostNull_ForAnOrdinaryWindow()
    {
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo> { TestDataFactory.CreateWindowInfo() with { ClassName = "Notepad" } });

        var dto = CreateSut().FindWindows().Should().ContainSingle().Which;

        dto.ClassName.Should().Be("Notepad");
        dto.IsCloaked.Should().BeFalse();
        dto.OwnerHandle.Should().BeNull();
        dto.HostProcessName.Should().BeNull();
        dto.HostProcessId.Should().BeNull();
    }

    [Fact]
    public void FindWindows_ReturnsEmptyList_WhenNoWindowsFound()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void FindWindows_MapsWindowInfoToDto()
    {
        // Arrange
        var windowInfo = TestDataFactory.CreateWindowInfo(
            handleValue: 12345,
            title: "Test Window",
            processName: "testapp",
            processId: 999,
            x: 100, y: 200,
            width: 800, height: 600,
            state: WindowState.Normal,
            isVisible: true,
            monitorIndex: 1);

        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo> { windowInfo });
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result.Should().HaveCount(1);
        var dto = result[0];
        dto.Handle.Should().Be(12345);
        dto.Title.Should().Be("Test Window");
        dto.ProcessName.Should().Be("testapp");
        dto.ProcessId.Should().Be(999);
        dto.Bounds.X.Should().Be(100);
        dto.Bounds.Y.Should().Be(200);
        dto.Bounds.Width.Should().Be(800);
        dto.Bounds.Height.Should().Be(600);
        dto.State.Should().Be("normal");
        dto.IsVisible.Should().BeTrue();
        dto.MonitorIndex.Should().Be(1);
    }

    [Fact]
    public void FindWindows_ConvertsNullHandleCorrectly()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo>());
        var sut = CreateSut();

        // Act
        sut.FindWindows(handle: null);

        // Assert
        _mockWindowService.Verify(w => w.FindWindows(It.Is<WindowQuery>(q => q.Handle == null)), Times.Once);
    }

    #endregion

    #region GetForegroundWindow Tests

    [Fact]
    public void GetForegroundWindow_WhenNoWindowIsInTheForeground_IsANoForegroundWindowToolError()
    {
        // Arrange
        _mockWindowService.Setup(w => w.GetForegroundWindow())
            .Returns((WindowInfo?)null);
        var sut = CreateSut();

        // Act
        var act = () => sut.GetForegroundWindow();

        // Assert - roadmap M2.1: never a null result, which a client receives as empty content
        act.Should().Throw<WindowsAppManagerMcp.Infrastructure.ToolErrorException>()
            .Where(e => e.ErrorCode == nameof(WindowErrorCode.NoForegroundWindow) && e.Message.Contains("No window is in the foreground"));
    }

    [Fact]
    public void GetForegroundWindow_ReturnsDto_WhenWindowExists()
    {
        // Arrange
        var windowInfo = TestDataFactory.CreateWindowInfo(title: "Foreground Window");
        _mockWindowService.Setup(w => w.GetForegroundWindow())
            .Returns(windowInfo);
        var sut = CreateSut();

        // Act
        var result = sut.GetForegroundWindow();

        // Assert
        result.Should().NotBeNull();
        result!.Title.Should().Be("Foreground Window");
    }

    #endregion

    #region WindowState Mapping Tests

    [Theory]
    [InlineData(WindowState.Normal, "normal")]
    [InlineData(WindowState.Minimized, "minimized")]
    [InlineData(WindowState.Maximized, "maximized")]
    public void FindWindows_MapsWindowStateToLowerCase(WindowState state, string expectedString)
    {
        // Arrange
        var windowInfo = TestDataFactory.CreateWindowInfo(state: state);
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns(new List<WindowInfo> { windowInfo });
        var sut = CreateSut();

        // Act
        var result = sut.FindWindows();

        // Assert
        result[0].State.Should().Be(expectedString);
    }

    #endregion

    #region Bounded titles (roadmap M3.F14)

    private static readonly string LongTitle = "Inbox - " + new string('a', 5000);

    private static string CutTitle => LongTitle[..(AppText.MaxLength - 1)] + "…";

    [Fact]
    public void FindWindows_CutsALongTitle_WithAnEllipsis()
    {
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns([TestDataFactory.CreateWindowInfo(title: LongTitle), TestDataFactory.CreateWindowInfo(title: "Untitled - Notepad")]);

        var result = CreateSut().FindWindows(titleContains: "Inbox");

        result.Select(w => w.Title).Should().Equal(CutTitle, "Untitled - Notepad");
    }

    [Fact]
    public void GetForegroundWindow_CutsALongTitle()
    {
        var window = TestDataFactory.CreateWindowInfo(title: LongTitle);
        _mockWindowService.Setup(w => w.GetForegroundWindow()).Returns(window);

        CreateSut().GetForegroundWindow().Title.Should().Be(CutTitle);
    }

    [Theory]
    [InlineData(WindowDetail.Compact)]
    [InlineData(WindowDetail.Standard)]
    [InlineData(WindowDetail.Full)]
    public void GetWindowInfo_CutsALongTitle_AtEveryDetail(WindowDetail detail)
    {
        var window = TestDataFactory.CreateWindowInfo(title: LongTitle);
        _mockWindowService.Setup(w => w.IsValidWindow(window.Handle)).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
        _mockWindowService.Setup(w => w.GetWindowDetails(window.Handle))
            .Returns(new WindowDetails(window, false, 3, false, true, true, 96));

        var result = CreateSut().GetWindowInfo(handle: window.Handle, detail: detail);

        result.Title.Should().Be(CutTitle);
    }

    // get_window_info detail 'full': the integrity of the window's process (roadmap M6.6)

    private readonly Mock<IProcessSecurityProbe> _mockSecurityProbe = new();

    private static ProcessIntegrity Integrity(int processId, IntegrityLevel level) => level switch
    {
        IntegrityLevel.Medium => new(processId, level, ProcessIntegrity.MediumRid, false, null),
        IntegrityLevel.High => new(processId, level, ProcessIntegrity.HighRid, true, null),
        _ => new(processId, level, null, null, "access denied")
    };

    /// <summary>A medium-integrity server; the processes at these levels, Medium for any other.</summary>
    private WindowFinderTools CreateSutWithProbe(IReadOnlyDictionary<int, IntegrityLevel>? levels = null)
    {
        _mockSecurityProbe.Setup(p => p.ServerIntegrity).Returns(Integrity(9_999, IntegrityLevel.Medium));
        _mockSecurityProbe.Setup(p => p.GetIntegrity(It.IsAny<int>()))
            .Returns<int>(id => Integrity(id, levels != null && levels.TryGetValue(id, out var level) ? level : IntegrityLevel.Medium));
        return new WindowFinderTools(_mockWindowService.Object, securityProbe: _mockSecurityProbe.Object);
    }

    private WindowInfo UseWindow(WindowInfo window)
    {
        _mockWindowService.Setup(w => w.IsValidWindow(window.Handle)).Returns(true);
        _mockWindowService.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
        _mockWindowService.Setup(w => w.GetWindowDetails(window.Handle)).Returns(new WindowDetails(window, false, 3, false, true, true, 96));
        return window;
    }

    [Theory]
    [InlineData(IntegrityLevel.Medium, false, null)]
    [InlineData(IntegrityLevel.High, true, true)]
    [InlineData(IntegrityLevel.LikelyElevated, null, true)]
    [InlineData(IntegrityLevel.Restricted, null, null)]
    public void GetWindowInfo_Full_ReportsTheIntegrityOfTheWindowsProcess_AndAboveServerOnlyWhenTrue(
        IntegrityLevel level, bool? elevated, bool? aboveServer)
    {
        // Arrange
        var window = UseWindow(TestDataFactory.CreateWindowInfo(0x100, "Administrator: Windows PowerShell", "powershell", 4_242));
        var sut = CreateSutWithProbe(new Dictionary<int, IntegrityLevel> { [4_242] = level });

        // Act
        var result = sut.GetWindowInfo(handle: window.Handle, detail: WindowDetail.Full);

        // Assert
        result.IntegrityLevel.Should().Be(level);
        result.Elevated.Should().Be(elevated);
        result.AboveServer.Should().Be(aboveServer);
    }

    [Fact]
    public void GetWindowInfo_Full_OfAUwpWindow_ReportsTheAppsIntegrity_AndAboveServerWhenEitherProcessIsAbove()
    {
        // Arrange - the window reports the app's process; ApplicationFrameHost owns the frame
        var window = UseWindow(TestDataFactory.CreateWindowInfo(0x200, "Calculator", "CalculatorApp", 500) with
        {
            HostProcessName = "ApplicationFrameHost",
            HostProcessId = 400
        });
        var sut = CreateSutWithProbe(new Dictionary<int, IntegrityLevel> { [400] = IntegrityLevel.High });

        // Act
        var result = sut.GetWindowInfo(handle: window.Handle, detail: WindowDetail.Full);

        // Assert
        result.IntegrityLevel.Should().Be(IntegrityLevel.Medium);
        result.AboveServer.Should().BeTrue("Windows drops input to the frame window, which the host process owns");
    }

    [Theory]
    [InlineData(WindowDetail.Compact)]
    [InlineData(WindowDetail.Standard)]
    public void GetWindowInfo_BelowFull_ReportsNoIntegrity_AndReadsNone(WindowDetail detail)
    {
        // Arrange
        var window = UseWindow(TestDataFactory.CreateWindowInfo(0x100));
        var sut = CreateSutWithProbe();

        // Act
        var result = sut.GetWindowInfo(handle: window.Handle, detail: detail);

        // Assert
        result.IntegrityLevel.Should().BeNull();
        result.Elevated.Should().BeNull();
        result.AboveServer.Should().BeNull();
        _mockSecurityProbe.Verify(p => p.GetIntegrity(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void GetWindowInfo_Full_WithoutASecurityProbe_LeavesTheIntegrityOut()
    {
        // Arrange
        var window = UseWindow(TestDataFactory.CreateWindowInfo(0x100));

        // Act
        var result = CreateSut().GetWindowInfo(handle: window.Handle, detail: WindowDetail.Full);

        // Assert
        result.Success.Should().BeTrue();
        result.IntegrityLevel.Should().BeNull();
        result.AboveServer.Should().BeNull();
    }

    [Fact]
    public void GetOwnedWindows_CutsALongTitle()
    {
        var owner = TestDataFactory.CreateWindowInfo(handleValue: 0x100);
        var dialog = TestDataFactory.CreateWindowInfo(handleValue: 0x200, title: LongTitle);
        _mockWindowService.Setup(w => w.IsValidWindow(owner.Handle)).Returns(true);
        _mockWindowService.Setup(w => w.GetOwnedWindows(owner.Handle, false)).Returns([new OwnedWindowInfo(dialog, true, true)]);

        var result = CreateSut().GetOwnedWindows(handle: owner.Handle);

        result.Windows!.Single().Title.Should().Be(CutTitle);
    }

    // Windows on other virtual desktops (roadmap M7.3)

    private readonly Mock<INativeVirtualDesktopWrapper> _mockVirtualDesktops = new(MockBehavior.Strict);

    private WindowFinderTools CreateSutWithDesktops(IReadOnlyDictionary<nint, VirtualDesktopPlacement> placements)
    {
        _mockVirtualDesktops.Setup(d => d.GetPlacements(It.IsAny<IReadOnlyList<nint>>()))
            .Returns<IReadOnlyList<nint>>(windows => windows
                .Select(w => placements.TryGetValue(w, out var placement) ? placement : VirtualDesktopPlacement.Unknown).ToList());
        return new WindowFinderTools(_mockWindowService.Object, virtualDesktops: _mockVirtualDesktops.Object);
    }

    [Fact]
    public void FindWindows_MarksTheCloakedWindowsOnAnotherVirtualDesktop_AskingTheManagerOnce()
    {
        // Arrange - on screen, on another desktop, a suspended Store frame and one the manager cannot place
        var shown = TestDataFactory.CreateWindowInfo(0x100);
        var elsewhere = TestDataFactory.CreateWindowInfo(0x200) with { IsCloaked = true };
        var suspended = TestDataFactory.CreateWindowInfo(0x300) with { IsCloaked = true };
        var unknown = TestDataFactory.CreateWindowInfo(0x400) with { IsCloaked = true };
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([shown, elsewhere, suspended, unknown]);
        var sut = CreateSutWithDesktops(new Dictionary<nint, VirtualDesktopPlacement>
        {
            [elsewhere.Handle] = VirtualDesktopPlacement.OtherDesktop,
            [suspended.Handle] = VirtualDesktopPlacement.NoDesktop
        });

        // Act
        var result = sut.FindWindows(includeCloaked: true);

        // Assert - only the cloaked windows are asked about, in one call; a window not on screen for another reason is unmarked
        result.Select(w => w.IsOnOtherDesktop).Should().Equal(null, true, null, null);
        _mockVirtualDesktops.Verify(d => d.GetPlacements(It.Is<IReadOnlyList<nint>>(
            windows => windows.SequenceEqual(new[] { elsewhere.Handle, suspended.Handle, unknown.Handle }))), Times.Once);
    }

    [Fact]
    public void FindWindows_WithoutCloakedWindows_DoesNotAskTheVirtualDesktopManager()
    {
        // Arrange
        _mockWindowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>()))
            .Returns([TestDataFactory.CreateWindowInfo(0x100), TestDataFactory.CreateWindowInfo(0x200)]);
        var sut = CreateSutWithDesktops(new Dictionary<nint, VirtualDesktopPlacement>());

        // Act
        var result = sut.FindWindows();

        // Assert
        result.Should().OnlyContain(w => w.IsOnOtherDesktop == null);
        _mockVirtualDesktops.Verify(d => d.GetPlacements(It.IsAny<IReadOnlyList<nint>>()), Times.Never);
    }

    [Theory]
    [InlineData(WindowDetail.Standard, VirtualDesktopPlacement.OtherDesktop, true)]
    [InlineData(WindowDetail.Full, VirtualDesktopPlacement.OtherDesktop, true)]
    [InlineData(WindowDetail.Standard, VirtualDesktopPlacement.NoDesktop, null)]
    [InlineData(WindowDetail.Compact, VirtualDesktopPlacement.OtherDesktop, null)]
    public void GetWindowInfo_MarksACloakedWindowOnAnotherVirtualDesktop_AtStandardAndFull(
        WindowDetail detail, VirtualDesktopPlacement placement, bool? expected)
    {
        // Arrange
        var window = UseWindow(TestDataFactory.CreateWindowInfo(0x100) with { IsCloaked = true });
        var sut = CreateSutWithDesktops(new Dictionary<nint, VirtualDesktopPlacement> { [window.Handle] = placement });

        // Act
        var result = sut.GetWindowInfo(handle: window.Handle, detail: detail);

        // Assert
        result.Success.Should().BeTrue();
        result.IsOnOtherDesktop.Should().Be(expected);
    }

    [Fact]
    public void GetWindowInfo_OfAWindowThatIsNotCloaked_DoesNotAskTheVirtualDesktopManager()
    {
        // Arrange
        var window = UseWindow(TestDataFactory.CreateWindowInfo(0x100));
        var sut = CreateSutWithDesktops(new Dictionary<nint, VirtualDesktopPlacement>());

        // Act
        var result = sut.GetWindowInfo(handle: window.Handle, detail: WindowDetail.Full);

        // Assert
        result.IsOnOtherDesktop.Should().BeNull();
        _mockVirtualDesktops.Verify(d => d.GetPlacements(It.IsAny<IReadOnlyList<nint>>()), Times.Never);
    }

    // get_window_events (roadmap M7.2)

    private readonly Mock<IWindowEventJournal> _journal = new();

    private static readonly DateTimeOffset EventTime = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static WindowEventEntry Event(long seq, WindowEventType type, long handle, string? process) =>
        new(seq, EventTime, type, handle, "Save As", "#32770", process, 4_100);

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void GetWindowEvents_ALimitOutside1To200_IsInvalidArguments_AndReadsNothing(int limit)
    {
        var sut = new WindowFinderTools(_mockWindowService.Object, journal: _journal.Object);

        var act = () => sut.GetWindowEvents(limit: limit);

        act.Should().Throw<ToolErrorException>().Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _journal.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetWindowEvents_PassesSinceSeqAndLimit_AndFiltersByTypeProcessAndHandle()
    {
        // Arrange
        Func<WindowEventEntry, bool>? include = null;
        _journal.Setup(j => j.Read(41, 20, It.IsAny<Func<WindowEventEntry, bool>?>()))
            .Callback<long?, int, Func<WindowEventEntry, bool>?>((_, _, filter) => include = filter)
            .Returns(new WindowEventRead([], 41, EventTime, StartedNow: false, Missed: 0, More: false));
        var sut = new WindowFinderTools(_mockWindowService.Object, journal: _journal.Object);

        // Act
        sut.GetWindowEvents(sinceSeq: 41, types: [WindowEventType.Shown], processName: "Notepad.exe", handle: 0x200, limit: 20);

        // Assert - the type, the whole process name with or without .exe, and the window must all match
        include.Should().NotBeNull();
        include!(Event(42, WindowEventType.Shown, 0x200, "notepad")).Should().BeTrue();
        include(Event(43, WindowEventType.Hidden, 0x200, "notepad")).Should().BeFalse();
        include(Event(44, WindowEventType.Shown, 0x300, "notepad")).Should().BeFalse();
        include(Event(45, WindowEventType.Shown, 0x200, "notepad++")).Should().BeFalse();
        include(Event(46, WindowEventType.Shown, 0x200, null)).Should().BeFalse();
    }

    [Fact]
    public void GetWindowEvents_WithoutFilters_ReadsEverything_AndReportsOnlyTheFlagsThatApply()
    {
        // Arrange
        var events = new[] { Event(1, WindowEventType.Shown, 0x200, "notepad") };
        _journal.Setup(j => j.Read(null, 50, null))
            .Returns(new WindowEventRead(events, 1, EventTime, StartedNow: true, Missed: 0, More: false));
        var sut = new WindowFinderTools(_mockWindowService.Object, journal: _journal.Object);

        // Act
        var result = sut.GetWindowEvents();

        // Assert
        result.Success.Should().BeTrue();
        result.Events.Should().Equal(events);
        result.NextSeq.Should().Be(1);
        result.StartedNow.Should().BeTrue();
        result.Missed.Should().BeNull();
        result.More.Should().BeNull();
    }

    #endregion
}
