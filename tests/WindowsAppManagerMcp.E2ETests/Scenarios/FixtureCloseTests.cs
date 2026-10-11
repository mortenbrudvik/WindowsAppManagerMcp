using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// close_window's guard (roadmap M1.7) and verified close (roadmap M2.5) on the real desktop: the fixture's document,
/// an ordinary app window with unsaved changes, is not blocked (the close reaches it, it shows its save prompt, and
/// close_window reports that prompt with its text and buttons), while the desktop shell is refused by the server itself
/// with ProtectedWindow. Input tier, because posting WM_CLOSE
/// changes a window. The harness guard lets window calls through only for fixture windows, so even a broken
/// server guard could not post anything to the shell: the call would fail with E2EGuardException instead
/// of returning ProtectedWindow.
/// </summary>
public class FixtureCloseTests
{
    private static readonly string[] ShellClasses = ["Progman", "Shell_TrayWnd"];

    private readonly ITestOutputHelper _output;

    public FixtureCloseTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public async Task CloseWindow_ReachesTheFixtureDocument_ReportsItsSavePrompt_ButRefusesTheDesktopShell()
    {
        // Arrange - the document starts with unsaved changes, so closing it shows the fixture's save prompt
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, new FixtureLaunchOptions { OpenDocument = true });
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");
        var tools = e2e.Server.Get<WindowManagerTools>();
        var hostGuard = e2e.Server.Get<IHostGuard>();
        _output.WriteLine($"Host guard: {hostGuard.Host}");
        hostGuard.IsHostWindow(document).Should().BeFalse(
            "the fixture is not the agent's host; if the guard recorded it (for example as the startup foreground " +
            "window), this test cannot tell a blocked close from a refused one");

        var shellWindows = ShellClasses
            .SelectMany(className => e2e.Server.Windows.FindWindows(new WindowQuery { ClassName = className }))
            .ToList();
        shellWindows.Should().NotBeEmpty("the desktop session should have explorer's desktop (Progman) or taskbar (Shell_TrayWnd)");

        // Act 1 - the desktop shell
        var shellResults = new List<(WindowInfo Window, CloseWindowResult Result)>();
        foreach (var window in shellWindows)
        {
            shellResults.Add((window, await tools.CloseWindow(window.Handle)));
        }

        // Assert 1 - refused by the server, before anything was posted
        foreach (var (window, result) in shellResults)
        {
            _output.WriteLine($"close_window {window.Handle} ({window.ClassName}): {result}");
            result.Success.Should().BeFalse("{0} ({1}) is part of the desktop shell", window.Handle, window.ClassName);
            result.ErrorCode.Should().Be(nameof(WindowErrorCode.ProtectedWindow));
        }

        // Act 2 - the fixture's document
        var documentResult = await tools.CloseWindow(document, waitMs: 10_000);

        // Assert 2 - not blocked: the close reached the document, which asks whether to save its changes, and the
        // result describes that prompt (a WPF MessageBox: the enabled popup of the disabled document)
        e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentClosePrompted, e => e.GetHandle("hwnd") == document);
        _output.WriteLine($"close_window {document}: {documentResult}");
        documentResult.Status.Should().Be("promptShown", documentResult.Error);
        documentResult.ErrorCode.Should().Be(nameof(WindowErrorCode.PromptShown));
        documentResult.Prompt!.Title.Should().StartWith(FixtureContract.SavePromptTitlePrefix);
        documentResult.Prompt.Text.Should().Contain("save changes");
        documentResult.Prompt.Buttons.Should().Equal("Yes", "No", "Cancel");
    }
}
