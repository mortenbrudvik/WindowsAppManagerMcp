using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// close_window, close_windows_batch and close_application at the tool layer: force reaches the service, the service's
/// refusals come back with their error code, a batch of more than 3 windows needs confirm (roadmap M1.7), and the close is
/// verified (roadmap M2.5): the tools wait for the windows through the close verifier, outside the input gate, and report
/// closed, hidden, stillOpen or promptShown. The window service and the verifier are mocks; the guard itself is tested in
/// WindowServiceCloseTests, the waiting in WindowCloseVerifierTests.
/// </summary>
public class WindowManagerToolsCloseTests
{
    private static readonly ClosePrompt SavePrompt =
        new(0x2001, "Editor", "#32770", "Do you want to save changes?", ["Yes", "No", "Cancel"], ClosePromptKind.ModalPopup);

    private readonly Mock<IWindowService> _windowService = new();
    private readonly Mock<IWindowCloseVerifier> _verifier = new();
    private readonly List<bool> _gateFreeDuringTheWait = [];

    public WindowManagerToolsCloseTests()
    {
        _windowService.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(true);
        _windowService.Setup(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>())).Returns((nint h, bool _) => WindowCloseResult.Sent(h));
        _windowService.Setup(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<nint> handles, bool _, int _, CancellationToken _) => handles.Select(WindowCloseResult.Sent).ToList());
        WindowsReport(_ => CloseStatus.Closed);
        _verifier.Setup(v => v.WaitForProcessesToExitAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private WindowManagerTools CreateSut() =>
        new(_windowService.Object, Mock.Of<IForegroundService>(), closeVerifier: _verifier.Object, gateWaitTimeout: TimeSpan.FromSeconds(5));

    private static WindowCloseRequest[] Requests(params long[] handles) => handles.Select(h => new WindowCloseRequest(h)).ToArray();

    /// <summary>The verifier reports each window with <paramref name="status"/>, and records whether the input gate was free meanwhile.</summary>
    private void WindowsReport(Func<nint, CloseStatus> status) =>
        _verifier.Setup(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<nint> handles, TimeSpan _, CancellationToken _) =>
            {
                // The tool must have released the gate: taking it here would time out otherwise
                using (var gate = InputGate.TryEnter(TimeSpan.FromSeconds(5), CancellationToken.None))
                {
                    _gateFreeDuringTheWait.Add(gate != null);
                }

                return handles.Select(h => status(h) == CloseStatus.PromptShown
                    ? new CloseObservation(h, CloseStatus.PromptShown, SavePrompt)
                    : new CloseObservation(h, status(h))).ToList();
            });

    #region close_window

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseWindow_PassesForceToTheService(bool force)
    {
        // Act
        var result = await CreateSut().CloseWindow(12345, force);

        // Assert
        result.Should().Be(new CloseWindowResult(Success: true, Status: "closed"));
        _windowService.Verify(w => w.CloseWindow(12345, force), Times.Once);
    }

    [Fact]
    public async Task CloseWindow_ByDefault_DoesNotForce_AndWaitsTwoSeconds_OutsideTheGate()
    {
        // Act
        await CreateSut().CloseWindow(12345);

        // Assert
        _windowService.Verify(w => w.CloseWindow(12345, false), Times.Once);
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never);
        _verifier.Verify(v => v.WaitForCloseAsync(
            It.Is<IReadOnlyList<nint>>(h => h.SequenceEqual(new nint[] { 12345 })), TimeSpan.FromSeconds(2), It.IsAny<CancellationToken>()), Times.Once);
        _gateFreeDuringTheWait.Should().Equal(true);
    }

    [Theory]
    [InlineData(WindowErrorCode.ProtectedWindow)]
    [InlineData(WindowErrorCode.AccessDenied)]
    [InlineData(WindowErrorCode.CloseFailed)]
    public async Task CloseWindow_Refused_ReturnsTheServiceErrorAndCode_WithoutWaiting(WindowErrorCode code)
    {
        // Arrange
        _windowService.Setup(w => w.CloseWindow(12345, false)).Returns(WindowCloseResult.Failed(12345, code, "why"));

        // Act
        var result = await CreateSut().CloseWindow(12345);

        // Assert
        result.Should().Be(new CloseWindowResult(Success: false, Error: "why", ErrorCode: code.ToString()));
        _verifier.Verify(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindow_WaitMsZero_OnlySendsTheMessage()
    {
        var result = await CreateSut().CloseWindow(12345, waitMs: 0);

        result.Should().Be(new CloseWindowResult(Success: true, Status: "requested"));
        _verifier.Verify(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(30001)]
    public async Task CloseWindow_WaitMsOutOfRange_IsInvalidArguments_AndSendsNothing(int waitMs)
    {
        var result = await CreateSut().CloseWindow(12345, waitMs: waitMs);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        result.Error.Should().Contain("0 and 30000");
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindow_APrompt_IsPromptShown_WithThePrompt()
    {
        // Arrange
        WindowsReport(_ => CloseStatus.PromptShown);

        // Act
        var result = await CreateSut().CloseWindow(12345);

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be("promptShown");
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PromptShown));
        result.Prompt.Should().BeEquivalentTo(new ClosePromptDto(0x2001, "Editor", "#32770", "Do you want to save changes?", ["Yes", "No", "Cancel"], "modalPopup"));
        result.Error.Should().Contain("Nothing was answered");
    }

    [Theory]
    [InlineData(CloseStatus.StillOpen, false, "stillOpen", nameof(WindowErrorCode.StillOpen))]
    [InlineData(CloseStatus.Hidden, true, "hidden", null)]
    public async Task CloseWindow_ReportsWhatBecameOfTheWindow(CloseStatus status, bool success, string expectedStatus, string? errorCode)
    {
        WindowsReport(_ => status);

        var result = await CreateSut().CloseWindow(12345);

        result.Success.Should().Be(success);
        result.Status.Should().Be(expectedStatus);
        result.ErrorCode.Should().Be(errorCode);
    }

    [Fact]
    public async Task CloseWindow_WhilePaused_IsPausedByUser_AndSendsNothing()
    {
        // Arrange
        using var session = new InputSession(new FakeInputWrapper());
        session.Pause(InputControlSources.Hotkey);
        var sut = new WindowManagerTools(_windowService.Object, Mock.Of<IForegroundService>(), session, closeVerifier: _verifier.Object);

        // Act
        var act = () => sut.CloseWindow(12345);

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.PausedByUser));
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region close_windows_batch

    [Fact]
    public async Task CloseWindowsBatch_MoreThanThreeWindowsWithoutConfirm_IsRefused_AndClosesNothing()
    {
        // Act
        var act = () => CreateSut().CloseWindowsBatch(Requests(1, 2, 3, 4));

        // Assert
        (await act.Should().ThrowAsync<ToolErrorException>())
            .Where(e => e.ErrorCode == nameof(WindowErrorCode.ConfirmationRequired))
            .WithMessage("*4 windows*confirm=true*Nothing was closed*");
        _windowService.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_MoreThanThreeWindowsWithConfirm_ClosesThem()
    {
        // Act
        var result = await CreateSut().CloseWindowsBatch(Requests(1, 2, 3, 4), delayBetweenMs: 0, confirm: true);

        // Assert
        result.TotalRequested.Should().Be(4);
        result.Succeeded.Should().Be(4);
        result.Results.Should().OnlyContain(r => r.Status == "closed");
        _windowService.Verify(w => w.CloseWindowsBatchAsync(
            It.Is<IReadOnlyList<nint>>(h => h.SequenceEqual(new nint[] { 1, 2, 3, 4 })), false, 0,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CloseWindowsBatch_UpToThreeWindows_NeedsNoConfirm(int count)
    {
        // Act
        var result = await CreateSut().CloseWindowsBatch(Requests(Enumerable.Range(1, count).Select(i => (long)i).ToArray()));

        // Assert
        result.Succeeded.Should().Be(count);
    }

    [Fact]
    public async Task CloseWindowsBatch_PassesForceToTheService()
    {
        // Act
        await CreateSut().CloseWindowsBatch(Requests(1, 2), force: true);

        // Assert
        _windowService.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), true, 50, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseWindowsBatch_ReportsEachRefusalWithItsErrorCode_AndWaitsOnlyForTheWindowsItSentTo()
    {
        // Arrange
        _windowService.Setup(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), false, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                WindowCloseResult.Sent(1),
                WindowCloseResult.Failed(2, WindowErrorCode.ProtectedWindow, "the desktop"),
                WindowCloseResult.Failed(3, WindowErrorCode.InvalidHandle, "Invalid window handle")
            ]);

        // Act
        var result = await CreateSut().CloseWindowsBatch(Requests(1, 2, 3));

        // Assert
        result.Succeeded.Should().Be(1);
        result.Failed.Should().Be(2);
        result.Results.Should().Equal(
            new WindowResultItem(1, true, Status: "closed"),
            new WindowResultItem(2, false, Error: "the desktop", ErrorCode: "ProtectedWindow"),
            new WindowResultItem(3, false, Error: "Invalid window handle", ErrorCode: "InvalidHandle"));
        _verifier.Verify(v => v.WaitForCloseAsync(It.Is<IReadOnlyList<nint>>(h => h.SequenceEqual(new nint[] { 1 })),
            TimeSpan.FromSeconds(2), It.IsAny<CancellationToken>()), Times.Once);
        _gateFreeDuringTheWait.Should().Equal(true);
    }

    [Fact]
    public async Task CloseWindowsBatch_ReportsEachWindowsStatus_AndAPromptPerWindow()
    {
        // Arrange
        WindowsReport(h => h == 2 ? CloseStatus.PromptShown : h == 3 ? CloseStatus.StillOpen : CloseStatus.Closed);

        // Act
        var result = await CreateSut().CloseWindowsBatch(Requests(1, 2, 3), waitMs: 500);

        // Assert
        result.Results.Select(r => (r.Handle, r.Success, r.Status, r.ErrorCode)).Should().Equal(
            (1L, true, "closed", (string?)null),
            (2L, false, "promptShown", nameof(WindowErrorCode.PromptShown)),
            (3L, false, "stillOpen", nameof(WindowErrorCode.StillOpen)));
        result.Results[1].Prompt!.Title.Should().Be("Editor");
        _verifier.Verify(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), TimeSpan.FromMilliseconds(500), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseWindowsBatch_WaitMsZero_ReportsRequested()
    {
        var result = await CreateSut().CloseWindowsBatch(Requests(1, 2), waitMs: 0);

        result.Results.Should().OnlyContain(r => r.Success && r.Status == "requested");
        _verifier.Verify(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseWindowsBatch_WaitMsOutOfRange_IsAToolError()
    {
        var act = () => CreateSut().CloseWindowsBatch(Requests(1), waitMs: 40000);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    [Fact]
    public async Task CloseWindowsBatch_PassesTheCallsToken_SoTheClientCanCancelTheBatch()
    {
        // Arrange
        using var call = new CancellationTokenSource();

        // Act
        await CreateSut().CloseWindowsBatch(Requests(1, 2), cancellationToken: call.Token);

        // Assert
        _windowService.Verify(w => w.CloseWindowsBatchAsync(It.IsAny<IReadOnlyList<nint>>(), false, 50, call.Token), Times.Once);
        _verifier.Verify(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<TimeSpan>(), call.Token), Times.Once);
    }

    #endregion

    #region close_application

    private static WindowInfo AppWindow(long handle, int processId, string processName, string title, string className = "AppWindow") =>
        TestDataFactory.CreateWindowInfo(handleValue: (int)handle, processId: processId, processName: processName, title: title) with
        {
            ClassName = className
        };

    private void Desktop(params WindowInfo[] windows)
    {
        _windowService.Setup(w => w.GetApplicationWindows()).Returns(windows);
        foreach (var window in windows)
        {
            _windowService.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
        }
    }

    [Fact]
    public async Task CloseApplication_ByName_ClosesTheAltTabWindowsOfEveryProcessWithThatName()
    {
        // Arrange - two Notepad processes, and another app
        Desktop(
            AppWindow(1, 700, "notepad", "a.txt - Notepad"),
            AppWindow(2, 701, "Notepad", "b.txt - Notepad"),
            AppWindow(3, 800, "mspaint", "Untitled - Paint"));
        _windowService.Setup(w => w.CloseWindow(It.IsAny<nint>(), false)).Returns((nint h, bool _) =>
        {
            // Closed windows leave the Alt-Tab set
            _windowService.Setup(w => w.GetApplicationWindows()).Returns([AppWindow(3, 800, "mspaint", "Untitled - Paint")]);
            return WindowCloseResult.Sent(h);
        });

        // Act
        var result = await CreateSut().CloseApplication(processName: "NOTEPAD.exe");

        // Assert
        result.Success.Should().BeTrue();
        result.Status.Should().Be("closed");
        result.ProcessIds.Should().Equal(700, 701);
        result.Windows.Select(w => (w.Handle, w.Status)).Should().Equal((1L, "closed"), (2L, "closed"));
        result.RemainingWindows.Should().BeEmpty();
        result.Exited.Should().BeTrue();
        _windowService.Verify(w => w.CloseWindow(3, It.IsAny<bool>()), Times.Never);
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), true), Times.Never, "close_application never forces");
        _gateFreeDuringTheWait.Should().Equal(true);
    }

    [Fact]
    public async Task CloseApplication_ByHandle_ClosesThatWindowsProcess()
    {
        // Arrange - the handle is a dialog of the app, which is not in Alt+Tab itself
        Desktop(AppWindow(1, 700, "notepad", "a.txt - Notepad"), AppWindow(3, 800, "mspaint", "Untitled - Paint"));
        _windowService.Setup(w => w.GetWindowInfo(9)).Returns(AppWindow(9, 700, "notepad", "Find", "#32770"));
        WindowsReport(_ => CloseStatus.Closed);

        // Act
        var result = await CreateSut().CloseApplication(handle: 9);

        // Assert
        result.ProcessIds.Should().Equal(700);
        _windowService.Verify(w => w.CloseWindow(1, false), Times.Once);
        _windowService.Verify(w => w.CloseWindow(3, It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CloseApplication_APrompt_IsPromptShown_WithTheRemainingWindows()
    {
        // Arrange
        Desktop(AppWindow(1, 700, "notepad", "a.txt - Notepad"));
        WindowsReport(_ => CloseStatus.PromptShown);

        // Act
        var result = await CreateSut().CloseApplication(processId: 700);

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be("promptShown");
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.PromptShown));
        result.PromptDetected.Should().BeTrue();
        result.Prompt!.Buttons.Should().Equal("Yes", "No", "Cancel");
        result.RemainingWindows.Should().ContainSingle().Which.Handle.Should().Be(1);
        result.Exited.Should().BeNull();
        _verifier.Verify(v => v.WaitForProcessesToExitAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CloseApplication_WindowsThatStayWithoutAPrompt_AreStillOpen()
    {
        Desktop(AppWindow(1, 700, "busy", "Busy"));
        WindowsReport(_ => CloseStatus.StillOpen);

        var result = await CreateSut().CloseApplication(processId: 700, timeoutMs: 1000);

        result.Status.Should().Be("stillOpen");
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.StillOpen));
        result.Error.Should().Contain("1000 ms");
        _verifier.Verify(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), TimeSpan.FromSeconds(1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseApplication_AProcessThatKeepsRunning_IsClosed_ButNotExited()
    {
        // Arrange - the app closed its window and stays in the notification area
        Desktop(AppWindow(1, 700, "trayapp", "Tray App"));
        _windowService.Setup(w => w.CloseWindow(1, false)).Returns(() =>
        {
            _windowService.Setup(w => w.GetApplicationWindows()).Returns([]);
            return WindowCloseResult.Sent(1);
        });
        _verifier.Setup(v => v.WaitForProcessesToExitAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([700]);

        // Act
        var result = await CreateSut().CloseApplication(processId: 700);

        // Assert
        result.Success.Should().BeTrue();
        result.Exited.Should().BeFalse();
        result.RunningProcessIds.Should().Equal(700);
    }

    [Fact]
    public async Task CloseApplication_WithoutAnAltTabWindow_IsNoClosableWindow_AndSendsNothing()
    {
        Desktop(AppWindow(3, 800, "mspaint", "Untitled - Paint"));

        var result = await CreateSut().CloseApplication(processName: "trayonly");

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(ProcessErrorCode.NoClosableWindow));
        result.Status.Should().BeNull();
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(WindowErrorCode.ProtectedWindow)]
    [InlineData(WindowErrorCode.TargetDeniedByPolicy)]
    public async Task CloseApplication_AWindowThatWouldBeRefused_RefusesTheWholeApplication(WindowErrorCode code)
    {
        // Arrange - the second window is the agent's host, or denied by the user's policy
        Desktop(AppWindow(1, 700, "app", "One"), AppWindow(2, 700, "app", "Two"));
        _windowService.Setup(w => w.CheckClose(2, false)).Returns(WindowCloseResult.Failed(2, code, "Window 2 is refused."));

        // Act
        var result = await CreateSut().CloseApplication(processId: 700);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(code.ToString());
        result.Error.Should().Contain("Window 2 is refused.").And.Contain("Nothing was closed");
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(1L, 700, null)]
    [InlineData(null, 700, "app")]
    [InlineData(null, 0, null)]
    [InlineData(null, null, " .exe")]
    public async Task CloseApplication_NotExactlyOneValidTarget_IsInvalidArguments(long? handle, int? processId, string? processName)
    {
        var act = () => CreateSut().CloseApplication(handle, processId, processName);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CloseApplication_AHandleThatIsNoWindow_IsInvalidHandle()
    {
        var act = () => CreateSut().CloseApplication(handle: 4242);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidHandle));
    }

    [Fact]
    public async Task CloseApplication_MoreThanThreeWindowsWithoutConfirm_IsConfirmationRequired()
    {
        Desktop(Enumerable.Range(1, 4).Select(i => AppWindow(i, 700, "app", $"Window {i}")).ToArray());

        var act = () => CreateSut().CloseApplication(processId: 700);

        (await act.Should().ThrowAsync<ToolErrorException>()).Which.ErrorCode.Should().Be(nameof(WindowErrorCode.ConfirmationRequired));
        _windowService.Verify(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task CloseApplication_APackagedAppsFrameThatRefusesTheMessage_IsClosedThroughUiAutomation()
    {
        // Arrange - Windows did not take WM_CLOSE for the frame (CloseFailed)
        Desktop(AppWindow(1, 600, "CalculatorApp", "Calculator", "ApplicationFrameWindow"));
        _windowService.Setup(w => w.CloseWindow(1, false)).Returns(WindowCloseResult.Failed(1, WindowErrorCode.CloseFailed, "quota"));
        _verifier.Setup(v => v.CloseWithUiAutomationAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        WindowsReport(_ => CloseStatus.Closed);

        // Act
        var result = await CreateSut().CloseApplication(processId: 600);

        // Assert
        result.Windows.Should().ContainSingle().Which.Status.Should().Be("closed");
        _verifier.Verify(v => v.CloseWithUiAutomationAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CloseApplication_AnOrdinaryWindowThatRefusesTheMessage_IsNotClosedThroughUiAutomation()
    {
        Desktop(AppWindow(1, 700, "app", "One"));
        _windowService.Setup(w => w.CloseWindow(1, false)).Returns(WindowCloseResult.Failed(1, WindowErrorCode.AccessDenied, "elevated"));

        var result = await CreateSut().CloseApplication(processId: 700);

        result.Status.Should().Be("stillOpen");
        result.Windows.Should().ContainSingle().Which.Should().Be(new ClosedWindowItem(1, "One", "notSent", "elevated", "AccessDenied"));
        _verifier.Verify(v => v.CloseWithUiAutomationAsync(It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion
}
