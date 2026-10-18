using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// What every UI Automation read of a window checks first (roadmap M3.1, shared with M3.4): whether the user's target
/// policy allows reading it (<see cref="ReadVerdict"/>), whether the window responds, and which application's process a
/// stuck read is counted against (<see cref="IUiaWorker"/>). These are Win32 reads that Windows answers without the window's
/// thread, except the WM_NULL probe, which gives up after <see cref="RespondTimeoutMs"/>.
/// </summary>
public static class UiaTargets
{
    /// <summary>How long the hung-window check waits for the window to answer a message.</summary>
    public const uint RespondTimeoutMs = 500;

    /// <summary>
    /// The target policy's verdict on a UI Automation read of the window: the policy covers the reads of a window as it
    /// covers captures (capture_window, capture_with_elements), so no window of an app the user denies is read, named or
    /// not; an element of another process inside a window it allows is checked by its own process
    /// (<see cref="ForeignElementsOf"/>). Matched on the window's process and, for a UWP frame window (an ApplicationFrameWindow, or another window of
    /// ApplicationFrameHost), on the app whose CoreWindow it hosts (see <see cref="UwpFrames"/>), as the window listings
    /// name them, and then on the window itself, which is refused when it is a security prompt that a shared process shows
    /// (<see cref="ITargetPolicy.EvaluateWindow"/>). Fails closed: a window whose process name cannot be read, or that
    /// cannot be checked, is refused while the policy denies anything. Allowed without a policy or a window wrapper, and for
    /// a handle that is no window (the read then reports that).
    /// </summary>
    /// <param name="windows">The window wrapper, or null to check nothing.</param>
    /// <param name="policy">The target policy, or null to check nothing.</param>
    /// <param name="windowHandle">The window to read.</param>
    /// <param name="processName">Looks up a process name (a caller that checks many windows passes a cached lookup); null
    /// for <see cref="INativeWindowWrapper.GetProcessName"/>.</param>
    public static TargetPolicyVerdict ReadVerdict(
        INativeWindowWrapper? windows, ITargetPolicy? policy, nint windowHandle, Func<uint, string?>? processName = null)
    {
        if (windows == null || policy == null || windowHandle == nint.Zero || !policy.DeniesAnything())
        {
            return TargetPolicyVerdict.Allow;
        }

        try
        {
            if (!windows.IsWindow(windowHandle))
            {
                return TargetPolicyVerdict.Allow;
            }

            processName ??= windows.GetProcessName;
            var processId = windows.GetWindowThreadProcessId(windowHandle);
            var names = new List<string?>();
            if (processId != 0)
            {
                // The window's own name is read anyway, so a frame of any class costs no other lookup (roadmap M3.F7)
                var windowName = processName(processId);
                if (UwpFrames.IsFrame(windows.GetClassName(windowHandle), processId, _ => windowName) &&
                    UwpFrames.FindHostedAppProcessId(windows, windowHandle, processId) is { } app)
                {
                    names.Add(processName(app));
                }

                names.Add(windowName);
            }

            var verdict = policy.Evaluate(names);
            if (!verdict.Allowed)
            {
                return verdict;
            }

            // A security prompt that a shared process shows, by its structure (roadmap M3.F5)
            if (policy.EvaluateWindow(windowHandle) is { Allowed: false } prompt)
            {
                return prompt;
            }

            if (processId != 0 && names.All(name => !string.IsNullOrWhiteSpace(name)))
            {
                return verdict;
            }

            // A process that cannot be named cannot be checked; a window that closed meanwhile is left to the read
            return windows.IsWindow(windowHandle) ? TargetPolicyVerdict.Unreadable : TargetPolicyVerdict.Allow;
        }
        catch (Exception)
        {
            return TargetPolicyVerdict.Unreadable;
        }
    }

    /// <summary>
    /// The TargetDeniedByPolicy message of a UI Automation read that <see cref="ReadVerdict"/> refuses, or null when the
    /// policy allows it.
    /// </summary>
    public static string? PolicyRefusal(INativeWindowWrapper? windows, ITargetPolicy? policy, nint windowHandle) =>
        ReadVerdict(windows, policy, windowHandle) is { Allowed: false } verdict ? PolicyRefusalMessage(windowHandle, verdict) : null;

