using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// What WindowCloseVerifier says and logs when UI Automation fails it: a window whose prompt could not be inspected is still
/// reported (StillOpen, or PromptShown with the Win32 prompt), with a warning that the prompt could not be inspected, so the
/// agent does not read "still open, no prompt" as fact; an unexpected exception (a COMException) is logged as a warning with
/// the exception. The window service is a mock and UI Automation an in-memory tree.
/// </summary>
public class WindowCloseVerifierDiagnosticsTests
{
    private static readonly nint Editor = new(0x1001);
    private static readonly nint Prompt = new(0x2001);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);
    private static readonly IReadOnlySet<nint> ShownBefore = new HashSet<nint> { Editor };

    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IUIAutomationWrapper> _automation = new();
    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly CapturingLoggerProvider _logs = new();

    public WindowCloseVerifierDiagnosticsTests()
    {
        _windows.Setup(w => w.IsValidWindow(Editor)).Returns(true);
        _windows.Setup(w => w.GetWindowInfo(Editor)).Returns(TestDataFactory.CreateWindowInfo(handleValue: (int)Editor, processId: 700));
    }

    private WindowCloseVerifier CreateSut() =>
        new(_windows.Object, _tree, _worker, _automation.Object, pollInterval: TimeSpan.FromMilliseconds(5),
            promptCheckInterval: TimeSpan.FromMilliseconds(10),
            logger: LoggerFactory.Create(b => b.AddProvider(_logs)).CreateLogger<WindowCloseVerifier>());

    private IEnumerable<CapturedLogEntry> Warnings => _logs.Entries.Where(e => e.Level == LogLevel.Warning);

    [Fact]
    public async Task AWindowWhosePromptReadThrows_IsStillOpen_WithAWarningThatItsPromptCouldNotBeInspected()
    {
        // Arrange - the provider fails the read of the window's own element
        var failure = new COMException("The server threw an exception.", unchecked((int)0x80010105));
        _tree.FromHandleException = failure;

        // Act
        var result = await CreateSut().WaitForCloseAsync([Editor], ShownBefore, Short, CancellationToken.None);

        // Assert
        var observation = result.Should().ContainSingle().Which;
        observation.Status.Should().Be(CloseStatus.StillOpen);
        observation.Warning.Should().Contain("prompt could not be inspected").And.Contain("COMException");
        Warnings.Should().ContainSingle(e => ReferenceEquals(e.Exception, failure));
    }

    [Fact]
    public async Task AWindowWhosePromptReadTimesOut_IsStillOpen_WithAWarning_ButNoWarningIsLogged()
    {
        // Arrange - a slow app is no server fault: logged below warning
        _tree.FromHandleException = new TimeoutException("The provider did not answer.");

        // Act
        var result = await CreateSut().WaitForCloseAsync([Editor], ShownBefore, Short, CancellationToken.None);

        // Assert
        result.Single().Warning.Should().Contain("prompt could not be inspected");
        Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task AWindowWhoseElementsGoAwayWhileTheyAreRead_IsStillOpen_WithAWarning_ButNoWarningIsLogged()
    {
        // Arrange - a closing window: no fault of the server
        _tree.FromHandleException = new UiaElementUnavailableException("The element is not available.");

        // Act
        var result = await CreateSut().WaitForCloseAsync([Editor], ShownBefore, Short, CancellationToken.None);

        // Assert
        result.Single().Warning.Should().Contain("prompt could not be inspected").And.Contain("went away");
        Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task AWindowWhosePromptReadWorks_HasNoWarning()
    {
        _tree.WithWindow(Editor, new FakeUiaNode("Window", "Editor").With(new FakeUiaNode("Document", "Text")));

        var result = await CreateSut().WaitForCloseAsync([Editor], ShownBefore, Short, CancellationToken.None);

        result.Should().Equal(new CloseObservation(Editor, CloseStatus.StillOpen));
        _logs.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task AWin32PromptWhoseTextCannotBeRead_IsShown_WithAWarning()
    {
        // Arrange - a task dialog without Static and Button controls, whose UI Automation read fails
        var prompt = new ClosePrompt(Prompt, "Editor", "#32770", null, [], ClosePromptKind.ModalPopup);
        _windows.Setup(w => w.FindBlockingDialog(Editor)).Returns(prompt);
        _tree.FromHandleException = new COMException("The server threw an exception.");

        // Act
        var result = await CreateSut().WaitForCloseAsync([Editor], ShownBefore, Short, CancellationToken.None);

        // Assert
        var observation = result.Single();
        observation.Status.Should().Be(CloseStatus.PromptShown);
        observation.Prompt.Should().Be(prompt);
        observation.Warning.Should().Contain("text and buttons could not be read");
    }

    [Fact]
    public async Task CloseWithUiAutomation_ThatThrowsUnexpectedly_IsFalse_AndLogsTheException()
    {
        // Arrange
        var failure = new COMException("The server threw an exception.");
        _automation.Setup(a => a.CloseWindow(Editor, It.IsAny<CancellationToken>())).Throws(failure);

        // Act
        var closed = await CreateSut().CloseWithUiAutomationAsync(Editor, CancellationToken.None);

        // Assert
        closed.Should().BeFalse();
        Warnings.Should().ContainSingle(e => ReferenceEquals(e.Exception, failure));
    }
}
