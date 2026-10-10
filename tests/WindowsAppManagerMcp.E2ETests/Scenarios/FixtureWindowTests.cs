namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// find_windows and get_window_info against the fixture, compared with what the fixture reports about
/// itself. No-input tier: the fixture is shown without activation, and the server can only read.
/// </summary>
public class FixtureWindowTests
{
    private readonly ITestOutputHelper _output;

    public FixtureWindowTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public void FindWindows_ByProcessId_ReportsTheMainWindowAsTheFixtureSeesIt()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var ready = e2e.Fixture.Ready;

        // Act
        var windows = e2e.Server.Windows.FindWindows(processId: e2e.Fixture.ProcessId);

        // Assert
        var main = windows.Should().ContainSingle(w => w.Handle == ready.MainWindowHandle).Subject;
        main.Title.Should().Be(FixtureContract.MainTitle(e2e.Fixture.Instance));
        main.ProcessName.Should().Be(FixtureContract.ProcessName);
        main.ProcessId.Should().Be(e2e.Fixture.ProcessId);
        main.IsVisible.Should().BeTrue();
        main.State.Should().Be(WindowState.Normal);
        main.Bounds.Should().Be(
            new WindowRect(ready.WindowRect.X, ready.WindowRect.Y, ready.WindowRect.Width, ready.WindowRect.Height),
            "the server and the fixture are both DPI aware, so both read the same physical window rectangle");
    }

    [DesktopNoInputFact]
    public void FindWindows_ByTitle_FindsOnlyThisFixtureInstance()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);

        // Act
        var windows = e2e.Server.Windows.FindWindows(titleContains: FixtureContract.MainTitle(e2e.Fixture.Instance));

        // Assert
        windows.Should().ContainSingle().Which.Handle.Should().Be(e2e.Fixture.MainWindowHandle);
    }

    [DesktopNoInputFact]
    public void FindWindows_WithTheDocumentOpen_ListsItAsASecondTopLevelWindow()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenDocument = true });
        var document = e2e.Fixture.WaitForEvent(FixtureContract.Events.DocumentOpened).GetHandle("hwnd");

        // Act
        var windows = Wait.ForValue(
            () =>
            {
                var listed = e2e.Server.Windows.FindWindows(processId: e2e.Fixture.ProcessId);
                return listed.Any(w => w.Handle == document) ? listed : null;
            },
            E2ESession.FindWindowTimeout);

        // Assert
        windows.Should().NotBeNull("find_windows should list the document window 0x{0:X}", document);
        var listed = windows!;
        listed.Select(w => w.Handle).Should().Contain(new[] { e2e.Fixture.MainWindowHandle, document });
        listed.Single(w => w.Handle == document).Title
            .Should().Be(FixtureContract.DocumentTitle(e2e.Fixture.Instance, dirty: true));
        E2ENative.GetWindow(document, E2ENative.GW_OWNER).Should().Be(nint.Zero,
            "the document is an unowned top-level window, like a second window of a real app");
    }

    [DesktopNoInputFact]
    public void OpenModal_IsOwnedByTheMainWindowAndDisablesIt()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenModal = true });
        var opened = e2e.Fixture.WaitForEvent(FixtureContract.Events.ModalOpened);
        var modal = opened.GetHandle("hwnd");
        var main = e2e.Fixture.MainWindowHandle;

        // Act
        var info = e2e.Server.Windows.GetWindowInfo(modal);

        // Assert - what the BlockedByModal check of the input guard (roadmap M1.5) relies on
        info.Should().NotBeNull();
        info!.Title.Should().Be(FixtureContract.ModalTitle(e2e.Fixture.Instance));
        info.ProcessId.Should().Be(e2e.Fixture.ProcessId);
        opened.GetHandle("ownerHwnd").Should().Be(main);
        E2ENative.GetWindow(modal, E2ENative.GW_OWNER).Should().Be(main);
        E2ENative.IsWindowEnabled(main).Should().BeFalse("ShowDialog disables the owner while the dialog is open");

        // Recorded, not asserted yet: whether GW_ENABLEDPOPUP finds a WPF dialog decides how M1.5 reports it.
        _output.WriteLine(
            $"GetWindow(main, GW_ENABLEDPOPUP) = 0x{E2ENative.GetWindow(main, E2ENative.GW_ENABLEDPOPUP):X}; modal is 0x{modal:X}.");
    }
}
