namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// The accessibility warm-up (roadmap M3.3) against live Chromium content (roadmap M3.F4), no-input tier: the fixture's
/// WebView2 window shows local HTML in a Chrome_RenderWidgetHostHWND of msedgewebview2.exe, and Chromium builds the page's
/// accessibility tree only once a UI Automation client asks for it. The first read of the window must warm it up (host
/// WebView2, status Ready) and see the page; a later read of the same window must not wait again. Skipped, with the reason,
/// on a machine without the WebView2 runtime (<see cref="WebView2Runtime"/>).
/// </summary>
public class FixtureWebViewTests
{
    /// <summary>How long WebView2 gets to start and load the page: its first start on a machine can take a while.</summary>
    private static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Deep enough for the page: the window, the WebView2 control's panes, the document and its elements.</summary>
    private const int TreeDepth = 12;

    private readonly ITestOutputHelper _output;

    public FixtureWebViewTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>The WebView2 window, once its page has loaded; fails with the fixture's reason when it could not load it.</summary>
    private nint WaitForThePage(E2ESession e2e)
    {
        var outcome = e2e.Fixture.WaitForAnyEvent([FixtureContract.Events.WebViewReady, FixtureContract.Events.WebViewFailed], PageTimeout);
        outcome.Name.Should().Be(FixtureContract.Events.WebViewReady,
            "the WebView2 window should show its page, but it could not (runtime missing: {0}): {1}",
            outcome.GetBoolean("runtimeMissing"), outcome.GetString("message"));
        var window = outcome.GetHandle("hwnd");
        _output.WriteLine($"WebView2 window 0x{window:X}, runtime {outcome.GetString("runtimeVersion")}.");
        return window;
    }

    private void Log(AccessibilityWarmupInfo? warmup, string read) =>
        _output.WriteLine(warmup == null
            ? $"{read}: no warm-up"
            : $"{read}: warm-up {warmup.Status} host={warmup.Host} waited={warmup.WaitedMs} ms polls={warmup.Polls} " +
              $"documentChildren={warmup.DocumentChildCount} warning={warmup.Warning}");

    [DesktopNoInputFact(Requires = typeof(WebView2Runtime))]
    public async Task GetUIElements_OfTheWebViewWindow_WarmsUpItsPage_AndReadsIt()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenWebView = true });
        var window = WaitForThePage(e2e);

        // Act - the first UI Automation read of the window
        var result = await e2e.Server.UiElements.GetUIElementsAsync(window, TreeDepth);
        Log(result.AccessibilityWarmup, "get_ui_elements");
        var elements = UiaReads.Flatten(result.Elements).ToList();
        foreach (var element in elements.Where(e => e.FrameworkId == "Chrome" || e.ControlType is "Document" or "Button"))
        {
            _output.WriteLine($"  {element.ControlType} '{element.Name}' aid={element.AutomationId} fw={element.FrameworkId}");
        }

        // Assert - the window holds a render widget of msedgewebview2.exe, so the read asked for its page and waited for it
        result.Success.Should().BeTrue(result.Error);
        var warmup = result.AccessibilityWarmup;
        warmup.Should().NotBeNull("the window holds a Chrome_RenderWidgetHostHWND, which the read warms up first");
        warmup!.Host.Should().Be(WebContentHost.WebView2, "the render widget belongs to msedgewebview2.exe");
        warmup.Status.Should().Be(AccessibilityWarmupStatus.Ready, warmup.Warning);
        warmup.DocumentChildCount.Should().BeGreaterThan(0);
        elements.Should().Contain(e => e.ControlType == "Document", "the page's document is in the tree");
        elements.Should().Contain(e => e.ControlType == "Button" && e.Name == FixtureContract.WebButtonName,
            "the read after the warm-up sees the page's own elements, not an empty document");
    }

    [DesktopNoInputFact(Requires = typeof(WebView2Runtime))]
    public async Task FindUiElements_FindsThePagesButton_AndALaterReadOfTheWindowDoesNotWaitAgain()
    {
        // Arrange - a first read warms the window up
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output, new FixtureLaunchOptions { OpenWebView = true });
        var window = WaitForThePage(e2e);
        var first = await e2e.Server.UiElements.GetUIElementsAsync(window, maxDepth: 2);
        Log(first.AccessibilityWarmup, "first read");
        first.AccessibilityWarmup.Should().NotBeNull();
        first.AccessibilityWarmup!.Status.Should().NotBe(AccessibilityWarmupStatus.NotReady, first.AccessibilityWarmup.Warning);

        // Act
        var found = await e2e.Server.Get<IElementSearchService>().FindAsync(
            new ElementQuery(window, Name: FixtureContract.WebButtonName, ControlTypes: ["Button"], Timeout: Timeout));
        Log(found.AccessibilityWarmup, "find_ui_elements");

        // Assert - the window and its render widget are remembered, so the search reads at once
        found.Success.Should().BeTrue(found.Error);
        var button = found.Elements.Should().ContainSingle().Which;
        _output.WriteLine($"  {button.Ref} {button.ControlType} '{button.Name}' aid={button.AutomationId} bounds={button.Bounds}");
        button.Ref.Should().NotBeNull();
        found.AccessibilityWarmup.Should().BeNull("the first read warmed the window up, and the server remembers it");
    }
}
