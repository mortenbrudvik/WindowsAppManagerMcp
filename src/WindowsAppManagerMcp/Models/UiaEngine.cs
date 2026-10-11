namespace WindowsAppManagerMcp.Models;

/// <summary>The UI Automation engine behind the server's UI Automation reads (IUiaTree, roadmap M3.2).</summary>
public enum UiaEngine
{
    /// <summary>
    /// The COM client (IUIAutomation, UI Automation 3): per-call connection and transaction timeouts, raw control
    /// type ids, LegacyIAccessible and one-call cached reads.
    /// </summary>
    Uia3 = 1,

    /// <summary>The managed client (System.Windows.Automation): no per-call timeouts and no newer control type ids.</summary>
    Uia2 = 2
}
