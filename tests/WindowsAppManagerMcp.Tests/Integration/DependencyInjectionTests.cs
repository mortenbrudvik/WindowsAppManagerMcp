using Microsoft.Extensions.DependencyInjection;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// Integration tests for DI container configuration.
/// Tests T7.3 from BACKLOG-TECHNICAL.md.
///
/// These tests verify that the DI container is configured correctly
/// with proper service registrations and lifetimes.
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

        var services = new ServiceCollection();

        // Register services exactly as in Program.cs
        services.AddSingleton<INativeWindowWrapper, NativeWindowWrapper>();
        services.AddSingleton<IMonitorService, MonitorService>();
        services.AddSingleton<IWindowService, WindowService>();
        services.AddSingleton<IInputValidationService, InputValidationService>();
        services.AddSingleton<IBrowserDetectionService, BrowserDetectionService>();
        services.AddSingleton<IProcessService, ProcessService>();
        services.AddSingleton<ILayoutService>(sp =>
        {
            var windowService = sp.GetRequiredService<IWindowService>();
            var monitorService = sp.GetRequiredService<IMonitorService>();
            var processService = sp.GetRequiredService<IProcessService>();
            return new LayoutService(windowService, monitorService, processService, _testLayoutPath);
        });

        // Register MCP tools
        services.AddSingleton<WindowFinderTools>();
        services.AddSingleton<WindowManagerTools>();
        services.AddSingleton<MonitorInfoTools>();
        services.AddSingleton<AppLauncherTools>();
        services.AddSingleton<LayoutPresetTools>();

        _serviceProvider = services.BuildServiceProvider();
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
        var windowService = _serviceProvider.GetService<IWindowService>();
        var monitorService = _serviceProvider.GetService<IMonitorService>();
        var processService = _serviceProvider.GetService<IProcessService>();
        var layoutService = _serviceProvider.GetService<ILayoutService>();
        var inputValidationService = _serviceProvider.GetService<IInputValidationService>();

        nativeWrapper.Should().NotBeNull();
        windowService.Should().NotBeNull();
        monitorService.Should().NotBeNull();
        processService.Should().NotBeNull();
        layoutService.Should().NotBeNull();
        inputValidationService.Should().NotBeNull();
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

            // Also resolve tools (which depend on services)
            _serviceProvider.GetRequiredService<WindowFinderTools>();
            _serviceProvider.GetRequiredService<WindowManagerTools>();
            _serviceProvider.GetRequiredService<MonitorInfoTools>();
            _serviceProvider.GetRequiredService<AppLauncherTools>();
            _serviceProvider.GetRequiredService<LayoutPresetTools>();
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

    #endregion
}
