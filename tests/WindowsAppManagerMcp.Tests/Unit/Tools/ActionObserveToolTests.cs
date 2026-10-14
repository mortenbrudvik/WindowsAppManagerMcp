using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Tools;

/// <summary>
/// observe on the input and dialog tools (roadmap M3.F32), as element_action has it (roadmap M3.12): get_desktop_state's
/// observation after the action as observed, of the window the action was for while it is still a window (else of the
/// window in front), and with 'screenshot' its capture as image content first; refused before anything is sent when
/// get_desktop_state, or for a screenshot capture_window, is not served; never naming a window the action was refused for.
/// </summary>
public class ActionObserveToolTests
{
    private static readonly nint Notepad = 0x1A0B2C;
    private static readonly nint Dialog = 0x5000;
    private static readonly nint InFront = 0x77;

    private readonly Mock<IInputSimulationService> _input = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IWindowService> _windows = new();
    private readonly Mock<IDesktopStateService> _desktopState = new();
    private readonly List<string> _order = [];
    private DesktopStateRequest? _observed;

    public ActionObserveToolTests()
    {
        var notepad = new WindowSummary(Notepad, "notes.txt - Notepad", "notepad", 4242);
        _input.Setup(s => s.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() => _order.Add("click"))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(true, x, y, "left") { ReceivingWindow = notepad });
        _input.Setup(s => s.RightClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(true, x, y, "right") { ReceivingWindow = notepad });
        _input.Setup(s => s.DoubleClick(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns((int x, int y, InputTarget _, bool _, CancellationToken _) => new ClickResult(true, x, y, "double") { ReceivingWindow = notepad });
        _input.Setup(s => s.TypeTextAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<TextControlChars?>(), It.IsAny<TextInputMethod>(), It.IsAny<CancellationToken>()))
            .Callback(() => _order.Add("type"))
            .ReturnsAsync((string text, int _, InputTarget _, bool _, TextControlChars? _, TextInputMethod _, CancellationToken _) =>
                new TypeTextResult(true, text.Length, text.Length) { ReceivingWindow = notepad });
        _input.Setup(s => s.SendKeysAsync(It.IsAny<string>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string keys, InputTarget _, bool _, int _, CancellationToken _) => new SendKeysResult(true, keys) { ReceivingWindow = notepad });
        _dialogs.Setup(d => d.RespondAsync(It.IsAny<DialogResponseRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _order.Add("press"))
            .ReturnsAsync(new DialogResponseResult(true, Dialog, "Notepad", DialogClosed: true));
        _windows.Setup(w => w.IsValidWindow(It.IsAny<nint>())).Returns(true);
        _desktopState.Setup(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DesktopStateRequest request, CancellationToken _) =>
            {
                _order.Add($"observe {request.Window}");
                _observed = request;
            })
            .ReturnsAsync((DesktopStateRequest request, CancellationToken _) => new DesktopStateSnapshot(
                new DesktopState(true, request.Window ?? InFront, "after", null, null, null, new DesktopInputState(false), null, null, null,
                    null, null, null, 9),
                request.Include.Contains(DesktopStateSection.Screenshot) ? new EncodedImage([1, 2, 3], "image/png") : null));
    }

    private InputSimulationTools CreateInputSut() => new(_input.Object, _windows.Object, desktopState: _desktopState.Object);

