namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Binding a desktop window to the browser window CDP reports for its tabs (Browser.getWindowForTarget, roadmap M5.3):
/// the same state and, for a normal window, the same bounds once DIPs are scaled to pixels.
/// </summary>
public class CdpWindowBinderTests
{
    private static CdpWindowGeometry Normal(int left, int top, int right, int bottom, double scale = 1.0) =>
        new(left, top, right, bottom, CdpWindowState.Normal, scale);

    [Fact]
    public void Matches_ANormalWindow_WithTheSameBounds()
    {
        CdpWindowBinder.Matches(Normal(100, 50, 1300, 850), new CdpBrowserWindow(1, 100, 50, 1200, 800, "normal"))
            .Should().BeTrue();
    }

    [Fact]
    public void Matches_ScalesTheDipsByTheWindowsDpi()
    {
        // 150 %: Chrome reports DIPs, GetWindowRect gives pixels
        CdpWindowBinder.Matches(Normal(150, 75, 1950, 1275, 1.5), new CdpBrowserWindow(1, 100, 50, 1200, 800, "normal"))
            .Should().BeTrue();
        CdpWindowBinder.Matches(Normal(150, 75, 1950, 1275, 1.5), new CdpBrowserWindow(1, 150, 75, 1800, 1200, "normal"))
            .Should().BeFalse();
    }

    [Fact]
    public void Matches_AllowsTheRoundingOfAFewPixels()
    {
        CdpWindowBinder.Matches(Normal(103, 47, 1302, 849), new CdpBrowserWindow(1, 100, 50, 1200, 800, "normal"))
            .Should().BeTrue();
    }

    [Fact]
    public void DoesNotMatch_AWindowCascadedTenDipsAway()
    {
        // Chromium opens a new window 10 DIPs below and right of the last one
        CdpWindowBinder.Matches(Normal(110, 60, 1310, 860), new CdpBrowserWindow(1, 100, 50, 1200, 800, "normal"))
            .Should().BeFalse();
        CdpWindowBinder.Matches(Normal(165, 90, 1965, 1290, 1.5), new CdpBrowserWindow(1, 100, 50, 1200, 800, "normal"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(CdpWindowState.Maximized, "maximized", true)]
    [InlineData(CdpWindowState.Maximized, "normal", false)]
    [InlineData(CdpWindowState.Minimized, "minimized", true)]
    [InlineData(CdpWindowState.Minimized, "maximized", false)]
    [InlineData(CdpWindowState.Normal, "minimized", false)]
    [InlineData(CdpWindowState.Normal, "maximized", false)]
    [InlineData(CdpWindowState.Normal, "fullscreen", true)]
    public void Matches_AWindowThatIsNotNormal_ByItsStateAlone(CdpWindowState state, string reported, bool expected)
    {
        // A maximized, minimized or full-screen window reports its restored bounds, which cannot be compared
        var window = new CdpWindowGeometry(-8, -8, 1928, 1048, state, 1.0);

        CdpWindowBinder.Matches(window, new CdpBrowserWindow(1, 300, 200, 800, 600, reported)).Should().Be(expected);
    }

    [Fact]
    public void MatchingWindows_ReturnsEveryWindowItCanBe()
    {
        var windows = new[]
        {
            new CdpBrowserWindow(1, 0, 0, 800, 600, "maximized"),
            new CdpBrowserWindow(2, 100, 50, 1200, 800, "normal"),
            new CdpBrowserWindow(3, 0, 0, 800, 600, "maximized")
        };

        CdpWindowBinder.MatchingWindows(Normal(100, 50, 1300, 850), windows).Should().BeEquivalentTo([2]);
        CdpWindowBinder.MatchingWindows(new CdpWindowGeometry(-8, -8, 1928, 1048, CdpWindowState.Maximized, 1.0), windows)
            .Should().BeEquivalentTo([1, 3]);
    }
}
