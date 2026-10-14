using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// get_focused_element and get_element_at_point: reads single elements through
/// <see cref="IUiaTree"/>, on an <see cref="IUiaWorker"/> thread with a hard deadline.
/// </summary>
/// <remarks>
/// Elements are returned as snapshots, with a ref (roadmap M3.4) that later calls can name them by: registered under the
/// top-level window the result reports, so the registry can find the element again in it, with the list row the element is
/// in (roadmap M3.F10), so the ref goes stale once a recycled row shows another item. A window that shows web content
/// (Chromium, Electron, WebView2) is warmed up first (<see cref="IAccessibilityWarmup"/>, roadmap M3.3), within a third of
/// the timeout: the foreground window for the focused element, the window at the point for the element at a point.
/// Otherwise the first read of such a window finds only its frame, because its page's tree is built only once asked for.
/// An element in a window of an app the user's target policy denies is not read (TargetDeniedByPolicy, see
/// <see cref="UiaTargets.ReadVerdict"/>): the foreground window, or the window at the point, is checked before the warm-up and
/// any read; the element's own window and process (the focus can be in a prompt of another app) before its details are read.
/// </remarks>
public class ElementInspectionService : IElementInspectionService
{
    /// <summary>Values longer than this are cut, and ValueTruncated is set.</summary>
    public const int MaxValueLength = 200;

    /// <summary>The most ancestors get_element_at_point returns.</summary>
    public const int MaxAncestors = 10;

    // Bounds the climb from the focused element to the nearest element with a window handle
    private const int MaxWindowSearchDepth = 64;

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly INativeHitTestWrapper _hitTest;
    private readonly INativeWindowWrapper _windows;
    private readonly IMonitorService _monitors;
    private readonly IAccessibilityWarmup? _warmup;
    private readonly IElementRegistry? _registry;
    private readonly ITargetPolicy? _policy;

    /// <param name="warmup">Warms up the web content of Chromium, Electron and WebView2 windows before they are read
    /// (roadmap M3.3). The server always passes it; without it no window is warmed up.</param>
    /// <param name="registry">Gives the element its ref (roadmap M3.4); without it the element has none.</param>
    /// <param name="policy">The user's target policy, which refuses reads of a window of an app it denies. The server always
    /// passes it; without it no window is refused.</param>
    public ElementInspectionService(
        IUiaTree tree,
        IUiaWorker worker,
        INativeHitTestWrapper hitTest,
        INativeWindowWrapper windows,
        IMonitorService monitors,
        IAccessibilityWarmup? warmup = null,
        IElementRegistry? registry = null,
        ITargetPolicy? policy = null)
    {
        _tree = tree;
        _worker = worker;
        _hitTest = hitTest;
        _windows = windows;
        _monitors = monitors;
        _warmup = warmup;
        _registry = registry;
        _policy = policy;
    }

    /// <inheritdoc />
    public Task<FocusedElementResult> GetFocusedElementAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        FocusedElementResult Failure(string error, UIElementErrorCode code) =>
            new(false, null, null, null, null, null, Elapsed(stopwatch), error, code);

        // Keyboard focus is in the foreground window, so a hung foreground application is the one
        // that can block this read
        var foreground = SafeRead(_windows.GetForegroundWindow);
        if (UiaTargets.PolicyRefusal(_windows, _policy, foreground) is { } denied)
        {
            return Task.FromResult(Failure(denied, UIElementErrorCode.TargetDeniedByPolicy));
        }

        var targetProcessId = AppProcessOf(foreground);

