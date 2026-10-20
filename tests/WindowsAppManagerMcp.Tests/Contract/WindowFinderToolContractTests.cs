using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: what an MCP client sends to and gets back from the window discovery tools. The new
/// find_windows filters bind from their JSON names, every window carries className and isCloaked,
/// and ownerHandle and the UWP host fields appear only when they apply. Runs the real
/// WindowFinderTools through an in-process MCP server with a mocked IWindowService.
/// </summary>
public class WindowFinderToolContractTests
{
    private readonly Mock<IWindowService> _windowService = new();
    private readonly Mock<IProcessSecurityProbe> _securityProbe = new();

    public WindowFinderToolContractTests()
    {
        // As the real probe answers: get_window_info's detail 'full' reads the integrity through it, and never gets null
        _securityProbe.Setup(p => p.ServerIntegrity).Returns(new ProcessIntegrity(31_000, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null));
        _securityProbe.Setup(p => p.GetIntegrity(It.IsAny<int>()))
            .Returns((int id) => new ProcessIntegrity(id, IntegrityLevel.Medium, ProcessIntegrity.MediumRid, false, null));
    }

    [Fact]
    public async Task FindWindows_BindsTheFilterArguments()
    {
        // Arrange
        _windowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([]);
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("find_windows", new Dictionary<string, object?>
        {
            ["className"] = "Shell_TrayWnd",
            ["includeCloaked"] = true,
            ["includeToolWindows"] = true,
            ["includeShell"] = true,
        });

        // Assert
        result.IsError.Should().NotBe(true);
        _windowService.Verify(w => w.FindWindows(It.Is<WindowQuery>(q =>
            q.ClassName == "Shell_TrayWnd" && q.IncludeCloaked && q.IncludeToolWindows && q.IncludeShell)), Times.Once);
    }

    [Fact]
    public async Task FindWindows_ReportsIdentity_AndOmitsOwnerAndHostWhenTheyDoNotApply()
    {
        // Arrange
        var notepad = TestDataFactory.CreateWindowInfo(1001, "Untitled - Notepad", "notepad", 100) with { ClassName = "Notepad" };
        var calculator = TestDataFactory.CreateWindowInfo(1002, "Calculator", "CalculatorApp", 600) with
        {
            ClassName = "ApplicationFrameWindow",
            HostProcessName = "ApplicationFrameHost",
            HostProcessId = 500
        };
        var dialog = TestDataFactory.CreateWindowInfo(1003, "Save As", "notepad", 100) with
        {
            ClassName = "#32770",
            OwnerHandle = new nint(1001)
        };
        _windowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([notepad, calculator, dialog]);
        await using var server = await StartServerAsync();

        // Act
        var windows = ParsePayload(await server.CallToolAsync("find_windows")).EnumerateArray().ToList();

        // Assert
        windows.Should().HaveCount(3);
        windows[0].GetProperty("className").GetString().Should().Be("Notepad");
        windows[0].GetProperty("isCloaked").GetBoolean().Should().BeFalse();
        windows[0].TryGetProperty("ownerHandle", out _).Should().BeFalse();
        windows[0].TryGetProperty("hostProcessName", out _).Should().BeFalse();
        windows[0].TryGetProperty("hostProcessId", out _).Should().BeFalse();

        windows[1].GetProperty("processName").GetString().Should().Be("CalculatorApp");
        windows[1].GetProperty("processId").GetInt32().Should().Be(600);
        windows[1].GetProperty("hostProcessName").GetString().Should().Be("ApplicationFrameHost");
        windows[1].GetProperty("hostProcessId").GetInt32().Should().Be(500);

        windows[2].GetProperty("ownerHandle").GetInt64().Should().Be(1001);
        windows.Where(w => w.TryGetProperty("sessionLock", out _)).Should().BeEmpty("the lock is get_foreground_window's, not every window's");
    }

