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
            .Returns((nint handle, int _, int _, int _, int _, bool _) => WindowChangeResult.Done(handle));
        mock.Setup(w => w.SetWindowState(It.IsAny<nint>(), It.IsAny<WindowStateRequest>()))
            .Returns((nint handle, WindowStateRequest _) => WindowChangeResult.Done(handle));
        mock.Setup(w => w.MoveWindowToMonitor(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<MonitorPositioning>(), It.IsAny<bool>()))
            .Returns((nint handle, int _, MonitorPositioning _, bool _) => WindowChangeResult.Done(handle));

        // GetWindowInfo returns the first matching window, or a default for any valid handle
        foreach (var window in testWindows)
        {
            mock.Setup(w => w.GetWindowInfo(window.Handle)).Returns(window);
        }
        // For any other non-zero handle, return a default window info
        mock.Setup(w => w.GetWindowInfo(It.Is<nint>(h => h != nint.Zero && !testWindows.Any(tw => tw.Handle == h))))
            .Returns(CreateWindowInfo());
        mock.Setup(w => w.GetWindowInfo(nint.Zero)).Returns((WindowInfo?)null);

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

        mock.Setup(p => p.LaunchApplicationAsync(
                It.IsAny<string>(),
                It.IsAny<string[]?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string exe, string[]? args, string? dir, bool wait, int timeout, CancellationToken _) =>
                new LaunchResult(
                    Success: true,
                    ProcessId: 9999,
                    WindowHandle: wait ? new nint(8888) : null,
                    ErrorMessage: null));

        return mock;
    }

    /// <summary>
    /// The real <see cref="LaunchPlacementService"/> over <paramref name="windows"/> and <paramref name="foreground"/>, on a
    /// manual clock that its waits move, so its watch after a placement (roadmap M3.F27) takes no time.
    /// <paramref name="duringWait"/> runs in each wait: what the app does meanwhile.
    /// </summary>
    public static LaunchPlacementService CreateInstantLaunchPlacement(
        IWindowService windows, IForegroundService foreground, IInputSession? session = null, Action? duringWait = null)
    {
        var clock = new ManualClock();
        return new LaunchPlacementService(windows, foreground, session, clock, (delay, token) =>
        {
            token.ThrowIfCancellationRequested();
            clock.Advance(delay);
            duringWait?.Invoke();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// An <see cref="IForegroundService"/> that reports every window as brought to the front with
    /// SetForegroundWindow, without touching anything.
    /// </summary>
    public static Mock<IForegroundService> CreateMockForegroundService()
    {
        var mock = new Mock<IForegroundService>();

        mock.Setup(f => f.EnsureForeground(It.IsAny<nint>(), It.IsAny<int>()))
            .Returns((nint handle, int _) => new FocusOutcome
            {
                Target = handle,
                IsForeground = true,
                Method = FocusMethod.SetForeground,
                ForegroundHandle = handle
            });

        return mock;
    }

    /// <summary>
    /// An <see cref="IInputTargetGuard"/> that allows all input and reports <paramref name="receiving"/>
    /// as the receiving window and the foreground window afterwards. For tests of what the input service
    /// sends; the guard's own rules are tested in InputTargetGuardTests.
    /// </summary>
    public static Mock<IInputTargetGuard> CreateAllowingInputGuard(WindowSummary? receiving = null)
    {
        var window = receiving ?? new WindowSummary(0x5150, "Untitled - Notepad", "notepad", 5150);
        var allowed = InputGuardResult.Allow(InputTarget.None, (nint)window.Handle, window, (nint)window.Handle);
        var mock = new Mock<IInputTargetGuard>();

        mock.Setup(g => g.CheckKeyboard(It.IsAny<InputTarget>(), It.IsAny<KeyboardInputKind>(), It.IsAny<IReadOnlyList<InputEvent>?>()))
            .Returns((InputTarget target, KeyboardInputKind _, IReadOnlyList<InputEvent>? _) => allowed with { Target = target });
        mock.Setup(g => g.CheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputTarget>()))
            .Returns((int _, int _, InputTarget target) => allowed with { Target = target });
        mock.Setup(g => g.RecheckKeyboard(It.IsAny<InputGuardResult>())).Returns((InputGuardResult approved) => approved);
        mock.Setup(g => g.RecheckMouse(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<InputGuardResult>()))
            .Returns((int _, int _, InputGuardResult approved) => approved);
        mock.Setup(g => g.DescribeForeground()).Returns(window);

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
            mock.Setup(n => n.GetProcessName((uint)w.ProcessId)).Returns(w.ProcessName);
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
            .Returns(Win32CallResult.Ok);
        mock.Setup(n => n.ShowWindow(It.Is<nint>(h => h != nint.Zero), It.IsAny<int>()))
            .Returns(true);
        mock.Setup(n => n.SetForegroundWindow(It.Is<nint>(h => h != nint.Zero)))
            .Returns(true);
        mock.Setup(n => n.BringWindowToTop(It.Is<nint>(h => h != nint.Zero)))
            .Returns(true);
        mock.Setup(n => n.PostMessage(It.Is<nint>(h => h != nint.Zero),
                It.IsAny<uint>(), It.IsAny<nint>(), It.IsAny<nint>()))
            .Returns(Win32CallResult.Ok);

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

        mock.Setup(s => s.GetBrowserUrlAsync(It.IsAny<nint>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(urlResult);
        mock.Setup(s => s.GetBrowserContentAsync(It.IsAny<nint>(), It.IsAny<BrowserContentOptions?>(), It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(contentResult);
        mock.Setup(s => s.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>())).ReturnsAsync(urlResult);
        mock.Setup(s => s.IsBrowserWindow(It.Is<nint>(h => h != nint.Zero))).Returns(true);
        mock.Setup(s => s.IsBrowserWindow(nint.Zero)).Returns(false);
        mock.Setup(s => s.GetBrowserType(It.IsAny<nint>(), It.IsAny<string>())).Returns(browserType);

        return mock;
    }

    #endregion

    #region Screenshot Factories

    public static ScreenshotResult CreateScreenshotResult(
        bool success = true,
        string imageFormat = "jpeg",
        int width = 1920,
        int height = 1080,
        int regionX = 0,
        int regionY = 0,
        int? monitorIndex = 0,
        double scaleFactor = 1.0,
        string? error = null,
        string? errorCode = null)
    {
        if (!success)
        {
            return new ScreenshotResult(
                Success: false,
                ImageFormat: null,
                MimeType: null,
                Width: 0,
                Height: 0,
                CapturedRegion: null,
                ImageToScreen: null,
                MonitorIndex: null,
                ScaleFactor: null,
                Error: error,
                ErrorCode: errorCode);
        }

        return new ScreenshotResult(
            Success: true,
            ImageFormat: imageFormat,
            MimeType: imageFormat == "png" ? "image/png" : "image/jpeg",
            Width: width,
            Height: height,
            CapturedRegion: new CapturedRegion(regionX, regionY, width, height),
            ImageToScreen: new ImageToScreenTransform(regionX, regionY, 1, 1),
            MonitorIndex: monitorIndex,
            ScaleFactor: scaleFactor,
            ByteSize: 4,
            CaptureMethod: "ScreenBitBlt",
            CoordinateSpace: CoordinateSpaces.PhysicalVirtualScreen);
    }

    /// <summary>A tiny stand-in for encoded image bytes; tool-layer tests only pass them on.</summary>
    public static EncodedImage CreateEncodedImage(string mimeType = "image/jpeg") =>
        new(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, mimeType);

    public static ScreenCapture<ScreenshotResult> CreateScreenCapture(ScreenshotResult? metadata = null)
    {
        metadata ??= CreateScreenshotResult();
        return new ScreenCapture<ScreenshotResult>(metadata, metadata.Success ? CreateEncodedImage(metadata.MimeType!) : null);
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

    /// <summary>
    /// A capture wrapper that returns blank pixels: screen regions at the requested origin and size,
    /// windows at (<paramref name="windowOriginX"/>, <paramref name="windowOriginY"/>) with
    /// <paramref name="captureWidth"/> x <paramref name="captureHeight"/> pixels.
    /// </summary>
    public static Mock<IScreenCaptureWrapper> CreateMockScreenCaptureWrapper(
        byte[]? pixelData = null,
        int captureWidth = 1920,
        int captureHeight = 1080,
        int windowOriginX = 0,
        int windowOriginY = 0)
    {
        var mock = new Mock<IScreenCaptureWrapper>();

        // CaptureScreenRegion - return pixel data sized for the requested dimensions
        mock.Setup(s => s.CaptureScreenRegion(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((int x, int y, int w, int h) =>
                new CaptureResult(pixelData ?? new byte[w * h * 4], w, h, x, y, "ScreenBitBlt"));

        // CaptureWindow - return pixel data with specified default dimensions
        var windowPixelData = pixelData ?? new byte[captureWidth * captureHeight * 4];
        mock.Setup(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>()))
            .Returns(new CaptureResult(windowPixelData, captureWidth, captureHeight, windowOriginX, windowOriginY, "PrintWindow"));

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

        var capture = CreateScreenCapture(CreateScreenshotResult(success: success, error: error));

        mock.Setup(s => s.CaptureMonitor(It.IsAny<int>(), It.IsAny<CaptureOptions>()))
            .Returns(capture);

        mock.Setup(s => s.CaptureRegion(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CaptureOptions>()))
            .Returns(capture);

        mock.Setup(s => s.CaptureWindow(It.IsAny<nint>(), It.IsAny<bool>(), It.IsAny<CaptureOptions>()))
            .Returns(capture);

        var annotated = CreateAnnotatedScreenshotResult(success: success, error: error);
        mock.Setup(s => s.CaptureWindowWithElements(
                It.IsAny<nint>(),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<UIElementInfo>>(),
                It.IsAny<bool>(),
                It.IsAny<ElementOverlayOptions>(),
                It.IsAny<CaptureOptions>(), It.IsAny<CapturedWindow?>()))
            .Returns(new ScreenCapture<AnnotatedScreenshotResult>(annotated, success ? CreateEncodedImage() : null));

        return mock;
    }

    public static AnnotatedScreenshotResult CreateAnnotatedScreenshotResult(
        bool success = true,
        string imageFormat = "jpeg",
        int width = 800,
        int height = 600,
        long windowHandle = 12345,
        string? windowTitle = "Test Window",
        IReadOnlyList<AnnotatedElement>? elements = null,
        int elementCount = 3,
        double captureMs = 25.0,
        double annotationMs = 15.0,
        double? scaleFactor = 1.0,
        string? error = null,
        string? errorCode = null)
    {
        if (success && elements == null)
        {
            elements = new List<AnnotatedElement>
            {
                new AnnotatedElement("Submit", "Button", "btnSubmit",
                    new BoundsDto(100, 100, 80, 30), new BoundsDto(200, 200, 80, 30), true, "green"),
                new AnnotatedElement("Name", "Edit", "txtName",
                    new BoundsDto(100, 150, 200, 25), new BoundsDto(200, 250, 200, 25), true, "blue"),
                new AnnotatedElement("Accept", "CheckBox", "chkAccept",
                    new BoundsDto(100, 200, 150, 20), new BoundsDto(200, 300, 150, 20), true, "orange")
            };
            elementCount = elements.Count;
        }

        return new AnnotatedScreenshotResult(
            Success: success,
            ImageFormat: success ? imageFormat : null,
            MimeType: success ? (imageFormat == "png" ? "image/png" : "image/jpeg") : null,
            Width: success ? width : 0,
            Height: success ? height : 0,
            WindowHandle: windowHandle,
            WindowTitle: success ? windowTitle : null,
            CapturedRegion: success ? new CapturedRegion(100, 100, width, height) : null,
            ImageToScreen: success ? new ImageToScreenTransform(100, 100, 1, 1) : null,
            Elements: success ? elements : null,
            ElementCount: success ? elementCount : 0,
            CaptureElapsedMs: captureMs,
            AnnotationElapsedMs: annotationMs,
            MonitorScaleFactor: scaleFactor,
            Error: error,
            ErrorCode: errorCode,
            CoordinateSpace: success ? CoordinateSpaces.PhysicalVirtualScreen : null);
    }

    #endregion

    #region UIElement Factories

    public static UIElementInfo CreateUIElementInfo(
        string? name = "Test Element",
        string controlType = "Button",
        string? automationId = "btnTest",
        int x = 100,
        int y = 100,
        int width = 80,
        int height = 30,
        bool isEnabled = true,
        bool isOffscreen = false,
        IReadOnlyList<UIElementInfo>? children = null)
    {
        return new UIElementInfo(
            Name: name,
            ControlType: controlType,
            AutomationId: automationId,
            Bounds: new BoundsDto(x, y, width, height),
            IsEnabled: isEnabled,
            IsOffscreen: isOffscreen,
            Children: children);
    }

    public static UIElementResult CreateUIElementResult(
        bool success = true,
        long windowHandle = 12345,
        string? windowTitle = "Test Window",
        IReadOnlyList<UIElementInfo>? elements = null,
        int totalElementCount = 5,
        int depth = 5,
        double elapsedMs = 50.0,
        string? error = null,
        UIElementErrorCode errorCode = UIElementErrorCode.None)
    {
        if (success && elements == null)
        {
            elements = new List<UIElementInfo>
            {
                CreateUIElementInfo("Submit", "Button", "btnSubmit"),
                CreateUIElementInfo("Name", "Edit", "txtName", x: 100, y: 150, width: 200, height: 25),
                CreateUIElementInfo("Accept Terms", "CheckBox", "chkTerms", x: 100, y: 200, width: 150, height: 20)
            };
        }

        return new UIElementResult(
            Success: success,
            WindowHandle: windowHandle,
            WindowTitle: success ? windowTitle : null,
            Elements: success ? elements : null,
            TotalElementCount: success ? (elements?.Count ?? totalElementCount) : 0,
            Depth: success ? depth : 0,
            ElapsedMilliseconds: elapsedMs,
            Error: error,
            ErrorCode: errorCode);
    }

    public static Mock<IUIElementService> CreateMockUIElementService(
        bool success = true,
        string? error = null,
        UIElementErrorCode errorCode = UIElementErrorCode.None)
    {
        var mock = new Mock<IUIElementService>();

        mock.Setup(s => s.IsAvailable()).Returns(true);

        var result = CreateUIElementResult(
            success: success,
            error: error,
            errorCode: errorCode);

        mock.Setup(s => s.GetUIElementsAsync(
                It.IsAny<nint>(),
                It.IsAny<int>(),
                It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        return mock;
    }

    #endregion

    /// <summary>
    /// Makes ShowWindow on <paramref name="handle"/> change what IsIconic, IsZoomed and IsWindowVisible report, as
    /// Windows does: SW_MINIMIZE minimizes (remembering whether the window was maximized), SW_MAXIMIZE maximizes, and
    /// SW_RESTORE returns a minimized window to its previous state and makes any other window normal. SW_SHOWNOACTIVATE
    /// is simulated like SW_RESTORE, so a window minimized from maximized needs it twice to become normal (the worst case
    /// the geometry placements allow for). The window is visible. Returns the state as it is now, for assertions.
    /// </summary>
    public static Func<WindowState> SimulateShowStates(Mock<INativeWindowWrapper> native, nint handle, WindowState initial = WindowState.Normal)
    {
        var minimized = initial == WindowState.Minimized;
        var maximized = initial == WindowState.Maximized;
        var restoresToMaximized = false;

        native.Setup(n => n.IsIconic(handle)).Returns(() => minimized);
        native.Setup(n => n.IsZoomed(handle)).Returns(() => maximized);
        native.Setup(n => n.IsWindowVisible(handle)).Returns(true);
        native.Setup(n => n.ShowWindow(handle, It.IsAny<int>())).Returns((nint _, int command) =>
        {
            switch (command)
            {
                case 6: // SW_MINIMIZE
                    restoresToMaximized = maximized || (minimized && restoresToMaximized);
                    minimized = true;
                    maximized = false;
                    break;
                case 3: // SW_MAXIMIZE
                    minimized = false;
                    maximized = true;
                    break;
                case 9: // SW_RESTORE
                case 4: // SW_SHOWNOACTIVATE: restores like SW_RESTORE, without activating
                    maximized = minimized && restoresToMaximized;
                    minimized = false;
                    break;
            }

            return true;
        });

        return () => minimized ? WindowState.Minimized : maximized ? WindowState.Maximized : WindowState.Normal;
    }
}
