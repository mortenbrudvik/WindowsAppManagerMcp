using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The target policy as configured in the server's environment (<see cref="TargetPolicySettings"/>):
/// the deny list wins, then the allow list, if there is one, decides. While the deny list includes the
/// default list, it also refuses the security prompt that a shared process shows (<see cref="SecurityPrompts"/>).
/// </summary>
public sealed class TargetPolicy : ITargetPolicy
{
    private readonly HashSet<string> _denied;
    private readonly HashSet<string>? _allowed;
    private readonly INativeWindowWrapper? _windows;
    private readonly bool _deniesSecurityPrompts;

    /// <param name="settings">The deny and allow lists.</param>
    /// <param name="windows">The Win32 window reads that recognize a security prompt by its structure
    /// (<see cref="EvaluateWindow"/>); null to recognize none.</param>
    public TargetPolicy(TargetPolicySettings settings, INativeWindowWrapper? windows = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _denied = new HashSet<string>(settings.DeniedProcesses.Select(TargetPolicySettings.NormalizeProcessName),
            StringComparer.OrdinalIgnoreCase);
        _allowed = settings.AllowedProcesses == null
            ? null
            : new HashSet<string>(settings.AllowedProcesses.Select(TargetPolicySettings.NormalizeProcessName),
                StringComparer.OrdinalIgnoreCase);
        _windows = windows;

        // The prompt goes with the default list's security prompts (consent, smartscreen): a list that drops them drops it
        _deniesSecurityPrompts = TargetPolicySettings.DefaultDeniedProcesses.All(_denied.Contains);
    }

    public TargetPolicySettings Settings { get; }

    public TargetPolicyVerdict Evaluate(IReadOnlyCollection<string?> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        var names = processNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => TargetPolicySettings.NormalizeProcessName(name!))
            .Where(name => name.Length > 0)
            .ToList();

        // The deny list wins over the allow list
        if (names.FirstOrDefault(_denied.Contains) is { } denied)
        {
            return TargetPolicyVerdict.Deny(denied, $"'{denied}' is on {DenyListName}");
        }

        if (_allowed != null && !names.Any(_allowed.Contains))
        {
            var name = names.FirstOrDefault();
            return TargetPolicyVerdict.Deny(name,
                name == null
                    ? $"its process name cannot be read, and the server has an allow list ({TargetPolicySettings.AllowVariable})"
                    : $"'{name}' is not on the server's allow list ({TargetPolicySettings.AllowVariable})");
        }

        return TargetPolicyVerdict.Allow;
    }

    public TargetPolicyVerdict EvaluateWindow(nint window)
    {
        if (_windows == null || !_deniesSecurityPrompts || !SecurityPrompts.IsOpenFileSecurityWarning(_windows, window))
        {
            return TargetPolicyVerdict.Allow;
        }

        var processName = ProcessNameOf(window);
        return TargetPolicyVerdict.Deny(processName,
            $"it is Windows' \"{SecurityPrompts.OpenFileSecurityWarning}\" prompt" +
            (processName == null ? string.Empty : $" (shown by '{processName}')") +
            $", which asks whether to open or run a downloaded file: {DenyListName} covers it whichever process shows it, " +
            "and only the user answers it");
    }

    private string DenyListName => Settings.DenyListSource == TargetPolicySettings.DefaultSource
        ? $"the server's default deny list (set {TargetPolicySettings.DenyVariable} to change it)"
        : $"the server's deny list ({TargetPolicySettings.DenyVariable})";

    /// <summary>The name of the window's process, for the verdict; null when it cannot be read.</summary>
    private string? ProcessNameOf(nint window)
    {
        try
        {
            var processId = _windows!.GetWindowThreadProcessId(window);
            return processId == 0 ? null : _windows.GetProcessName(processId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