    [Fact]
    public async Task FindWindows_MarksTheAgentHostWindow_AndOmitsTheFlagForOtherWindows()
    {
        // Arrange
        var terminal = TestDataFactory.CreateWindowInfo(1001, "pwsh", "WindowsTerminal", 100) with { IsAgentHost = true };
        var notepad = TestDataFactory.CreateWindowInfo(1002, "Untitled - Notepad", "notepad", 200);
        _windowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([terminal, notepad]);
        await using var server = await StartServerAsync();

        // Act
        var windows = ParsePayload(await server.CallToolAsync("find_windows")).EnumerateArray().ToList();

        // Assert
        windows[0].GetProperty("isAgentHost").GetBoolean().Should().BeTrue();
        windows[1].TryGetProperty("isAgentHost", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetForegroundWindow_ReportsAnUntitledWindowByClass()
    {
        // Arrange - a context menu is in the foreground
        _windowService.Setup(w => w.GetForegroundWindow())
            .Returns(TestDataFactory.CreateWindowInfo(2001, "", "explorer", 300) with { ClassName = "#32768" });
        await using var server = await StartServerAsync();

        // Act
        var window = ParsePayload(await server.CallToolAsync("get_foreground_window"));

        // Assert
        window.GetProperty("handle").GetInt64().Should().Be(2001);
        window.GetProperty("title").GetString().Should().BeEmpty();
        window.GetProperty("className").GetString().Should().Be("#32768");
    }

    [Fact]
    public async Task GetForegroundWindow_ReportsTheSessionLock_AsAStringAfterTheWindowsFields()
    {
        // Arrange - issue #18: the lock screen app in front, and whether the session is locked
        _windowService.Setup(w => w.GetForegroundWindow())
            .Returns(TestDataFactory.CreateWindowInfo(2002, "Windows Default Lock Screen", "LockApp", 400) with
            {
                ClassName = "Windows.UI.Core.CoreWindow"
            });
        _securityProbe.Setup(p => p.GetSessionLock()).Returns(SessionLockState.Unlocked);
        await using var server = await StartServerAsync();

        // Act
        var window = ParsePayload(await server.CallToolAsync("get_foreground_window"));

        // Assert
        window.GetProperty("handle").GetInt64().Should().Be(2002);
        window.GetProperty("processName").GetString().Should().Be("LockApp");
        window.GetProperty("sessionLock").GetString().Should().Be("unlocked");
        window.EnumerateObject().Last().Name.Should().Be("sessionLock");
    }

    [Fact]
    public async Task GetForegroundWindow_WithNoWindowInFront_IsAnErrorWithNoForegroundWindow_NotEmptyContent()
    {
        // Arrange - for a moment during a switch, or on the secure desktop, no window is in the foreground
        _windowService.Setup(w => w.GetForegroundWindow()).Returns((WindowInfo?)null);
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("get_foreground_window");

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be("NoForegroundWindow");
        payload.GetProperty("error").GetString().Should().Contain("No window is in the foreground");
    }

    [Fact]
    public async Task FindWindows_BindsTheSelectorFilters_SortAndLimit()
    {
        // Arrange
        _windowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([]);
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("find_windows", new Dictionary<string, object?>
        {
            ["titleEquals"] = "Untitled - Notepad",
            ["titleRegex"] = "^Untitled",
            ["processName"] = "note",
            ["processNameContains"] = true,
            ["includeUntitled"] = true,
            ["sort"] = "title",
            ["limit"] = 3,
        });

        // Assert
        result.IsError.Should().NotBe(true);
        _windowService.Verify(w => w.FindWindows(It.Is<WindowQuery>(q =>
            q.TitleEquals == "Untitled - Notepad" && q.TitleRegex == "^Untitled" && q.ProcessName == "note" && q.ProcessNameContains &&
            q.IncludeUntitled && q.Sort == WindowSort.Title && q.Limit == 3)), Times.Once);
    }

    [Fact]
    public async Task FindWindows_ReportsTheMonitorsIdAndTheZone_AndLeavesTheMonitorOutWhenItCannotBeRead()
    {
        // Arrange - issue #12: a tile another program placed, a window over two monitors, and one whose monitor cannot be read
        var tile = TestDataFactory.CreateWindowInfo(1001, monitorIndex: 2) with { MonitorId = "m-0d6fc36f", Zone = "left_half" };
        var across = TestDataFactory.CreateWindowInfo(1002) with { MonitorId = "m-81c07e5b", SpansMonitors = true };
        var unread = TestDataFactory.CreateWindowInfo(1003) with { MonitorIndex = null };
        _windowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([tile, across, unread]);
        await using var server = await StartServerAsync();

        // Act
        var windows = ParsePayload(await server.CallToolAsync("find_windows")).EnumerateArray().ToList();

        // Assert
        windows[0].GetProperty("monitorIndex").GetInt32().Should().Be(2);
        windows[0].GetProperty("monitorId").GetString().Should().Be("m-0d6fc36f");
        windows[0].GetProperty("zone").GetString().Should().Be("left_half");
        windows[0].TryGetProperty("spansMonitors", out _).Should().BeFalse();

        windows[1].GetProperty("spansMonitors").GetBoolean().Should().BeTrue();
        windows[1].TryGetProperty("zone", out _).Should().BeFalse();

        foreach (var field in new[] { "monitorIndex", "monitorId", "zone", "spansMonitors" })
        {
            windows[2].TryGetProperty(field, out _).Should().BeFalse($"{field} is left out, never monitor 0 as a guess");
        }
    }

    [Fact]
    public async Task FindWindows_BindsIncludeMinimized()
    {
        _windowService.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([]);
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("find_windows", new Dictionary<string, object?> { ["includeMinimized"] = false });

        result.IsError.Should().NotBe(true);
        _windowService.Verify(w => w.FindWindows(It.Is<WindowQuery>(q => !q.IncludeMinimized)), Times.Once);
    }

    [Fact]
    public async Task IncludeMinimized_IsNoSelectorField_LikeSortAndLimit()
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("get_window_info", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad", ["includeMinimized"] = false }
        });

        result.IsError.Should().BeTrue();
        ParsePayload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _windowService.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
    }

