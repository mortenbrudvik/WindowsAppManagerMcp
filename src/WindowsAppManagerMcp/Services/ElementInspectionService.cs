using System.Diagnostics;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// get_focused_element and get_element_at_point: reads single elements through
/// <see cref="IUiaTree"/>, on an <see cref="IUiaWorker"/> thread with a hard deadline.
/// </summary>
/// <remarks>
/// Elements are returned as snapshots. Once the element registry exists (M3.4), the results can
/// also carry a ref that later calls can act on.
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

    public ElementInspectionService(
        IUiaTree tree,
        IUiaWorker worker,
        INativeHitTestWrapper hitTest,
        INativeWindowWrapper windows,
        IMonitorService monitors)
    {
        _tree = tree;
        _worker = worker;
        _hitTest = hitTest;
        _windows = windows;
        _monitors = monitors;
    }

    /// <inheritdoc />
    public FocusedElementResult GetFocusedElement(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        FocusedElementResult Failure(string error, UIElementErrorCode code) =>
            new(false, null, null, null, null, null, Elapsed(stopwatch), error, code);

        // Keyboard focus is in the foreground window, so a hung foreground application is the one
        // that can block this read
        var targetProcessId = AppProcessOf(SafeRead(_windows.GetForegroundWindow));

        return Read(
            targetProcessId,
            () =>
            {
                if (!_tree.IsAvailable())
                {
                    return Failure("UI Automation is not available on this system", UIElementErrorCode.AutomationUnavailable);
                }

                var node = _tree.GetFocusedElement();
                if (node == null)
                {
                    return Failure("No element has keyboard focus.", UIElementErrorCode.ElementNotFound);
                }

                var (details, identity) = ReadDetails(node);
                var window = FindOwningWindow(node, identity);
                var (processId, processName) = DescribeProcess(identity, window);

                return new FocusedElementResult(
                    Success: true,
                    Element: details,
                    WindowHandle: window == nint.Zero ? null : window,
                    WindowTitle: WindowTitle(window),
                    ProcessId: processId,
                    ProcessName: processName,
                    ElapsedMilliseconds: Elapsed(stopwatch));
            },
            Failure,
            "The focused element",
            timeout,
            cancellationToken);
    }

    /// <inheritdoc />
    public ElementAtPointResult GetElementAtPoint(
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
            return Failure(
                $"The point ({x}, {y}) is not on any monitor. Coordinates are physical virtual-screen pixels; see get_monitors.",
                UIElementErrorCode.CoordinatesOutOfBounds);
        }

        var ancestorCount = Math.Clamp(ancestors, 0, MaxAncestors);

        // The top-level window hit-tested at the point. WindowFromPoint and GetAncestor do not wait
        // for the window's application, so this runs before the read, and the application that the
        // window shows (for a UWP frame, the app it hosts) is the one that can block it.
        var hitWindow = SafeRead(() => _hitTest.GetRootWindow(_hitTest.WindowFromPoint(x, y)));

        return Read(
            AppProcessOf(hitWindow),
            () =>
            {
                if (!_tree.IsAvailable())
                {
                    return Failure("UI Automation is not available on this system", UIElementErrorCode.AutomationUnavailable);
                }

                var node = _tree.FromPoint(x, y);
                if (node == null)
                {
                    return Failure($"There is no UI Automation element at ({x}, {y}).", UIElementErrorCode.ElementNotFound);
                }

                var (details, identity) = ReadDetails(node);
                var chain = ReadAncestors(node, ancestorCount);

                // WindowFromPoint skips hidden windows and disabled child windows. It practically
                // always hits a window (the desktop is under everything); the element's own window is
                // only a fallback for when it hit none
                var window = hitWindow != nint.Zero ? hitWindow : FindOwningWindow(node, identity);
                var (processId, processName) = DescribeProcess(identity, window);

                return new ElementAtPointResult(
                    Success: true,
                    X: x,
                    Y: y,
                    Element: details,
                    Ancestors: chain,
                    WindowHandle: window == nint.Zero ? null : window,
                    WindowTitle: WindowTitle(window),
                    ProcessId: processId,
                    ProcessName: processName,
                    ElapsedMilliseconds: Elapsed(stopwatch));
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
    private T Read<T>(
        int targetProcessId,
        Func<T> read,
        Func<string, UIElementErrorCode, T> failure,
        string subject,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return _worker.Run(targetProcessId, _ => read(), deadline.Token, TimeSpan.Zero);
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

    private (UIElementDetails Details, UiaNodeIdentity Identity) ReadDetails(IUiaNode node)
    {
        var properties = _tree.Read(node);
        var identity = _tree.ReadIdentity(node);
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

        return (details, identity);
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
    private static (string? Value, bool Truncated) Truncate(string? value)
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
                    Bounds: properties.Bounds.ToBounds()));
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
