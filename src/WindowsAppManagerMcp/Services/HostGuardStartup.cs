using Microsoft.Extensions.Hosting;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Creates the <see cref="IHostGuard"/> singleton when the server starts. The guard records the
/// foreground window when it is created; created lazily, at the first tool call, it could record
/// whatever window the agent or the user had brought to the front by then.
/// </summary>
public sealed class HostGuardStartup(IHostGuard hostGuard) : IHostedService
{
    /// <summary>The guard, created and recorded by the time the host starts this service.</summary>
    public IHostGuard HostGuard { get; } = hostGuard;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