    [Fact]
    public async Task GetWindowInfo_Full_OfAMinimizedWindow_ReportsRestore()
    {
        // Arrange - minimized from maximized on the second monitor
        var minimized = TestDataFactory.CreateWindowInfo(1001, state: WindowState.Minimized, monitorIndex: 1) with { MonitorId = "m-81c07e5b" };
        var monitor = TestDataFactory.CreateMonitorInfo(index: 1, isPrimary: false, boundsX: 1920) with { Id = "m-81c07e5b" };
        _windowService.Setup(w => w.IsValidWindow(minimized.Handle)).Returns(true);
        _windowService.Setup(w => w.GetWindowDetails(minimized.Handle)).Returns(new WindowDetails(minimized, false, 4, false, true, true, 96)
        {
            Restore = new WindowRestore(WindowState.Maximized, new WindowRect(2000, 100, 814, 607), monitor)
        });
        await using var server = await StartServerAsync();

        // Act
        var payload = ParsePayload(await server.CallToolAsync("get_window_info",
            new Dictionary<string, object?> { ["handle"] = 1001, ["detail"] = "full" }));

        // Assert
        var restore = payload.GetProperty("restore");
        restore.GetProperty("state").GetString().Should().Be("maximized");
        restore.GetProperty("bounds").GetProperty("x").GetInt32().Should().Be(2000);
        restore.GetProperty("monitorIndex").GetInt32().Should().Be(1);
        restore.GetProperty("monitorId").GetString().Should().Be("m-81c07e5b");
        payload.TryGetProperty("zone", out _).Should().BeFalse("a minimized window has none");
    }

