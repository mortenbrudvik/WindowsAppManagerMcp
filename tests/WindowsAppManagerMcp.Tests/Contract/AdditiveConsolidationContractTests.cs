using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: roadmap M6.1's additive consolidations as an MCP client sees them, through an in-process server with the
/// production registrations over <see cref="ContractServices"/> (mocks: nothing real moves, no browser is read).
/// get_monitors reports virtualScreen; set_window_bounds takes either pair alone, and refuses half a pair before any
/// window is touched; get_browser_url with a bare foreground selector reads the browser in front.
/// </summary>
public class AdditiveConsolidationContractTests
{
    private static async Task<(CallToolResult Result, ContractServices Services)> CallAsync(
        string tool, IReadOnlyDictionary<string, object?> arguments, Action<ContractServices>? arrange = null)
    {
        var services = new ContractServices(ContractMode.Typical);
        arrange?.Invoke(services);
        await using var server = await InProcessMcpServer.StartAsync(
            [], builder => builder.AddWindowsAppManagerTools(), configureServices: services.Register);
        return (await server.CallToolAsync(tool, arguments), services);
    }

    private static JsonElement Payload(CallToolResult result) =>
        JsonDocument.Parse(result.Content.OfType<TextContentBlock>().Single().Text).RootElement.Clone();

    [Fact]
    public async Task GetMonitors_ReportsTheVirtualScreen()
    {
        // Act
        var (result, services) = await CallAsync("get_monitors", new Dictionary<string, object?>());
        using var _ = services;

        // Assert - the typical desktop's two monitors together: 1920 left of a 2560 x 1440 primary
        var screen = Payload(result).GetProperty("virtualScreen");
        (screen.GetProperty("x").GetInt32(), screen.GetProperty("y").GetInt32(), screen.GetProperty("width").GetInt32(),
            screen.GetProperty("height").GetInt32()).Should().Be((-1920, 0, 4480, 1440));
    }

    [Fact]
    public async Task SetWindowBounds_SizeOnly_IsAResize()
    {
        // Act
        var (result, services) = await CallAsync("set_window_bounds", new Dictionary<string, object?>
        {
            ["handle"] = TypicalDesktop.NotepadHandle, ["width"] = 1200, ["height"] = 800
        });
        using var _ = services;

        // Assert
        result.IsError.Should().NotBe(true);
        Payload(result).GetProperty("success").GetBoolean().Should().BeTrue();
        services.Windows.Verify(w => w.ResizeWindow((nint)TypicalDesktop.NotepadHandle, 1200, 800), Times.Once);
        services.Windows.Verify(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task SetWindowBounds_SizeOnly_WithBringToFront_IsARaisingResize()
    {
        // Act - the typical fake answers this overload too, so a step runner or argument fuzzing can take this path
        var (result, services) = await CallAsync("set_window_bounds", new Dictionary<string, object?>
        {
            ["handle"] = TypicalDesktop.NotepadHandle, ["width"] = 1200, ["height"] = 800, ["bringToFront"] = true
        });
        using var _ = services;

        // Assert
        result.IsError.Should().NotBe(true);
        Payload(result).GetProperty("success").GetBoolean().Should().BeTrue();
        services.Windows.Verify(w => w.ResizeWindow((nint)TypicalDesktop.NotepadHandle, 1200, 800, true), Times.Once);
        services.Windows.Verify(w => w.ResizeWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SetWindowBounds_PositionOnly_IsAMove_ThatRaisesTheWindow()
    {
        // Act
        var (result, services) = await CallAsync("set_window_bounds", new Dictionary<string, object?>
        {
            ["handle"] = TypicalDesktop.NotepadHandle, ["x"] = -1800, ["y"] = 240
        });
        using var _ = services;

        // Assert
        Payload(result).GetProperty("success").GetBoolean().Should().BeTrue();
        services.Windows.Verify(w => w.MoveWindow((nint)TypicalDesktop.NotepadHandle, -1800, 240, true), Times.Once);
    }

    [Fact]
    public async Task SetWindowBounds_HalfAPair_IsInvalidArguments_AndChangesNothing()
    {
        // Act
        var (result, services) = await CallAsync("set_window_bounds", new Dictionary<string, object?>
        {
            ["handle"] = TypicalDesktop.NotepadHandle, ["x"] = 100, ["width"] = 800, ["height"] = 600
        });
        using var _ = services;

        // Assert
        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be("InvalidArguments");
        services.Windows.Verify(w => w.SetWindowFrame(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<bool>()), Times.Never);
        services.Windows.Verify(w => w.ResizeWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        services.Windows.Verify(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task GetBrowserUrl_WithABareForegroundSelector_IsGetActiveBrowserUrl()
    {
        // Arrange - the agent's terminal is in front
        void TerminalInFront(ContractServices s) =>
            s.Browser.Setup(b => b.GetActiveBrowserUrlAsync(It.IsAny<CdpOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new BrowserUrlResult(false, null, null, null, "Foreground window 'WindowsTerminal' is not a browser",
                    BrowserErrorCode.NoBrowserInForeground));

        // Act
        var (result, services) = await CallAsync("get_browser_url",
            new Dictionary<string, object?> { ["window"] = new Dictionary<string, object?> { ["foreground"] = true } }, TerminalInFront);
        using var _ = services;

        // Assert
        result.IsError.Should().BeTrue();
        Payload(result).GetProperty("errorCode").GetString().Should().Be("NoBrowserInForeground");
        services.Windows.Verify(w => w.ResolveWindow(It.IsAny<WindowSelector>()), Times.Never);
    }
}
