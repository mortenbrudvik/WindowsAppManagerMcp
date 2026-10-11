namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// The DPI awareness the server reports: GetAwarenessFromDpiAwarenessContext names only unaware, system and per-monitor,
/// so per-monitor v2 and GDI-scaled unawareness are told apart by comparing the context with theirs.
/// </summary>
public class DpiAwarenessTests
{
    [Theory]
    [InlineData(2, true, false, DpiAwareness.PerMonitorV2)]
    [InlineData(2, false, false, DpiAwareness.PerMonitor)]
    [InlineData(1, false, false, DpiAwareness.System)]
    [InlineData(0, false, false, DpiAwareness.Unaware)]
    [InlineData(0, false, true, DpiAwareness.UnawareGdiScaled)]
    [InlineData(-1, false, false, DpiAwareness.Unknown)]
    [InlineData(7, false, false, DpiAwareness.Unknown)]
    public void FromContext_NamesTheAwareness(int awareness, bool perMonitorV2, bool gdiScaled, DpiAwareness expected)
    {
        DpiAwarenessNames.FromContext(awareness, perMonitorV2, gdiScaled).Should().Be(expected);
    }

    [Theory]
    [InlineData(DpiAwareness.PerMonitorV2, true)]
    [InlineData(DpiAwareness.PerMonitor, false)]
    [InlineData(DpiAwareness.System, false)]
    [InlineData(DpiAwareness.Unknown, false)]
    public void IsExpected_OnlyForPerMonitorV2(DpiAwareness awareness, bool expected)
    {
        // What the manifest declares; anything else means coordinates may be scaled, and the server says so
        DpiAwarenessNames.IsExpected(awareness).Should().Be(expected);
    }
}