    [Theory]
    [InlineData("sort", "newest", "sort order")]
    [InlineData("limit", 0, "limit")]
    [InlineData("titleRegex", "(unclosed", "titleRegex")]
    public async Task FindWindows_AnInvalidSortLimitOrRegex_IsInvalidArguments(string argument, object value, string named)
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("find_windows", new Dictionary<string, object?> { [argument] = value });

        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        payload.GetProperty("error").GetString().Should().Contain(named);
        _windowService.Verify(w => w.FindWindows(It.IsAny<WindowQuery>()), Times.Never);
    }

    [Fact]
    public async Task GetWindowInfo_BindsTheWindowSelectorFromItsJsonNames()
    {
        // Arrange
        var notepad = TestDataFactory.CreateWindowInfo(1001, "Untitled - Notepad", "notepad", 100) with { ClassName = "Notepad" };
        _windowService.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>())).Returns(WindowResolution.Found(notepad));
        _windowService.Setup(w => w.GetWindowInfo(notepad.Handle)).Returns(notepad);
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("get_window_info", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?>
            {
                ["processName"] = "notepad", ["titleContains"] = "Untitled", ["className"] = "Notepad", ["index"] = 0,
                ["includeCloaked"] = true, ["processNameContains"] = false
            },
            ["detail"] = "compact",
        });

        // Assert
        result.IsError.Should().NotBe(true);
        var payload = ParsePayload(result);
        payload.GetProperty("handle").GetInt64().Should().Be(1001);
        payload.GetProperty("state").GetString().Should().Be("normal");
        payload.TryGetProperty("bounds", out _).Should().BeFalse("compact leaves the bounds out");
        _windowService.Verify(w => w.ResolveWindow(new WindowSelector
        {
            ProcessName = "notepad", TitleContains = "Untitled", ClassName = "Notepad", Index = 0, IncludeCloaked = true
        }), Times.Once);
    }

    [Fact]
    public async Task AWindowSelectorWithAnUnknownProperty_IsInvalidArguments_SoATypoNeverWidensTheMatch()
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("get_window_info", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad", ["titleContain"] = "Untitled" }
        });

        result.IsError.Should().BeTrue();
        ParsePayload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
        _windowService.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
    }

    [Fact]
    public async Task AnAmbiguousSelector_IsAnError_ThatListsTheCandidates()
    {
        // Arrange
        var chrome = Enumerable.Range(0, 2)
            .Select(i => TestDataFactory.CreateWindowInfo(2001 + i, $"Tab {i} - Google Chrome", "chrome", 300) with { ClassName = "Chrome_WidgetWin_1" })
            .ToList();
        _windowService.Setup(w => w.ResolveWindow(It.IsAny<WindowSelector>()))
            .Returns(WindowResolution.Failed("AmbiguousWindow", "2 windows match processName 'chrome'.", chrome));
        await using var server = await StartServerAsync();

        // Act
        var result = await server.CallToolAsync("get_window_info", new Dictionary<string, object?>
        {
            ["window"] = new Dictionary<string, object?> { ["processName"] = "chrome" }
        });

        // Assert
        result.IsError.Should().BeTrue();
        var payload = ParsePayload(result);
        payload.GetProperty("success").GetBoolean().Should().BeFalse();
        payload.GetProperty("errorCode").GetString().Should().Be("AmbiguousWindow");
        var candidates = payload.GetProperty("candidates").EnumerateArray().ToList();
        candidates.Should().HaveCount(2);
        candidates[0].GetProperty("handle").GetInt64().Should().Be(2001);
        candidates[0].GetProperty("title").GetString().Should().Be("Tab 0 - Google Chrome");
        candidates[0].GetProperty("processName").GetString().Should().Be("chrome");
        candidates[0].GetProperty("processId").GetInt32().Should().Be(300);
        candidates[0].GetProperty("className").GetString().Should().Be("Chrome_WidgetWin_1");
    }

    [Fact]
    public async Task GetWindowInfo_WithHandleAndWindow_IsInvalidArguments()
    {
        await using var server = await StartServerAsync();

        var result = await server.CallToolAsync("get_window_info", new Dictionary<string, object?>
        {
            ["handle"] = 1001,
            ["window"] = new Dictionary<string, object?> { ["processName"] = "notepad" }
        });

        result.IsError.Should().BeTrue();
        ParsePayload(result).GetProperty("errorCode").GetString().Should().Be(ToolErrorReporting.InvalidArgumentsErrorCode);
    }

    private Task<InProcessMcpServer> StartServerAsync() =>
        InProcessMcpServer.StartAsync(
            [],
            builder => builder
                .AddToolErrorReporting()
                .WithServerTools(new WindowFinderTools(_windowService.Object, securityProbe: _securityProbe.Object)));

    private static JsonElement ParsePayload(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        return JsonDocument.Parse(text).RootElement;
    }
}
