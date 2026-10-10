using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The target policy as configured in the server's environment (<see cref="TargetPolicySettings"/>):
/// the deny list wins, then the allow list, if there is one, decides.
/// </summary>
public sealed class TargetPolicy : ITargetPolicy
{
    private readonly HashSet<string> _denied;
    private readonly HashSet<string>? _allowed;

    public TargetPolicy(TargetPolicySettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _denied = new HashSet<string>(settings.DeniedProcesses.Select(TargetPolicySettings.NormalizeProcessName),
            StringComparer.OrdinalIgnoreCase);
        _allowed = settings.AllowedProcesses == null
            ? null
            : new HashSet<string>(settings.AllowedProcesses.Select(TargetPolicySettings.NormalizeProcessName),
                StringComparer.OrdinalIgnoreCase);
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
            var source = Settings.DenyListSource == TargetPolicySettings.DefaultSource
                ? $"the server's default deny list (set {TargetPolicySettings.DenyVariable} to change it)"
                : $"the server's deny list ({TargetPolicySettings.DenyVariable})";
            return TargetPolicyVerdict.Deny(denied, $"'{denied}' is on {source}");
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
}
