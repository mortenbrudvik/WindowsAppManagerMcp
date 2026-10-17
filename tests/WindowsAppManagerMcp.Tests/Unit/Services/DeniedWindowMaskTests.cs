using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The policy's mask of screen reads (owner decision D8): the areas of the windows the target policy denies, blacked out of a
/// screen copy before OCR (roadmap M6.3, Read and Apply) and left out of what wait_for_visual compares (roadmap M6.4, Over),
/// with one verdict per window and process.
/// </summary>
public class DeniedWindowMaskTests
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExLayered = 0x80000;
    private const long WsExNoRedirectionBitmap = 0x200000;

    /// <summary>A top-level window of the fake desktop; the list is in z-order, topmost first.</summary>
    public sealed record FakeWindow(
        nint Handle,
        Rectangle Bounds,
        bool Denied = false,
        bool Visible = true,
        bool Minimized = false,
        bool Cloaked = false,
        long ExStyle = 0,
        Rectangle? Frame = null,
        bool RectUnreadable = false,
        bool Shaped = false,
        bool Gone = false,
        bool PolicyThrows = false);

    public static (Mock<INativeWindowWrapper> Native, Mock<ICaptureTargetPolicy> Policy) Desktop(params FakeWindow[] windows)
    {
        var native = new Mock<INativeWindowWrapper>();
        native.Setup(n => n.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> visit) =>
        {
            foreach (var window in windows)
            {
                if (!visit(window.Handle))
                {
                    break;
                }
            }
        });
        FakeWindow Of(nint handle) => windows.Single(w => w.Handle == handle);
        native.Setup(n => n.IsWindowVisible(It.IsAny<nint>())).Returns((nint h) => Of(h).Visible);
        native.Setup(n => n.IsIconic(It.IsAny<nint>())).Returns((nint h) => Of(h).Minimized);
        native.Setup(n => n.IsCloaked(It.IsAny<nint>())).Returns((nint h) => Of(h).Cloaked);
        native.Setup(n => n.GetWindowRect(It.IsAny<nint>())).Returns((nint h) => Of(h).RectUnreadable
            ? null
            : (Of(h).Bounds.Left, Of(h).Bounds.Top, Of(h).Bounds.Right, Of(h).Bounds.Bottom));
        native.Setup(n => n.GetExtendedFrameBounds(It.IsAny<nint>())).Returns((nint h) =>
            Of(h).Frame is { } f ? (f.Left, f.Top, f.Right, f.Bottom) : null);
        native.Setup(n => n.GetWindowLongPtr(It.IsAny<nint>(), GwlExStyle)).Returns((nint h, int _) => (nint)Of(h).ExStyle);
        native.Setup(n => n.HasWindowRegion(It.IsAny<nint>())).Returns((nint h) => Of(h).Shaped);
        native.Setup(n => n.IsWindow(It.IsAny<nint>())).Returns((nint h) => !Of(h).Gone);

        native.Setup(n => n.GetWindowThreadProcessId(It.IsAny<nint>())).Returns((nint h) => (uint)h + 1000);

        var policy = new Mock<ICaptureTargetPolicy>();
        policy.Setup(p => p.Check(It.IsAny<nint>())).Returns((nint h) =>
            Of(h).PolicyThrows ? throw new InvalidOperationException("the window is gone")
            : Of(h).Denied ? new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "KeePass is on the deny list") : null);
        return (native, policy);
    }

    private static IReadOnlyList<DeniedWindowArea> Read(params FakeWindow[] windows)
    {
        var (native, policy) = Desktop(windows);
        return new DeniedWindowMask(native.Object, policy.Object, null).Read();
    }

    [Fact]
    public void Read_ADeniedWindowInTheOpen_IsItsWholeRectangle()
    {
        var areas = Read(new FakeWindow(1, new Rectangle(100, 100, 400, 300), Denied: true));

        areas.Should().ContainSingle().Which.Should().BeEquivalentTo(new DeniedWindowArea(1, [new Rectangle(100, 100, 400, 300)]));
    }

    [Fact]
    public void Read_AnOpaqueWindowAbove_HidesThePartItCovers_ByItsVisibleFrame()
    {
        var areas = Read(
            new FakeWindow(2, new Rectangle(93, 0, 214, 207), Frame: new Rectangle(100, 0, 200, 200)),
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true));

        // The opaque window covers x 100-300 of the denied one's band (its invisible borders do not count)
        areas.Should().ContainSingle().Which.Areas.Should().BeEquivalentTo(
            [new Rectangle(0, 0, 100, 100), new Rectangle(300, 0, 100, 100)]);
    }

    [Theory]
    [InlineData(WsExLayered)]
    [InlineData(WsExTransparent)]
    [InlineData(WsExNoRedirectionBitmap)] // drawn through DirectComposition, which can be translucent (Windows Terminal)
    public void Read_ALayeredClickThroughOrCompositedWindowAbove_HidesNothing(long exStyle)
    {
        var areas = Read(
            new FakeWindow(2, new Rectangle(0, 0, 1000, 1000), ExStyle: exStyle),
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true));

        areas.Should().ContainSingle().Which.Areas.Should().Equal(new Rectangle(0, 0, 400, 100));
    }

    [Fact]
    public void Read_AWindowShapedByARegionAbove_HidesNothing()
    {
        // SetWindowRgn (rounded or cut-out windows): what lies outside the region shows through
        var areas = Read(
            new FakeWindow(2, new Rectangle(0, 0, 1000, 1000), Shaped: true),
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true));

        areas.Should().ContainSingle().Which.Areas.Should().Equal(new Rectangle(0, 0, 400, 100));
    }

    [Fact]
    public void Read_ADeniedWindowThatClosedDuringTheRead_IsGone_NotARefusal()
    {
        var areas = Read(
            new FakeWindow(3, new Rectangle(0, 0, 400, 100), Denied: true, RectUnreadable: true, Gone: true),
            new FakeWindow(4, new Rectangle(0, 0, 400, 100), PolicyThrows: true, Gone: true),
            new FakeWindow(1, new Rectangle(500, 0, 100, 100), Denied: true));

        areas.Should().ContainSingle().Which.Window.Should().Be(1);
    }

    [Fact]
    public void Read_APolicyCheckThatFailsOnALiveWindow_Propagates()
    {
        var act = () => Read(new FakeWindow(4, new Rectangle(0, 0, 400, 100), PolicyThrows: true));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Read_AWindowBelow_HidesNothing()
    {
        var areas = Read(
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true),
            new FakeWindow(2, new Rectangle(0, 0, 1000, 1000)));

        areas.Should().ContainSingle().Which.Areas.Should().Equal(new Rectangle(0, 0, 400, 100));
    }

    [Fact]
    public void Read_ADeniedWindowCoveredWhole_IsLeftOut()
    {
        var areas = Read(
            new FakeWindow(2, new Rectangle(0, 0, 1000, 1000)),
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true));

        areas.Should().BeEmpty();
    }

    [Fact]
    public void Read_HiddenMinimizedAndCloakedWindows_NeitherShowNorHide()
    {
        var areas = Read(
            new FakeWindow(3, new Rectangle(0, 0, 1000, 1000), Visible: false),
            new FakeWindow(4, new Rectangle(0, 0, 1000, 1000), Minimized: true),
            new FakeWindow(5, new Rectangle(0, 0, 1000, 1000), Cloaked: true),
            new FakeWindow(6, new Rectangle(0, 0, 50, 50), Denied: true, Cloaked: true),
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true));

        areas.Should().ContainSingle().Which.Window.Should().Be(1);
    }

    [Fact]
    public void Read_ADeniedWindowAboveAnother_CoversItWithItsOwnMask()
    {
        var areas = Read(
            new FakeWindow(1, new Rectangle(0, 0, 200, 200), Denied: true),
            new FakeWindow(2, new Rectangle(100, 0, 200, 200), Denied: true));

        areas.Should().HaveCount(2);
        areas.Single(a => a.Window == 2).Areas.Should().Equal(new Rectangle(200, 0, 100, 200));
    }

    [Fact]
    public void Read_AskedNothingOfThePolicy_ForWindowsThatAreNotDrawn()
    {
        var (native, policy) = Desktop(new FakeWindow(3, new Rectangle(0, 0, 10, 10), Visible: false));

        new DeniedWindowMask(native.Object, policy.Object, null).Read();

        policy.Verify(p => p.Check(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void Read_ADeniedWindowWhoseRectangleCannotBeRead_FailsClosed()
    {
        var act = () => Read(new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true, RectUnreadable: true));

        act.Should().Throw<DeniedWindowUnlocatedException>().Which.Window.Should().Be(1);
    }

    [Fact]
    public void Read_AnAllowedWindowWhoseRectangleCannotBeRead_IsSkipped()
    {
        var areas = Read(
            new FakeWindow(2, new Rectangle(0, 0, 1000, 1000), RectUnreadable: true),
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true));

        areas.Should().ContainSingle().Which.Areas.Should().Equal(new Rectangle(0, 0, 400, 100));
    }

    [Fact]
    public void Apply_BlacksOutTheAreasOnTheImage_AndCountsEachWindowOnce()
    {
        // A 10 x 4 white image at (100, 50)
        var pixels = new CaptureResult(Enumerable.Repeat((byte)255, 10 * 4 * 4).ToArray(), 10, 4, 100, 50, "ScreenBitBlt");
        IReadOnlyList<DeniedWindowArea> before = [new(7, [new Rectangle(98, 49, 4, 2)])];
        IReadOnlyList<DeniedWindowArea> after = [new(7, [new Rectangle(102, 49, 2, 2)]), new(8, [new Rectangle(0, 0, 10, 10)])];

        var masked = DeniedWindowMask.Apply(pixels, before, after);

        masked.Should().Be(1, "window 8 is off the image");
        var black = Enumerable.Range(0, 40).Where(i => pixels.Bgra[i * 4] == 0).Select(i => (X: 100 + i % 10, Y: 50 + i / 10)).ToList();
        black.Should().BeEquivalentTo(new[] { (100, 50), (101, 50), (102, 50), (103, 50) },
            "only row 50 of the image lies in the areas, at x 100-101 before and 102-103 after");
    }

    [Fact]
    public void Subtract_CutsOutEveryHole()
    {
        var parts = DeniedWindowMask.Subtract(new Rectangle(0, 0, 10, 10), [new Rectangle(2, 2, 2, 2), new Rectangle(6, 6, 10, 10)]);

        parts.Sum(p => p.Width * p.Height).Should().Be(100 - 4 - 16);
        parts.Should().OnlyContain(p => !p.IntersectsWith(new Rectangle(2, 2, 2, 2)) && !p.IntersectsWith(new Rectangle(6, 6, 10, 10)));
    }

    #region Over (roadmap M6.4)

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<ICaptureTargetPolicy> _policy = new();
    private readonly List<nint> _all = [];

    public DeniedWindowMaskTests()
    {
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> visit) =>
        {
            foreach (var window in _all)
            {
                if (!visit(window))
                {
                    break;
                }
            }
        });
    }

    private nint Add(int handle, (int, int, int, int) rect, bool denied = true, bool visible = true, bool minimized = false, bool cloaked = false)
    {
        var window = (nint)handle;
        _all.Add(window);
        _windows.Setup(w => w.IsWindowVisible(window)).Returns(visible);
        _windows.Setup(w => w.IsIconic(window)).Returns(minimized);
        _windows.Setup(w => w.IsCloaked(window)).Returns(cloaked);
        _windows.Setup(w => w.GetWindowRect(window)).Returns(rect);
        _windows.Setup(w => w.GetWindowThreadProcessId(window)).Returns((uint)handle + 1);
        _policy.Setup(p => p.Check(window)).Returns(denied ? new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied") : null);
        return window;
    }

    private DeniedWindowMask CreateSut() => new(_windows.Object, _policy.Object, new TargetPolicy(TargetPolicySettings.Default));

    [Fact]
    public void Over_ListsTheDeniedWindowsOnTheScreen_ClippedToTheArea()
    {
        Add(1, (-100, 50, 200, 150));
        Add(2, (300, 300, 400, 400), denied: false);
        Add(3, (0, 0, 50, 50), visible: false);
        Add(4, (0, 0, 50, 50), minimized: true);
        Add(5, (0, 0, 50, 50), cloaked: true);
        Add(6, (1000, 1000, 1100, 1100));

        var masked = CreateSut().Over(new Rectangle(0, 0, 500, 500));

        masked.Should().Equal(new MaskedWindow(1, 0, 50, 200, 100));
    }

    [Fact]
    public void Over_AsksThePolicyOncePerWindowAndProcess()
    {
        var window = Add(1, (0, 0, 100, 100));
        var sut = CreateSut();

        sut.Over(new Rectangle(0, 0, 500, 500));
        sut.Over(new Rectangle(0, 0, 500, 500));
        _windows.Setup(w => w.GetWindowThreadProcessId(window)).Returns(77u); // the handle was reused by another process
        sut.Over(new Rectangle(0, 0, 500, 500));

        _policy.Verify(p => p.Check(window), Times.Exactly(2));
    }

    [Fact]
    public void Over_WithAPolicyThatDeniesNothing_ListsNoWindow()
    {
        Add(1, (0, 0, 100, 100));
        var sut = new DeniedWindowMask(_windows.Object, _policy.Object, new TargetPolicy(new TargetPolicySettings([], null, "test")));

        sut.Over(new Rectangle(0, 0, 500, 500)).Should().BeEmpty();
        _windows.Verify(w => w.EnumWindows(It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    [Fact]
    public void Over_ADeniedWindowThatCannotBePlaced_MasksTheWholeArea()
    {
        // Fail-closed without throwing: a wait or a capture's signature then compares nothing of the area
        var (native, policy) = Desktop(new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true, RectUnreadable: true));

        var masked = new DeniedWindowMask(native.Object, policy.Object, null).Over(new Rectangle(-50, 10, 500, 300));

        masked.Should().Equal(new MaskedWindow(1, -50, 10, 500, 300));
    }

    [Fact]
    public void Over_LeavesOutWhatAnOpaqueWindowCovers_AndBoxesTheRest()
    {
        // An opaque window over the denied one's middle: it shows on both sides, so its box is the band
        var (native, policy) = Desktop(
            new FakeWindow(2, new Rectangle(100, 0, 200, 100)),
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true),
            new FakeWindow(3, new Rectangle(0, 200, 100, 100), Denied: true),
            new FakeWindow(4, new Rectangle(0, 200, 100, 100)));

        var masked = new DeniedWindowMask(native.Object, policy.Object, null).Over(new Rectangle(0, 0, 350, 1000));

        // Window 1 shows at x 0-100 and 300-350 of the area: one box around both
        masked.Should().Equal(new MaskedWindow(1, 0, 0, 350, 100), new MaskedWindow(3, 0, 200, 100, 100));
    }

    [Fact]
    public void Over_AWindowThatIsCoveredWhole_IsNotMasked()
    {
        var (native, policy) = Desktop(
            new FakeWindow(2, new Rectangle(0, 0, 1000, 1000)),
            new FakeWindow(1, new Rectangle(0, 0, 400, 100), Denied: true));

        new DeniedWindowMask(native.Object, policy.Object, null).Over(new Rectangle(0, 0, 500, 500)).Should().BeEmpty();
    }

    #endregion
}
