using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract (roadmap M3.11): a capture's captureId, then a click at a point of its image, through an in-process MCP
/// server with the real ScreenshotTools, ScreenshotService, CaptureRegistry, CoordinateSpaceResolver,
/// InputSimulationTools, InputSimulationService and InputTargetGuard. Every native wrapper is mocked, so the input that
/// is or is not sent is a mock call. The point is converted by the server and then guarded exactly as a screen point:
/// a capture of one window never becomes a click on another.
/// </summary>
public class CaptureCoordinateContractTests
{
    private static readonly nint Notepad = 0x200;
    private static readonly nint Calculator = 0x300;
    private const int NotepadPid = 2001;
    private const int CalculatorPid = 3001;

    // Notepad's capture: 1600 x 900 at (0, 100), scaled to 1568 x 882, so image (392, 196) is screen (400, 300)
    private readonly Mock<IScreenCaptureWrapper> _capture =
        TestDataFactory.CreateMockScreenCaptureWrapper(captureWidth: 1600, captureHeight: 900, windowOriginX: 0, windowOriginY: 100);

    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeHitTestWrapper> _hitTest = new();
    private readonly Mock<INativeInputWrapper> _input = new();
    private readonly Mock<IWindowService> _windowService = new();
    private readonly Mock<IForegroundService> _foreground = new();
    private readonly Mock<IProcessSecurityProbe> _security = new();
    private readonly Mock<IHostGuard> _hostGuard = new();
    private readonly Mock<INativeThreadInputWrapper> _threads = new();
    private readonly CaptureRegistry _registry = new();
    private readonly Mock<IUIElementService> _uiElements = TestDataFactory.CreateMockUIElementService();
    private readonly Mock<IElementRegistry> _elementRegistry = new();
    private (int Left, int Top, int Right, int Bottom) _notepadRect = (0, 100, 1600, 1000);

    public CaptureCoordinateContractTests()
    {
        _threads.Setup(t => t.GetWindowThreadId(It.IsAny<nint>())).Returns((nint window) => (uint)window + 1);
        _threads.Setup(t => t.GetGuiThreadInfo(It.IsAny<uint>())).Returns(new GuiThreadInfo(0, 0, 0, 0, 0));

        Window(Notepad, NotepadPid, "notepad", "Untitled - Notepad");
        Window(Calculator, CalculatorPid, "CalculatorApp", "Calculator");
        _windows.Setup(w => w.GetWindowRect(Notepad)).Returns(() => _notepadRect);
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns(true);

        var medium = new ProcessIntegrity(0, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null);
        _security.Setup(s => s.ServerIntegrity).Returns(medium);
        _security.Setup(s => s.GetIntegrity(It.IsAny<int>())).Returns(medium);
        _security.Setup(s => s.IsInputDesktopAvailable()).Returns(true);

        _input.Setup(i => i.SetCursorPos(It.IsAny<int>(), It.IsAny<int>())).Returns(true);
        _input.Setup(i => i.GetCursorPos()).Returns((400, 300));
        _input.Setup(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()))
            .Returns((IReadOnlyList<InputEvent> events) => (uint)events.Count);

