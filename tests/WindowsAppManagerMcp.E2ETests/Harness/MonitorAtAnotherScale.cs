using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// The requirement of the scenarios that open the fixture on a monitor whose scale differs from the primary's (roadmap
/// M3.F30): a machine with at least one such monitor. Checked only after the tier's opt-in passed.
/// </summary>
public sealed class MonitorAtAnotherScale : IDesktopTestRequirement
{
    /// <inheritdoc />
    public string? MissingReason => MissingReasonFor(PrimaryDpi, MonitorDpis());

    /// <summary>
    /// The system DPI, which is the primary monitor's at sign-in. The test host is made per-monitor DPI aware first, as every
    /// E2E session makes it: the requirement is checked before any session starts, and a DPI-unaware process reads 96 for
    /// every monitor and for the system.
    /// </summary>
    public static uint PrimaryDpi
    {
        get
        {
            E2EProcess.EnsureServerDpiAwareness();
            return GetDpiForSystem();
        }
    }

    /// <summary>Why the scenario cannot run, given the primary's DPI and every monitor's, or null when one differs.</summary>
    public static string? MissingReasonFor(uint primaryDpi, IEnumerable<uint> monitorDpis) =>
        monitorDpis.Any(dpi => dpi != 0 && dpi != primaryDpi)
            ? null
            : $"No monitor of this machine has a scale other than the primary's ({primaryDpi * 100 / 96}%), and the scenario " +
              "opens the fixture on such a monitor. Connect a second monitor and give it another scale in Settings, Display.";

    private static List<uint> MonitorDpis()
    {
        var dpis = new List<uint>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            dpis.Add(GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : 0);
            return true;
        }, 0);
        return dpis;
    }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
