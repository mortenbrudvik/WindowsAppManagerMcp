using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The areas of windows the target policy denies, blacked out of a screen copy before OCR (roadmap M6.3, M6 decision D8).
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
        bool RectUnreadable = false);

    public static (Mock<INativeWindowWrapper> Native, Mock<IWindowService> Windows) Desktop(params FakeWindow[] windows)
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

        var service = new Mock<IWindowService>();
        service.Setup(s => s.EvaluateTargetPolicy(It.IsAny<nint>())).Returns((nint h) =>
            Of(h).Denied ? TargetPolicyVerdict.Deny("KeePass", "it is on the deny list") : TargetPolicyVerdict.Allow);
        return (native, service);
    }

    private static IReadOnlyList<DeniedWindowArea> Read(params FakeWindow[] windows)
    {
        var (native, service) = Desktop(windows);
        return new DeniedWindowMask(native.Object, service.Object).Read();
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
        var (native, service) = Desktop(new FakeWindow(3, new Rectangle(0, 0, 10, 10), Visible: false));

        new DeniedWindowMask(native.Object, service.Object).Read();

        service.Verify(s => s.EvaluateTargetPolicy(It.IsAny<nint>()), Times.Never);
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
}