    /// <summary>The TargetDeniedByPolicy message of a UI Automation read of <paramref name="windowHandle"/>.</summary>
    public static string PolicyRefusalMessage(nint windowHandle, TargetPolicyVerdict verdict) =>
        $"Window {(long)windowHandle} is refused by the server's target policy: {verdict.Reason}. Nothing was read: the policy " +
        "covers the UI Automation reads of a window as it covers input and captures. It is set by the user in the server's " +
        "environment and no tool argument changes it; ask the user to do this step.";

    /// <summary>
    /// The check of the elements of other processes inside <paramref name="windowHandle"/>, for one read of a window that
    /// passed <see cref="ReadVerdict"/> (see <see cref="ForeignElements"/>).
    /// </summary>
    /// <param name="windows">The window wrapper, or null to check nothing.</param>
    /// <param name="policy">The target policy, or null to check nothing.</param>
    /// <param name="windowHandle">The window read.</param>
    public static ForeignElements ForeignElementsOf(INativeWindowWrapper? windows, ITargetPolicy? policy, nint windowHandle) =>
        new(windows, policy, windowHandle);

    /// <summary>
    /// Why the window must not be read, or null when it responds (or cannot be checked): Windows reports its thread hung
    /// (IsHungAppWindow), or it does not answer a no-op message within <see cref="RespondTimeoutMs"/> (SendMessageTimeout
    /// WM_NULL with SMTO_ABORTIFHUNG). For a UWP frame window (an ApplicationFrameWindow, or another window of
    /// ApplicationFrameHost: <see cref="UwpFrames.IsFrame"/>), the CoreWindow it hosts is checked the same way: the frame's
    /// thread is the shared ApplicationFrameHost's, while the app's elements are served by the CoreWindow's thread in the
    /// app's own process. A handle that is no window is left to the UI Automation lookup, which reports WindowNotFound. A
    /// check that throws does not stop the read: the read's own deadline still bounds it.
    /// </summary>
    /// <param name="windows">The window wrapper, or null to check nothing.</param>
    /// <param name="windowHandle">The window to check.</param>
    /// <param name="what">What would not be read, for the message ("its UI elements were not read").</param>
    public static string? NotRespondingReason(INativeWindowWrapper? windows, nint windowHandle, string what = "its UI elements were not read")
    {
        if (windows == null)
        {
            return null;
        }

        try
        {
            if (!windows.IsWindow(windowHandle))
            {
                return null;
            }

            if (Unresponsive(windows, windowHandle, windowHandle, what) is { } frame)
            {
                return frame;
            }

            // A UWP app answers on its CoreWindow's thread, not the frame host's
            var processId = windows.GetWindowThreadProcessId(windowHandle);
            return processId != 0 && UwpFrames.IsFrame(windows.GetClassName(windowHandle), processId, windows.GetProcessName) &&
                   UwpFrames.FindHostedCoreWindow(windows, windowHandle, processId) is { } core
                ? Unresponsive(windows, core.Window, windowHandle, what)
                : null;
        }
        catch (Exception)
        {
            // The check is a courtesy: the read's own deadline still bounds it
            return null;
        }
    }

    /// <summary>
    /// The not-responding message when <paramref name="probed"/> is reported hung or does not answer WM_NULL in time, or
    /// null; a window that closed meanwhile (<paramref name="window"/>, the one to read) is left to the lookup.
    /// </summary>
    private static string? Unresponsive(INativeWindowWrapper windows, nint probed, nint window, string what)
    {
        if (windows.IsHungAppWindow(probed))
        {
            return $"The window is not responding (Windows reports it hung), so {what}: every UI " +
                   "Automation call to it would block until it recovers. Wait, then check it again with find_windows or " +
                   "get_window_info (isResponding).";
        }

        // Null: the message could not be sent (the window is gone, or a more privileged one); the read decides
        if (windows.RespondsWithin(probed, RespondTimeoutMs) == false)
        {
            // It may have closed meanwhile; then the lookup reports that
            if (!windows.IsWindow(probed) || !windows.IsWindow(window))
            {
                return null;
            }

            return $"The window did not answer a message within {RespondTimeoutMs} ms, so {what}: " +
                   "the application is busy or hung. Wait, then try again.";
        }

        return null;
    }

