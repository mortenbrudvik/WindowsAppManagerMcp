using System.Windows.Automation;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Makes the first UI Automation call of the process on one thread at a time. The managed UI
/// Automation client fails its first call in a process with E_FAIL (a COMException) when other
/// threads make their first call at the same moment; RootElement, FromHandle and FocusedElement all
/// do this. Once one call has returned, calls from any number of threads work.
/// </summary>
/// <remarks>
/// <para>Every UI Automation read runs on its own worker thread (UiaWorker), and MCP clients send
/// tool calls in parallel, so without this gate the first get_ui_elements, get_focused_element,
/// get_element_at_point or browser reads of a session could fail with AutomationUnavailable, which
/// reads as permanent.</para>
/// <para>Every adapter calls <see cref="EnsureStarted"/> before its first UI Automation call. After
/// the first success it costs one volatile read.</para>
/// </remarks>
public sealed class UiaClientStartup
{
    private readonly Action _firstCall;
    private readonly object _gate = new();
    private volatile bool _started;

    /// <summary>
    /// Creates a gate around <paramref name="firstCall"/>, the call that starts the client. Tests pass
    /// a fake; production code uses <see cref="Shared"/>.
    /// </summary>
    public UiaClientStartup(Action firstCall)
    {
        _firstCall = firstCall ?? throw new ArgumentNullException(nameof(firstCall));
    }

    /// <summary>The process-wide gate around the managed client's first call (reading the desktop root element).</summary>
    public static UiaClientStartup Shared { get; } = new(() => _ = AutomationElement.RootElement);

    /// <summary>True once the first call has returned.</summary>
    public bool IsStarted => _started;

    /// <summary>
    /// Makes the first call unless it has already returned once. Other threads wait until it returns.
    /// </summary>
    /// <exception cref="Exception">Whatever the first call throws (for example a COMException when UI
    /// Automation is not available). The next call tries again.</exception>
    public void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _firstCall();
            _started = true;
        }
    }
}
