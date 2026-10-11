using System.Diagnostics;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// What a close request led to (roadmap M2.5): WindowCloseVerifier polls the windows through a mocked
/// <see cref="IWindowService"/> and reads prompts from an in-memory UI Automation tree (<see cref="FakeUiaTree"/>), so no
/// real window is read or closed.
/// </summary>
public class WindowCloseVerifierTests
{
    private static readonly nint Editor = new(0x1001);
    private static readonly nint Other = new(0x1002);
    private static readonly nint Prompt = new(0x2001);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IUIAutomationWrapper> _automation = new();
    private readonly Mock<IProcessService> _processes = new();
    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();

    public WindowCloseVerifierTests()
    {
        OpenWindow(Editor);
        OpenWindow(Other);
    }

    private WindowCloseVerifier CreateSut(bool withUiAutomation = true) => withUiAutomation
        ? new WindowCloseVerifier(_windows.Object, _tree, _worker, _automation.Object, _processes.Object,
            pollInterval: TimeSpan.FromMilliseconds(5), promptCheckInterval: TimeSpan.FromMilliseconds(10))
        : new WindowCloseVerifier(_windows.Object, pollInterval: TimeSpan.FromMilliseconds(5), promptCheckInterval: TimeSpan.FromMilliseconds(10));

    private void OpenWindow(nint handle, bool visible = true, bool cloaked = false)
    {
        _windows.Setup(w => w.IsValidWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetWindowInfo(handle)).Returns(TestDataFactory.CreateWindowInfo(handleValue: (int)handle, processId: 700, isVisible: visible) with
        {
            IsCloaked = cloaked
        });
    }

    private void CloseAfter(nint handle, int checks)
    {
        var calls = 0;
        _windows.Setup(w => w.IsValidWindow(handle)).Returns(() => ++calls < checks);
    }

    private static ClosePrompt SavePrompt(string? text = "Do you want to save changes?", params string[] buttons) =>
        new(Prompt, "Editor", "#32770", text, buttons.Length == 0 ? ["Yes", "No", "Cancel"] : buttons, ClosePromptKind.ModalPopup);

    #region Waiting for the close

    [Fact]
    public async Task AWindowThatCloses_IsClosed()
    {
        // Arrange - gone at the third check
        CloseAfter(Editor, checks: 3);

        // Act
        var result = await CreateSut().WaitForCloseAsync([Editor], Long, CancellationToken.None);

        // Assert
        result.Should().Equal(new CloseObservation(Editor, CloseStatus.Closed));
    }

