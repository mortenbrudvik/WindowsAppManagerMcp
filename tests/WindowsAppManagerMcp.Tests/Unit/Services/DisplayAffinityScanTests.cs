using System.Drawing;
using WindowsAppManagerMcp.Infrastructure;
using static WindowsAppManagerMcp.Tests.Unit.Services.DeniedWindowMaskTests;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The windows that keep themselves out of captures (issue #17): the drawn windows with a display affinity, each with its
/// visible frame, named only where the user's target policy allows it, and cut to a capture's area once per window.
/// </summary>
public class DisplayAffinityScanTests
{
    private static readonly TargetPolicy DeniesSome = new(TargetPolicySettings.Default);
    private static readonly TargetPolicy DeniesNothing = new(new TargetPolicySettings([], null, "test"));

    private static IReadOnlyList<ExcludedWindow> Read(params FakeWindow[] windows)
    {
        var (native, policy) = Desktop(windows);
        return new DisplayAffinityScan(native.Object, policy.Object, DeniesSome).Read();
    }

    [Fact]
    public void Read_AWindowExcludedFromCapture_IsListed_WithItsAffinityProcessTitleAndVisibleFrame()
    {
        var windows = Read(
            new FakeWindow(1, new Rectangle(1593, 893, 334, 114), Frame: new Rectangle(1600, 900, 320, 100),
                Affinity: WdaExcludeFromCapture, Title: "Board saved", ProcessName: "Enify"),
            new FakeWindow(2, new Rectangle(0, 0, 1920, 1080), Title: "Editor", ProcessName: "notepad"));

        windows.Should().Equal(new ExcludedWindow(1, DisplayAffinity.ExcludeFromCapture, "Enify", "Board saved", 1600, 900, 320, 100));
    }

    [Fact]
    public void Read_AWindowShownOnlyOnTheMonitor_IsListedAsMonitor()
    {
        var windows = Read(new FakeWindow(1, new Rectangle(10, 20, 300, 200), Affinity: WdaMonitor, Title: "Player", ProcessName: "drm"));

        windows.Should().ContainSingle().Which.Affinity.Should().Be(DisplayAffinity.Monitor);
    }

    [Fact]
    public void Read_FrontToBack_LeavesOutWindowsThatAreNotDrawn()
    {
        var windows = Read(
            new FakeWindow(3, new Rectangle(0, 0, 100, 100), Visible: false, Affinity: WdaExcludeFromCapture),
            new FakeWindow(4, new Rectangle(0, 0, 100, 100), Minimized: true, Affinity: WdaExcludeFromCapture),
            new FakeWindow(5, new Rectangle(0, 0, 100, 100), Cloaked: true, Affinity: WdaExcludeFromCapture),
            new FakeWindow(1, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture),
            new FakeWindow(6, new Rectangle(0, 0, 100, 100)),
            new FakeWindow(2, new Rectangle(0, 0, 100, 100), Affinity: WdaMonitor));

        windows.Select(w => w.Handle).Should().Equal(1, 2);
    }

    [Fact]
    public void Read_OfAWindowWithoutAnAffinity_ReadsOnlyItsVisibilityAndAffinity()
    {
        // One EnumWindows pass plus cheap reads: the many windows without an affinity cost two calls each
        var (native, policy) = Desktop(new FakeWindow(6, new Rectangle(0, 0, 100, 100)));

        new DisplayAffinityScan(native.Object, policy.Object, DeniesSome).Read();

        native.Verify(n => n.IsIconic(6), Times.Never);
        native.Verify(n => n.IsCloaked(6), Times.Never);
        native.Verify(n => n.GetWindowRect(6), Times.Never);
        policy.Verify(p => p.Check(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void Read_WithAPolicyThatDeniesNothing_StillWalksTheWindows_AndNamesThemWithoutAskingTheCheck()
    {
        // Unlike the policy's mask, the scan never stops early
        var (native, policy) = Desktop(new FakeWindow(1, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture,
            Title: "Toast", ProcessName: "app"));

        var windows = new DisplayAffinityScan(native.Object, policy.Object, DeniesNothing).Read();

        windows.Should().ContainSingle().Which.Title.Should().Be("Toast");
        policy.Verify(p => p.Check(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void Read_AWindowThePolicyDenies_IsListedByHandleAffinityAndRectangleOnly()
    {
        var windows = Read(new FakeWindow(1, new Rectangle(0, 0, 100, 100), Denied: true, Affinity: WdaExcludeFromCapture,
            Title: "Vault unlocked", ProcessName: "KeePass"));

        windows.Should().Equal(new ExcludedWindow(1, DisplayAffinity.ExcludeFromCapture, null, null, 0, 0, 100, 100));
    }

    [Fact]
    public void Read_WithoutAPolicyCheck_OrWhenTheCheckFails_NamesNoWindow()
    {
        // Fail-closed: a window whose process cannot be checked is listed as a denied one is
        var (native, policy) = Desktop(
            new FakeWindow(1, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture, Title: "Toast", ProcessName: "app"),
            new FakeWindow(2, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture, Title: "Bar", ProcessName: "app",
                PolicyThrows: true));

        var withoutCheck = new DisplayAffinityScan(native.Object, capturePolicy: null, DeniesSome).Read();
        var failing = new DisplayAffinityScan(native.Object, policy.Object, DeniesSome).Read();

        withoutCheck.Should().HaveCount(2).And.OnlyContain(w => w.ProcessName == null && w.Title == null);
        failing.Single(w => w.Handle == 2).Should().Match<ExcludedWindow>(w => w.ProcessName == null && w.Title == null);
        failing.Single(w => w.Handle == 1).Title.Should().Be("Toast");
    }

    [Fact]
    public void Read_LeavesOutTheCapturedWindow_WhichItsOwnWarningReports()
    {
        var (native, policy) = Desktop(
            new FakeWindow(1, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture),
            new FakeWindow(2, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture));

        var windows = new DisplayAffinityScan(native.Object, policy.Object, DeniesSome).Read(leaveOut: 2);

        windows.Select(w => w.Handle).Should().Equal(1);
    }

    [Fact]
    public void Read_AWindowThatClosedDuringTheRead_IsGone()
    {
        var windows = Read(
            new FakeWindow(1, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture, RectUnreadable: true, Gone: true),
            new FakeWindow(2, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture));

        windows.Select(w => w.Handle).Should().Equal(2);
    }

    [Fact]
    public void Read_CutsALongTitle_AsTheWindowToolsCutIt()
    {
        var windows = Read(new FakeWindow(1, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture, Title: new string('x', 500)));

        windows.Single().Title.Should().HaveLength(AppText.MaxLength).And.EndWith("…");
    }

    [Fact]
    public void Read_AsksThePolicyOncePerWindowAndProcess()
    {
        var (native, policy) = Desktop(new FakeWindow(1, new Rectangle(0, 0, 100, 100), Affinity: WdaExcludeFromCapture, Title: "Toast"));
        var sut = new DisplayAffinityScan(native.Object, policy.Object, DeniesSome);

        sut.Read();
        sut.Read();

        policy.Verify(p => p.Check(1), Times.Once);
    }

    [Fact]
    public void Within_CutsEachWindowToTheArea_ListsEachOnceWithItsLastRead_AndLeavesOutThoseOutsideIt()
    {
        ExcludedWindow Toast(int x) => new(1, DisplayAffinity.ExcludeFromCapture, "app", "Toast", x, 900, 320, 100);
        var outside = new ExcludedWindow(2, DisplayAffinity.Monitor, null, null, 5000, 0, 100, 100);
        var bar = new ExcludedWindow(3, DisplayAffinity.ExcludeFromCapture, "app", "Bar", -50, 0, 200, 40);

        var windows = DisplayAffinityScan.Within(new Rectangle(0, 0, 1920, 1080), [Toast(1500), outside, bar, Toast(1700)]);

        windows.Should().Equal(Toast(1700) with { Width = 220 }, bar with { X = 0, Width = 150 });
    }

    [Fact]
    public void Within_NoWindowOverTheArea_IsNull_SoTheResultLeavesTheFieldOut()
    {
        DisplayAffinityScan.Within(new Rectangle(0, 0, 100, 100), [new ExcludedWindow(1, DisplayAffinity.Monitor, null, null, 200, 0, 10, 10)])
            .Should().BeNull();
        DisplayAffinityScan.Within(new Rectangle(0, 0, 100, 100), []).Should().BeNull();
    }

    [Theory]
    [InlineData(0x0u, null)]
    [InlineData(0x1u, DisplayAffinity.Monitor)]
    [InlineData(0x11u, DisplayAffinity.ExcludeFromCapture)]
    [InlineData(0x10u, DisplayAffinity.ExcludeFromCapture)]
    [InlineData(0x2u, DisplayAffinity.Monitor)]
    public void DisplayAffinities_ReadsGetWindowDisplayAffinitysValue(uint value, DisplayAffinity? expected)
    {
        DisplayAffinities.Of(value).Should().Be(expected);
    }
}