    /// <summary>
    /// The process of the app that the window shows, or 0 when it is not known. For a UWP frame window that is the app's
    /// own process, not the ApplicationFrameHost that owns the frames of every UWP app (see <see cref="UwpFrames"/>).
    /// </summary>
    public static int TargetProcessId(INativeWindowWrapper? windows, nint windowHandle)
    {
        try
        {
            return windows == null ? 0 : (int)UwpFrames.AppProcessId(windows, windowHandle);
        }
        catch (Exception)
        {
            // Only the stuck-read count depends on it
            return 0;
        }
    }
}

/// <summary>
/// The target policy's check of the elements of another process than the window read (an embedded WebView2, an app or preview
/// handler shown as a child window of another app), for one read of a window that passed <see cref="UiaTargets.ReadVerdict"/>:
/// that verdict is the window's, and UI Automation serves such an element from its own process, which the user may deny. An
/// element of the window's own process (for a UWP frame also of the app it shows, see <see cref="UiaTargets.TargetProcessId"/>)
/// is allowed with the window; any other process is matched on its own name, as get_focused_element checks the element it
/// read (<see cref="TargetPolicyExtensions.EvaluateFailClosed(ITargetPolicy, IReadOnlyCollection{string?})"/>), and fails
/// closed: an element whose process is not known (0), or whose process cannot be named, is refused while the policy denies
/// anything. The policy matches process names, so that is the verdict a check of the element's own window would give too,
/// without the walk up its ancestors to find it. An element is checked by the process UI Automation read with it
/// (<see cref="UiaElement.ProcessId"/>: part of every walk's and search's read, so no further call), which covers the elements
/// with no window of their own, such as every element of a web page. Win32 reads only, each process named once per read;
/// nothing is checked without a policy that denies anything (<see cref="Active"/>). Thread safe: a walk the worker abandoned
/// can still be adding to it while its caller reads it.
/// </summary>
public sealed class ForeignElements
{
    private readonly INativeWindowWrapper? _windows;
    private readonly ITargetPolicy? _policy;
    private readonly nint _window;
    private readonly object _gate = new();
    private readonly Dictionary<int, (string? Name, TargetPolicyVerdict Verdict)> _verdicts = [];
    private readonly Dictionary<int, int> _refused = [];
    private readonly HashSet<string> _countedRuntimeIds = [];
    private readonly HashSet<IUiaNode> _countedNodes = new(ReferenceEqualityComparer.Instance);
    private HashSet<int>? _windowProcesses;

    internal ForeignElements(INativeWindowWrapper? windows, ITargetPolicy? policy, nint window)
    {
        _windows = windows;
        _policy = policy;
        _window = window;
        Active = windows != null && policy != null && window != nint.Zero && policy.DeniesAnything();
    }

    /// <summary>Whether anything can be refused: there is a policy that denies anything, and the Win32 reads to check it.</summary>
    public bool Active { get; }

    /// <summary>
    /// The verdict on an element that UI Automation says belongs to <paramref name="processId"/> (its ProcessId property):
    /// allowed for the window's own processes, else by the process's name, failing closed (see the class).
    /// </summary>
    public TargetPolicyVerdict Verdict(int processId)
    {
        if (!Active)
        {
            return TargetPolicyVerdict.Allow;
        }

        if (processId <= 0)
        {
            return TargetPolicyVerdict.Deny(null, "the element's process is not known, so it cannot be checked against the policy");
        }

        lock (_gate)
        {
            if (WindowProcesses().Contains(processId))
            {
                return TargetPolicyVerdict.Allow;
            }

            if (!_verdicts.TryGetValue(processId, out var known))
            {
                string? name = null;
                try
                {
                    name = _windows!.GetProcessName((uint)processId);
                    known = (name, _policy!.EvaluateFailClosed([name]));
                }
                catch (Exception)
                {
                    known = (name, TargetPolicyVerdict.Unreadable);
                }

                _verdicts[processId] = known;
            }

            return known.Verdict;
        }
    }

