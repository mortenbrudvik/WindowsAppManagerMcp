namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Finds the DevTools port a browser window's own process listens on (roadmap M5.3), so the reading tools need no
/// cdpPort. It reads the process's command line and, for port 0, its profile's DevToolsActivePort, and checks the
/// listener on 127.0.0.1; it never scans ports and connects to nothing.
/// </summary>
public interface IDebugPortLocator
{
    /// <summary>The DevTools port of process <paramref name="processId"/>, or why there is none to use.</summary>
    DebugPortLookup Find(int processId);
}

/// <summary>What <see cref="IDebugPortLocator.Find"/> found.</summary>
public enum DebugPortLookupStatus
{
    /// <summary>The process listens on the port its command line or port file names.</summary>
    Found,

    /// <summary>The command line has no --remote-debugging-port: a browser started without a debug port.</summary>
    NoDebugPort,

    /// <summary>The command line could not be read (for example an elevated browser), so nothing is known.</summary>
    CommandLineUnreadable,

    /// <summary>The switch names no port, or port 0 without a profile whose DevToolsActivePort names one.</summary>
    PortNotKnown,

    /// <summary>No listener of the process was found on the port (or the listener table could not be read).</summary>
    NotListening,

    /// <summary>Another process listens on the port.</summary>
    HeldByOtherProcess
}

/// <summary>
/// The DevTools port found for a process, or why none is used.
/// </summary>
/// <param name="Port">The port, proven to be the process's own listener on 127.0.0.1; null otherwise.</param>
/// <param name="Detail">A sentence that says what was found, for an error or a fallback's cdpError.</param>
public sealed record DebugPortLookup(DebugPortLookupStatus Status, int? Port, string Detail)
{
    /// <summary>
    /// Whether a read that falls back to UI Automation says why in cdpError: the browser was started with a debug port,
    /// but none could be proven its own. A browser started without one reads through UI Automation as before, silently.
    /// </summary>
    public bool WorthReporting =>
        Status is DebugPortLookupStatus.PortNotKnown or DebugPortLookupStatus.NotListening or DebugPortLookupStatus.HeldByOtherProcess;
}
