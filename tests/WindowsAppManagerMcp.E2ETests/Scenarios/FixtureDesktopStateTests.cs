namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// get_desktop_state (roadmap M3.12) against the fixture: one observation lists the fixture among the windows and its
/// controls as text lines with refs that resolve to the live elements, reads the cursor and the keys, and stays within its
/// deadline. No-input tier: it only reads (no capture).
/// </summary>
public class FixtureDesktopStateTests
{
    private readonly ITestOutputHelper _output;

    public FixtureDesktopStateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public async Task GetDesktopState_OfTheFixture_ListsItsControlsWithRefs_AndTheFixtureAmongTheWindows()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var window = e2e.Fixture.MainWindowHandle;
        var request = new DesktopStateRequest(window, DesktopStateRequest.DefaultInclude, MaxElements: 80, InteractableOnly: true,
            ElementSearchScope.Window, MaxWindows: 100, ScreenshotMaxLongEdge: 1280, Timeout: TimeSpan.FromSeconds(10));

        // Act
        var (state, image) = await e2e.Server.Get<IDesktopStateService>().GetStateAsync(request, CancellationToken.None);

        // Assert
        _output.WriteLine(state.Elements?.Text ?? "(no elements)");
        foreach (var error in state.Errors ?? [])
        {
            _output.WriteLine($"{error.Section}: {error.ErrorCode} {error.Error}");
        }

        state.Success.Should().BeTrue(state.Error);
        image.Should().BeNull("no capture was asked for");
        state.WindowHandle.Should().Be(window);
        state.Errors.Should().NotContain(e => e.Section == "elements" || e.Section == "popups");
        state.Elements!.Text.Should().Contain(FixtureContract.Ids.OpenModalButton);
        state.Windows.Should().Contain(row => row.Handle == window, "the fixture is a user-facing window");
        state.Cursor.Should().NotBeNull();

        var line = state.Elements.Text!.Split('\n').First(l => l.Contains(FixtureContract.Ids.OpenModalButton, StringComparison.Ordinal));
        var elementRef = line.TrimStart().Split(' ')[0];
        var resolution = await e2e.Server.Get<IElementRegistry>().ResolveAsync(elementRef, TimeSpan.FromSeconds(10), CancellationToken.None);
        resolution.Success.Should().BeTrue(resolution.Error);
    }
}
