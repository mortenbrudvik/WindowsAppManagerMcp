using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// The requirement of the scenarios that name the left and the right monitor (roadmap M6.7): a machine with at least two
/// monitors. Checked only after the tier's opt-in passed.
/// </summary>
public sealed class TwoMonitors : IDesktopTestRequirement
{
    private const int SmCMonitors = 80;

    /// <inheritdoc />
    public string? MissingReason => GetSystemMetrics(SmCMonitors) >= 2
        ? null
        : "This machine has one monitor, and the scenario places the fixture on the left and the right one. Connect a second monitor.";

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
