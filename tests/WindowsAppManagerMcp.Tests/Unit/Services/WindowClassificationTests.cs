namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The pure window rules of <see cref="WindowClassification"/>: which windows are part of the desktop
/// shell, and which are dialog boxes. No window is read.
/// </summary>
public class WindowClassificationTests
{
    private static readonly nint ShellWindow = 0x100;
    private static readonly nint OtherWindow = 0x200;
    private const uint ShellPid = 300;
    private const uint OtherPid = 500;

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    [InlineData("shell_traywnd")]
    public void IsShellWindow_ShellClassOfTheShellsProcess_IsTrue(string className)
    {
        WindowClassification.IsShellWindow(OtherWindow, className, ShellPid, ShellWindow, ShellPid).Should().BeTrue();
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void IsShellWindow_ShellClassOfAnotherProcess_IsFalse(string className)
    {
        WindowClassification.IsShellWindow(OtherWindow, className, OtherPid, ShellWindow, ShellPid).Should().BeFalse(
            "SHCreateWorkerWindow makes WorkerW windows in any process");
    }

    [Theory]
    [InlineData("WorkerW", 0u)]
    [InlineData("Shell_TrayWnd", 0u)]
    [InlineData("Progman", OtherPid)]
    public void IsShellWindow_WhileTheShellsProcessIsNotKnown_GoesByTheClassAlone(string className, uint processId)
    {
        WindowClassification.IsShellWindow(OtherWindow, className, processId, shellWindow: nint.Zero, shellProcessId: 0)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("CustomShellDesktop", OtherPid)]
    [InlineData("", 0u)]
    public void IsShellWindow_TheShellWindow_IsTrueWhateverItsClassAndProcess(string className, uint processId)
    {
        WindowClassification.IsShellWindow(ShellWindow, className, processId, ShellWindow, ShellPid).Should().BeTrue();
    }

    [Theory]
    [InlineData("CabinetWClass")]
    [InlineData("#32770")]
    [InlineData(null)]
    public void IsShellWindow_OtherClassOfTheShellsProcess_IsFalse(string? className)
    {
        // File Explorer folder windows and the shell's dialogs belong to explorer.exe too
        WindowClassification.IsShellWindow(OtherWindow, className, ShellPid, ShellWindow, ShellPid).Should().BeFalse();
    }

    [Theory]
    [InlineData("#32770", true)]
    [InlineData("#32768", false)] // a menu
    [InlineData("Dialog", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDialogClass_MatchesOnlyTheDialogClass(string? className, bool expected)
    {
        WindowClassification.IsDialogClass(className).Should().Be(expected);
    }
}
