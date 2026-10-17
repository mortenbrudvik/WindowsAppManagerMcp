using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// Integration tests for DI container configuration.
/// Tests T7.3 from BACKLOG-TECHNICAL.md.
///
/// The container is built with the same registration methods Program.cs uses
/// (<see cref="ServiceRegistration"/>), so these tests cover what the server really registers.
/// Only the layout storage differs: a temp folder instead of %LOCALAPPDATA%. Resolving services
/// only constructs them; no test here sends input, starts processes or changes windows.
/// </summary>
public class DependencyInjectionTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly string _testLayoutPath;

    public DependencyInjectionTests()
    {
        _testLayoutPath = Path.Combine(
            Path.GetTempPath(),
            $"DITests_{Guid.NewGuid()}");

        _serviceProvider = BuildServerServices().BuildServiceProvider();
    }

    /// <summary>
    /// The registrations of Program.cs, without the stdio transport and with layouts and the audit log in a temp folder.
    /// </summary>
    private ServiceCollection BuildServerServices()
    {
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(
            () => LayoutStorageOptions.ForPath(_testLayoutPath),
            () => AuditLogOptions.ForFolder(Path.Combine(_testLayoutPath, "audit")));
        services.AddMcpServer().AddWindowsAppManagerTools();
        return services;
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_testLayoutPath))
        {
            Directory.Delete(_testLayoutPath, recursive: true);
        }
    }

    #region T7.3.1: All Services Resolve Successfully

    [Fact]
    public void ServiceProvider_AllServicesResolve_Successfully()
    {
        // Act & Assert - All services should resolve without exception
        var nativeWrapper = _serviceProvider.GetService<INativeWindowWrapper>();
        var uiAutomationWrapper = _serviceProvider.GetService<IUIAutomationWrapper>();
        var windowService = _serviceProvider.GetService<IWindowService>();
        var monitorService = _serviceProvider.GetService<IMonitorService>();
        var processService = _serviceProvider.GetService<IProcessService>();
        var layoutService = _serviceProvider.GetService<ILayoutService>();
        var inputValidationService = _serviceProvider.GetService<IInputValidationService>();
        var browserReadingService = _serviceProvider.GetService<IBrowserReadingService>();

        nativeWrapper.Should().NotBeNull();
        uiAutomationWrapper.Should().NotBeNull();
        windowService.Should().NotBeNull();
        monitorService.Should().NotBeNull();
        processService.Should().NotBeNull();
        layoutService.Should().NotBeNull();
        inputValidationService.Should().NotBeNull();
        browserReadingService.Should().NotBeNull();
    }

    [Fact]
    public void ThePopupService_IsASingleton_AndTheElementSearchUsesIt()
    {
        // Resolving constructs them only: nothing is enumerated and no UI Automation client starts (roadmap M3.5)
        var popups = _serviceProvider.GetRequiredService<IPopupService>();
        var search = _serviceProvider.GetRequiredService<IElementSearchService>();

        popups.Should().BeOfType<PopupService>().And.BeSameAs(_serviceProvider.GetRequiredService<IPopupService>());
        typeof(ElementSearchService).GetField("_popups", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(search)
            .Should().BeSameAs(popups, "find_ui_elements' popup and desktop scopes need it");
    }

    [Fact]
    public void EveryUiAutomationRead_GetsTheTargetPolicy()
    {
        // The user's target policy refuses the reads of a window of an app it denies: a read service built without it would
        // read every window
        var policy = _serviceProvider.GetRequiredService<ITargetPolicy>();

        foreach (var service in new object[]
                 {
                     _serviceProvider.GetRequiredService<IUIElementService>(),
                     _serviceProvider.GetRequiredService<IElementInspectionService>(),
                     _serviceProvider.GetRequiredService<IElementSearchService>(),
                     _serviceProvider.GetRequiredService<IElementReadService>(),
                     _serviceProvider.GetRequiredService<IElementWaitService>(),
                     _serviceProvider.GetRequiredService<IPopupService>(),
                     _serviceProvider.GetRequiredService<IDesktopStateService>()
                 })
        {
            service.GetType().GetField("_policy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)
                .Should().BeSameAs(policy, "{0} reads UI Automation", service.GetType().Name);
        }
    }

    [Fact]
    public void TheTreeWalk_GetsTheSecurityProbe_ForItsElevationWarning()
    {
        // get_ui_elements warns about a window it cannot read because it runs elevated (roadmap M3.F16), with the probe the
        // input guard uses; a walk built without it would never say so
        var walk = _serviceProvider.GetRequiredService<IUIElementService>();

        typeof(UIElementService).GetField("_security", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(walk)
            .Should().BeSameAs(_serviceProvider.GetRequiredService<IProcessSecurityProbe>());
    }

    [Fact]
    public void TheProcessListAndTheFullWindowRead_GetTheSecurityProbe_AndTheListGetsThePolicy()
    {
        // list_processes reports integrity and aboveServer with the input guard's probe, and never reads the command line of
        // a process the policy denies; get_window_info's 'full' reports the window's integrity (roadmap M6.6). Built
        // without them, the fields would silently be left out and a denied process's command line would be read
        var processes = _serviceProvider.GetRequiredService<IProcessService>();
        var windowTools = _serviceProvider.GetRequiredService<WindowFinderTools>();
        var probe = _serviceProvider.GetRequiredService<IProcessSecurityProbe>();

        typeof(ProcessService).GetField("_securityProbe", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(processes)
            .Should().BeSameAs(probe);
        typeof(ProcessService).GetField("_targetPolicy", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(processes)
            .Should().BeSameAs(_serviceProvider.GetRequiredService<ITargetPolicy>());
        typeof(WindowFinderTools).GetField("_securityProbe", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(windowTools)
            .Should().BeSameAs(probe);
    }

    [Fact]
    public void TheDialogAndMenuPathServices_ActOnlyThroughTheOneElementActionService_AndTheirToolsResolve()
    {
        // respond_to_dialog, file_dialog and menu_path (roadmap M3.5, M3.14) press, set and expand through element_action's
        // service, so its guards apply; resolving starts no UI Automation client
        var actions = _serviceProvider.GetRequiredService<IElementActionService>();
        var dialogs = _serviceProvider.GetRequiredService<IDialogService>();
        var fileDialogs = _serviceProvider.GetRequiredService<IFileDialogService>();
        var menuPaths = _serviceProvider.GetRequiredService<IMenuPathService>();

        foreach (var service in new object[] { dialogs, fileDialogs, menuPaths })
        {
            service.GetType().GetField("_actions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)
                .Should().BeSameAs(actions, "{0} acts only through element_action's service", service.GetType().Name);
        }

        typeof(FileDialogService).GetField("_dialogs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(fileDialogs)
            .Should().BeSameAs(dialogs, "the replace prompt is answered as respond_to_dialog answers it");
        _serviceProvider.GetRequiredService<WindowsAppManagerMcp.Tools.DialogTools>().Should().NotBeNull();
        var tools = _serviceProvider.GetRequiredService<WindowsAppManagerMcp.Tools.ElementActionTools>();
        typeof(WindowsAppManagerMcp.Tools.ElementActionTools).GetField("_menuPaths", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tools).Should().BeSameAs(menuPaths, "element_action's menu_path needs it");
    }

    [Fact]
    public void TheElementReads_AreASingleton_AndTheirToolsResolve()
    {
        // read_element_text and read_table (roadmap M3.9); resolving starts no UI Automation client
        var reads = _serviceProvider.GetRequiredService<IElementReadService>();

        reads.Should().BeOfType<ElementReadService>().And.BeSameAs(_serviceProvider.GetRequiredService<IElementReadService>());
        _serviceProvider.GetRequiredService<WindowsAppManagerMcp.Tools.ElementReadTools>().Should().NotBeNull();
    }

    [Fact]
    public void TheElementRegistryAndSearch_AreSingletons_BuiltWithTheProductionConstructor()
    {
        // Resolving constructs them only: neither UI Automation engine starts a client before its first read
        var registry = _serviceProvider.GetRequiredService<IElementRegistry>();
        var search = _serviceProvider.GetRequiredService<IElementSearchService>();

        registry.Should().BeOfType<ElementRegistry>().And.BeSameAs(_serviceProvider.GetRequiredService<IElementRegistry>());
        search.Should().BeOfType<ElementSearchService>().And.BeSameAs(_serviceProvider.GetRequiredService<IElementSearchService>());
        registry.Count.Should().Be(0);

        // The production constructor numbers windows from a random start in eight million (the test-only one takes it); both
        // take WAM_UIA_PROVIDER_CONTAINS (roadmap M3.F12), which the container holds as read from the environment
        var production = typeof(ElementRegistry).GetConstructors()
            .Single(c => c.GetParameters().Length == 4);
        production.GetParameters().Select(p => p.ParameterType).Should()
            .Equal(typeof(IUiaTree), typeof(IUiaWorker), typeof(INativeWindowWrapper), typeof(ProviderContainsOptions));
        _serviceProvider.GetRequiredService<ProviderContainsOptions>().Should().Be(ProviderContainsOptions.FromEnvironment());
    }

    [Fact]
    public void TheElementWait_IsASingleton()
    {
        // wait_for_element (roadmap M3.8); resolving it starts no UI Automation client
        var wait = _serviceProvider.GetRequiredService<IElementWaitService>();

        wait.Should().BeOfType<ElementWaitService>().And.BeSameAs(_serviceProvider.GetRequiredService<IElementWaitService>());
    }

    [Fact]
    public void TheElementWait_WakesOnTheServersUiAutomationEvents()
    {
        // Roadmap M3.F22: one element event source, over the server's UI Automation tree; resolving it registers nothing
        var wait = _serviceProvider.GetRequiredService<IElementWaitService>();
        var events = _serviceProvider.GetRequiredService<IElementEventSource>();

        events.Should().BeOfType<ElementEventSource>();
        typeof(ElementEventSource).GetField("_tree", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(events)
            .Should().BeSameAs(_serviceProvider.GetRequiredService<IUiaTree>());
        typeof(ElementWaitService).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wait)
            .Should().BeSameAs(events);
    }

    [Fact]
    public void TheWindowWait_WakesOnTheServersWindowEvents()
    {
        // Roadmap M3.F22: one window event source, over the native hooks; resolving it installs no hook (the first wait
        // that sleeps does)
        var wait = _serviceProvider.GetRequiredService<IWindowWaitService>();
        var events = _serviceProvider.GetRequiredService<IWindowEventSource>();

        events.Should().BeOfType<WindowEventSource>();
        _serviceProvider.GetRequiredService<INativeWinEventWrapper>().Should().BeOfType<NativeWinEventWrapper>();
        typeof(WindowWaitService).GetField("_events", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(wait)
            .Should().BeSameAs(events);
    }

    #endregion

    #region T7.3.2: INativeWindowWrapper Singleton

    [Fact]
    public void INativeWindowWrapper_Singleton_SameInstance()
    {
        // Act
        var instance1 = _serviceProvider.GetRequiredService<INativeWindowWrapper>();
        var instance2 = _serviceProvider.GetRequiredService<INativeWindowWrapper>();

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    #endregion

    #region T7.3.3: IWindowService Singleton

    [Fact]
    public void IWindowService_Singleton_SameInstance()
    {
        // Act
        var instance1 = _serviceProvider.GetRequiredService<IWindowService>();
        var instance2 = _serviceProvider.GetRequiredService<IWindowService>();

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    #endregion

    #region T7.3.3: IMonitorService Singleton

    [Fact]
    public void IMonitorService_Singleton_SameInstance()
    {
        // Act
        var instance1 = _serviceProvider.GetRequiredService<IMonitorService>();
        var instance2 = _serviceProvider.GetRequiredService<IMonitorService>();

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    #endregion

    #region T7.3.4: IProcessService Singleton

    [Fact]
    public void IProcessService_Singleton_SameInstance()
    {
        // Act
        var instance1 = _serviceProvider.GetRequiredService<IProcessService>();
        var instance2 = _serviceProvider.GetRequiredService<IProcessService>();

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    #endregion

    #region T7.3.5: ILayoutService Singleton

    [Fact]
    public void ILayoutService_Singleton_SameInstance()
    {
        // Act
        var instance1 = _serviceProvider.GetRequiredService<ILayoutService>();
        var instance2 = _serviceProvider.GetRequiredService<ILayoutService>();

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    #endregion

    #region T7.3.6: IInputValidationService Singleton

    [Fact]
    public void IInputValidationService_Singleton_SameInstance()
    {
        // Act
        var instance1 = _serviceProvider.GetRequiredService<IInputValidationService>();
        var instance2 = _serviceProvider.GetRequiredService<IInputValidationService>();

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    #endregion

    #region T7.3.7: LayoutService Factory Receives Correct Dependencies

    [Fact]
    public void LayoutService_Factory_ReceivesCorrectDependencies()
    {
        // Act
        var layoutService = _serviceProvider.GetRequiredService<ILayoutService>();

        // Assert - LayoutService should be properly constructed
        // We verify it works by testing that it can perform operations
        layoutService.Should().BeOfType<LayoutService>();
        layoutService.GetAllPresets().Should().NotBeNull();
    }

    [Fact]
    public void UIElementService_ReceivesTheWindowWrapper_SoStuckReadsAreCountedPerApplication()
    {
        // Act - the window wrapper is an optional constructor parameter; without it every walk would
        // share one stuck-read count, and one hung application would block all of them
        var service = _serviceProvider.GetRequiredService<IUIElementService>();
        var windows = typeof(UIElementService)
            .GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service);

        // Assert
        windows.Should().BeSameAs(_serviceProvider.GetRequiredService<INativeWindowWrapper>());
    }

    #endregion

    #region T7.3.8: Service Provider Build No Circular Dependencies

    [Fact]
    public void ServiceProvider_Build_NoCircularDependencies()
    {
        // Act & Assert - Building the service provider should not throw
        // The constructor already built it, so this verifies no exceptions were thrown
        // Additionally, resolve all services to verify complete graph is valid

        Action act = () =>
        {
            // Resolve all services to build complete dependency graph
            _serviceProvider.GetRequiredService<IWindowService>();
            _serviceProvider.GetRequiredService<IMonitorService>();
            _serviceProvider.GetRequiredService<IProcessService>();
            _serviceProvider.GetRequiredService<ILayoutService>();
            _serviceProvider.GetRequiredService<IInputValidationService>();
            _serviceProvider.GetRequiredService<IUIAutomationWrapper>();
            _serviceProvider.GetRequiredService<IBrowserReadingService>();

            // Also resolve tools (which depend on services)
            _serviceProvider.GetRequiredService<WindowFinderTools>();
            _serviceProvider.GetRequiredService<WindowManagerTools>();
            _serviceProvider.GetRequiredService<MonitorInfoTools>();
            _serviceProvider.GetRequiredService<AppLauncherTools>();
            _serviceProvider.GetRequiredService<LayoutPresetTools>();
            _serviceProvider.GetRequiredService<BrowserReadingTools>();
        };

        act.Should().NotThrow();
    }

    #endregion

    #region Additional: Tools Resolve with Correct Service Dependencies

    [Fact]
    public void WindowFinderTools_Resolves_WithDependencies()
    {
        // Act
        var tools = _serviceProvider.GetRequiredService<WindowFinderTools>();

        // Assert
        tools.Should().NotBeNull();
    }

    [Fact]
    public void WindowManagerTools_Resolves_WithDependencies()
    {
        // Act
        var tools = _serviceProvider.GetRequiredService<WindowManagerTools>();

        // Assert
        tools.Should().NotBeNull();
    }

    [Fact]
    public void MonitorInfoTools_Resolves_WithDependencies()
    {
        // Act
        var tools = _serviceProvider.GetRequiredService<MonitorInfoTools>();

        // Assert
        tools.Should().NotBeNull();
    }

    [Fact]
    public void AppLauncherTools_Resolves_WithDependencies()
    {
        // Act
        var tools = _serviceProvider.GetRequiredService<AppLauncherTools>();

        // Assert
        tools.Should().NotBeNull();
    }

    [Fact]
    public void LayoutPresetTools_Resolves_WithDependencies()
    {
        // Act
        var tools = _serviceProvider.GetRequiredService<LayoutPresetTools>();

        // Assert
        tools.Should().NotBeNull();
    }

    [Fact]
    public void BrowserReadingTools_Resolves_WithDependencies()
    {
        // Act
        var tools = _serviceProvider.GetRequiredService<BrowserReadingTools>();

        // Assert
        tools.Should().NotBeNull();
    }

    #endregion

    #region Program.cs Wiring

    [Fact]
    public void AddWindowsAppManagerServices_EveryRegistrationIsASingletonThatResolves()
    {
        // Arrange - only the server's own registrations, not the MCP SDK's
        var registrations = new ServiceCollection()
            .AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(_testLayoutPath))
            .ToList();

        // Act & Assert
        registrations.Should().NotBeEmpty();
        foreach (var registration in registrations)
        {
            registration.Lifetime.Should().Be(ServiceLifetime.Singleton, "{0} is registered as a singleton", registration.ServiceType.Name);
            _serviceProvider.GetService(registration.ServiceType).Should().NotBeNull(
                "{0} must resolve from the server's container", registration.ServiceType.Name);
        }
    }

    [Theory]
    [InlineData(typeof(INativeInputWrapper))]
    [InlineData(typeof(IScreenCaptureWrapper))]
    [InlineData(typeof(IScreenshotService))]
    [InlineData(typeof(IUIElementService))]
    [InlineData(typeof(IInputSimulationService))]
    [InlineData(typeof(ICdpService))]
    [InlineData(typeof(ICdpTransport))]
    [InlineData(typeof(ITcpPortOwnerResolver))]
    [InlineData(typeof(IDebugBrowserReadiness))]
    public void AddWindowsAppManagerServices_RegistersService(Type serviceType)
    {
        _serviceProvider.GetService(serviceType).Should().NotBeNull();
    }

    [Fact]
    public async Task CdpService_ReachesTheBrowserThroughTheRegisteredTransport()
    {
        // Arrange - the server's registrations with the transport replaced, so nothing reaches the network
        var transport = new RecordingCdpTransport();
        var services = BuildServerServices();
        services.AddSingleton<ICdpTransport>(transport);
        using var provider = services.BuildServiceProvider();

        // Act
        var result = await provider.GetRequiredService<ICdpService>().GetPageTargetsAsync(9222, TimeSpan.FromSeconds(1));

        // Assert
        result.ErrorCode.Should().Be(BrowserErrorCode.CdpUnavailable);
        transport.Paths.Should().Equal("/json/list");
    }

    [Fact]
    public async Task LaunchBrowserWithDebug_ChecksReadiness_ThroughTheRegisteredCdpService()
    {
        // Arrange - the server's registrations with the transport and the port owner replaced, so nothing reaches the
        // network or reads the real TCP table
        var transport = new RecordingCdpTransport();
        var services = BuildServerServices();
        services.AddSingleton<ICdpTransport>(transport);
        services.AddSingleton<ITcpPortOwnerResolver>(new FakePortOwnerResolver(null));
        using var provider = services.BuildServiceProvider();
        var processService = provider.GetRequiredService<IProcessService>();
        var readiness = typeof(ProcessService).GetField("_debugBrowserReadiness", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(processService);

        // Act
        var answer = await ((IDebugBrowserReadiness)readiness!).WaitUntilReadyAsync(
            new DebugBrowserEndpoint("chrome", 4321, 13538, Path.GetTempPath()), TimeSpan.FromMilliseconds(100), CancellationToken.None);

        // Assert
        readiness.Should().BeSameAs(provider.GetRequiredService<IDebugBrowserReadiness>()).And.BeOfType<DebugBrowserReadinessProbe>();
        answer.Ready.Should().BeFalse();
        transport.Paths.Should().NotBeEmpty().And.OnlyContain(path => path == "/json/version");
    }

    [Fact]
    public async Task DisposingTheServer_DisposesTheCdpTransport()
    {
        // Arrange - a port this test listens on, so a transport that was not disposed reaches only the test, never a browser
        var provider = BuildServerServices().BuildServiceProvider();
        var transport = provider.GetRequiredService<ICdpTransport>();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

            // Act
            provider.Dispose();
            var result = await transport.GetJsonAsync(port, "/json/list", TimeSpan.FromSeconds(1), CancellationToken.None);

            // Assert - its HttpClient is gone, so nothing was sent
            result.Error.Should().Contain(nameof(ObjectDisposedException));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Answers every /json request with CdpUnavailable and records its path.</summary>
    private sealed class RecordingCdpTransport : ICdpTransport
    {
        public List<string> Paths { get; } = new();

        public Task<CdpResult<System.Text.Json.JsonDocument>> GetJsonAsync(
            int port, string path, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult(CdpResult<System.Text.Json.JsonDocument>.Fail(BrowserErrorCode.CdpUnavailable, "recorded"));
        }

        public Task<CdpResult<ICdpConnection>> ConnectAsync(
            Uri webSocketUrl, int expectedPort, string label, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No connection is expected.");
    }

    /// <summary>
    /// Captures keep visual signatures only while wait_for_visual is served (roadmap M6.4, owner decision D9), so a server
    /// without the visual toolset computes and holds none; without a selection (ToolsetSelection.All) every tool is served.
    /// </summary>
    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("+visual", null, null, true)]
    [InlineData("all", null, null, true)]
    [InlineData("+visual", "wait_for_visual", null, false)]
    [InlineData("+visual", null, "1", true)]
    [InlineData(null, null, "1", false)]
    public void TheCaptureRegistry_KeepsSignatures_OnlyWhileWaitForVisualIsServed(string? toolsets, string? excluded, string? readOnly, bool keeps)
    {
        // Arrange
        var selection = ToolsetSelection.FromEnvironment(name => name switch
        {
            ToolsetSelection.ToolsetsVariable => toolsets,
            ToolsetSelection.ExcludeToolsVariable => excluded,
            ToolsetSelection.ReadOnlyVariable => readOnly,
            _ => null
        }, Toolsets.Server);
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(_testLayoutPath),
            () => AuditLogOptions.ForFolder(Path.Combine(_testLayoutPath, "audit")));
        services.AddMcpServer().AddWindowsAppManagerTools(selection);
        using var provider = services.BuildServiceProvider();

        // Act & Assert
        provider.GetRequiredService<ICaptureRegistry>().KeepsSignatures.Should().Be(keeps);
    }

    [Fact]
    public void TheCaptureRegistry_KeepsSignatures_WithoutASelection_AsEveryToolIsServed()
    {
        _serviceProvider.GetRequiredService<ICaptureRegistry>().KeepsSignatures.Should().BeTrue();
        _serviceProvider.GetRequiredService<IVisualChangeService>().Should().BeOfType<VisualChangeService>();
    }

    [Fact]
    public void AddWindowsAppManagerServices_RegistersEveryToolClass()
    {
        // Arrange
        var toolTypes = typeof(ServiceRegistration).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .ToList();

        // Assert
        toolTypes.Should().NotBeEmpty();
        foreach (var toolType in toolTypes)
        {
            _serviceProvider.GetService(toolType).Should().NotBeNull("{0} must be registered", toolType.Name);
        }
    }

    [Fact]
    public void AddWindowsAppManagerTools_RegistersEveryTool()
    {
        // Act
        var options = _serviceProvider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        // Assert
        options.ToolCollection.Should().NotBeNull();
        options.ToolCollection!.Select(t => t.ProtocolTool.Name)
            .Should().BeEquivalentTo(ServerToolCatalog.Tools.Keys);
    }

    [Fact]
    public void AddWindowsAppManagerTools_RegistersTheToolErrorFilter()
    {
        // The errorCode contract in docs/TOOLS.md (InvalidArguments, ToolError, PresetNotFound, ...)
        // depends on this filter being in the server's pipeline.
        var filter = typeof(ToolErrorReporting).GetMethod(nameof(ToolErrorReporting.Filter));

        // Act
        var options = _serviceProvider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        // Assert - added right after the audit log's: the SDK runs the first filter added as the outermost
        // (ServerFilterPipelineTests), so it also logs the input gate filter's wait and the calls that filter refuses
        // while input is paused
        options.Filters.Request.CallToolFilters.Should().HaveCount(4);
        options.Filters.Request.CallToolFilters[1].Method.Should().BeSameAs(filter);
    }

    [Fact]
    public void AddWindowsAppManagerTools_RegistersTheUndeclaredArgumentFilter_InsideTheErrorFilter_OutsideTheGate()
    {
        // M3 review: inside the audit log and the error filter, so the refusal is audited and reported as a tool error;
        // outside the input gate filter, so it never waits for the gate or a pause
        var filters = _serviceProvider.GetRequiredService<IOptions<McpServerOptions>>().Value.Filters.Request.CallToolFilters;

        filters[2].Method.Should().BeSameAs(typeof(UndeclaredArgumentToolFilter).GetMethod(nameof(UndeclaredArgumentToolFilter.Filter)));
        filters[3].Method.Should().BeSameAs(typeof(InputGateToolFilter).GetMethod(nameof(InputGateToolFilter.Filter)));
    }

    [Fact]
    public void AddWindowsAppManagerTools_RegistersTheAuditLogFilter_AsTheOutermost()
    {
        // Outermost (roadmap M2.17): it records the result the agent gets, after the error filter turned an exception
        // into an error result, and a call the input gate filter refused while input was paused
        var filter = typeof(AuditLogToolFilter).GetMethod(nameof(AuditLogToolFilter.Filter));

        var options = _serviceProvider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        options.Filters.Request.CallToolFilters[0].Method.Should().BeSameAs(filter);
    }

    [Fact]
    public void AddWindowsAppManagerServices_ByDefault_WritesTheAuditLogWhereFromEnvironmentSays()
    {
        // Arrange - Program.cs's default. Creating the audit log touches no file, and only its folder is read here, so
        // nothing is created under %LOCALAPPDATA%.
        using var provider = new ServiceCollection().AddWindowsAppManagerServices().BuildServiceProvider();

        // Act
        var folder = provider.GetRequiredService<IAuditLog>().Folder;

        // Assert
        folder.Should().Be(AuditLogOptions.FromEnvironment().Folder);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AuditLogOptions.FolderEnvironmentVariable)))
        {
            folder.Should().Be(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LayoutStorageOptions.AppDataFolderName, "audit"));
        }
    }

    [Fact]
    public void AddWindowsAppManagerServices_WithAuditLog_UsesIt()
    {
        _serviceProvider.GetRequiredService<IAuditLog>().Folder.Should().Be(Path.Combine(_testLayoutPath, "audit"));
        _serviceProvider.GetRequiredService<IServerInfoService>().Should().NotBeNull();
    }

    [Fact]
    public void AddWindowsAppManagerTools_RegistersTheInputGateFilter()
    {
        // Window-changing tools must not run between an input call's guard check and its input
        var filter = typeof(InputGateToolFilter).GetMethod(nameof(InputGateToolFilter.Filter))!;

        var options = _serviceProvider.GetRequiredService<IOptions<McpServerOptions>>().Value;

        options.Filters.Request.CallToolFilters.Select(f => f.Method).Should().Contain(filter);
    }

    [Fact]
    public void AddWindowsAppManagerServices_ByDefault_StoresLayoutsWhereFromEnvironmentSays()
    {
        // Arrange - Program.cs's default storage. Constructing LayoutService does not touch the file
        // system, and only the path is read here, so nothing is created under %LOCALAPPDATA%.
        using var provider = new ServiceCollection().AddWindowsAppManagerServices().BuildServiceProvider();
        var expected = LayoutStorageOptions.FromEnvironment().LayoutsPath;

        // Act
        var layoutsPath = provider.GetRequiredService<ILayoutService>().LayoutsPath;

        // Assert
        layoutsPath.Should().Be(expected);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LayoutStorageOptions.LayoutsPathEnvironmentVariable)))
        {
            layoutsPath.Should().Be(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LayoutStorageOptions.AppDataFolderName,
                "layouts"));
        }
    }

    [Fact]
    public void AddWindowsAppManagerServices_WithLayoutStorage_UsesIt()
    {
        // Act
        var layoutsPath = _serviceProvider.GetRequiredService<ILayoutService>().LayoutsPath;

        // Assert
        layoutsPath.Should().Be(_testLayoutPath);
    }

    #endregion

    #region Browser Reading Services Singleton Tests

    [Fact]
    public void IUIAutomationWrapper_Singleton_SameInstance()
    {
        // Act
        var instance1 = _serviceProvider.GetRequiredService<IUIAutomationWrapper>();
        var instance2 = _serviceProvider.GetRequiredService<IUIAutomationWrapper>();

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    [Fact]
    public void IBrowserReadingService_Singleton_SameInstance()
    {
        // Act
        var instance1 = _serviceProvider.GetRequiredService<IBrowserReadingService>();
        var instance2 = _serviceProvider.GetRequiredService<IBrowserReadingService>();

        // Assert
        instance1.Should().BeSameAs(instance2);
    }

    #endregion
}
