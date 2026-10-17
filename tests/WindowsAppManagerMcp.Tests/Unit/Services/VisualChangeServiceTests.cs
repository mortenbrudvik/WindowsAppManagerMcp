using System.Drawing;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// wait_for_visual's waits (roadmap M6.4) over scripted frames: a fake capture wrapper lends one frame per check, and a
/// manual clock moves by each pause, so no screen is read and no time passes.
/// </summary>
public class VisualChangeServiceTests
{
    private static readonly nint Window = 0x5150;
    private const uint ProcessId = 4242;
    private const uint Thread = 77;

    private readonly ManualClock _clock = new();
    private readonly FakePixelScreen _screen = new();
    private readonly Mock<IScreenshotService> _screenshots = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IMonitorService> _monitors = new();
    private readonly Mock<ICaptureTargetPolicy> _capturePolicy = new();
    private readonly Mock<INativeThreadInputWrapper> _threads = new();
    private readonly InlineWindowCalls _windowCalls = new();
    private readonly CaptureRegistry _registry;

    public VisualChangeServiceTests()
    {
        _registry = new CaptureRegistry(_clock, keepSignatures: true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(ProcessId);
        _windows.Setup(w => w.GetWindowThreadId(Window)).Returns(Thread);
        _windows.Setup(w => w.GetWindowRect(Window)).Returns((100, 200, 740, 680));
        _monitors.Setup(m => m.GetAllMonitors()).Returns(
        [
            new MonitorInfo(0, "DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 1.0),
            new MonitorInfo(1, "DISPLAY2", false, new MonitorRect(1920, 0, 2560, 1440), new MonitorRect(1920, 0, 2560, 1400), 1.5)
        ]);
        _screen.VirtualScreen = (0, 0, 4480, 1440);
    }

    private VisualChangeService CreateSut(ITargetPolicy? policy = null) => new(
        _screen, _screenshots.Object, _windows.Object, _monitors.Object, _capturePolicy.Object, _windowCalls, _registry, _threads.Object,
        policy ?? new TargetPolicy(new TargetPolicySettings([], null, "test")), _clock,
        (pause, token) =>
        {
            token.ThrowIfCancellationRequested();
            _clock.Advance(pause);
            return Task.CompletedTask;
        });

    private static VisualWaitRequest Request(VisualTarget target, VisualWaitCondition condition = VisualWaitCondition.Changed,
        int timeoutMs = 2000, int stableMs = 500, double minChangedPercent = 0) => new()
    {
        Target = target,
        Condition = condition,
        TimeoutMs = timeoutMs,
        PollMs = 200,
        StableMs = stableMs,
        MinChangedPercent = minChangedPercent
    };

    private static VisualTarget.Window TheWindow(bool includeFrame = true) => new(Window, includeFrame);

    /// <summary>A 640 x 480 frame of the window at (100, 200).</summary>
    private static VisualFrames WindowFrame() => new(640, 480, 100, 200);

    #region changed

    [Fact]
    public async Task Changed_AWindowThatChangesAtTheThirdCheck_IsSatisfied_WithWhereItChanged()
    {
        // Arrange - a 40 x 40 panel at (300, 20) of the window recolors
        var before = WindowFrame();
        _screen.Frames(before, before, before.Copy().Paint(300, 20, 40, 40, (0x60, 0x20, 0xA0)));

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Satisfied.Should().BeTrue();
        result.Condition.Should().Be("changed");
        result.Reason.Should().Be("pixels");
        result.Checks.Should().Be(3);
        result.ElapsedMs.Should().Be(400);
        result.ChangedPercent.Should().Be(1);
        result.ChangedRegions.Should().Equal(new BoundsDto(100 + 288, 200 + 16, 64, 48));
        result.CapturedRegion.Should().Be(new CapturedRegion(100, 200, 640, 480));
        result.CaptureMethod.Should().Be("PrintWindow");
        _screen.Sources.Should().AllBeEquivalentTo(new PixelSource.Window(Window, true));
        _windowCalls.Calls.Should().Be(3, "PrintWindow runs under the window-call deadline");
    }

    [Fact]
    public async Task Changed_RegistersTheLastFrame_AsACaptureSinceCanCompareWith()
    {
        // Arrange
        var before = WindowFrame();
        var after = before.Copy().Paint(0, 0, 1, 1, (0, 0, 0));
        _screen.Frames(before, after);

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow(includeFrame: false)));

        // Assert
        var record = _registry.Find(result.CaptureId!)!;
        record.Signature!.Hashes.ToArray().Should().Equal(after.Signature().Hashes.ToArray());
        record.Geometry.ImageToScreen.Should().Be(new ImageToScreenTransform(100, 200, 1, 1));
        record.Geometry.Window.Should().Be(new CapturedWindow((long)Window, (int)ProcessId, WindowRect.FromLTRB(100, 200, 740, 680), false,
            new WindowRect(100, 200, 640, 480)));
    }

