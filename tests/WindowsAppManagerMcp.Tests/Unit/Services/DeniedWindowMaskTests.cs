using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The policy's mask of screen reads (owner decision D8, roadmap M6.4): the denied top-level windows on the screen over an
/// area, clipped to it, with one verdict per window and process.
/// </summary>
public class DeniedWindowMaskTests
{
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
}
