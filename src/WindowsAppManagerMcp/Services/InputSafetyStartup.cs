using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Logs the safety settings in force once, when the server starts: the target policy (WAM_DENY_PROCESSES,
/// WAM_ALLOW_PROCESSES) and the destructive chords (WAM_DESTRUCTIVE_CHORDS), and as a warning the chord
/// entries that were ignored. A mistyped setting is otherwise easy to miss, and these settings protect the
/// user's password managers and terminal.
/// </summary>
public sealed class InputSafetyStartup(ITargetPolicy policy, InputGuardOptions options, ILogger<InputSafetyStartup>? logger = null)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = policy.Settings;
        logger?.LogInformation(
            "Target policy: denied processes {Denied} (from {DenySource}); allowed processes {Allowed}. " +
            "Destructive chords that need windowHandle: {Chords} (from {ChordSource})",
            settings.DeniedProcesses.Count == 0 ? "none" : string.Join(", ", settings.DeniedProcesses),
            settings.DenyListSource,
            settings.AllowedProcesses == null ? $"all that are not denied ({TargetPolicySettings.AllowVariable} not set)" : string.Join(", ", settings.AllowedProcesses),
            options.DestructiveChords.Count == 0 ? "none" : string.Join(" ", options.DestructiveChords.Select(c => c.Spec)),
            options.DestructiveChordSource);

        if (options.InvalidChordEntries.Count > 0)
        {
            logger?.LogWarning(
                "{Variable} entries that are not one key chord in send_keys syntax (separate chords with spaces) are ignored, " +
                "and the default chords stay in force: {Entries}",
                InputGuardOptions.DestructiveChordsVariable, string.Join(" ", options.InvalidChordEntries));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
