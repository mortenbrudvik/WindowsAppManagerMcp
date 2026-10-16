using System.Security.Cryptography;
using System.Text;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Derives a monitor's identity from the display configuration's targets (roadmap M6.7): a short stable id, the EDID
/// name and whether it is a built-in panel. Pure; MonitorService reads the targets at every snapshot refresh.
/// </summary>
/// <remarks>
/// The GDI device name (\\.\DISPLAYn) is only the join key: it renumbers on docking and on driver or adapter changes. The
/// id is hashed from the monitor's device path (DISPLAYCONFIG_TARGET_DEVICE_NAME.monitorDevicePath), which stays the same
/// for a monitor on the same port. It is per monitor and port, not per panel: the same monitor on another port or dock gets
/// a new id, and two identical monitors on two ports get two ids.
/// </remarks>
public static class MonitorIdentity
{
    /// <summary>What every monitor id starts with, so an id never reads as an index or a device name.</summary>
    public const string IdPrefix = "m-";

    /// <summary>The separator of the friendly names of cloned monitors.</summary>
    public const string CloneNameSeparator = " + ";

    /// <summary>The id of a monitor with this device path; null when there is no path.</summary>
    public static string? IdOf(string? devicePath) => IdOf([devicePath]);

    /// <summary>
    /// The id of a monitor with these device paths: "m-" plus the first 8 hex digits of the SHA-256 of the upper-cased
    /// paths, sorted and joined with line feeds (one path in the usual case; several in clone mode, where one monitor shows
    /// on several targets). Missing and repeated paths are skipped. Null when no path is left.
    /// </summary>
    public static string? IdOf(IEnumerable<string?> devicePaths)
    {
        var paths = devicePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', paths)));
        return IdPrefix + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    /// <summary>
    /// Whether a connector is a built-in panel's: internal, LVDS, embedded DisplayPort or embedded UDI. LVDS is the panel
    /// link of older laptops and all-in-ones, whose drivers report it for the built-in screen rather than internal; PCs
    /// have no external LVDS port.
    /// </summary>
    public static bool IsInternalTechnology(DisplayOutputTechnology technology) =>
        technology is DisplayOutputTechnology.Internal
            or DisplayOutputTechnology.Lvds
            or DisplayOutputTechnology.DisplayPortEmbedded
            or DisplayOutputTechnology.UdiEmbedded;

    /// <summary>
    /// The monitor with its identity from the targets whose GDI source is the monitor's device name (compared ignoring
    /// case). A monitor no target names is returned unchanged, with its identity unknown (null). When any of its targets
    /// was not read (<see cref="DisplayTarget.TargetRead"/>), its id and name are unknown too, so a cloned monitor never gets
    /// an id hashed from only some of its paths; whether it is internal still follows the connectors of its paths.
    /// </summary>
    public static MonitorInfo Identify(MonitorInfo monitor, IReadOnlyList<DisplayTarget> targets)
    {
        var own = targets.Where(t => IsSource(t, monitor)).ToList();
        if (own.Count == 0)
        {
            return monitor;
        }

        var isInternal = own.Any(t => IsInternalTechnology(t.OutputTechnology));
        if (own.Any(t => !t.TargetRead))
        {
            return monitor with { IsInternal = isInternal };
        }

        var names = own
            .Select(t => t.FriendlyName?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return monitor with
        {
            Id = IdOf(own.Select(t => t.DevicePath)),
            FriendlyName = names.Count == 0 ? null : string.Join(CloneNameSeparator, names),
            IsInternal = isInternal
        };
    }

    private static bool IsSource(DisplayTarget target, MonitorInfo monitor) =>
        string.Equals(target.GdiDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase);
}
