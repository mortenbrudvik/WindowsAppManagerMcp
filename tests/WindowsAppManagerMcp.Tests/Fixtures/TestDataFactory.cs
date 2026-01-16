namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Factory for creating test data objects.
/// </summary>
public static class TestDataFactory
{
    #region WindowInfo Factory

    public static WindowInfo CreateWindowInfo(
        int handleValue = 12345,
        string title = "Test Window",
        string processName = "testapp",
        int processId = 1000,
        int x = 100, int y = 100,
        int width = 800, int height = 600,
        WindowState state = WindowState.Normal,
        bool isVisible = true,
        int monitorIndex = 0)
    {
        return new WindowInfo(
            Handle: new nint(handleValue),
            Title: title,
            ProcessName: processName,
            ProcessId: processId,
            Bounds: new WindowRect(x, y, width, height),
            State: state,
            IsVisible: isVisible,
            MonitorIndex: monitorIndex);
    }

    public static IReadOnlyList<WindowInfo> CreateTypicalWindowSet()
    {
        return new List<WindowInfo>
        {
            CreateWindowInfo(1001, "Document.txt - Notepad", "notepad", 1001),
            CreateWindowInfo(1002, "Google Chrome", "chrome", 1002, 200, 100, 1200, 800),
            CreateWindowInfo(1003, "Visual Studio Code", "Code", 1003, 0, 0, 960, 1040),
            CreateWindowInfo(1004, "Windows Terminal", "WindowsTerminal", 1004, 960, 520, 960, 520)
        };
    }

    #endregion

    #region MonitorInfo Factory

    public static MonitorInfo CreateMonitorInfo(
        int index = 0,
        string deviceName = @"\\.\DISPLAY1",
        bool isPrimary = true,
        int boundsX = 0, int boundsY = 0,
        int boundsWidth = 1920, int boundsHeight = 1080,
        int workAreaX = 0, int workAreaY = 0,
        int workAreaWidth = 1920, int workAreaHeight = 1040,
        double scaleFactor = 1.0)
    {
        return new MonitorInfo(
            Index: index,
            DeviceName: deviceName,
            IsPrimary: isPrimary,
            Bounds: new MonitorRect(boundsX, boundsY, boundsWidth, boundsHeight),
            WorkArea: new MonitorRect(workAreaX, workAreaY, workAreaWidth, workAreaHeight),
            ScaleFactor: scaleFactor);
    }

    public static IReadOnlyList<MonitorInfo> CreateDualMonitorSetup()
    {
        return new List<MonitorInfo>
        {
            CreateMonitorInfo(0, @"\\.\DISPLAY1", true,
                0, 0, 1920, 1080, 0, 0, 1920, 1040),
            CreateMonitorInfo(1, @"\\.\DISPLAY2", false,
                1920, 0, 2560, 1440, 1920, 0, 2560, 1400, 1.25)
        };
    }

    #endregion

    #region LayoutPreset Factory

    public static LayoutPreset CreateLayoutPreset(
        string name = "test-preset",
        string? description = "Test layout preset",
        List<WindowPlacement>? placements = null)
    {
        return new LayoutPreset(
            Name: name,
            Description: description,
            Placements: placements ?? new List<WindowPlacement>
            {
                CreateWindowPlacement()
            });
    }

    public static WindowPlacement CreateWindowPlacement(
        string? titleContains = null,
        string processName = "testapp",
        int monitorIndex = 0,
        double x = 0, double y = 0,
        double width = 0.5, double height = 1.0,
        string? launchCommand = null,
        bool focus = false,
        int order = 0)
    {
        return new WindowPlacement(
            Matcher: new WindowMatcher(titleContains, processName),
            MonitorIndex: monitorIndex,
            Position: new RelativePosition(x, y, width, height),
            LaunchCommand: launchCommand,
            Focus: focus,
            Order: order);
    }

    #endregion

    #region Mock Service Factories