    [Fact]
    public async Task Changed_Since_ACaptureBeforeTheChange_IsSatisfiedAtTheFirstCheck()
    {
        // Arrange - the capture was taken before the click; the change was done before the wait began
        var before = WindowFrame();
        var since = Register(before, window: true);
        _screen.Frames(before.Copy().Paint(10, 10, 4, 4, (0, 0, 0)));

        // Act
        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since(since), timeoutMs: 0));

        // Assert
        result.Satisfied.Should().BeTrue(result.Error);
        result.Checks.Should().Be(1);
        result.ChangedRegions.Should().Equal(new BoundsDto(100, 200, 16, 16));
        _screen.Sources.Should().Equal(new PixelSource.Window(Window, true));
    }

    [Fact]
    public async Task Changed_Since_AScreenCapture_WatchesItsRectangle()
    {
        var before = new VisualFrames(300, 200, 2000, 100);
        var since = Register(before, window: false);
        _screen.Frames(before.Copy().Paint(299, 199, 1, 1, (0, 0, 0)));

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since(since), timeoutMs: 0));

        result.Satisfied.Should().BeTrue(result.Error);
        _screen.Sources.Should().Equal(new PixelSource.Screen(2000, 100, 300, 200));
        result.CaptureMethod.Should().Be("ScreenBitBlt");
    }

    [Fact]
    public async Task Changed_Since_TheSameFrame_WithTimeoutZero_TimesOutAfterOneCheck()
    {
        var before = WindowFrame();
        var since = Register(before, window: true);
        _screen.Frames(before);

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since(since), timeoutMs: 0));

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.Checks.Should().Be(1);
        result.ChangedPercent.Should().Be(0);
        result.ChangedRegions.Should().BeNull();
        result.CaptureId.Should().NotBeNull("the last frame is the next wait's since");
    }

    [Fact]
    public async Task Changed_NothingChanges_TimesOut_AndSaysHowToCatchAnEarlierChange()
    {
        _screen.Frames(WindowFrame());

        var result = await CreateSut().WaitAsync(Request(TheWindow(), timeoutMs: 1000));

        result.Success.Should().BeFalse();
        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.Checks.Should().Be(6);
        result.ElapsedMs.Should().Be(1000);
        result.Error.Should().Contain("since");
    }

    [Fact]
    public async Task Changed_ASmallerChangeThanMinChangedPercent_DoesNotCount_ButShowsOnTheTimeout()
    {
        // One tile of 1200 is 0.08 %
        var before = WindowFrame();
        _screen.Frames(before, before.Copy().Paint(0, 0, 1, 1, (0, 0, 0)));

        var result = await CreateSut().WaitAsync(Request(TheWindow(), timeoutMs: 600, minChangedPercent: 0.5));

        result.TimedOut.Should().BeTrue();
        result.ChangedPercent.Should().Be(0.08);
        result.ChangedRegions.Should().Equal(new BoundsDto(100, 200, 16, 16));
        result.Error.Should().Contain("minChangedPercent 0.5");
    }

    [Fact]
    public async Task TheTimeoutMessage_WritesItsNumbers_TheSameInEveryCulture()
    {
        // A decimal comma in the error would read as a list to the agent
        var before = WindowFrame();
        _screen.Frames(before, before.Copy().Paint(0, 0, 1, 1, (0, 0, 0)));
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");
        try
        {
            var result = await CreateSut().WaitAsync(Request(TheWindow(), timeoutMs: 600, minChangedPercent: 0.5));

            result.Error.Should().Contain("0.08% of the tiles changed").And.Contain("minChangedPercent 0.5");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task Changed_AWindowThatResized_IsSatisfied_WithReasonResized()
    {
        _screen.Frames(WindowFrame(), new VisualFrames(800, 480, 100, 200));

        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        result.Satisfied.Should().BeTrue();
        result.Reason.Should().Be("resized");
        result.ChangedPercent.Should().Be(100);
        result.ChangedRegions.Should().Equal(new BoundsDto(100, 200, 800, 480));
    }

    [Theory]
    [InlineData(ScreenshotErrorCode.InvalidHandle, "closed")]
    [InlineData(ScreenshotErrorCode.WindowMinimized, "minimized")]
    [InlineData(ScreenshotErrorCode.WindowHidden, "hidden")]
    [InlineData(ScreenshotErrorCode.Cloaked, "hidden")]
    public async Task Changed_AWindowThatLeavesTheScreen_IsSatisfied_WithWhy(ScreenshotErrorCode code, string reason)
    {
        // Arrange - fine at the start and at the first check, gone at the second
        _screen.Frames(WindowFrame());
        _screenshots.SetupSequence(s => s.CheckWindow(Window))
            .Returns((CaptureRefusal?)null).Returns((CaptureRefusal?)null).Returns(new CaptureRefusal(code, "gone"));

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        // Assert
        result.Satisfied.Should().BeTrue();
        result.Reason.Should().Be(reason);
        result.ChangedRegions.Should().Equal(new BoundsDto(100, 200, 640, 480));
        result.CaptureId.Should().BeNull("a window off the screen has nothing to click on");
    }

    [Fact]
    public async Task Changed_Since_AWindowWhoseHandleNowBelongsToAnotherProcess_IsClosed()
    {
        var since = Register(WindowFrame(), window: true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Window)).Returns(9999u);

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since(since), timeoutMs: 0));

        result.Satisfied.Should().BeTrue();
        result.Reason.Should().Be("closed");
        _screen.Sources.Should().BeEmpty();
    }

    #endregion

    #region stable

    [Fact]
    public async Task Stable_AnAnimationThatStops_IsSatisfiedOnceNothingChangedForStableMs()
    {
        // Arrange - a spinner turns for four frames, then stays
        var still = WindowFrame();
        var frames = Enumerable.Range(0, 4).Select(i => still.Copy().Paint(10 + i * 2, 10, 2, 2, (0, 0, 0))).ToList();
        frames.Add(still);
        _screen.Frames([.. frames]);

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow(), VisualWaitCondition.Stable, stableMs: 500));

        // Assert - the last change was at 800 ms; at 1400 nothing had changed for 600 ms
        result.Satisfied.Should().BeTrue(result.Error);
        result.Condition.Should().Be("stable");
        result.StableForMs.Should().Be(600);
        result.ElapsedMs.Should().Be(1400);
        result.Reason.Should().BeNull();
        result.ChangedRegions.Should().BeNull();
    }

    [Fact]
    public async Task Stable_ZeroStableMs_NeedsTwoEqualFrames()
    {
        _screen.Frames(WindowFrame());

        var result = await CreateSut().WaitAsync(Request(TheWindow(), VisualWaitCondition.Stable, stableMs: 0));

        result.Satisfied.Should().BeTrue();
        result.Checks.Should().Be(2);
        result.StableForMs.Should().Be(200);
    }

    [Fact]
    public async Task Stable_AnEndlessAnimation_TimesOut_WithWhatKeptMoving()
    {
        // Arrange - a dot that moves along the top row of tiles at every frame
        var still = WindowFrame();
        _screen.FrameAt = i => still.Copy().Paint(i % 2 * 16, 0, 1, 1, (0, 0, 0));

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow(), VisualWaitCondition.Stable, timeoutMs: 1000));

        // Assert
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be("WaitTimedOut");
        result.ChangedRegions.Should().Equal(new BoundsDto(100, 200, 32, 16));
        result.Error.Should().Contain("kept changing");
    }

    [Fact]
    public async Task Stable_AWindowThatResized_StartsTheStableTimeAgain()
    {
        _screen.Frames(WindowFrame(), new VisualFrames(800, 480, 100, 200));

        var result = await CreateSut().WaitAsync(Request(TheWindow(), VisualWaitCondition.Stable, stableMs: 400));

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(600, "the resize at 200 ms started the 400 ms again");
    }

    [Theory]
    [InlineData(ScreenshotErrorCode.InvalidHandle)]
    [InlineData(ScreenshotErrorCode.WindowMinimized)]
    public async Task Stable_AWindowThatLeavesTheScreen_IsAnError_WithItsCode(ScreenshotErrorCode code)
    {
        _screen.Frames(WindowFrame());
        _screenshots.SetupSequence(s => s.CheckWindow(Window))
            .Returns((CaptureRefusal?)null).Returns((CaptureRefusal?)null).Returns(new CaptureRefusal(code, "gone"));

        var result = await CreateSut().WaitAsync(Request(TheWindow(), VisualWaitCondition.Stable));

        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be(code.ToString());
    }

    #endregion

    #region Window targets

    [Fact]
    public async Task AWindowThePolicyDenies_IsRefused_BeforeAnyPixelIsRead()
    {
        _capturePolicy.Setup(p => p.Check(Window)).Returns(new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied"));

        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("TargetDeniedByPolicy");
        result.Checks.Should().Be(0);
        _screen.Sources.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ScreenshotErrorCode.WindowMinimized)]
    [InlineData(ScreenshotErrorCode.InvalidHandle)]
    [InlineData(ScreenshotErrorCode.Cloaked)]
    public async Task AWindowThatCannotBeCaptured_AtTheStart_IsRefused(ScreenshotErrorCode code)
    {
        _screenshots.Setup(s => s.CheckWindow(Window)).Returns(new CaptureRefusal(code, "no"));

        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        result.ErrorCode.Should().Be(code.ToString());
        _screen.Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task AFrameCapture_IsCutToTheVisibleFrame_AsCaptureWindowCutsIt()
    {
        // Arrange - GetWindowRect's invisible borders: the visible frame is 7 pixels in on the left, right and bottom
        _windows.Setup(w => w.GetExtendedFrameBounds(Window)).Returns((107, 200, 733, 673));
        _screen.Frames(WindowFrame(), WindowFrame().Paint(600, 400, 1, 1, (0, 0, 0)));

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        // Assert
        result.CapturedRegion.Should().Be(new CapturedRegion(107, 200, 626, 473));
        result.ChangedRegions.Should().ContainSingle().Which.Should().Be(new BoundsDto(107 + 592, 200 + 400, 16, 16));
    }

    [Fact]
    public async Task AWindowThatDoesNotAnswer_IsCopiedFromTheScreen_ForTheRestOfTheWait_WithAWarning()
    {
        // Arrange
        _windowCalls.NotResponding = true;
        var before = WindowFrame();
        _screen.Frames(before, before, before.Copy().Paint(0, 0, 1, 1, (0, 0, 0)));

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        // Assert
        result.Satisfied.Should().BeTrue();
        result.CaptureMethod.Should().Be("ScreenBitBlt");
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("WindowNotResponding:");
        _windowCalls.Calls.Should().Be(1, "after the first deadline the window is not asked again");
        _screen.Sources.Should().AllBeEquivalentTo(new PixelSource.WindowFromScreen(Window, true));
    }

    [Fact]
    public async Task ATextCaretThatBlinks_IsNoChange()
    {
        // Arrange - the caret at (130, 210, 1 x 16) on and off
        _threads.Setup(t => t.GetGuiThreadInfo(Thread)).Returns(new GuiThreadInfo(0, Window, Window, 0, 0, Window, new WindowRect(130, 210, 1, 16)));
        var off = WindowFrame();
        var on = off.Copy().Paint(30, 10, 1, 16, (0, 0, 0));
        _screen.Frames(off, on, off, on);

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow(), timeoutMs: 600));

        // Assert
        result.TimedOut.Should().BeTrue();
        result.ChangedPercent.Should().Be(0);
    }

    [Fact]
    public async Task AWindowScaledByWindows_Warns_ThatTheRegionsCanBeOffset()
    {
        _monitors.Setup(m => m.GetMonitorForWindow(Window)).Returns(
            new MonitorInfo(1, "DISPLAY2", false, new MonitorRect(1920, 0, 2560, 1440), new MonitorRect(1920, 0, 2560, 1400), 1.5));
        _windows.Setup(w => w.GetDpiForWindow(Window)).Returns(96u);
        _screen.Frames(WindowFrame(), WindowFrame().Paint(0, 0, 1, 1, (0, 0, 0)));

        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        result.Warnings.Should().ContainSingle().Which.Should().StartWith("DpiScaledWindow:");
    }

    [Fact]
    public async Task AFrameOfOneColor_Warns_ThatItMayNeverChange()
    {
        _screen.Frames(new VisualFrames(640, 480, 100, 200, (0, 0, 0)));

        var result = await CreateSut().WaitAsync(Request(TheWindow(), VisualWaitCondition.Stable, stableMs: 0));

        result.Satisfied.Should().BeTrue();
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("ImageBlank:");
    }

    #endregion

    #region Region and monitor targets

    [Fact]
    public async Task ARegion_IsCopiedFromTheScreen_AndRegistersTheScreenItWasReadIn()
    {
        var before = new VisualFrames(300, 200, 50, 60);
        _screen.Frames(before, before.Copy().Paint(0, 0, 1, 1, (0, 0, 0)));

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Region(50, 60, 300, 200)));

        result.Satisfied.Should().BeTrue();
        _screen.Sources.Should().AllBeEquivalentTo(new PixelSource.Screen(50, 60, 300, 200));
        var geometry = _registry.Find(result.CaptureId!)!.Geometry;
        geometry.VirtualScreen.Should().Be(new VirtualScreenBounds(0, 0, 4480, 1440));
        geometry.Monitors.Should().HaveCount(2);
        geometry.Window.Should().BeNull();
    }

    [Fact]
    public async Task ARegionPartlyOffTheScreen_IsCutToIt_WithAWarning()
    {
        _screen.Frames(new VisualFrames(80, 40, 4400, 1400).Paint(0, 0, 1, 1, (0, 0, 0)));

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Region(4400, 1400, 200, 200), VisualWaitCondition.Stable, stableMs: 0));

        _screen.Sources.Should().AllBeEquivalentTo(new PixelSource.Screen(4400, 1400, 80, 40));
        result.Warnings.Should().ContainSingle().Which.Should().StartWith("RegionClipped:");
    }

    [Theory]
    [InlineData(0, 0, 0, 10, "InvalidRegion")]
    [InlineData(5000, 0, 10, 10, "RegionOffScreen")]
    public async Task ARegionThatCannotBeRead_IsRefused(int x, int y, int width, int height, string code)
    {
        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Region(x, y, width, height)));

        result.ErrorCode.Should().Be(code);
        _screen.Sources.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAreaOverThePollingBudget_IsRefused()
    {
        _screen.VirtualScreen = (0, 0, 7680 * 2, 4320);

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Monitor(ScreenshotService.VirtualScreenIndex)));

        result.ErrorCode.Should().Be("InvalidRegion");
        result.Error.Should().Contain("32 megapixels");
    }

    [Fact]
    public async Task AMonitor_IsItsBounds_AndMinusOneIsTheVirtualScreen()
    {
        _screen.FrameAt = _ => new VisualFrames(1, 1);
        await CreateSut().WaitAsync(Request(new VisualTarget.Monitor(1), VisualWaitCondition.Stable, stableMs: 0));
        await CreateSut().WaitAsync(Request(new VisualTarget.Monitor(-1), VisualWaitCondition.Stable, stableMs: 0));

        _screen.Sources.Should().Contain(new PixelSource.Screen(1920, 0, 2560, 1440));
        _screen.Sources.Should().Contain(new PixelSource.Screen(0, 0, 4480, 1440));
    }

    [Fact]
    public async Task AnUnknownMonitor_IsInvalidMonitorIndex()
    {
        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Monitor(2)));

        result.ErrorCode.Should().Be("InvalidMonitorIndex");
    }

    [Fact]
    public async Task AWindowThePolicyDenies_OverARegion_IsLeftOut_AndListed()
    {
        // Arrange - a denied window at (100, 100, 200 x 100) changes; nothing else does
        var denied = (nint)0x7070;
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> visit) => visit(denied));
        _windows.Setup(w => w.IsWindowVisible(denied)).Returns(true);
        _windows.Setup(w => w.GetWindowRect(denied)).Returns((100, 100, 300, 200));
        _capturePolicy.Setup(p => p.Check(denied)).Returns(new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied"));
        var before = new VisualFrames(400, 300);
        _screen.Frames(before, before.Copy().Paint(150, 120, 20, 20, (0, 0, 0)));

        // Act
        var result = await CreateSut(new TargetPolicy(TargetPolicySettings.Default)).WaitAsync(Request(new VisualTarget.Region(0, 0, 400, 300), timeoutMs: 400));

        // Assert
        result.TimedOut.Should().BeTrue("the only change is inside the denied window");
        result.ChangedPercent.Should().Be(0);
        result.MaskedWindows.Should().Equal(new MaskedWindow((long)denied, 100, 100, 200, 100));
        _capturePolicy.Verify(p => p.Check(denied), Times.Once(), "one verdict per window and process for the whole wait");
    }

    [Fact]
    public async Task AWindowThePolicyDenies_ThatOpensRightBeforeARead_IsLeftOut_OfThatFrameToo()
    {
        // Arrange - the denied window is listed only from the second read on: it opened after the windows were listed for
        // that read, but before its pixels were copied
        var denied = (nint)0x7070;
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> visit) =>
        {
            if (_screen.Sources.Count >= 2)
            {
                visit(denied);
            }
        });
        _windows.Setup(w => w.IsWindowVisible(denied)).Returns(true);
        _windows.Setup(w => w.GetWindowRect(denied)).Returns((100, 100, 300, 200));
        _capturePolicy.Setup(p => p.Check(denied)).Returns(new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied"));
        var before = new VisualFrames(400, 300);
        _screen.Frames(before, before.Copy().Paint(100, 100, 200, 100, (0, 0, 0)));

        // Act
        var result = await CreateSut(new TargetPolicy(TargetPolicySettings.Default)).WaitAsync(Request(new VisualTarget.Region(0, 0, 400, 300), timeoutMs: 400));

        // Assert
        result.TimedOut.Should().BeTrue("the only change is the denied window itself");
        result.ChangedPercent.Should().Be(0);
        result.MaskedWindows.Should().Equal(new MaskedWindow((long)denied, 100, 100, 200, 100));
    }

    [Fact]
    public async Task APolicyThatDeniesNothing_ListsNoWindows()
    {
        _screen.Frames(new VisualFrames(40, 40));

        await CreateSut().WaitAsync(Request(new VisualTarget.Region(0, 0, 40, 40), VisualWaitCondition.Stable, stableMs: 0));

        _windows.Verify(w => w.EnumWindows(It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    [Fact]
    public async Task AScreenTarget_LeavesOutTheCaretOfTheWindowInFront()
    {
        // Arrange
        var front = (nint)0x9090;
        _windows.Setup(w => w.GetForegroundWindow()).Returns(front);
        _windows.Setup(w => w.GetWindowThreadId(front)).Returns(88u);
        _threads.Setup(t => t.GetGuiThreadInfo(88u)).Returns(new GuiThreadInfo(0, front, front, 0, 0, front, new WindowRect(20, 20, 2, 16)));
        var off = new VisualFrames(100, 100);
        _screen.Frames(off, off.Copy().Paint(20, 20, 2, 16, (0, 0, 0)));

        // Act
        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Region(0, 0, 100, 100), timeoutMs: 400));

        // Assert
        result.TimedOut.Should().BeTrue();
    }

    #endregion

    #region since

    [Fact]
    public async Task Since_AnUnknownCapture_IsCaptureNotFound()
    {
        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since("c99-abcdef")));

        result.ErrorCode.Should().Be("CaptureNotFound");
        result.Error.Should().Contain("output 'metadata'");
    }

    [Fact]
    public async Task Since_ACaptureWithoutASignature_IsCaptureNotFound_AndSaysWhy()
    {
        var since = _registry.Register(Geometry(WindowFrame(), window: true)).CaptureId;

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since(since)));

        result.ErrorCode.Should().Be("CaptureNotFound");
        result.Error.Should().Contain("no visual signature");
    }

    [Fact]
    public async Task Since_AWindowThePolicyDenies_IsRefused()
    {
        var since = Register(WindowFrame(), window: true);
        _capturePolicy.Setup(p => p.Check(Window)).Returns(new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied"));

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since(since)));

        result.ErrorCode.Should().Be("TargetDeniedByPolicy");
        _screen.Sources.Should().BeEmpty();
    }

    #endregion

    #region Review fixes: a read that changes how it reads, the policy's mask kept with a frame, windows that go

    [Fact]
    public async Task AWindowThatStopsAnswering_MidWait_IsComparedWithItsFirstScreenCopy_NotWithWhatPrintWindowDrew()
    {
        // Arrange - PrintWindow first, then the window stops answering: the screen copy differs (a covering window, the
        // rounded corners) although the window drew nothing new
        var printed = WindowFrame();
        var copied = printed.Copy().Paint(0, 0, 640, 8, (0x20, 0x20, 0x20));
        VisualFrames[] frames = [printed, copied, copied];
        _screen.FrameAt = i =>
        {
            _windowCalls.NotResponding = true;
            return frames[Math.Min(i, frames.Length - 1)];
        };

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow(), timeoutMs: 600));

        // Assert
        result.TimedOut.Should().BeTrue("nothing was drawn: the frames differ only in how they were read");
        result.ChangedPercent.Should().Be(0);
        result.CaptureMethod.Should().Be("ScreenBitBlt");
        result.Warnings.Should().Contain(w => w.StartsWith("WindowNotResponding:"))
            .And.Contain(w => w.StartsWith(VisualChangeService.BaselineResetWarning + ":"));
    }

    [Fact]
    public async Task AWindowThatStopsAnswering_MidWait_StillShowsAChangeMadeAfterTheSwitch()
    {
        var printed = WindowFrame();
        var copied = printed.Copy().Paint(0, 0, 640, 8, (0x20, 0x20, 0x20));
        VisualFrames[] frames = [printed, copied, copied, copied.Copy().Paint(300, 300, 4, 4, (0xFF, 0, 0))];
        _screen.FrameAt = i =>
        {
            _windowCalls.NotResponding = true;
            return frames[Math.Min(i, frames.Length - 1)];
        };

        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        result.Satisfied.Should().BeTrue(result.Error);
        result.ChangedRegions.Should().Equal(new BoundsDto(100 + 288, 200 + 288, 16, 16));
    }

    [Fact]
    public async Task Since_ACaptureReadTheOtherWay_StartsFromTheFirstFrame_WithAWarning()
    {
        // Arrange - capture_window copied the screen (the window did not answer then); the wait reads it with PrintWindow
        var copied = WindowFrame().Paint(0, 0, 640, 8, (0x20, 0x20, 0x20));
        var since = _registry.Register(Geometry(copied, window: true), copied.Signature("ScreenBitBlt")).CaptureId;
        _screen.Frames(WindowFrame());

        // Act
        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since(since), timeoutMs: 400));

        // Assert
        result.TimedOut.Should().BeTrue();
        result.Warnings.Should().ContainSingle(w => w.StartsWith(VisualChangeService.BaselineResetWarning + ":"))
            .Which.Should().Contain("since");
    }

    [Fact]
    public async Task Stable_WithFewerThanTwoFrames_TimesOut_SayingSo_NotThatTheTargetKeptChanging()
    {
        _screen.Frames(WindowFrame());

        var result = await CreateSut().WaitAsync(Request(TheWindow(), VisualWaitCondition.Stable, timeoutMs: 0, stableMs: 0));

        result.TimedOut.Should().BeTrue();
        result.ChangedRegions.Should().BeNull();
        result.Error.Should().Contain("one frame").And.NotContain("kept changing");
    }

    [Fact]
    public async Task Stable_WithStableMsAsLongAsTimeoutMs_IsSatisfied_HoweverLongTheStartTook()
    {
        // Arrange - the checks before the first frame take 300 ms; the window does not change
        _screen.Frames(WindowFrame());
        var first = true;
        _screenshots.Setup(s => s.CheckWindow(Window)).Returns(() =>
        {
            if (first)
            {
                first = false;
                _clock.Advance(TimeSpan.FromMilliseconds(300));
            }

            return null;
        });

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow(), VisualWaitCondition.Stable, timeoutMs: 1000, stableMs: 1000));

        // Assert
        result.Satisfied.Should().BeTrue(result.Error);
        result.StableForMs.Should().BeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public async Task Since_ARegionWaitsFrame_KeepsTheRectangleOfADeniedWindow_ThatClosedSince()
    {
        // Arrange - a region wait while a denied window is over it, then the denied window closes; its rectangle changes
        var denied = (nint)0x7070;
        var open = true;
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> visit) =>
        {
            if (open)
            {
                visit(denied);
            }
        });
        _windows.Setup(w => w.IsWindowVisible(denied)).Returns(true);
        _windows.Setup(w => w.GetWindowRect(denied)).Returns((100, 100, 300, 200));
        _capturePolicy.Setup(p => p.Check(denied)).Returns(new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied"));
        var before = new VisualFrames(400, 300);
        _screen.Frames(before);
        var sut = CreateSut(new TargetPolicy(TargetPolicySettings.Default));
        var first = await sut.WaitAsync(Request(new VisualTarget.Region(0, 0, 400, 300), timeoutMs: 200));
        open = false;
        _screen.Frames(before.Copy().Paint(100, 100, 200, 100, (0, 0, 0)));

        // Act
        var result = await sut.WaitAsync(Request(new VisualTarget.Since(first.CaptureId!), timeoutMs: 200));

        // Assert
        result.TimedOut.Should().BeTrue("the closed window's rectangle stays left out: its closing is no change to report");
        result.ChangedPercent.Should().Be(0);
    }

    [Fact]
    public async Task AWindowCopiedFromTheScreen_LeavesOutTheDeniedWindowsThatCoverIt_AndListsThem()
    {
        // Arrange - the window does not answer; a denied window covers part of it and changes
        _windowCalls.NotResponding = true;
        var denied = (nint)0x7070;
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> visit) =>
        {
            visit(denied);
            visit(Window);
        });
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.GetWindowRect(denied)).Returns((200, 300, 400, 400));
        _capturePolicy.Setup(p => p.Check(denied)).Returns(new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied"));
        var before = WindowFrame();
        _screen.Frames(before, before.Copy().Paint(120, 120, 40, 40, (0, 0, 0)));

        // Act
        var result = await CreateSut(new TargetPolicy(TargetPolicySettings.Default)).WaitAsync(Request(TheWindow(), timeoutMs: 400));

        // Assert
        result.TimedOut.Should().BeTrue("the only change is inside the denied window");
        result.MaskedWindows.Should().Equal(new MaskedWindow((long)denied, 200, 300, 200, 100));
    }

    [Fact]
    public async Task AWindowTarget_WhoseHandleIsGivenToAnotherProcess_MidWait_IsClosed()
    {
        _screen.Frames(WindowFrame());
        _windows.SetupSequence(w => w.GetWindowThreadProcessId(Window)).Returns(ProcessId).Returns(ProcessId).Returns(9999u);

        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        result.Satisfied.Should().BeTrue();
        result.Reason.Should().Be("closed");
    }

    [Fact]
    public async Task AWindowThatClosesWhileItIsCopiedFromTheScreen_IsClosed_NotCaptureFailed()
    {
        // Arrange - the window does not answer, and the second screen copy fails because it closed meanwhile
        _windowCalls.NotResponding = true;
        var reads = 0;
        _screen.FrameAt = _ => ++reads == 2 ? throw new InvalidOperationException("Invalid window handle.") : WindowFrame();
        _screenshots.Setup(s => s.CheckWindow(Window)).Returns(() =>
            reads >= 2 ? new CaptureRefusal(ScreenshotErrorCode.InvalidHandle, "gone") : null);

        // Act
        var result = await CreateSut().WaitAsync(Request(TheWindow()));

        // Assert
        result.Satisfied.Should().BeTrue(result.Error);
        result.Reason.Should().Be("closed");
    }

    [Fact]
    public async Task Since_AWindowThatIsGoneAtTheFirstCheck_ReportsTheCapturesRectangle()
    {
        var since = Register(WindowFrame(), window: true);
        _screenshots.Setup(s => s.CheckWindow(Window)).Returns(new CaptureRefusal(ScreenshotErrorCode.WindowMinimized, "minimized"));

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Since(since), timeoutMs: 0));

        result.Reason.Should().Be("minimized");
        result.ChangedRegions.Should().Equal(new BoundsDto(100, 200, 640, 480));
    }

    [Fact]
    public async Task ARegionWait_ReadsTheMonitors_OnlyForTheFrameItRegisters()
    {
        var before = new VisualFrames(40, 40);
        _screen.Frames(before, before, before, before.Copy().Paint(0, 0, 1, 1, (0, 0, 0)));

        var result = await CreateSut().WaitAsync(Request(new VisualTarget.Region(0, 0, 40, 40)));

        result.Checks.Should().Be(4);
        _monitors.Verify(m => m.GetAllMonitors(), Times.Once());
    }

    #endregion

    #region Cancellation and progress

    [Fact]
    public async Task ACancelledWait_Throws_AndReadsNoMore()
    {
        using var cancel = new CancellationTokenSource();
        _screen.FrameAt = i =>
        {
            if (i == 2)
            {
                cancel.Cancel();
            }

            return WindowFrame();
        };

        var wait = () => CreateSut().WaitAsync(Request(TheWindow()), cancellationToken: cancel.Token);

        await wait.Should().ThrowAsync<OperationCanceledException>();
        _screen.Sources.Should().HaveCount(3);
    }

    [Fact]
    public async Task ALongWait_ReportsItsProgress_AboutEverySecond()
    {
        _screen.Frames(WindowFrame());
        var progress = new RecordingProgress<OperationProgress>();

        await CreateSut().WaitAsync(Request(TheWindow(), timeoutMs: 3000), progress);

        progress.Reports.Select(r => r.Completed).Should().Equal(1000, 2000);
        progress.Reports.Should().OnlyContain(r => r.Total == 3000 && r.Message.StartsWith("waiting for changed"));
    }

    #endregion

    /// <summary>Registers a capture of <paramref name="frame"/> with its signature, as a capture taken while visual is served does.</summary>
    private string Register(VisualFrames frame, bool window) =>
        _registry.Register(Geometry(frame, window), frame.Signature(window ? "PrintWindow" : "ScreenBitBlt")).CaptureId;

    private static CaptureGeometry Geometry(VisualFrames frame, bool window) => new(
        frame.Width, frame.Height, new ImageToScreenTransform(frame.OriginX, frame.OriginY, 1, 1),
        new CapturedRegion(frame.OriginX, frame.OriginY, frame.Width, frame.Height),
        window ? new CapturedWindow((long)Window, (int)ProcessId, WindowRect.FromLTRB(100, 200, 740, 680), IncludeFrame: true) : null,
        window ? null : new VirtualScreenBounds(0, 0, 4480, 1440));

    /// <summary>A capture wrapper that lends one scripted frame per read and records what was read.</summary>
    private sealed class FakePixelScreen : IScreenCaptureWrapper
    {
        private readonly List<PixelSource> _sources = [];

        public (int X, int Y, int Width, int Height) VirtualScreen { get; set; }

        /// <summary>The frame of the n-th read (from 0).</summary>
        public Func<int, VisualFrames> FrameAt { get; set; } = _ => throw new InvalidOperationException("No frames scripted.");

        public IReadOnlyList<PixelSource> Sources => _sources;

        /// <summary>One frame per read, the last one repeated.</summary>
        public void Frames(params VisualFrames[] frames) => FrameAt = i => frames[Math.Min(i, frames.Length - 1)];

        public T WithPixels<T>(PixelSource source, PixelReader<T> read)
        {
            var frame = FrameAt(_sources.Count);
            _sources.Add(source);
            var method = source is PixelSource.Window ? "PrintWindow" : "ScreenBitBlt";
            return read(PixelView.Of(frame.Capture(method)));
        }

        public (int X, int Y, int Width, int Height) GetVirtualScreenBounds() => VirtualScreen;

        public CaptureResult CaptureScreenRegion(int x, int y, int width, int height) => throw new NotSupportedException();

        public CaptureResult CaptureWindow(nint windowHandle, bool includeFrame) => throw new NotSupportedException();

        public CaptureResult CaptureWindowFromScreen(nint windowHandle, bool includeFrame) => throw new NotSupportedException();

        public bool IsValidWindow(nint windowHandle) => true;
    }

    /// <summary>Runs window calls on the calling thread, or refuses them as a runner refuses a window that does not answer.</summary>
    private sealed class InlineWindowCalls : IWindowCallRunner
    {
        public bool NotResponding { get; set; }

        public int Calls { get; private set; }

        public T Run<T>(nint window, Func<T> call)
        {
            Calls++;
            return NotResponding ? throw new WindowNotRespondingException(window, "Window did not answer within 5 s.") : call();
        }
    }
}
