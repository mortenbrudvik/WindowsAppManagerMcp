namespace WindowsAppManagerMcp.Tests.Unit.Tools;

using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

/// <summary>
/// respond_to_dialog's and file_dialog's arguments (roadmap M3.5, M3.14): the window they act on, the timeouts, and what is
/// refused before anything is read. The services are DialogServiceTests' and FileDialogServiceTests'.
/// </summary>
public class DialogToolsTests
{
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IFileDialogService> _fileDialogs = new();
    private readonly Mock<IWindowService> _windows = new();
    private DialogResponseRequest? _respond;
    private FileDialogRequest? _complete;

    public DialogToolsTests()
    {
        _dialogs.Setup(d => d.RespondAsync(It.IsAny<DialogResponseRequest>(), It.IsAny<CancellationToken>()))
            .Callback((DialogResponseRequest request, CancellationToken _) => _respond = request)
            .ReturnsAsync(new DialogResponseResult(true, 0x5000, DialogClosed: true));
        _fileDialogs.Setup(d => d.CompleteAsync(It.IsAny<FileDialogRequest>(), It.IsAny<CancellationToken>()))
            .Callback((FileDialogRequest request, CancellationToken _) => _complete = request)
            .ReturnsAsync(new FileDialogResult(true, "save", 0x5000, Completed: true));
        _windows.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns(WindowResolution.Found(TestDataFactory.CreateWindowInfo(handleValue: 0x4400)));
    }

    private DialogTools CreateSut() => new(_dialogs.Object, _fileDialogs.Object, _windows.Object);

    private static async Task<ToolErrorException> RefusedAsync(Func<Task> call) =>
        (await FluentActions.Awaiting(call).Should().ThrowAsync<ToolErrorException>()).Which;

    [Fact]
    public async Task RespondToDialog_WithoutAWindow_NamesTheDialogInFront_WhichTheServiceOnlyReadsOrCancels()
    {
        await CreateSut().RespondToDialog("yes");

        _respond.Should().Be(new DialogResponseRequest(0, "yes", false, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RespondToDialog_WithASelector_AnswersTheDialogOfTheWindowItResolvesTo_ClampingTheTimeout()
    {
        await CreateSut().RespondToDialog("Cancel", window: new WindowSelector { ProcessName = "notepad" }, timeoutMs: 99_000);

        _respond!.Window.Should().Be((nint)0x4400);
        _respond.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task RespondToDialog_WithoutAButton_AsksOnlyToReadTheDialog()
    {
        await CreateSut().RespondToDialog(handle: 0x5000);

        _respond.Should().Be(new DialogResponseRequest(0x5000, null, false, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RespondToDialog_WithAnEmptyButton_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().RespondToDialog(" "));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _respond.Should().BeNull();
    }

    [Fact]
    public async Task AllowHostTarget_NeedsAWindowNamedByHandle()
    {
        var error = await RefusedAsync(() => CreateSut().RespondToDialog("ok", allowHostTarget: true));
        error.Message.Should().Contain("named by its handle");

        await CreateSut().RespondToDialog("ok", handle: 0x5000, allowHostTarget: true);
        _respond!.AllowHostTarget.Should().BeTrue();
    }

    [Fact]
    public async Task FileDialog_PassesThePathActionAndOverwrite_WithTheDefaultTimeout()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wam-{Guid.NewGuid():N}.txt");

        await CreateSut().FileDialog(path, FileDialogAction.Save, overwrite: true, handle: 0x5000);

        _complete.Should().Be(new FileDialogRequest(0x5000, path, FileDialogAction.Save, true, false, TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(@"\\server\share\report.txt")]
    [InlineData(@"report.txt")]
    [InlineData(@"C:\docs\*.txt")]
    public async Task FileDialog_RefusesAnInvalidPath_BeforeTheDialogIsTouched(string path)
    {
        var error = await RefusedAsync(() => CreateSut().FileDialog(path, FileDialogAction.Save));

        error.ErrorCode.Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _complete.Should().BeNull();
    }

    [Fact]
    public async Task FileDialog_OpenOfAMissingFile_IsRefused()
    {
        var error = await RefusedAsync(() => CreateSut().FileDialog(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt"),
            FileDialogAction.Open));

        error.Message.Should().Contain("does not exist");
    }
}