    /// <summary>
    /// Whether a walk or search leaves out an element that UI Automation says belongs to <paramref name="processId"/> (a page
    /// whose root's identity was read): true when <see cref="Verdict"/> refuses it, and then it is counted for
    /// <see cref="LeftOutWarning"/>, once per call.
    /// </summary>
    public bool LeavesOut(int processId)
    {
        if (Verdict(processId).Allowed)
        {
            return false;
        }

        lock (_gate)
        {
            _refused[processId] = _refused.GetValueOrDefault(processId) + 1;
        }

        return true;
    }

    /// <summary>
    /// Whether a walk leaves out the UI Automation element of <paramref name="elementWindow"/> (its NativeWindowHandle), by
    /// the process of that window (Win32's, see <see cref="LeavesOut(int)"/>): for a page found by its window rather than
    /// read as an element. Never for the window read itself, nor for no window (0), which says nothing about the element's
    /// process.
    /// </summary>
    public bool LeavesOutWindow(nint elementWindow) =>
        Active && elementWindow != nint.Zero && elementWindow != _window && LeavesOut(ProcessOf(elementWindow));

    /// <summary>
    /// Whether a walk, search or table read leaves out <paramref name="element"/>, as a read of the tree returned it: true
    /// when <see cref="Verdict"/> refuses a process it is known to belong to (<see cref="Refused"/>). Counted once per element
    /// for <see cref="LeftOutWarning"/>, however often it is checked (by its runtime id, else by its node).
    /// </summary>
    /// <param name="element">The element as read.</param>
    /// <param name="identityProcessId">The process its identity names, when that was read too; null otherwise.</param>
    public bool LeavesOut(UiaElement element, int? identityProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (Refused(element, identityProcessId) is not { } process)
        {
            return false;
        }

        lock (_gate)
        {
            var first = element.RuntimeId is { Count: > 0 } runtimeId
                ? _countedRuntimeIds.Add(string.Join('.', runtimeId))
                : _countedNodes.Add(element.Node);
            if (first)
            {
                _refused[process] = _refused.GetValueOrDefault(process) + 1;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the policy refuses <paramref name="element"/>, as <see cref="LeavesOut(UiaElement, int?)"/> decides, without
    /// counting it: for an element that is neither returned nor reported left out, such as a list searchVirtualized would ask
    /// for its items.
    /// </summary>
    public bool Refuses(UiaElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Refused(element, null) != null;
    }

    /// <summary>
    /// The TargetDeniedByPolicy message of a read that returns <paramref name="element"/>'s value or text
    /// (<see cref="RefusalMessage"/>), or null when it is allowed (<see cref="Refused"/>).
    /// </summary>
    /// <param name="element">The element as read (the resolution of its ref reads it).</param>
    /// <param name="identityProcessId">The process its identity names, when that was read too; null otherwise.</param>
    public string? Refusal(UiaElement element, int? identityProcessId = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Refused(element, identityProcessId) is { } process ? RefusalMessage(process, Verdict(process)) : null;
    }

    /// <summary>
    /// The process of <paramref name="element"/> that <see cref="Verdict"/> refuses, or null when none is: the process UI
    /// Automation read with it (<see cref="UiaElement.ProcessId"/>, 0 when it did not say, which is refused), and the one its
    /// identity names (<paramref name="identityProcessId"/>), when that was read; the element is refused when either is. Only
    /// an element built without its process, and whose identity was not read, is checked by the process of its own window
    /// instead (a Win32 read), and is allowed with the window when it has none.
    /// </summary>
    private int? Refused(UiaElement element, int? identityProcessId)
    {
        if (!Active)
        {
            return null;
        }

        IEnumerable<int> processes;
        if (element.ProcessId == null && identityProcessId == null)
        {
            var window = element.NativeWindowHandle;
            if (window == nint.Zero || window == _window)
            {
                return null;
            }

            processes = [ProcessOf(window)];
        }
        else
        {
            processes = new[] { element.ProcessId, identityProcessId }.OfType<int>().Distinct();
        }

        foreach (var process in processes)
        {
            if (!Verdict(process).Allowed)
            {
                return process;
            }
        }

        return null;
    }

    /// <summary>The process of a window (Win32), or 0 when it cannot be read.</summary>
    private int ProcessOf(nint window)
    {
        try
        {
            return (int)_windows!.GetWindowThreadProcessId(window);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// How many elements were left out unread (<see cref="LeavesOut(int)"/>, <see cref="LeavesOut(UiaElement, int?)"/>,
    /// <see cref="LeavesOutWindow"/>).
    /// </summary>
    public int LeftOutCount
    {
        get
        {
            lock (_gate)
            {
                return _refused.Values.Sum();
            }
        }
    }

    /// <summary>
    /// The TargetDeniedByPolicy message of a read that returns an element's value or text, refused for the element's own
    /// process (<paramref name="verdict"/> from <see cref="Verdict"/>).
    /// </summary>
    public string RefusalMessage(int processId, TargetPolicyVerdict verdict) =>
        $"The element belongs to {Describe(processId)}, not to the process of window {(long)_window}, and the server's target " +
        $"policy refuses it: {verdict.Reason}. Nothing was read: the policy covers an app shown inside another app's window (an " +
        "embedded web view, a preview, a child window of another program) as it covers the app's own windows. It is set by the " +
        "user in the server's environment and no tool argument changes it; ask the user to do this step.";

    /// <summary>
    /// What a walk or search says about the elements it left out unread (<see cref="LeftOutCount"/>), after
    /// <paramref name="code"/> and a colon when one is given; null when it left none out.
    /// </summary>
    /// <param name="what">What became of them, such as "those cells are null".</param>
    public string? LeftOutWarning(string? code, string what)
    {
        List<(int ProcessId, int Count)> refused;
        lock (_gate)
        {
            refused = _refused.Select(r => (r.Key, r.Value)).OrderBy(r => r.Key).ToList();
        }

        if (refused.Count == 0)
        {
            return null;
        }

        var count = refused.Sum(r => r.Count);
        var processes = string.Join(", ", refused.Select(r => Describe(r.ProcessId)));
        var reasons = string.Join("; ", refused.Select(r => Verdict(r.ProcessId).Reason).OfType<string>().Distinct());
        var message =
            $"{count} element{(count == 1 ? "" : "s")} of window {(long)_window} belong{(count == 1 ? "s" : "")} to {processes}, " +
            $"not to the window's process, and the server's target policy refuses {(refused.Count == 1 ? "it" : "them")} " +
            $"({reasons}). Nothing of {(count == 1 ? "it" : "them")} was read: {what}. The policy is set by the user in the " +
            "server's environment and no tool argument changes it; ask the user to do steps in that app.";
        return code == null ? message : $"{code}: {message}";
    }

    /// <summary>"process 4242 ('KeePass')", or without a name when it cannot be read.</summary>
    private string Describe(int processId)
    {
        string? name;
        lock (_gate)
        {
            name = _verdicts.GetValueOrDefault(processId).Name;
        }

        return processId <= 0
            ? "a process UI Automation does not name"
            : string.IsNullOrWhiteSpace(name) ? $"process {processId}" : $"process {processId} ('{name}')";
    }

    /// <summary>The window's own process, and for a UWP frame the app's (Win32 reads, once). Under the lock.</summary>
    private HashSet<int> WindowProcesses()
    {
        if (_windowProcesses != null)
        {
            return _windowProcesses;
        }

        var processes = new HashSet<int>();
        try
        {
            processes.Add((int)_windows!.GetWindowThreadProcessId(_window));
        }
        catch (Exception)
        {
            // Not known: every element is then checked by its own process
        }

        processes.Add(UiaTargets.TargetProcessId(_windows, _window));
        processes.Remove(0);
        return _windowProcesses = processes;
    }
}
