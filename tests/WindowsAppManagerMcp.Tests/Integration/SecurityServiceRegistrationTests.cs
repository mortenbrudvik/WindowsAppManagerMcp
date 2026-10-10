using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// The safety probes are registered by <see cref="ServiceRegistration"/>, the same method
/// Program.cs uses, and the host guard is created when the server starts. Resolving the services
/// only reads (the process list, the console and foreground windows); nothing is changed.
/// </summary>
public sealed class SecurityServiceRegistrationTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(
            Path.Combine(Path.GetTempPath(), $"SecurityRegistrationTests_{Guid.NewGuid()}")))
        .BuildServiceProvider();

    public void Dispose() => _provider.Dispose();

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
}