        return ReadAsync(
            targetProcessId,
            token =>
            {
                if (!_tree.IsAvailable())
                {
                    return Failure("UI Automation is not available on this system", UIElementErrorCode.AutomationUnavailable);
                }

                var warmup = WarmUp(foreground, timeout, token);
                var node = _tree.GetFocusedElement();
                if (node == null)
                {
                    return Failure("No element has keyboard focus.", UIElementErrorCode.ElementNotFound)
                        with { AccessibilityWarmup = warmup };
                }

                var identity = _tree.ReadIdentity(node);
                var window = FindOwningWindow(node, identity);
                var (processId, processName) = DescribeProcess(identity, window);
                if (ElementRefusal(window, foreground, processName) is { } refused)
                {
                    return Failure(refused, UIElementErrorCode.TargetDeniedByPolicy) with { AccessibilityWarmup = warmup };
                }

                var (details, element) = ReadDetails(node, identity);
                return new FocusedElementResult(
                    Success: true,
                    Element: details with { Ref = Register(window, element, token) },
                    WindowHandle: window == nint.Zero ? null : window,
                    WindowTitle: WindowTitle(window),
                    ProcessId: processId,
                    ProcessName: processName,
                    ElapsedMilliseconds: Elapsed(stopwatch),
                    AccessibilityWarmup: warmup);
            },
            Failure,
            "The focused element",
            timeout,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ElementAtPointResult> GetElementAtPointAsync(
        int x,
        int y,
        int ancestors,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        ElementAtPointResult Failure(string error, UIElementErrorCode code) =>
            new(false, x, y, null, null, null, null, null, null, Elapsed(stopwatch), error, code);

        if (!IsOnAMonitor(x, y))
        {
            return Task.FromResult(Failure(
                $"The point ({x}, {y}) is not on any monitor. Coordinates are physical virtual-screen pixels; see get_monitors.",
                UIElementErrorCode.CoordinatesOutOfBounds));
        }

        var ancestorCount = Math.Clamp(ancestors, 0, MaxAncestors);

        // The top-level window hit-tested at the point. WindowFromPoint and GetAncestor do not wait
        // for the window's application, so this runs before the read, and the application that the
        // window shows (for a UWP frame, the app it hosts) is the one that can block it.
        var hitWindow = SafeRead(() => _hitTest.GetRootWindow(_hitTest.WindowFromPoint(x, y)));
        if (UiaTargets.PolicyRefusal(_windows, _policy, hitWindow) is { } denied)
        {
            return Task.FromResult(Failure(denied, UIElementErrorCode.TargetDeniedByPolicy));
        }

        return ReadAsync(
            AppProcessOf(hitWindow),
            token =>
            {
                if (!_tree.IsAvailable())
                {
                    return Failure("UI Automation is not available on this system", UIElementErrorCode.AutomationUnavailable);
                }

                // Before the hit test: a page whose tree is not built yet answers with its render widget's pane
                var warmup = WarmUp(hitWindow, timeout, token);
                var node = _tree.FromPoint(x, y);
                if (node == null)
                {
                    return Failure($"There is no UI Automation element at ({x}, {y}).", UIElementErrorCode.ElementNotFound)
                        with { AccessibilityWarmup = warmup };
                }

                // WindowFromPoint skips hidden windows and disabled child windows. It practically
                // always hits a window (the desktop is under everything); the element's own window is
                // only a fallback for when it hit none
                var identity = _tree.ReadIdentity(node);
                var window = hitWindow != nint.Zero ? hitWindow : FindOwningWindow(node, identity);
                var (processId, processName) = DescribeProcess(identity, window);
                if (ElementRefusal(window, hitWindow, processName) is { } refused)
                {
                    return Failure(refused, UIElementErrorCode.TargetDeniedByPolicy) with { AccessibilityWarmup = warmup };
                }

                var (details, element) = ReadDetails(node, identity);
                var chain = ReadAncestors(node, ancestorCount);

                return new ElementAtPointResult(
                    Success: true,
                    X: x,
                    Y: y,
                    Element: details with { Ref = Register(window, element, token) },
                    Ancestors: chain,
                    WindowHandle: window == nint.Zero ? null : window,
                    WindowTitle: WindowTitle(window),
                    ProcessId: processId,
                    ProcessName: processName,
                    ElapsedMilliseconds: Elapsed(stopwatch),
                    AccessibilityWarmup: warmup);
            },
            Failure,
            "The element at the point",
            timeout,
            cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="read"/> on the worker with a deadline of <paramref name="timeout"/>, and
    /// turns its failures into results.
    /// </summary>
    private async Task<T> ReadAsync<T>(
        int targetProcessId,
        Func<CancellationToken, T> read,
        Func<string, UIElementErrorCode, T> failure,
        string subject,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return await _worker.RunAsync(targetProcessId, read, deadline.Token, TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (TimeoutException) when (cancellationToken.IsCancellationRequested)
        {
            // The request was cancelled, not timed out: let the SDK report it
            throw new OperationCanceledException(cancellationToken);
        }
        catch (UiaReadRefusedException ex)
        {
            // Earlier reads of the same application are still stuck; nothing was waited for
            return failure(ex.Message, UIElementErrorCode.OperationTimedOut);
        }
        catch (TimeoutException ex)
        {
            return failure(
                $"{ex.Message} Waited {timeout.TotalMilliseconds:0} ms. Try again; if it keeps happening, the application may be hung.",
                UIElementErrorCode.OperationTimedOut);
        }
        catch (UiaElementUnavailableException ex)
        {
            return failure($"{subject} disappeared while it was read: {ex.Message}", UIElementErrorCode.ElementAccessFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return failure($"Error reading UI Automation: {ex.Message}", UIElementErrorCode.OperationException);
        }
    }

    /// <summary>
    /// Warms up the web content of <paramref name="window"/> (roadmap M3.3) within a third of <paramref name="timeout"/>, so
    /// the read keeps most of it; null when there is no window, nothing to warm up, or no warm-up service.
    /// </summary>
    private AccessibilityWarmupInfo? WarmUp(nint window, TimeSpan timeout, CancellationToken cancellationToken) =>
        window == nint.Zero ? null : _warmup?.WarmUp(window, AccessibilityWarmup.BudgetFor(timeout), cancellationToken);

    /// <summary>
    /// The process of the application that <paramref name="window"/> (a top-level window) shows, or 0
    /// when there is none or it cannot be read. For a UWP app that is the app's own process, not the
    /// ApplicationFrameHost that owns the frames of every UWP app: stuck reads are counted per process,
    /// and one hung UWP app must not block reads of the others.
    /// </summary>
    private int AppProcessOf(nint window) =>
        window == nint.Zero ? 0 : (int)SafeRead(() => UwpFrames.AppProcessId(_windows, window));

    /// <summary>
    /// A Win32 read made before the UI Automation read. A failure only means "unknown": the worker then
    /// counts the read for an unknown target, and get_element_at_point uses the element's own window.
    /// </summary>
    private static TValue SafeRead<TValue>(Func<TValue> read) where TValue : struct
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>
    /// The element's ref under <paramref name="window"/>, or null without a window or a registry, or when the element has
    /// no runtime id. The list row the element is in is read first (<see cref="ElementRows"/>: at most
    /// <see cref="ElementRows.MaxLevels"/> ancestors) and recorded with the ref, as find_ui_elements and the walks record it,
    /// so the ref is stale once a recycled row shows another item (roadmap M3.F10). An element whose row cannot be read is
    /// registered without it.
    /// </summary>
    private string? Register(nint window, UiaElement? element, CancellationToken cancellationToken)
    {
        if (_registry == null || window == nint.Zero || element == null)
        {
            return null;
        }

        ElementRow? row = null;
        try
        {
            row = ElementRows.For(_tree).Of(element, cancellationToken);
        }
        catch (Exception)
        {
            // An ancestor that is gone, failed or did not answer, or the deadline: registered without its row
        }

        try
        {
            return _registry.Register(window, element, new ElementOrigin(_registry.NextGeneration(), Row: row));
        }
        catch (Exception)
        {
            // The element is returned without a ref
            return null;
        }
    }

    /// <summary>
    /// Why the element read is not returned: its window, unless that is <paramref name="checkedWindow"/> (checked before the
    /// read), or its own process belongs to an app the user's target policy denies, or cannot be named while the policy denies
    /// anything (as <see cref="UiaTargets.ReadVerdict"/> refuses a window). Null when it is allowed.
    /// </summary>
    private string? ElementRefusal(nint window, nint checkedWindow, string? processName)
    {
        if (_policy == null)
        {
            return null;
        }

        if (window != checkedWindow && UiaTargets.PolicyRefusal(_windows, _policy, window) is { } denied)
        {
            return denied;
        }

        return _policy.EvaluateFailClosed([processName]) is { Allowed: false } verdict
            ? UiaTargets.PolicyRefusalMessage(window != nint.Zero ? window : checkedWindow, verdict)
            : null;
    }

    /// <summary>
    /// The element's details from its <paramref name="identity"/> (who provides it) and its other properties, and, for the
    /// registry, the element with its runtime id (null when it has none, or the registry is not used).
    /// </summary>
    private (UIElementDetails Details, UiaElement? Element) ReadDetails(IUiaNode node, UiaNodeIdentity identity)
    {
        var properties = _tree.Read(node);
        var interaction = _tree.ReadInteraction(node);

        // A password field's value is never read, not even to mask it
        var (value, truncated) = !identity.IsPassword && interaction.ActionPatterns.Contains("Value")
            ? Truncate(TryReadValue(node))
            : (null, false);

        var details = new UIElementDetails(
            Name: EmptyToNull(properties.Name),
            ControlType: properties.ControlType,
            AutomationId: EmptyToNull(properties.AutomationId),
            ClassName: EmptyToNull(identity.ClassName),
            FrameworkId: EmptyToNull(identity.FrameworkId),
            Bounds: properties.Bounds.ToBounds(),
            IsEnabled: properties.IsEnabled,
            IsOffscreen: properties.IsOffscreen,
            IsKeyboardFocusable: interaction.IsKeyboardFocusable,
            HasKeyboardFocus: interaction.HasKeyboardFocus,
            IsPassword: identity.IsPassword,
            Patterns: interaction.ActionPatterns,
            Value: value,
            ValueTruncated: truncated);

        var element = _registry == null
            ? null
            : new UiaElement(
                node,
                properties,
                identity.ClassName,
                interaction.IsKeyboardFocusable,
                interaction.ActionPatterns.Aggregate(UiaPatterns.None, (all, name) =>
                    Enum.TryParse<UiaPatterns>(name, out var pattern) ? all | pattern : all),
                TryReadRuntimeId(node),
                ControlTypeId: null);
        return (details, element);
    }

    private IReadOnlyList<int>? TryReadRuntimeId(IUiaNode node)
    {
        try
        {
            return _tree.GetRuntimeId(node);
        }
        catch (Exception)
        {
            // Without a runtime id the element has no ref
            return null;
        }
    }

    private string? TryReadValue(IUiaNode node)
    {
        try
        {
            return _tree.ReadValue(node);
        }
        catch (Exception)
        {
            // The value is optional; some providers refuse to give it out
            return null;
        }
    }

    /// <summary>Cuts a value to <see cref="MaxValueLength"/>, never inside a surrogate pair.</summary>
    internal static (string? Value, bool Truncated) Truncate(string? value)
    {
        if (value == null || value.Length <= MaxValueLength)
        {
            return (value, false);
        }

        var length = char.IsHighSurrogate(value[MaxValueLength - 1]) ? MaxValueLength - 1 : MaxValueLength;
        return (value[..length], true);
    }

    /// <summary>
    /// The ancestors of <paramref name="node"/>, nearest first, up to <paramref name="count"/>. Stops
    /// at the top-level window, and at the first ancestor that cannot be read.
    /// </summary>
    private List<UIElementAncestor> ReadAncestors(IUiaNode node, int count)
    {
        var ancestors = new List<UIElementAncestor>();
        var current = node;

        try
        {
            while (ancestors.Count < count && _tree.GetParent(current) is { } parent)
            {
                var properties = _tree.Read(parent);
                var identity = _tree.ReadIdentity(parent);
                ancestors.Add(new UIElementAncestor(
                    Name: EmptyToNull(properties.Name),
                    ControlType: properties.ControlType,
                    AutomationId: EmptyToNull(properties.AutomationId),
                    ClassName: EmptyToNull(identity.ClassName),
                    Bounds: properties.Bounds.ToBounds(),
                    FrameworkId: EmptyToNull(identity.FrameworkId)));
                current = parent;
            }
        }
        catch (Exception)
        {
            // An ancestor disappeared or its provider failed: return the chain read so far
        }

        return ancestors;
    }

    /// <summary>
    /// The top-level window of the nearest element at or above <paramref name="node"/> that has a
    /// window handle of its own, or 0 when none could be found.
    /// </summary>
    private nint FindOwningWindow(IUiaNode node, UiaNodeIdentity identity)
    {
        try
        {
            var current = node;
            var handle = identity.NativeWindowHandle;
            for (var depth = 0; handle == nint.Zero && depth < MaxWindowSearchDepth; depth++)
            {
                if (_tree.GetParent(current) is not { } parent)
                {
                    return nint.Zero;
                }

                current = parent;
                handle = _tree.ReadIdentity(current).NativeWindowHandle;
            }

            return handle == nint.Zero ? nint.Zero : _hitTest.GetRootWindow(handle);
        }
        catch (Exception)
        {
            // The window is optional; the element was read
            return nint.Zero;
        }
    }

    /// <summary>
    /// The process of the element (UI Automation's ProcessId), or of the window when the element
    /// does not report one.
    /// </summary>
    private (int? ProcessId, string? ProcessName) DescribeProcess(UiaNodeIdentity identity, nint window)
    {
        var processId = identity.ProcessId > 0
            ? identity.ProcessId
            : window != nint.Zero ? (int)_windows.GetWindowThreadProcessId(window) : 0;

        return processId > 0 ? (processId, _windows.GetProcessName((uint)processId)) : (null, null);
    }

    private string? WindowTitle(nint window) =>
        window == nint.Zero ? null : EmptyToNull(_windows.GetWindowText(window));

    private bool IsOnAMonitor(int x, int y) =>
        _monitors.GetAllMonitors().Any(m =>
            x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width &&
            y >= m.Bounds.Y && y < m.Bounds.Y + m.Bounds.Height);

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static double Elapsed(Stopwatch stopwatch) => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
}