    public static Mock<IWindowService> CreateMockWindowService(
        IReadOnlyList<WindowInfo>? windows = null)
    {
        var mock = new Mock<IWindowService>();
        var testWindows = windows ?? CreateTypicalWindowSet();

        mock.Setup(w => w.GetAllWindows(It.IsAny<bool>()))
            .Returns(testWindows);

        mock.Setup(w => w.IsValidWindow(It.Is<nint>(h => h != nint.Zero)))
            .Returns(true);
        mock.Setup(w => w.IsValidWindow(nint.Zero))
            .Returns(false);

        mock.Setup(w => w.SetWindowBounds(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(true);

        return mock;
    }

    public static Mock<IMonitorService> CreateMockMonitorService(
        IReadOnlyList<MonitorInfo>? monitors = null)
    {
        var mock = new Mock<IMonitorService>();
        var testMonitors = monitors ?? new List<MonitorInfo> { CreateMonitorInfo() };

        mock.Setup(m => m.GetAllMonitors())
            .Returns(testMonitors);

        mock.Setup(m => m.GetPrimaryMonitor())
            .Returns(testMonitors.First(m => m.IsPrimary));

        mock.Setup(m => m.GetMonitorIndex(It.IsAny<nint>()))
            .Returns(0);

        return mock;
    }

    public static Mock<IProcessService> CreateMockProcessService()
    {
        var mock = new Mock<IProcessService>();

        mock.Setup(p => p.LaunchApplication(
                It.IsAny<string>(),
                It.IsAny<string[]?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<int>()))
            .Returns((string exe, string[]? args, string? dir, bool wait, int timeout) =>
                new LaunchResult(
                    Success: true,
                    ProcessId: 9999,
                    WindowHandle: wait ? new nint(8888) : null,
                    ErrorMessage: null));

        return mock;
    }

    public static Mock<IBrowserDetectionService> CreateMockBrowserDetectionService(
        bool isBrowser = false,
        bool containsUrl = false,
        string? newWindowFlag = "--new-window")
    {
        var mock = new Mock<IBrowserDetectionService>();

        mock.Setup(b => b.IsBrowser(It.IsAny<string>()))
            .Returns(isBrowser);

        mock.Setup(b => b.ContainsUrl(It.IsAny<string[]?>()))
            .Returns(containsUrl);

        mock.Setup(b => b.GetNewWindowFlag(It.IsAny<string>()))
            .Returns(isBrowser ? newWindowFlag : null);

        return mock;
    }

    public static Mock<INativeWindowWrapper> CreateMockNativeWindowWrapper(
        IReadOnlyList<WindowInfo>? windows = null)
    {
        var mock = new Mock<INativeWindowWrapper>();
        var testWindows = windows ?? CreateTypicalWindowSet();

        // EnumWindows - calls callback for each window
        mock.Setup(n => n.EnumWindows(It.IsAny<Func<nint, bool>>()))
            .Callback<Func<nint, bool>>(callback =>
            {
                foreach (var w in testWindows)
                {
                    if (!callback(w.Handle))
                        break;
                }
            });

        // IsWindow - valid for test windows
        mock.Setup(n => n.IsWindow(It.Is<nint>(h => h != nint.Zero)))
            .Returns(true);
        mock.Setup(n => n.IsWindow(nint.Zero))
            .Returns(false);

        // IsWindowVisible - based on test data
        foreach (var w in testWindows)
        {
            mock.Setup(n => n.IsWindowVisible(w.Handle)).Returns(w.IsVisible);
            mock.Setup(n => n.IsIconic(w.Handle)).Returns(w.State == WindowState.Minimized);
            mock.Setup(n => n.IsZoomed(w.Handle)).Returns(w.State == WindowState.Maximized);
            mock.Setup(n => n.GetWindowText(w.Handle)).Returns(w.Title);
            mock.Setup(n => n.GetWindowThreadProcessId(w.Handle)).Returns((uint)w.ProcessId);
            mock.Setup(n => n.GetWindowRect(w.Handle))
                .Returns((w.Bounds.X, w.Bounds.Y, w.Bounds.X + w.Bounds.Width, w.Bounds.Y + w.Bounds.Height));
        }

        // Default behaviors for unknown handles
        mock.Setup(n => n.IsWindowVisible(It.Is<nint>(h => !testWindows.Any(w => w.Handle == h))))
            .Returns(false);
        mock.Setup(n => n.GetWindowText(It.Is<nint>(h => !testWindows.Any(w => w.Handle == h))))
            .Returns(string.Empty);
        mock.Setup(n => n.GetWindowRect(It.Is<nint>(h => !testWindows.Any(w => w.Handle == h))))
            .Returns((ValueTuple<int, int, int, int>?)null);

        // Window manipulation - return true for valid handles
        mock.Setup(n => n.SetWindowPos(It.Is<nint>(h => h != nint.Zero),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<uint>()))
            .Returns(true);
        mock.Setup(n => n.ShowWindow(It.Is<nint>(h => h != nint.Zero), It.IsAny<int>()))
            .Returns(true);
        mock.Setup(n => n.SetForegroundWindow(It.Is<nint>(h => h != nint.Zero)))
            .Returns(true);
        mock.Setup(n => n.BringWindowToTop(It.Is<nint>(h => h != nint.Zero)))
            .Returns(true);
        mock.Setup(n => n.PostMessage(It.Is<nint>(h => h != nint.Zero),
                It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()))
            .Returns(true);

        // GetForegroundWindow - return first test window or zero
        mock.Setup(n => n.GetForegroundWindow())
            .Returns(testWindows.Count > 0 ? testWindows[0].Handle : nint.Zero);

        // GetCurrentProcessId - return a fake PID that won't match test windows
        mock.Setup(n => n.GetCurrentProcessId()).Returns(99999u);

        return mock;
    }

    #endregion

    #region Browser Reading Factories

    public static Mock<IUIAutomationWrapper> CreateMockUIAutomationWrapper(
        bool isAvailable = true,
        string? defaultUrl = "https://example.com",
        string? defaultContent = "Page content")
    {
        var mock = new Mock<IUIAutomationWrapper>();

        mock.Setup(u => u.IsAvailable()).Returns(isAvailable);

        // Default GetBrowserUrl returns the specified URL for any valid call
        if (defaultUrl != null)
        {
            mock.Setup(u => u.GetBrowserUrl(It.IsAny<nint>(), It.IsAny<string>()))
                .Returns(defaultUrl);
        }

        // Default GetBrowserContent returns the specified content
        if (defaultContent != null)
        {
            mock.Setup(u => u.GetBrowserContent(It.IsAny<nint>(), It.IsAny<string>(), It.IsAny<int?>()))
                .Returns(defaultContent);
        }

        // TryGetElementFromHandle returns false for zero handle, true otherwise
        mock.Setup(u => u.TryGetElementFromHandle(nint.Zero, out It.Ref<object?>.IsAny))
            .Returns(false);
        mock.Setup(u => u.TryGetElementFromHandle(It.Is<nint>(h => h != nint.Zero), out It.Ref<object?>.IsAny))
            .Returns(true);

        return mock;
    }

    public static Mock<IBrowserReadingService> CreateMockBrowserReadingService(
        bool success = true,
        string? url = "https://example.com",
        string? content = "Page content",
        string? browserType = "chrome",
        string? pageTitle = "Test Page")
    {
        var mock = new Mock<IBrowserReadingService>();

        var urlResult = new BrowserUrlResult(
            Success: success,
            Url: success ? url : null,
            BrowserType: success ? browserType : null,
            PageTitle: success ? pageTitle : null,
            Error: success ? null : "Test error",
            ErrorCode: success ? BrowserErrorCode.None : BrowserErrorCode.UrlBarNotFound);

        var contentResult = new BrowserContentResult(
            Success: success,
            Content: success ? content : null,
            Url: success ? url : null,
            BrowserType: success ? browserType : null,
            PageTitle: success ? pageTitle : null,
            ContentLength: success ? content?.Length : null,
            Error: success ? null : "Test error",
            ErrorCode: success ? BrowserErrorCode.None : BrowserErrorCode.ContentExtractionFailed);

        mock.Setup(s => s.GetBrowserUrl(It.IsAny<nint>(), It.IsAny<int?>())).Returns(urlResult);
        mock.Setup(s => s.GetBrowserContent(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<int?>())).Returns(contentResult);
        mock.Setup(s => s.GetActiveBrowserUrl(It.IsAny<int?>())).Returns(urlResult);
        mock.Setup(s => s.IsBrowserWindow(It.Is<nint>(h => h != nint.Zero))).Returns(true);
        mock.Setup(s => s.IsBrowserWindow(nint.Zero)).Returns(false);
        mock.Setup(s => s.GetBrowserType(It.IsAny<nint>(), It.IsAny<string>())).Returns(browserType);

        return mock;
    }

    #endregion

    #region Screenshot Factories

    public static ScreenshotResult CreateScreenshotResult(
        bool success = true,
        string? imageData = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
        string imageFormat = "png",
        int width = 1920,
        int height = 1080,
        int regionX = 0,
        int regionY = 0,
        int? monitorIndex = 0,
        double scaleFactor = 1.0,
        string? error = null)
    {
        return new ScreenshotResult(
            Success: success,
            ImageData: success ? imageData : null,
            ImageFormat: imageFormat,
            Width: width,
            Height: height,
            CapturedRegion: new CapturedRegion(regionX, regionY, width, height),
            MonitorIndex: monitorIndex,
            ScaleFactor: scaleFactor,
            Error: error);
    }

    public static ScreenListResult CreateScreenListResult(
        IReadOnlyList<ScreenInfo>? screens = null,
        int primaryIndex = 0)
    {
        screens ??= new List<ScreenInfo>
        {
            CreateScreenInfo(0, true),
            CreateScreenInfo(1, false, x: 1920)
        };

        return new ScreenListResult(
            Screens: screens,
            PrimaryIndex: primaryIndex,
            VirtualScreen: new VirtualScreenBounds(
                screens.Min(s => s.X),
                screens.Min(s => s.Y),
                screens.Max(s => s.X + s.Width) - screens.Min(s => s.X),
                screens.Max(s => s.Y + s.Height) - screens.Min(s => s.Y)));
    }

    public static ScreenInfo CreateScreenInfo(
        int index = 0,
        bool isPrimary = true,
        string name = @"\\.\DISPLAY1",
        int x = 0,
        int y = 0,
        int width = 1920,
        int height = 1080,
        int workAreaX = 0,
        int workAreaY = 0,
        int workAreaWidth = 1920,
        int workAreaHeight = 1040,
        double scaleFactor = 1.0)
    {
        return new ScreenInfo(
            Index: index,
            Name: name,
            IsPrimary: isPrimary,
            X: x,
            Y: y,
            Width: width,
            Height: height,
            WorkAreaX: workAreaX,
            WorkAreaY: workAreaY,
            WorkAreaWidth: workAreaWidth,
            WorkAreaHeight: workAreaHeight,
            ScaleFactor: scaleFactor);
    }

    public static Mock<IScreenCaptureWrapper> CreateMockScreenCaptureWrapper(
        byte[]? pixelData = null,
        int captureWidth = 1920,
        int captureHeight = 1080)
    {
        var mock = new Mock<IScreenCaptureWrapper>();

        // CaptureScreenRegion - return pixel data sized for the requested dimensions
        mock.Setup(s => s.CaptureScreenRegion(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int x, int y, int w, int h) =>
            {
                // Return pixel data sized for the actual requested dimensions (4 bytes per pixel BGRA)
                return pixelData ?? new byte[w * h * 4];
            });

        // CaptureWindow - return pixel data with specified default dimensions
        var windowPixelData = pixelData ?? new byte[captureWidth * captureHeight * 4];
        mock.Setup(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns((windowPixelData, captureWidth, captureHeight));

        mock.Setup(s => s.GetVirtualScreenBounds())
            .Returns((0, 0, 1920, 1080));

        mock.Setup(s => s.IsValidWindow(It.Is<nint>(h => h != nint.Zero)))
            .Returns(true);
        mock.Setup(s => s.IsValidWindow(nint.Zero))
            .Returns(false);

        return mock;
    }

    public static Mock<IScreenshotService> CreateMockScreenshotService(
        bool success = true,
        string? error = null)
    {
        var mock = new Mock<IScreenshotService>();

        var screenListResult = CreateScreenListResult();
        mock.Setup(s => s.ListScreens()).Returns(screenListResult);

        var screenshotResult = CreateScreenshotResult(success: success, error: error);

        mock.Setup(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(screenshotResult);

        mock.Setup(s => s.CaptureRegion(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<string>(), It.IsAny<int>()))
            .Returns(screenshotResult);

        mock.Setup(s => s.CaptureWindow(
                It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<int>()))
            .Returns(screenshotResult);

        return mock;
    }

    #endregion
}
