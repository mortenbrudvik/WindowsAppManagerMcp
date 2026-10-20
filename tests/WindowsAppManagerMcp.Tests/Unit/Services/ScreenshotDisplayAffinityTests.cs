using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// What a capture says of the windows that keep themselves out of it (issue #17): pixels copied from the screen list the
/// windows that declare a display affinity over them (excludedFromCapture), read before and after the pixels; a window that
/// drew itself lists none; a captured window that declares one warns on every path; and a locked session or an input desktop
/// out of reach warns that the image may show the lock screen, without refusing the capture.
/// </summary>
public class ScreenshotDisplayAffinityTests
{
    private const uint ExcludeFromCapture = 0x11;
    private const uint Monitor = 0x1;

    private static readonly CaptureOptions FullResolution = new() { MaxDimension = 0, Format = CaptureFormat.Png, Output = CaptureOutputMode.Metadata };
    private static readonly nint Window = new(0x5000);
    private static readonly nint Toast = new(0x6000);
    private static readonly nint Bar = new(0x6100);

    private readonly Mock<IScreenCaptureWrapper> _capture =
        TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 400, captureHeight: 300, windowOriginX: 100, windowOriginY: 100);
    private readonly Mock<IMonitorService> _monitors = TestDataFactory.CreateMockMonitorService();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IWindowCallRunner> _calls = new();
    private readonly Mock<ICaptureTargetPolicy> _capturePolicy = new();
    private readonly Mock<IProcessSecurityProbe> _security = new();

    /// <summary>The top-level windows, front to back, as each EnumWindows pass lists them; the n-th pass from 0.</summary>
    private Func<int, nint[]> _desktop = _ => [];

    private int _passes;

    public ScreenshotDisplayAffinityTests()
    {
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns(true);
        _windows.Setup(w => w.EnumWindows(It.IsAny<Func<nint, bool>>())).Callback((Func<nint, bool> visit) =>
        {
            foreach (var window in _desktop(_passes++))
            {
                if (!visit(window))
                {
                    break;
                }
            }
        });

        // Toast: a 320 x 100 notification at (1500, 900), excluded from capture; Bar: a strip at the top, shown only on the monitor
        AddWindow(Toast, ExcludeFromCapture, (1500, 900, 1820, 1000), "Board saved", "Enify");
        AddWindow(Bar, Monitor, (0, 0, 1920, 40), "Source bar", "Enify");

        _calls.Setup(c => c.Run(It.IsAny<nint>(), It.IsAny<Func<CaptureResult>>()))
            .Returns((nint _, Func<CaptureResult> call) => call());
        _security.Setup(s => s.GetSessionLock()).Returns(SessionLockState.Unlocked);
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(true);
    }

    private void AddWindow(nint handle, uint affinity, (int Left, int Top, int Right, int Bottom) rect, string title, string process)
    {
        _windows.Setup(w => w.GetWindowDisplayAffinity(handle)).Returns(affinity);
        _windows.Setup(w => w.GetWindowRect(handle)).Returns(rect);
        _windows.Setup(w => w.GetWindowText(handle)).Returns(title);
        var processId = (uint)handle + 1;
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns(processId);
        _windows.Setup(w => w.GetProcessName(processId)).Returns(process);
    }

    private ScreenshotService CreateSut(ITargetPolicy? policy = null) =>
        new(_capture.Object, _monitors.Object, _windows.Object, _calls.Object, captureRegistry: null, capturePolicy: _capturePolicy.Object,
            targetPolicy: policy ?? new TargetPolicy(TargetPolicySettings.Default), security: _security.Object);

    #region Screen copies

    [Fact]
    public void CaptureMonitor_ListsTheWindowsThatKeepOutOfCaptures_OnIt()
    {
        // Arrange
        _desktop = _ => [Toast, Bar];

        // Act
        var metadata = CreateSut().CaptureMonitor(0, FullResolution).Metadata;

        // Assert
        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.ExcludedFromCapture.Should().Equal(
            new ExcludedWindow((long)Toast, DisplayAffinity.ExcludeFromCapture, "Enify", "Board saved", 1500, 900, 320, 100),
            new ExcludedWindow((long)Bar, DisplayAffinity.Monitor, "Enify", "Source bar", 0, 0, 1920, 40));
    }

    [Fact]
    public void CaptureRegion_CutsThemToTheRegion_AndLeavesOutThoseOutsideIt()
    {
        _desktop = _ => [Toast, Bar];

        var metadata = CreateSut().CaptureRegion(1400, 800, 200, 150, FullResolution).Metadata;

        metadata.ExcludedFromCapture.Should().Equal(
            new ExcludedWindow((long)Toast, DisplayAffinity.ExcludeFromCapture, "Enify", "Board saved", 1500, 900, 100, 50));
    }

    [Fact]
    public void CaptureRegion_AToastThatClosesDuringTheCapture_IsStillListed()
    {
        // Read before the pixels only: toasts are short-lived
        _desktop = pass => pass == 0 ? [Toast] : [];

        var metadata = CreateSut().CaptureRegion(1400, 800, 200, 150, FullResolution).Metadata;

        metadata.ExcludedFromCapture.Should().ContainSingle().Which.Handle.Should().Be((long)Toast);
        _passes.Should().Be(2, "one EnumWindows pass before the pixels and one after them");
    }

    [Fact]
    public void CaptureRegion_AToastThatOpensDuringTheCapture_IsListed()
    {
        _desktop = pass => pass == 0 ? [] : [Toast];

        var metadata = CreateSut().CaptureRegion(1400, 800, 200, 150, FullResolution).Metadata;

        metadata.ExcludedFromCapture.Should().ContainSingle().Which.Handle.Should().Be((long)Toast);
    }

    [Fact]
    public void CaptureMonitor_WithoutSuchWindows_LeavesTheFieldOut()
    {
        _desktop = _ => [Window];

        var metadata = CreateSut().CaptureMonitor(0, FullResolution).Metadata;

        metadata.ExcludedFromCapture.Should().BeNull();
    }

    [Fact]
    public void CaptureMonitor_AWindowThePolicyDenies_IsListedWithoutItsProcessAndTitle()
    {
        _desktop = _ => [Toast];
        _capturePolicy.Setup(p => p.Check(Toast)).Returns(new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied"));

        var metadata = CreateSut().CaptureMonitor(0, FullResolution).Metadata;

        metadata.ExcludedFromCapture.Should().Equal(
            new ExcludedWindow((long)Toast, DisplayAffinity.ExcludeFromCapture, null, null, 1500, 900, 320, 100));
    }

    [Fact]
    public void CapturePixels_OfARegion_HandsTheListToOcr()
    {
        _desktop = _ => [Toast];

        var raw = CreateSut().CapturePixels(new CaptureTarget.Region(1400, 800, 200, 150));

        raw.ExcludedFromCapture.Should().ContainSingle().Which.Should().Be(
            new ExcludedWindow((long)Toast, DisplayAffinity.ExcludeFromCapture, "Enify", "Board saved", 1500, 900, 100, 50));
    }

    #endregion

    #region Window captures

    [Fact]
    public void CaptureWindow_ThatDrewItself_ListsNoOtherWindow()
    {
        // PrintWindow: the window's own content only, so a toast over it is not missing from the image
        _desktop = _ => [Toast, Window];
        _capture.Setup(c => c.CaptureWindow(Window, true))
            .Returns(new CaptureResult(new byte[400 * 300 * 4], 400, 300, 1450, 850, CaptureMethods.PrintWindow));

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        metadata.CaptureMethod.Should().Be("PrintWindow");
        metadata.ExcludedFromCapture.Should().BeNull();
        _passes.Should().Be(1, "the list after the pixels is read only when they can show other windows");
    }

    [Fact]
    public void CaptureWindow_CopiedFromTheScreen_ListsTheWindowsOverIt_ButNotItself()
    {
        // Arrange - the window itself also keeps out of captures: its warning says so instead
        _desktop = _ => [Toast, Window];
        _windows.Setup(w => w.GetWindowDisplayAffinity(Window)).Returns(ExcludeFromCapture);
        _capture.Setup(c => c.CaptureWindowFromScreen(Window, true))
            .Returns(new CaptureResult(new byte[400 * 300 * 4], 400, 300, 1450, 850, CaptureMethods.ScreenBitBlt));

        // Act
        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true,
            FullResolution with { CaptureMethod = WindowCaptureMethod.Screen }).Metadata;

        // Assert
        metadata.ExcludedFromCapture.Should().Equal(
            new ExcludedWindow((long)Toast, DisplayAffinity.ExcludeFromCapture, "Enify", "Board saved", 1500, 900, 320, 100));
        metadata.Warnings.Should().ContainSingle(w => w.StartsWith("DisplayAffinity: ", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureWindow_ThatRefusedPrintWindow_ListsTheWindowsOverIt()
    {
        // BitBltFallback copies the window's device context, which can show the windows that cover it
        _desktop = _ => [Toast, Window];
        _capture.Setup(c => c.CaptureWindow(Window, true))
            .Returns(new CaptureResult(Enumerable.Repeat((byte)0x80, 400 * 300 * 4).ToArray(), 400, 300, 1450, 850, CaptureMethods.BitBltFallback));

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        metadata.ExcludedFromCapture.Should().ContainSingle().Which.Handle.Should().Be((long)Toast);
    }

    [Theory]
    [InlineData(ExcludeFromCapture, "excludeFromCapture")]
    [InlineData(Monitor, "monitor")]
    public void CaptureWindow_OfAWindowThatDeclaresADisplayAffinity_WarnsOnEveryPath(uint affinity, string name)
    {
        // PrintWindow here: what it returns for such a window is not known, so the warning says "may"
        _windows.Setup(w => w.GetWindowDisplayAffinity(Window)).Returns(affinity);

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.Warnings.Should().ContainSingle(w => w.StartsWith("DisplayAffinity: ", StringComparison.Ordinal))
            .Which.Should().Contain($"({name})").And.Contain("may be black, empty, or show what is behind it");
    }

    [Fact]
    public void CaptureWindow_OfAControlInAWindowThatDeclaresOne_Warns()
    {
        var root = new nint(0x5100);
        _windows.Setup(w => w.GetAncestor(Window, WindowAncestor.Root)).Returns(root);
        _windows.Setup(w => w.GetWindowDisplayAffinity(root)).Returns(ExcludeFromCapture);

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: false, FullResolution).Metadata;

        metadata.Warnings.Should().Contain(w => w.StartsWith("DisplayAffinity: ", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureWindowArea_CutsTheListToTheCrop()
    {
        _desktop = _ => [Toast, Window];
        _capture.Setup(c => c.CaptureWindowFromScreen(Window, true))
            .Returns(new CaptureResult(new byte[400 * 300 * 4], 400, 300, 1450, 850, CaptureMethods.ScreenBitBlt));

        var metadata = CreateSut().CaptureWindowArea(Window, includeFrame: true, new BoundsDto(1450, 850, 100, 100),
            FullResolution with { CaptureMethod = WindowCaptureMethod.Screen }).Metadata;

        metadata.ExcludedFromCapture.Should().ContainSingle().Which.Should().Match<ExcludedWindow>(w =>
            w.X == 1500 && w.Y == 900 && w.Width == 50 && w.Height == 50);
    }

    [Fact]
    public void CaptureWithElements_CopiedFromTheScreen_ListsTheWindowsOverIt_AndWarnsForItsOwnAffinity()
    {
        _desktop = _ => [Toast, Window];
        _windows.Setup(w => w.GetWindowDisplayAffinity(Window)).Returns(Monitor);
        _capture.Setup(c => c.CaptureWindowFromScreen(Window, true))
            .Returns(new CaptureResult(new byte[400 * 300 * 4], 400, 300, 1450, 850, CaptureMethods.ScreenBitBlt));

        var metadata = CreateSut().CaptureWindowWithElements(Window, "Editor", [], includeFrame: true, ElementOverlayOptions.Default,
            FullResolution with { CaptureMethod = WindowCaptureMethod.Screen }).Metadata;

        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.ExcludedFromCapture.Should().ContainSingle().Which.Handle.Should().Be((long)Toast);
        metadata.Warnings.Should().Contain(w => w.StartsWith("DisplayAffinity: ", StringComparison.Ordinal) && w.Contains("(monitor)"));
    }

    [Fact]
    public void CaptureWithElements_ThatDrewItself_ListsNoOtherWindow()
    {
        _desktop = _ => [Toast, Window];

        var metadata = CreateSut().CaptureWindowWithElements(Window, "Editor", [], includeFrame: true, ElementOverlayOptions.Default,
            FullResolution).Metadata;

        metadata.ExcludedFromCapture.Should().BeNull();
    }

    [Fact]
    public void TheMetadata_NamesTheAffinity_AndLeavesOutWhatIsNotKnown()
    {
        // As the capture tools write it: camelCase, the affinity by name, no null fields
        _desktop = _ => [Toast, Bar];
        _capturePolicy.Setup(p => p.Check(Bar)).Returns(new CaptureRefusal(ScreenshotErrorCode.TargetDeniedByPolicy, "denied"));

        var json = ToolImages.Create(CreateSut().CaptureMonitor(0, FullResolution).Metadata, image: null, isError: false)
            .Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Single().Text;

        json.Should().Contain("\"excludedFromCapture\":[" +
                              "{\"handle\":24576,\"affinity\":\"excludeFromCapture\",\"processName\":\"Enify\",\"title\":\"Board saved\"," +
                              "\"x\":1500,\"y\":900,\"width\":320,\"height\":100}," +
                              "{\"handle\":24832,\"affinity\":\"monitor\",\"x\":0,\"y\":0,\"width\":1920,\"height\":40}]");
    }

    #endregion

    #region A locked session (issues #17 and #18)

    [Fact]
    public void CaptureMonitor_WhileTheSessionIsLocked_SucceedsWithASessionLockedWarningFirst()
    {
        // The fake screen is black, as a locked one may be: the lock comes before ImageBlank, which it explains
        _security.Setup(s => s.GetSessionLock()).Returns(SessionLockState.Locked);

        var metadata = CreateSut().CaptureMonitor(0, FullResolution).Metadata;

        metadata.Success.Should().BeTrue("captures are not refused while the session is locked");
        metadata.Warnings![0].Should().StartWith("SessionLocked: ").And.Contain("lock screen");
        metadata.Warnings.Should().Contain(w => w.StartsWith("ImageBlank", StringComparison.Ordinal));
    }

    [Fact]
    public void CaptureWindow_WhileTheInputDesktopIsOutOfReach_WarnsDesktopUnavailable()
    {
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(false);

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.Warnings![0].Should().StartWith("DesktopUnavailable: ");
    }

    [Fact]
    public void CapturePixels_WhileTheSessionIsLocked_HandsTheWarningToOcr()
    {
        _security.Setup(s => s.GetSessionLock()).Returns(SessionLockState.Locked);

        var raw = CreateSut().CapturePixels(new CaptureTarget.Region(0, 0, 100, 100));

        raw.Warnings[0].Should().StartWith("SessionLocked: ");
    }

    [Fact]
    public void CaptureWithElements_WhileTheSessionIsLocked_Warns()
    {
        _security.Setup(s => s.GetSessionLock()).Returns(SessionLockState.Locked);

        var metadata = CreateSut().CaptureWindowWithElements(Window, "Editor", [], includeFrame: true, ElementOverlayOptions.Default,
            FullResolution).Metadata;

        metadata.Warnings![0].Should().StartWith("SessionLocked: ");
    }

    [Fact]
    public void ACapture_WhileTheSessionIsUnlockedOrItsStateUnknown_HasNoSessionWarning()
    {
        // Unknown is not taken for locked: the warning is for a lock Windows reports
        _security.Setup(s => s.GetSessionLock()).Returns(SessionLockState.Unknown);

        var metadata = CreateSut().CaptureWindow(Window, includeFrame: true, FullResolution).Metadata;

        metadata.Success.Should().BeTrue(metadata.Error);
        metadata.Warnings.Should().NotContain(w => w.StartsWith("SessionLocked", StringComparison.Ordinal) ||
                                                   w.StartsWith("DesktopUnavailable", StringComparison.Ordinal));
        _security.Verify(s => s.IsInputDesktopAvailable(), Times.Once);
    }

    #endregion
}
