using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// The safety probes are registered by <see cref="ServiceRegistration"/>, the same method
/// Program.cs uses, and the host guard is created when the server starts. Resolving the services
/// only reads (the process list, the console and foreground windows); nothing is changed. The window
/// wrapper is the read-only one, as in every test that reads the real desktop, so a future change to
/// HostGuard's constructor that changes a window fails instead of acting.
/// </summary>
public sealed class SecurityServiceRegistrationTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(
            Path.Combine(Path.GetTempPath(), $"SecurityRegistrationTests_{Guid.NewGuid()}")))
        .AddSingleton<INativeWindowWrapper>(new ReadOnlyNativeWindowWrapper())
        .BuildServiceProvider();

    public void Dispose() => _provider.Dispose();

    [Fact]
    public void TheHostGuardReadsThroughTheReadOnlyWindowWrapper()
    {
        // The last registration wins, so the guard built below is given the read-only wrapper
        _provider.GetRequiredService<INativeWindowWrapper>().Should().BeOfType<ReadOnlyNativeWindowWrapper>();
    }

    [Theory]
    [InlineData(typeof(INativeSecurityWrapper), typeof(NativeSecurityWrapper))]
    [InlineData(typeof(INativeProcessWrapper), typeof(NativeProcessWrapper))]
    [InlineData(typeof(IProcessSecurityProbe), typeof(ProcessSecurityProbe))]
    [InlineData(typeof(IHostGuard), typeof(HostGuard))]
    public void AddWindowsAppManagerServices_RegistersTheSafetyServicesAsSingletons(Type serviceType, Type implementation)
    {
        // Act
        var first = _provider.GetRequiredService(serviceType);
        var second = _provider.GetRequiredService(serviceType);

        // Assert
        first.Should().BeOfType(implementation);
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void AddWindowsAppManagerServices_CreatesTheHostGuardWhenTheServerStarts()
    {
        // Act - the host resolves its hosted services when it starts, before any tool call
        var startup = _provider.GetServices<IHostedService>().OfType<HostGuardStartup>().Should().ContainSingle().Subject;

        // Assert
        startup.HostGuard.Should().BeSameAs(_provider.GetRequiredService<IHostGuard>());
    }

    [Fact]
    public void KillProcess_AsksTheHostGuard()
    {
        // The host guard parameter is optional for tests, so check that the container fills it in
        var processService = _provider.GetRequiredService<IProcessService>();

        processService.GetType().GetField("_hostGuard", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(processService)
            .Should().BeSameAs(_provider.GetRequiredService<IHostGuard>());
    }
}