    private DialogTools CreateDialogSut() => new(_dialogs.Object, Mock.Of<IFileDialogService>(), _windows.Object, _desktopState.Object);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    [Fact]
    public async Task WithoutObserve_TheResultIsTheInputsAlone()
    {
        var result = await CreateInputSut().Click(100, 200, windowHandle: Notepad);

        result.Content.Should().ContainSingle();
        result.IsError.Should().NotBe(true);
        var payload = result.Payload<ClickResult>();
        payload.X.Should().Be(100);
        payload.Observed.Should().BeNull();
        _desktopState.Verify(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Click_WithObserveState_ObservesTheTargetWindow_AfterTheClick()
    {
        var result = await CreateInputSut().Click(100, 200, windowHandle: Notepad, observe: ObserveMode.State);

        _order.Should().Equal("click", $"observe {Notepad}");
        _observed!.Include.Should().BeEquivalentTo(DesktopStateRequest.DefaultInclude);
        result.Content.Should().ContainSingle("no capture was asked for");
        result.Payload<ClickResult>().Observed!.WindowHandle.Should().Be(Notepad);
    }

    [Fact]
    public async Task Click_WithoutATarget_ObservesTheWindowTheClickWentTo()
    {
        await CreateInputSut().Click(100, 200, observe: ObserveMode.State);

        _observed!.Window.Should().Be(Notepad);
    }

    [Fact]
    public async Task Click_WithObserveScreenshot_PutsTheCaptureFirst()
    {
        var result = await CreateInputSut().Click(100, 200, windowHandle: Notepad, observe: ObserveMode.Screenshot);

        _observed!.Include.Should().Contain(DesktopStateSection.Screenshot);
        result.Content.Should().HaveCount(2);
        result.Content[0].Should().BeOfType<ImageContentBlock>();
        result.Payload<ClickResult>().Observed.Should().NotBeNull();
    }

    [Fact]
    public async Task AWindowThatClosed_IsObservedAsTheWindowInFront()
    {
        // The click closed the window (a dialog's OK): what the agent sees next is the window in front
        _windows.Setup(w => w.IsValidWindow(Notepad)).Returns(false);

        await CreateInputSut().Click(100, 200, windowHandle: Notepad, observe: ObserveMode.State);

        _observed!.Window.Should().BeNull();
    }

    [Theory]
    [InlineData(InputErrorCode.TargetDeniedByPolicy)]
    [InlineData(InputErrorCode.TargetIsAgentHost)]
    public async Task AnInputRefusedForItsWindow_DoesNotObserveThatWindowByName(InputErrorCode refusal)
    {
        // The agent asked for input, not a read of a denied app or its own terminal: the default rules apply
        _input.Setup(s => s.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(new ClickResult(false, 100, 200, "left", "Refused.", refusal)
            {
                ReceivingWindow = new WindowSummary(Notepad, "notes.txt - Notepad", "notepad", 4242)
            });

        var result = await CreateInputSut().Click(100, 200, windowHandle: Notepad, observe: ObserveMode.State);

        result.IsError.Should().BeTrue();
        result.Payload<ClickResult>().ErrorCode.Should().Be(refusal);
        _observed!.Window.Should().BeNull("the window the input was refused for is not read by name");
    }

    [Fact]
    public async Task AFailedInput_IsStillObserved_AndStaysAnError()
    {
        _input.Setup(s => s.Click(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(new ClickResult(false, 100, 200, "left", "Covered.", InputErrorCode.TargetOccluded));

        var result = await CreateInputSut().Click(100, 200, windowHandle: Notepad, observe: ObserveMode.State);

        result.IsError.Should().BeTrue();
        result.Payload<ClickResult>().Observed.Should().NotBeNull("what covers the window helps after TargetOccluded");
        _observed!.Window.Should().Be(Notepad);
    }

    [Fact]
    public async Task RightAndDoubleClick_AreObserved()
    {
        var right = await CreateInputSut().RightClick(100, 200, windowHandle: Notepad, observe: ObserveMode.State);
        var twice = await CreateInputSut().DoubleClick(100, 200, windowHandle: Notepad, observe: ObserveMode.State);

        right.Payload<ClickResult>().Observed!.WindowHandle.Should().Be(Notepad);
        twice.Payload<ClickResult>().Observed!.WindowHandle.Should().Be(Notepad);
    }

    [Fact]
    public async Task TypeText_WithObserveState_ObservesTheTargetWindow_AfterTheTyping()
    {
        var result = await CreateInputSut().TypeText("hello", windowHandle: Notepad, observe: ObserveMode.State);

        _order.Should().Equal("type", $"observe {Notepad}");
        var payload = result.Payload<TypeTextResult>();
        payload.CharactersTyped.Should().Be(5);
        payload.Observed!.WindowHandle.Should().Be(Notepad);
    }

    [Fact]
    public async Task SendKeys_WithObserveScreenshot_PutsTheCaptureFirst()
    {
        var result = await CreateInputSut().SendKeys("{ENTER}", windowHandle: Notepad, observe: ObserveMode.Screenshot);

        result.Content[0].Should().BeOfType<ImageContentBlock>();
        result.Payload<SendKeysResult>().Observed!.WindowHandle.Should().Be(Notepad);
    }

    [Fact]
    public async Task ObserveScreenshot_IsRefusedBeforeAnythingIsSent_WhenCapturesAreOff()
    {
        _desktopState.Setup(s => s.ScreenshotUnavailable()).Returns("A screenshot needs capture_window.");

        var error = await RefusedAsync(() => CreateInputSut().TypeText("hello", windowHandle: Notepad, observe: ObserveMode.Screenshot));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        error.Message.Should().Contain("capture_window").And.EndWith("Nothing was sent.");
        _input.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ObserveMode.State)]
    [InlineData(ObserveMode.Screenshot)]
    public async Task Observe_IsRefusedBeforeAnythingIsSent_WhenGetDesktopStateIsNotServed(ObserveMode observe)
    {
        _desktopState.Setup(s => s.StateUnavailable()).Returns("observe returns get_desktop_state's observation.");

        var click = await RefusedAsync(() => CreateInputSut().Click(100, 200, windowHandle: Notepad, observe: observe));
        var keys = await RefusedAsync(() => CreateInputSut().SendKeys("{ENTER}", windowHandle: Notepad, observe: observe));
        var dialog = await RefusedAsync(() => CreateDialogSut().RespondToDialog("ok", handle: Dialog, observe: observe));

        click.Message.Should().Contain("get_desktop_state").And.EndWith("Nothing was sent.");
        keys.Message.Should().EndWith("Nothing was sent.");
        dialog.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        dialog.Message.Should().Contain("get_desktop_state").And.EndWith("Nothing was done.");
        _input.VerifyNoOtherCalls();
        _dialogs.VerifyNoOtherCalls();
        _desktopState.Verify(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RespondToDialog_ThatClosedTheDialog_ObservesTheWindowInFront_AfterThePress()
    {
        var result = await CreateDialogSut().RespondToDialog("ok", handle: Dialog, observe: ObserveMode.State);

        _order.Should().Equal("press", "observe ");
        _observed!.Window.Should().BeNull("the dialog is gone; the window in front, usually its owner, is what the agent sees next");
        var payload = result.Payload<DialogResponseResult>();
        payload.DialogClosed.Should().BeTrue();
        payload.Observed!.WindowHandle.Should().Be(InFront);
    }

    [Fact]
    public async Task RespondToDialog_ThatLeftTheDialogOpen_ObservesTheDialog()
    {
        _dialogs.Setup(d => d.RespondAsync(It.IsAny<DialogResponseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DialogResponseResult(true, Dialog, "Notepad", DialogClosed: false));

        var result = await CreateDialogSut().RespondToDialog("help", handle: Dialog, observe: ObserveMode.Screenshot);

        _observed!.Window.Should().Be(Dialog);
        result.Content[0].Should().BeOfType<ImageContentBlock>();
    }

    [Fact]
    public async Task RespondToDialog_RefusedForTheAgentsHost_DoesNotObserveItByName()
    {
        _dialogs.Setup(d => d.RespondAsync(It.IsAny<DialogResponseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DialogResponseResult(false, Dialog, Error: "Refused.", ErrorCode: nameof(InputErrorCode.TargetIsAgentHost)));

        var result = await CreateDialogSut().RespondToDialog("ok", handle: Dialog, observe: ObserveMode.State);

        result.IsError.Should().BeTrue();
        _observed!.Window.Should().BeNull();
    }

    [Fact]
    public async Task RespondToDialog_WithoutObserve_KeepsItsResult()
    {
        var result = await CreateDialogSut().RespondToDialog("ok", handle: Dialog);

        result.Content.Should().ContainSingle();
        result.Payload<DialogResponseResult>().Should().Be(new DialogResponseResult(true, Dialog, "Notepad", DialogClosed: true));
        _desktopState.Verify(s => s.GetStateAsync(It.IsAny<DesktopStateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(typeof(InputSimulationTools), nameof(InputSimulationTools.Click))]
    [InlineData(typeof(InputSimulationTools), nameof(InputSimulationTools.RightClick))]
    [InlineData(typeof(InputSimulationTools), nameof(InputSimulationTools.DoubleClick))]
    [InlineData(typeof(InputSimulationTools), nameof(InputSimulationTools.TypeText))]
    [InlineData(typeof(InputSimulationTools), nameof(InputSimulationTools.SendKeys))]
    [InlineData(typeof(DialogTools), nameof(DialogTools.RespondToDialog))]
    public void EveryObservingTool_DescribesObserveWithTheOneSharedText(Type tool, string method)
    {
        // One text, so tools/list repeats a short description rather than six of its own
        var observe = tool.GetMethod(method)!.GetParameters().Single(p => p.Name == "observe");

        observe.GetCustomAttribute<DescriptionAttribute>()!.Description.Should().Be(ActionObservation.ObserveDescription);
        observe.DefaultValue.Should().Be(ObserveMode.None);
    }
}