    [Fact]
    public async Task AWindowThatStaysOpen_WithoutAPrompt_IsStillOpen_WhenTheWaitEnds()
    {
        // Act
        var waited = Stopwatch.StartNew();
        var result = await CreateSut().WaitForCloseAsync([Editor], Short, CancellationToken.None);

        // Assert
        waited.Elapsed.Should().BeGreaterThanOrEqualTo(Short - TimeSpan.FromMilliseconds(20));
        result.Should().Equal(new CloseObservation(Editor, CloseStatus.StillOpen));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AWindowThatWasHiddenOrCloaked_IsHidden(bool visible, bool cloaked)
    {
        // Arrange - the app hid its window in the notification area instead of closing it
        OpenWindow(Editor, visible, cloaked);

        // Act
        var result = await CreateSut().WaitForCloseAsync([Editor], Short, CancellationToken.None);

        // Assert
        result.Should().Equal(new CloseObservation(Editor, CloseStatus.Hidden));
    }

    [Fact]
    public async Task AWin32PromptThatStays_EndsTheWaitEarly_AndIsReported()
    {
        // Arrange
        var prompt = SavePrompt();
        _windows.Setup(w => w.FindBlockingDialog(Editor)).Returns(prompt);

        // Act
        var waited = Stopwatch.StartNew();
        var result = await CreateSut().WaitForCloseAsync([Editor], Long, CancellationToken.None);

        // Assert
        waited.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a prompt that stays for two checks ends the wait");
        result.Should().ContainSingle().Which.Should().Be(new CloseObservation(Editor, CloseStatus.PromptShown, prompt));
    }

    [Fact]
    public async Task SeveralWindows_AreReportedInRequestOrder_OncePerHandle()
    {
        // Arrange
        CloseAfter(Other, checks: 2);
        var prompt = SavePrompt();
        _windows.Setup(w => w.FindBlockingDialog(Editor)).Returns(prompt);

        // Act
        var result = await CreateSut().WaitForCloseAsync([Editor, Other, Editor], Long, CancellationToken.None);

        // Assert
        result.Select(r => (r.Handle, r.Status)).Should().Equal(
            (Editor, CloseStatus.PromptShown), (Other, CloseStatus.Closed), (Editor, CloseStatus.PromptShown));
    }

    [Fact]
    public async Task CancellingTheCall_EndsTheWait()
    {
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = () => CreateSut().WaitForCloseAsync([Editor], Long, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region The prompt

    [Fact]
    public async Task AWin32PromptWithoutTextOrButtons_IsReadThroughUiAutomation()
    {
        // Arrange - a task dialog draws its text and buttons in a DirectUIHWND, not in Static and Button controls
        _windows.Setup(w => w.FindBlockingDialog(Editor)).Returns(SavePrompt(text: null, buttons: ["unused"]) with { Buttons = [] });
        _tree.WithWindow(Prompt, new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("TitleBar", "Editor").With(new FakeUiaNode("Button", "Close")),
            new FakeUiaNode("Pane").With(
                new FakeUiaNode("Text", "Do you want to save changes to Untitled?"),
                new FakeUiaNode("Button", "Save").With(new FakeUiaNode("Text", "Save")),
                new FakeUiaNode("Button", "Don't save"),
                new FakeUiaNode("Button", "Cancel"))));

        // Act
        var prompt = await CreateSut().FindPromptAsync(Editor, CancellationToken.None);

        // Assert
        prompt.Should().NotBeNull();
        prompt!.Handle.Should().Be(Prompt);
        prompt.Kind.Should().Be(ClosePromptKind.ModalPopup);
        prompt.Text.Should().Be("Do you want to save changes to Untitled?");
        prompt.Buttons.Should().Equal("Save", "Don't save", "Cancel");
        _worker.LastTargetProcessId.Should().Be(700);
    }

    [Fact]
    public async Task AWin32PromptWithTextAndButtons_IsNotReadAgain()
    {
        var prompt = SavePrompt();
        _windows.Setup(w => w.FindBlockingDialog(Editor)).Returns(prompt);

        (await CreateSut().FindPromptAsync(Editor, CancellationToken.None)).Should().Be(prompt);
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task AModalDialogInsideTheWindow_IsFoundThroughUiAutomation()
    {
        // Arrange - Win11 Notepad's save prompt is a ContentDialog: a modal Window element without a window of its own
        _tree.WithWindow(Editor, new FakeUiaNode("Window", "Untitled - Notepad").With(
            new FakeUiaNode("TitleBar", "Untitled - Notepad"),
            new FakeUiaNode("Pane", "Content").With(
                new FakeUiaNode("Document", "Text editor"),
                new FakeUiaNode("Window", "Notepad") { IsModal = true, ClassName = "Popup" }.With(
                    new FakeUiaNode("Text", "Do you want to save changes to Untitled?"),
                    new FakeUiaNode("Button", "Save"),
                    new FakeUiaNode("Button", "Don't save"),
                    new FakeUiaNode("Button", "Cancel")))));

        // Act
        var prompt = await CreateSut().FindPromptAsync(Editor, CancellationToken.None);

        // Assert
        prompt.Should().NotBeNull();
        prompt!.Kind.Should().Be(ClosePromptKind.InWindowDialog);
        prompt.Handle.Should().Be(Editor, "the dialog has no window of its own");
        prompt.Title.Should().Be("Notepad");
        prompt.ClassName.Should().Be("Popup");
        prompt.Text.Should().Be("Do you want to save changes to Untitled?");
        prompt.Buttons.Should().Equal("Save", "Don't save", "Cancel");
    }

    [Fact]
    public async Task ANonModalWindowElement_IsNoPrompt()
    {
        _tree.WithWindow(Editor, new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("Window", "Find") { IsModal = false }.With(new FakeUiaNode("Button", "Find next"))));

        (await CreateSut().FindPromptAsync(Editor, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnInWindowPrompt_IsReportedWhenTheWaitEnds()
    {
        _tree.WithWindow(Editor, new FakeUiaNode("Window", "Editor").With(
            new FakeUiaNode("Window", "Save?") { IsModal = true }.With(new FakeUiaNode("Button", "Save"))));

        var result = await CreateSut().WaitForCloseAsync([Editor], Short, CancellationToken.None);

        result.Should().ContainSingle().Which.Status.Should().Be(CloseStatus.PromptShown);
        result[0].Prompt!.Buttons.Should().Equal("Save");
    }

    [Fact]
    public async Task WithoutUiAutomation_OnlyWin32PromptsAreFound()
    {
        _tree.WithWindow(Editor, new FakeUiaNode("Window", "Editor").With(new FakeUiaNode("Window", "Save?") { IsModal = true }));

        (await CreateSut(withUiAutomation: false).FindPromptAsync(Editor, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AUiAutomationReadThatFails_FindsNoPrompt_AndDoesNotFailTheClose()
    {
        _tree.FromHandleException = new TimeoutException("The provider did not answer.");

        var result = await CreateSut().WaitForCloseAsync([Editor], Short, CancellationToken.None);

        result.Should().Equal(new CloseObservation(Editor, CloseStatus.StillOpen));
    }

    #endregion

    #region The UI Automation close and the processes

    [Fact]
    public async Task CloseWithUiAutomation_AsksTheWindowPattern()
    {
        _automation.Setup(a => a.CloseWindow(Editor)).Returns(true);

        (await CreateSut().CloseWithUiAutomationAsync(Editor, CancellationToken.None)).Should().BeTrue();
        _automation.Verify(a => a.CloseWindow(Editor), Times.Once);
    }

    [Fact]
    public async Task CloseWithUiAutomation_WithoutUiAutomation_OrWhenItThrows_IsFalse()
    {
        _automation.Setup(a => a.CloseWindow(Editor)).Throws(new InvalidOperationException("gone"));

        (await CreateSut(withUiAutomation: false).CloseWithUiAutomationAsync(Editor, CancellationToken.None)).Should().BeFalse();
        (await CreateSut().CloseWithUiAutomationAsync(Editor, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task WaitForProcessesToExit_ReturnsTheOnesStillRunning()
    {
        // Arrange - 700 exits at the second look, 701 keeps running (an app in the notification area)
        var looks = 0;
        _processes.Setup(p => p.GetProcessName(700)).Returns(() => ++looks < 2 ? "editor" : null);
        _processes.Setup(p => p.GetProcessName(701)).Returns("trayapp");

        // Act
        var running = await CreateSut().WaitForProcessesToExitAsync([700, 701], Short, CancellationToken.None);

        // Assert
        running.Should().Equal(701);
    }

    #endregion
}
