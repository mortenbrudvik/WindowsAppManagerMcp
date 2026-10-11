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
    public void TheElementRegistryAndSearch_AreSingletons_BuiltWithTheProductionConstructor()
    {
        // Resolving constructs them only: neither UI Automation engine starts a client before its first read
        var registry = _serviceProvider.GetRequiredService<IElementRegistry>();
        var search = _serviceProvider.GetRequiredService<IElementSearchService>();

        registry.Should().BeOfType<ElementRegistry>().And.BeSameAs(_serviceProvider.GetRequiredService<IElementRegistry>());
        search.Should().BeOfType<ElementSearchService>().And.BeSameAs(_serviceProvider.GetRequiredService<IElementSearchService>());
        registry.Count.Should().Be(0);

        // The production constructor numbers windows from a random start in eight million (the test-only one takes it)
        var production = typeof(ElementRegistry).GetConstructors()
            .Single(c => c.GetParameters().Length == 3);
        production.GetParameters().Select(p => p.ParameterType).Should()
            .Equal(typeof(IUiaTree), typeof(IUiaWorker), typeof(INativeWindowWrapper));
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
    public void AddWindowsAppManagerServices_RegistersService(Type serviceType)
    {
        _serviceProvider.GetService(serviceType).Should().NotBeNull();
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
        options.Filters.Request.CallToolFilters.Should().HaveCount(3);
        options.Filters.Request.CallToolFilters[1].Method.Should().BeSameAs(filter);
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