        At(Notepad);
    }

    private void Window(nint handle, int processId, string processName, string title)
    {
        _windows.Setup(w => w.IsWindow(handle)).Returns(true);
        _windows.Setup(w => w.GetAncestor(handle, It.IsAny<WindowAncestor>())).Returns(handle);
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns((uint)processId);
        _windows.Setup(w => w.GetWindowLongPtr(handle, -16)).Returns(new nint(0x10CF0000)); // visible overlapped window
        _windowService.Setup(s => s.GetWindowInfo(handle))
            .Returns(TestDataFactory.CreateWindowInfo((int)handle, title, processName, processId));
    }

    /// <summary>The window at screen point (400, 300).</summary>
    private void At(nint window)
    {
        _hitTest.Setup(h => h.WindowFromPoint(400, 300)).Returns(window);
        _hitTest.Setup(h => h.GetRootWindow(window)).Returns(window);
    }

    private void VerifyNothingSent()
    {
        _input.Verify(i => i.SendInput(It.IsAny<IReadOnlyList<InputEvent>>()), Times.Never);
        _input.Verify(i => i.SetCursorPos(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    private Task<InProcessMcpServer> StartServerAsync()
    {
        var monitors = TestDataFactory.CreateMockMonitorService(
            [TestDataFactory.CreateMonitorInfo(boundsX: 0, boundsY: 0, boundsWidth: 1920, boundsHeight: 1080)]);
        var guard = new InputTargetGuard(
            _windows.Object, _hitTest.Object, _threads.Object, new Mock<INativeProcessWrapper>().Object,
            _windowService.Object, _foreground.Object, _security.Object, _hostGuard.Object,
            new TargetPolicy(TargetPolicySettings.Default), InputGuardOptions.Default);
        var input = new InputSimulationService(monitors.Object, _input.Object, guard, new InputSession(_input.Object), hitTest: _hitTest.Object);
        var screenshots = new ScreenshotService(_capture.Object, monitors.Object, _windows.Object, captureRegistry: _registry);
        var resolver = new CoordinateSpaceResolver(_registry, _windows.Object, _hitTest.Object, _capture.Object);
        var screenshotTools = new ScreenshotTools(screenshots, _uiElements.Object, _windowService.Object,
            new TargetPolicy(TargetPolicySettings.Default), elementRegistry: _elementRegistry.Object);
        return InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddToolErrorReporting()
                .WithServerTools(new InputSimulationTools(input, _windowService.Object, resolver))
                .WithServerTools(screenshotTools));
    }

    private static JsonElement Payload(CallToolResult result)
    {
        var payload = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Last().Text).RootElement;
        if (payload.TryGetProperty("success", out var success))
        {
            (result.IsError == true).Should().Be(!success.GetBoolean(), "a result goes out with isError = !success");
        }

        return payload;
    }

    private static async Task<string> CaptureNotepadAsync(InProcessMcpServer server)
    {
        var metadata = Payload(await server.CallToolAsync("capture_window", new Dictionary<string, object?> { ["handle"] = (long)Notepad }));
        metadata.GetProperty("success").GetBoolean().Should().BeTrue();
        metadata.GetProperty("width").GetInt32().Should().Be(1568);
        return metadata.GetProperty("captureId").GetString()!;
    }

    [Fact]
    public async Task ACaptureId_ThenAClickAtAnImagePoint_ClicksTheConvertedScreenPoint()
    {
        // Arrange
        await using var server = await StartServerAsync();
        var captureId = await CaptureNotepadAsync(server);

        // Act - no windowHandle: the captured window is the target
        var payload = Payload(await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = 392,
            ["y"] = 196,
            ["captureId"] = captureId
        }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeTrue(payload.ToString());
        payload.GetProperty("x").GetInt32().Should().Be(400);
        payload.GetProperty("y").GetInt32().Should().Be(300);
        var requested = payload.GetProperty("requested");
        requested.GetProperty("x").GetInt32().Should().Be(392);
        requested.GetProperty("y").GetInt32().Should().Be(196);
        requested.GetProperty("space").GetString().Should().Be("image");
        requested.GetProperty("captureId").GetString().Should().Be(captureId);
        payload.GetProperty("receivingWindow").GetProperty("handle").GetInt64().Should().Be(Notepad);
        _input.Verify(i => i.SetCursorPos(400, 300), Times.AtLeastOnce);
    }

    [Fact]
    public async Task AMarksLegendCenter_ClickedWithTheCaptureId_ClicksTheElementsMiddle()
    {
        // Arrange - Notepad's Save button, whose middle is screen (400, 300) (roadmap M3.11: the marks and image-space
        // clicks agree)
        UIElementInfo save = new("Save", "Button", "btnSave", new BoundsDto(380, 290, 40, 20), IsEnabled: true,
            IsOffscreen: false, Ref: "w1000001e7");
        _uiElements.Setup(u => u.GetUIElementsAsync(Notepad, It.IsAny<int>(), It.IsAny<UIElementFilter?>(),
                It.IsAny<IProgress<OperationProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestDataFactory.CreateUIElementResult(windowHandle: Notepad, elements: [save]));
        _windowService.Setup(s => s.IsValidWindow(Notepad)).Returns(true);
        await using var server = await StartServerAsync();
        var capture = Payload(await server.CallToolAsync("capture_with_elements", new Dictionary<string, object?> { ["handle"] = (long)Notepad }));
        capture.TryGetProperty("legend", out var legendProperty).Should().BeTrue(capture.ToString());
        var legend = legendProperty.GetString()!;
        legend.Should().StartWith("[w1000001e7] Button 'Save' #btnSave center=(");
        var center = legend[(legend.IndexOf("center=(", StringComparison.Ordinal) + 8)..legend.IndexOf(')')].Split(',').Select(int.Parse).ToArray();

        // Act
        var payload = Payload(await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = center[0],
            ["y"] = center[1],
            ["captureId"] = capture.GetProperty("captureId").GetString()
        }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeTrue(payload.ToString());
        payload.GetProperty("x").GetInt32().Should().Be(400);
        payload.GetProperty("y").GetInt32().Should().Be(300);
    }

    [Fact]
    public async Task ARefCrop_ThenAClickAtItsMiddle_ClicksTheElement()
    {
        // Arrange - Notepad's Save button at (380, 290, 40, 20), named by its ref; the crop keeps 8 pixels around it
        const string saveRef = "w1000001e7";
        var element = new UiaElement(new FakeUiaNode("Button", "Save"),
            new UiaNodeProperties("Save", "Button", "btnSave", new UiaRect(380, 290, 40, 20), IsEnabled: true, IsOffscreen: false),
            ClassName: null, IsKeyboardFocusable: true, UiaPatterns.Invoke, RuntimeId: [42, 7], ControlTypeId: null);
        _elementRegistry.Setup(r => r.Lookup(saveRef)).Returns(new ElementLookup(saveRef, Notepad,
            new ElementFingerprint("Save", "Button", "btnSave", null, null), 1));
        _elementRegistry.Setup(r => r.ResolveAsync(saveRef, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementResolution(new ResolvedElement(saveRef, Notepad, element.Node, element, ElementResolutionPath.Live)));
        await using var server = await StartServerAsync();
        var crop = Payload(await server.CallToolAsync("capture_window", new Dictionary<string, object?> { ["ref"] = saveRef }));
        crop.GetProperty("capturedRegion").GetProperty("x").GetInt32().Should().Be(372, crop.ToString());
        crop.GetProperty("width").GetInt32().Should().Be(56);

        // Act - the middle of the 56 x 36 crop
        var payload = Payload(await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = 28,
            ["y"] = 18,
            ["captureId"] = crop.GetProperty("captureId").GetString()
        }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeTrue(payload.ToString());
        payload.GetProperty("x").GetInt32().Should().Be(400);
        payload.GetProperty("y").GetInt32().Should().Be(300);
        payload.GetProperty("receivingWindow").GetProperty("handle").GetInt64().Should().Be(Notepad);
    }

    [Fact]
    public async Task AnImagePoint_WhereAnotherWindowCoversTheCapturedOne_IsTargetOccluded()
    {
        // Arrange - Calculator now covers that spot: the capture's window is the target, so the guard refuses
        await using var server = await StartServerAsync();
        var captureId = await CaptureNotepadAsync(server);
        At(Calculator);

        // Act
        var payload = Payload(await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = 392, ["y"] = 196, ["captureId"] = captureId
        }));

        // Assert
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.TargetOccluded));
        VerifyNothingSent();
    }

    [Fact]
    public async Task AnImagePoint_AfterTheWindowMoved_IsStaleCapture()
    {
        // Arrange
        await using var server = await StartServerAsync();
        var captureId = await CaptureNotepadAsync(server);
        _notepadRect = (50, 100, 1650, 1000);

        // Act
        var result = await server.CallToolAsync("click", new Dictionary<string, object?> { ["x"] = 392, ["y"] = 196, ["captureId"] = captureId });

        // Assert
        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.StaleCapture));
        payload.GetProperty("requested").GetProperty("captureId").GetString().Should().Be(captureId);
        VerifyNothingSent();
    }

    [Fact]
    public async Task AnUnknownCaptureId_IsCaptureNotFound()
    {
        await using var server = await StartServerAsync();

        var payload = Payload(await server.CallToolAsync("mouse_move", new Dictionary<string, object?>
        {
            ["x"] = 1, ["y"] = 1, ["space"] = "image", ["captureId"] = "c41-000000"
        }));

        payload.GetProperty("errorCode").GetString().Should().Be(nameof(InputErrorCode.CaptureNotFound));
        VerifyNothingSent();
    }

    [Fact]
    public async Task AnImagePointOfOneWindow_WithAnotherWindowHandle_IsInvalidArguments()
    {
        await using var server = await StartServerAsync();
        var captureId = await CaptureNotepadAsync(server);

        var result = await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = 392, ["y"] = 196, ["captureId"] = captureId, ["windowHandle"] = (long)Calculator
        });

        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        VerifyNothingSent();
    }

    [Fact]
    public async Task AClientPoint_IsRelativeToTheWindowsClientArea()
    {
        // Arrange - Notepad's client area starts at (8, 131) on screen
        _hitTest.Setup(h => h.GetClientAreaOnScreen(Notepad)).Returns(new WindowRect(8, 131, 1584, 861));
        await using var server = await StartServerAsync();

        // Act
        var payload = Payload(await server.CallToolAsync("click", new Dictionary<string, object?>
        {
            ["x"] = 392, ["y"] = 169, ["space"] = "client", ["windowHandle"] = (long)Notepad
        }));

        // Assert
        payload.GetProperty("success").GetBoolean().Should().BeTrue(payload.ToString());
        (payload.GetProperty("x").GetInt32(), payload.GetProperty("y").GetInt32()).Should().Be((400, 300));
        payload.GetProperty("requested").GetProperty("space").GetString().Should().Be("client");
        payload.GetProperty("requested").TryGetProperty("captureId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnknownSpace_IsRefusedWithTheValues()
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("click", new Dictionary<string, object?> { ["x"] = 1, ["y"] = 1, ["space"] = "pixels" });

        result.IsError.Should().BeTrue();
        var payload = Payload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain("screen").And.Contain("image");
        VerifyNothingSent();
    }
}
