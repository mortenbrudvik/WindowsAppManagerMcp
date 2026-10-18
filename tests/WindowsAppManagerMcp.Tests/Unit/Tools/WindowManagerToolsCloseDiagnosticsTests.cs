using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// What the close tools say and log when something unexpected happens: a close_application window whose close throws is
/// still reported alone (notSent, CloseFailed), and the exception is logged as a warning, so a bug (a NullReferenceException)
/// is not lost in a per-window message; a window the verifier could not inspect for a prompt says so in its error. The
/// window service and the verifier are mocks.
/// </summary>
public class WindowManagerToolsCloseDiagnosticsTests
{
    private readonly Mock<IWindowService> _windowService = new();
    private readonly Mock<IWindowCloseVerifier> _verifier = new();
    private readonly CapturingLoggerProvider _logs = new();

    public WindowManagerToolsCloseDiagnosticsTests()
    {
        _windowService.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(true);
        _windowService.Setup(w => w.CloseWindow(It.IsAny<nint>(), It.IsAny<bool>())).Returns((nint h, bool _) => WindowCloseResult.Sent(h));
        _verifier.Setup(v => v.ShownWindows(It.IsAny<IReadOnlyList<nint>>())).Returns((IReadOnlyList<nint> handles) => handles.ToHashSet());
        _verifier.Setup(v => v.WaitForProcessesToExitAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        Report(h => new CloseObservation(h, CloseStatus.Closed));
    }

    private WindowManagerTools CreateSut()
    {
        InputGate.TagFlow();
        var factory = LoggerFactory.Create(builder => builder.AddProvider(_logs));
        return new(_windowService.Object, Mock.Of<IForegroundService>(), closeVerifier: _verifier.Object,
            gateWaitTimeout: TimeSpan.FromSeconds(5), logger: factory.CreateLogger<WindowManagerTools>());
    }

    private void Report(Func<nint, CloseObservation> observe) =>
        _verifier.Setup(v => v.WaitForCloseAsync(It.IsAny<IReadOnlyList<nint>>(), It.IsAny<IReadOnlySet<nint>>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<nint> handles, IReadOnlySet<nint> _, TimeSpan _, CancellationToken _) => handles.Select(observe).ToList());

    private void Desktop(params WindowInfo[] windows)
    {
        _windowService.Setup(w => w.GetApplicationWindows()).Returns(windows);
        foreach (var window in windows)
        {
            _windowService.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
        }
    }

    private static WindowInfo AppWindow(long handle, string title) =>
        TestDataFactory.CreateWindowInfo(handleValue: (int)handle, processId: 700, processName: "app", title: title) with { ClassName = "AppWindow" };

    [Fact]
    public async Task CloseApplication_AWindowWhoseCloseThrows_IsReportedAlone_AndTheExceptionIsLoggedAsAWarning()
    {
        // Arrange - a bug in the close path, not a refusal
        Desktop(AppWindow(1, "One"), AppWindow(2, "Two"));
        var bug = new NullReferenceException("Object reference not set to an instance of an object.");
        _windowService.Setup(w => w.CloseWindow(1, false)).Throws(bug);

        // Act
        var result = await CreateSut().CloseApplication(processId: 700);

        // Assert - the per-window result is kept, and the exception itself reaches the log
        result.Windows.Should().Equal(
            new ClosedWindowItem(1, "One", "notSent", $"The close could not be sent: {bug.Message}", "CloseFailed"),
            new ClosedWindowItem(2, "Two", "closed"));
        _logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && ReferenceEquals(e.Exception, bug))
            .Which.Message.Should().Contain("1");
    }

    [Fact]
    public async Task CloseWindow_AWindowWhosePromptCouldNotBeInspected_SaysSoInItsError()
    {
        // Arrange
        const string warning = "Its prompt could not be inspected: UI Automation failed (COMException: busy).";
        Report(h => new CloseObservation(h, CloseStatus.StillOpen) { Warning = warning });

        // Act
        var result = await CreateSut().CloseWindow(12345);

        // Assert
        result.Success.Should().BeFalse();
        result.Status.Should().Be("stillOpen");
        result.ErrorCode.Should().Be("StillOpen");
        result.Error.Should().Contain("still open").And.EndWith(warning);
    }

    [Fact]
    public async Task CloseWindow_AStillOpenWindowWithoutAWarning_KeepsItsError()
    {
        // Arrange
        Report(h => new CloseObservation(h, CloseStatus.StillOpen));

        // Act
        var result = await CreateSut().CloseWindow(12345);

        // Assert
        result.Error.Should().EndWith("Check it with find_windows, or wait longer.");
    }
}
