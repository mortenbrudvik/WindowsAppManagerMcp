using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Starts the user's pause hotkey when the server starts, and releases any input the server still holds when
/// it stops (roadmap M1.10). The release runs on <see cref="IHostApplicationLifetime.ApplicationStopping"/>,
/// which also fires when the MCP client closes stdin: the SDK's single-session host service stops the
/// application when its session ends. It runs again in <see cref="StopAsync"/>, after the hotkey is freed.
/// </summary>
/// <remarks>
/// Nothing is registered or started when the service is created; only <see cref="StartAsync"/> does, so
/// resolving it (as the DI tests do) touches nothing.
/// </remarks>
public sealed class InputSessionStartup : IHostedService, IDisposable
{
    private readonly IInputSession _session;
    private readonly IPauseHotkey _hotkey;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger<InputSessionStartup>? _logger;
    private CancellationTokenRegistration _stopping;

    public InputSessionStartup(
        IInputSession session,
        IPauseHotkey hotkey,
        IHostApplicationLifetime? lifetime = null,
        ILogger<InputSessionStartup>? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lifetime != null)
        {
            _stopping = _lifetime.ApplicationStopping.Register(() => Release("the server is stopping"));
        }

        _hotkey.Start(_session.TogglePauseByHotkey);
        var status = _hotkey.Status;
        switch (status.State)
        {
            case PauseHotkeyState.Registered:
                _logger?.LogInformation("Pause hotkey {Chord} (from {Source}) is registered: pressing it pauses and resumes the server's input. {Note}",
                    status.Chord, status.Source, status.Error ?? string.Empty);
                break;
            case PauseHotkeyState.Disabled:
                _logger?.LogWarning("The pause hotkey is turned off ({Variable}=none): the user cannot pause the server's input",
                    PauseHotkeyOptions.EnvironmentVariable);
                break;
            default:
                _logger?.LogWarning("The pause hotkey {Chord} is not available: {Error}", status.Chord, status.Error);
                break;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _hotkey.Stop();
        Release("the server stopped");
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private void Release(string reason)
    {
        var outcome = _session.ReleaseHeldInput(reason);
        if (outcome.StillHeld.Count > 0)
        {
            _logger?.LogWarning("Input the server held could not be released when it stopped: {Keys}", string.Join(", ", outcome.StillHeld));
        }
    }
}
