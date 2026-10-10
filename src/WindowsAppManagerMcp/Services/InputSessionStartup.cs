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
/// Before the hotkey starts, the session's pause is shared with every server on the desktop
/// (<see cref="INativeHotkeyWrapper.OpenDesktopPause"/>): each MCP client session runs its own server, only one
/// of them can register the chord, and its press must pause them all.
/// Nothing is registered, opened or started when the service is created; only <see cref="StartAsync"/> does, so
/// resolving it (as the DI tests do) touches nothing.
/// </remarks>
public sealed class InputSessionStartup : IHostedService, IDisposable
{
    private readonly IInputSession _session;
    private readonly IPauseHotkey _hotkey;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly ILogger<InputSessionStartup>? _logger;
    private readonly INativeHotkeyWrapper? _desktop;
    private CancellationTokenRegistration _stopping;

    /// <param name="session">The input session.</param>
    /// <param name="hotkey">The pause hotkey.</param>
    /// <param name="lifetime">The host's lifetime, to release held input when the application stops.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="desktop">Opens the pause the servers on the desktop share; without it the pause stays this server's own.</param>
    public InputSessionStartup(
        IInputSession session,
        IPauseHotkey hotkey,
        IHostApplicationLifetime? lifetime = null,
        ILogger<InputSessionStartup>? logger = null,
        INativeHotkeyWrapper? desktop = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _lifetime = lifetime;
        _logger = logger;
        _desktop = desktop;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_lifetime != null)
        {
            _stopping = _lifetime.ApplicationStopping.Register(() => Release("the server is stopping"));
        }

        SharePause();
        _hotkey.Start(_session.TogglePauseByHotkey);
        var status = _hotkey.Status;
        switch (status.State)
        {
            case PauseHotkeyState.Registered:
                _logger?.LogInformation("Pause hotkey {Chord} (from {Source}) is registered: pressing it pauses and resumes the input of every server on this desktop. {Note}",
                    status.Chord, status.Source, status.Error ?? string.Empty);
                break;
            case PauseHotkeyState.Disabled:
                _logger?.LogWarning("This server's pause hotkey is turned off ({Variable}=none): only another server's hotkey can pause its input",
                    PauseHotkeyOptions.EnvironmentVariable);
                break;
            case PauseHotkeyState.OtherServer when _session.SharesDesktopPause:
                _logger?.LogInformation("Pause hotkey {Chord}: {Note}", status.Chord, status.Error);
                break;
            case PauseHotkeyState.OtherServer:
                _logger?.LogWarning("Another server has the pause hotkey {Chord}, and this server could not share its pause, so pressing it does not pause this server",
                    status.Chord);
                break;
            default:
                _logger?.LogWarning("The pause hotkey {Chord} is not available: {Error}", status.Chord, status.Error);
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>Shares the session's pause with the other servers on the desktop.</summary>
    private void SharePause()
    {
        if (_desktop == null)
        {
            return;
        }

        try
        {
            _session.ShareDesktopPause(_desktop.OpenDesktopPause());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // For example: a server running as administrator created it, and this one is not
            _logger?.LogWarning(ex, "The pause cannot be shared with the other servers on this desktop: another server's pause hotkey " +
                                    "does not pause this server");
        }
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
