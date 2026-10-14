using System.Globalization;
using System.Text;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// get_desktop_state's observation (roadmap M3.12; see <see cref="IDesktopStateService"/>). The cheap parts (the window in
/// front, the cursor, the keys and the pause) are read first on the calling thread; the windows, the focused element, the
/// popups, the elements walk and the capture then run side by side, and the call waits for them until one deadline.
/// </summary>
/// <remarks>
/// The UI Automation parts get a little less than the deadline (<see cref="PartBudget"/>), so a walk that is asked to stop
/// can still hand back what it read before the call returns: a read still stuck in a UI Automation call then is abandoned
/// after <see cref="StopGrace"/>, which ends before the deadline, and the walk's lines read so far and the popups' list are
/// kept. Whatever has not answered at the deadline itself is reported as OperationTimedOut and left to finish on its own
/// (its refs are registered as usual). Nothing here takes the input gate: every part only reads.
/// </remarks>
public sealed class DesktopStateService : IDesktopStateService
{
    /// <summary>How deep the elements walk reads: the deepest get_ui_elements allows. maxElements and the deadline bound it.</summary>
    public const int MaxDepth = 10;

    /// <summary>The tool whose absence (a toolset, an exclusion or read-only mode the user chose) turns the capture off.</summary>
    private const string CaptureTool = "capture_window";

    /// <summary>The tool whose absence (a toolset or an exclusion the user chose) turns the observation off for other tools.</summary>
    private const string StateTool = "get_desktop_state";

    /// <summary>What the popups' element walks may use of maxElements together, so the window keeps the rest.</summary>
    private const int PopupShareDivisor = 2;

    private readonly IWindowService _windows;
    private readonly IInputSession _session;
    private readonly INativeInputWrapper _input;
    private readonly IElementInspectionService _inspection;
    private readonly IUIElementService _elements;
    private readonly IPopupService _popups;
    private readonly IScreenshotService _screenshots;
    private readonly ITargetPolicy _policy;
    private readonly TimeProvider _time;
    private readonly bool _captureServed;
    private readonly bool _stateServed;

    /// <param name="windows">The window in front, the window list and the named window.</param>
    /// <param name="session">The pause and the modifier keys that are down (get_control_status' reads).</param>
    /// <param name="input">The cursor and Caps Lock.</param>
    /// <param name="inspection">get_focused_element's read.</param>
    /// <param name="elements">get_ui_elements' walk (refs, hung-window precheck, warm-up).</param>
    /// <param name="popups">The target's popups and find_ui_elements' default scope.</param>
    /// <param name="screenshots">capture_window's capture (captureId).</param>
    /// <param name="policy">The user's target policy: never a read or a capture of a denied window, named or not.</param>
    /// <param name="toolsets">The tools the server serves; the capture follows capture_window, and the observation other tools
    /// return follows get_desktop_state. Null: every tool.</param>
    /// <param name="time">The clock the deadline and the parts' budget run on; default the system clock. Tests pass one they
    /// move by hand, so a busy machine cannot move a deadline (roadmap M3.F29).</param>
    public DesktopStateService(
        IWindowService windows,
        IInputSession session,
        INativeInputWrapper input,
        IElementInspectionService inspection,
        IUIElementService elements,
        IPopupService popups,
        IScreenshotService screenshots,
        ITargetPolicy policy,
        ToolsetSelection? toolsets = null,
        TimeProvider? time = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
        _elements = elements ?? throw new ArgumentNullException(nameof(elements));
        _popups = popups ?? throw new ArgumentNullException(nameof(popups));
        _screenshots = screenshots ?? throw new ArgumentNullException(nameof(screenshots));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _time = time ?? TimeProvider.System;
        _captureServed = toolsets?.Serves(CaptureTool, readOnlyHint: false) ?? true;
        _stateServed = toolsets?.Serves(StateTool, readOnlyHint: true) ?? true;
    }

    /// <summary>
    /// The time the UI Automation parts get: the deadline less a fifth of it (at most 1 s), which a walk that was asked to
    /// stop uses to hand back what it read.
    /// </summary>
    public static TimeSpan PartBudget(TimeSpan timeout) =>
        timeout - TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(1).Ticks, timeout.Ticks / 5));

    /// <summary>
    /// How long a UI Automation part that was asked to stop may stay in a call before it is abandoned with what it read: half
    /// of what <see cref="PartBudget"/> leaves of the deadline, so the part is back before the deadline (the walk's and the
    /// popups' own graces, 2 s and 1 s, are longer than that for every timeout). An abandoned read counts as a stuck read
    /// of its application until it returns, as any read the worker gives up on.
    /// </summary>
    public static TimeSpan StopGrace(TimeSpan timeout) => (timeout - PartBudget(timeout)) / 2;

    public string? ScreenshotUnavailable() => _captureServed
        ? null
        : "A screenshot needs capture_window, which this server does not serve: the user turned captures off (the capture " +
          "toolset, WAM_EXCLUDE_TOOLS or read-only mode). Leave the screenshot out; the rest of the observation does not need it.";

    public string? StateUnavailable() => _stateServed
        ? null
        : "observe returns get_desktop_state's observation, and this server does not serve get_desktop_state: the user turned " +
          "it off (the elements toolset or WAM_EXCLUDE_TOOLS). Leave observe out.";

    public async Task<DesktopStateSnapshot> GetStateAsync(DesktopStateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope == ElementSearchScope.Desktop)
        {
            throw new ArgumentException("get_desktop_state reads one window and its popups; scope desktop is find_ui_elements'.", nameof(request));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var start = _time.GetTimestamp();
        var include = request.Include;

        // The cheap parts: Win32 reads that no window's thread answers
        var foreground = Read(_windows.GetForegroundWindow);
        var target = ChooseTarget(request.Window, foreground);
        var input = ReadInputState();
        if (target.Invalid)
        {
            return new DesktopStateSnapshot(Failed(request.Window!.Value, input, _time.GetElapsedTime(start)), null);
        }

        var cursor = Read(_input.GetCursorPos) is { } point ? new ScreenPoint(point.X, point.Y) : null;

        var needsTarget = include.Contains(DesktopStateSection.Elements) || include.Contains(DesktopStateSection.Popups) ||
                          include.Contains(DesktopStateSection.Screenshot);
        var errors = new List<DesktopStateError>();
        if (needsTarget && target.Refusal is { } refusal)
        {
            errors.Add(refusal);
        }

        // Keyboard focus is in the window in front: when that window is not read (the agent's host unless it was named, an app
        // the user's policy denies), neither is its focus. Checked before the read, so neither is warmed up or read at all
        var readFocus = include.Contains(DesktopStateSection.Focus);
        if (readFocus && ForegroundRefusal(foreground, request.Window) is { } skipped)
        {
            errors.Add(FocusError(skipped, "the window in front"));
            readFocus = false;
        }

        using var deadlineTimer = new CancellationTokenSource(request.Timeout, _time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineTimer.Token);

        // The parts stop from the call's start, as the deadline counts, not from after the cheap reads
        var partBudget = PartBudget(request.Timeout) - _time.GetElapsedTime(start);
        if (partBudget < TimeSpan.Zero)
        {
            partBudget = TimeSpan.Zero;
        }

        var stopGrace = StopGrace(request.Timeout);
        using var partTimer = new CancellationTokenSource(partBudget, _time);
        using var partStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, partTimer.Token);

        var handle = target.Handle;
        var hasTarget = handle != nint.Zero;
        var windowsTask = include.Contains(DesktopStateSection.Windows)
            ? Task.Run(() => _windows.GetAllWindows(includeMinimized: true), CancellationToken.None)
            : null;
        var focusTask = readFocus
            ? Start(() => _inspection.GetFocusedElementAsync(partBudget, deadline.Token))
            : null;
        var popupsTask = include.Contains(DesktopStateSection.Popups) && hasTarget
            ? Start(() => _popups.GetActivePopupsAsync(handle, partBudget, deadline.Token, stopGrace))
            : null;
        var elementsTask = include.Contains(DesktopStateSection.Elements) && hasTarget
            ? Start(() => WalkAsync(handle, target.Title, request, partBudget, stopGrace, partStop.Token))
            : null;
        Task<ScreenCapture<ScreenshotResult>>? captureTask = null;
        if (include.Contains(DesktopStateSection.Screenshot) && hasTarget)
        {
            if (CaptureRefusal(handle) is { } captureRefusal)
            {
                errors.Add(captureRefusal);
            }
            else
            {
                var options = new CaptureOptions { MaxDimension = request.ScreenshotMaxLongEdge, Output = "image" };
                captureTask = Task.Run(() => _screenshots.CaptureWindow(handle, includeFrame: true, options), CancellationToken.None);
            }
        }

        // One deadline for everything: what has not answered by then is reported, not waited for
        Task?[] parts = [windowsTask, focusTask, popupsTask, elementsTask, captureTask];
        try
        {
            var remaining = request.Timeout - _time.GetElapsedTime(start);
            await Task.WhenAll(parts.OfType<Task>()).WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _time, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A part that failed or did not finish is reported below, each on its own
        }

        cancellationToken.ThrowIfCancellationRequested();

        // A part still running is abandoned: ask it to stop now, before its tokens' sources are disposed
        await partStop.CancelAsync().ConfigureAwait(false);
        await deadline.CancelAsync().ConfigureAwait(false);
        var timeoutMs = (int)request.Timeout.TotalMilliseconds;

        var windows = Collect(windowsTask, "windows", timeoutMs, errors);
        var rows = windows?.Take(Math.Max(1, request.MaxWindows)).Select(Row).ToList();

        var focus = Collect(focusTask, "focus", timeoutMs, errors) is { } focused
            ? FocusOf(focused, request.Window, foreground, errors)
            : null;

        var popupResult = Collect(popupsTask, "popups", timeoutMs, errors);
        var popups = popupResult is null ? null : PopupsOf(popupResult, errors);

        var walk = Collect(elementsTask, "elements", timeoutMs, errors);
        if (walk?.Error is { } walkError)
        {
            errors.Add(walkError);
        }

        // The same popups are left out of both sections; either may not have been read
        var popupsExcluded = Math.Max(popupResult?.PopupsExcluded ?? 0, walk?.PopupsExcluded ?? 0);

        EncodedImage? image = null;
        ScreenshotResult? screenshot = null;
        if (Collect(captureTask, "screenshot", timeoutMs, errors) is { } capture)
        {
            if (capture.Metadata.Success)
            {
                screenshot = capture.Metadata;
                image = capture.Image;
            }
            else
            {
                errors.Add(new DesktopStateError("screenshot", capture.Metadata.ErrorCode ?? nameof(ScreenshotErrorCode.CaptureFailed),
                    capture.Metadata.Error ?? "The window could not be captured."));
            }
        }

        var state = new DesktopState(
            Success: true,
            WindowHandle: handle,
            WindowTitle: AppText.Cut(target.Title),
            Foreground: foreground is null ? null : new DesktopWindow(foreground.Handle, AppText.Cut(foreground.Title),
                foreground.ProcessName, foreground.ProcessId, foreground.IsAgentHost ? true : null),
            Focus: focus,
            Cursor: cursor,
            Input: input,
            Windows: rows,
            WindowCount: windows?.Count,
            Popups: popups,
            Elements: walk?.Elements,
            Screenshot: screenshot,
            Errors: errors.Count == 0 ? null : Ordered(errors),
            ElapsedMilliseconds: Math.Round(_time.GetElapsedTime(start).TotalMilliseconds, 1),
            PopupsExcluded: popupsExcluded > 0 ? popupsExcluded : null);
        return new DesktopStateSnapshot(state, image);
    }

    /// <summary>
    /// The window to read: the one named unless the user's policy denies it, else the window in front unless it is the agent's
    /// host or a denied app. A window whose process cannot be named counts as denied while the policy denies anything, as the
    /// reads themselves refuse it (<see cref="UiaTargets.ReadVerdict"/>).
    /// </summary>
    private Target ChooseTarget(nint? named, WindowInfo? foreground)
    {
        if (named is { } window)
        {
            if (!_windows.IsValidWindow(window))
            {
                return new Target(window, null, null, Invalid: true);
            }

            // Named or not, a window of an app the user denies is not read, as get_ui_elements and capture_window refuse it
            var info = Read(() => _windows.GetWindowInfo(window));
            if (info != null && _policy.EvaluateFailClosed(info) is { Allowed: false } denied)
            {
                return new Target(nint.Zero, null, TargetError(nameof(InputErrorCode.TargetDeniedByPolicy),
                    $"The window named ('{info.Title}', {info.ProcessName}) is refused by the user's target policy: {denied.Reason}. " +
                    "Its elements, popups and capture were not read; no argument changes that."));
            }

            return new Target(window, info?.Title, null);
        }

        if (foreground is null)
        {
            return new Target(nint.Zero, null, TargetError(nameof(WindowErrorCode.NoForegroundWindow),
                "No window is in the foreground right now (windows may be switching, or a UAC prompt or the lock screen is shown), " +
                "so no window's elements, popups or capture were read. Call again shortly, or name the window (handle or window)."));
        }

        if (foreground.IsAgentHost)
        {
            return new Target(nint.Zero, null, TargetError(nameof(InputErrorCode.TargetIsAgentHost),
                $"The window in front is your own host window ('{foreground.Title}', {foreground.ProcessName}), so its elements, " +
                "popups and capture were not read. Name the window you mean (handle or window, for example from the windows " +
                "listed here)."));
        }

        if (_policy.EvaluateFailClosed(foreground) is { Allowed: false } verdict)
        {
            return new Target(nint.Zero, null, TargetError(nameof(InputErrorCode.TargetDeniedByPolicy),
                $"The window in front ('{foreground.Title}', {foreground.ProcessName}) is refused by the user's target policy: " +
                $"{verdict.Reason}. Its elements, popups and capture were not read; no argument changes that."));
        }

        return new Target(foreground.Handle, foreground.Title, null);
    }

    private static DesktopStateError TargetError(string code, string message) => new("target", code, message);

    /// <summary>
    /// Why the window in front, and so the keyboard focus in it, is not read: TargetIsAgentHost for the agent's host unless it
    /// is the window named, TargetDeniedByPolicy for an app the user's policy denies (or whose process cannot be named while
    /// the policy denies anything). Null when it is read, and when nothing is in front (the focus read then says where the
    /// focus is, and <see cref="FocusRefusal"/> checks that window).
    /// </summary>
    private string? ForegroundRefusal(WindowInfo? foreground, nint? named)
    {
        if (foreground is null)
        {
            return null;
        }

        if (foreground.IsAgentHost && foreground.Handle != named)
        {
            return nameof(InputErrorCode.TargetIsAgentHost);
        }

        return _policy.EvaluateFailClosed(foreground) is { Allowed: false } ? nameof(InputErrorCode.TargetDeniedByPolicy) : null;
    }

    /// <summary>Why the capture is not made: captures are turned off, or the policy denies the window (as capture_window).</summary>
    private DesktopStateError? CaptureRefusal(nint handle)
    {
        if (ScreenshotUnavailable() is { } unavailable)
        {
            return new DesktopStateError("screenshot", ToolErrorReporting.InvalidArgumentsErrorCode, unavailable);
        }

        // A window whose process cannot be read cannot be checked against the policy: nothing is captured (as capture_window)
        if (Read(() => _windows.GetWindowInfo(handle)) is not { } window)
        {
            return new DesktopStateError("screenshot", nameof(ScreenshotErrorCode.CaptureFailed),
                "The window could not be read (it may have closed), so it could not be checked against the user's target " +
                "policy. Nothing was captured; call get_desktop_state again, or capture_window.");
        }

        // A process that cannot be named cannot be checked either (as capture_window)
        return _policy.EvaluateFailClosed(window) is { Allowed: false } verdict
            ? new DesktopStateError("screenshot", nameof(ScreenshotErrorCode.TargetDeniedByPolicy),
                $"The window is refused by the user's target policy: {verdict.Reason}. Nothing was captured; no argument changes that.")
            : null;
    }

    /// <summary>The pause, the modifiers that are down and Caps Lock: what get_control_status reads, and the toggle bit.</summary>
    private DesktopInputState ReadInputState()
    {
        var pause = _session.PauseState;
        var modifiers = Read(_session.ReadModifiersDown)?.Select(k => k.Name).ToList();
        return new DesktopInputState(
            pause.Paused,
            pause.Paused ? pause.PausedBy : null,
            modifiers is { Count: > 0 } ? modifiers : null,
            Read(() => _input.IsKeyToggled(NativeEnums.VK_CAPITAL)));
    }

    /// <summary>
    /// The target's elements: with scope windowAndPopups, first each open popup (no tooltip, none of the agent's host or of an
    /// app the policy denies: they are counted), front first, within half of maxElements, each under a header line; then the
    /// window with what is left.
    /// </summary>
    private async Task<ElementsWalk> WalkAsync(
        nint window, string? title, DesktopStateRequest request, TimeSpan budget, TimeSpan stopGrace, CancellationToken stop)
    {
        var scope = request.Scope ?? (_popups.IsInForeground(window) ? ElementSearchScope.WindowAndPopups : ElementSearchScope.Window);
        var (readable, excluded) = scope == ElementSearchScope.WindowAndPopups && _popups.GetPopupList(window) is { } list
            ? list.ForReading()
            : ([], 0);
        var popups = readable.Where(p => p.Kind != PopupKind.Tooltip).ToList();

        var warmupBudget = AccessibilityWarmup.BudgetFor(budget);
        var text = new StringBuilder();
        var count = 0;
        var truncated = false;
        UIElementTruncationReason? reason = null;
        var popupBudget = popups.Count > 0 ? Math.Max(1, request.MaxElements / PopupShareDivisor) : 0;

        foreach (var popup in popups)
        {
            if (popupBudget <= 0 || stop.IsCancellationRequested)
            {
                truncated = true;
                reason ??= stop.IsCancellationRequested ? UIElementTruncationReason.Timeout : UIElementTruncationReason.MaxElements;
                break;
            }

            UIElementResult read;
            try
            {
                read = await _elements.GetUIElementsAsync(popup.Handle, MaxDepth, Filter(request, popupBudget, warmupBudget, stopGrace),
                    null, stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A popup whose provider fails is skipped like one that closed: the window is still read
                truncated = true;
                reason ??= UIElementTruncationReason.ElementsSkipped;
                continue;
            }

            if (!read.Success || read.Elements is not { Count: > 0 } elements)
            {
                // A popup that closed meanwhile, or does not answer: the window and the other popups are still read
                truncated |= !read.Success;
                reason ??= read.Success ? null : UIElementTruncationReason.ElementsSkipped;
                continue;
            }

            Append(text, $"# popup {(long)popup.Handle} {popup.Kind}{(string.IsNullOrEmpty(popup.Title) ? "" : $" {Quote(popup.Title)}")}");
            Append(text, UIElementTextFormat.ToText(elements));
            count += read.TotalElementCount;
            popupBudget -= read.TotalElementCount;
            if (read.Truncated)
            {
                truncated = true;
                reason ??= read.TruncationReason;
            }
        }

        var remaining = request.MaxElements - count;
        if (remaining <= 0)
        {
            return new ElementsWalk(Elements(text, count, true, UIElementTruncationReason.MaxElements, scope, null), null, excluded);
        }

        // Asked to stop during the popups: a window walk started now would only spend the time left before the deadline on
        // its hung-window check and first reads, and hand back nothing
        if (stop.IsCancellationRequested)
        {
            return new ElementsWalk(Elements(text, count, true, UIElementTruncationReason.Timeout, scope, null), null, excluded);
        }

        var result = await _elements.GetUIElementsAsync(window, MaxDepth, Filter(request, remaining, warmupBudget, stopGrace), null, stop)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            var error = new DesktopStateError("elements", result.ErrorCode.ToString(),
                result.Error ?? "The window's elements could not be read.");
            return new ElementsWalk(count > 0 ? Elements(text, count, true, reason ?? UIElementTruncationReason.ElementsSkipped, scope, null) : null,
                error, excluded);
        }

        if (result.Elements is { Count: > 0 } windowElements)
        {
            if (text.Length > 0)
            {
                Append(text, $"# window {(long)window}{(string.IsNullOrEmpty(title) ? "" : $" {Quote(title)}")}");
            }

            Append(text, UIElementTextFormat.ToText(windowElements));
            count += result.TotalElementCount;
        }

        if (result.Truncated)
        {
            truncated = true;
            reason = result.TruncationReason ?? reason;
        }

        // The window's warnings explain a thin or empty walk (roadmap M3.F16): a minimized or an elevated window
        return new ElementsWalk(
            Elements(text, count, truncated, truncated ? reason : null, scope, result.AccessibilityWarmup) with { Warnings = result.Warnings },
            null, excluded);
    }

    private static UIElementFilter Filter(DesktopStateRequest request, int maxElements, TimeSpan warmupBudget, TimeSpan stopGrace) => new(
        InteractableOnly: request.InteractableOnly,
        VisibleOnly: true,
        MaxElements: maxElements,
        AccessibilityWarmupBudget: warmupBudget,
        StopGrace: stopGrace);

    private static DesktopElements Elements(
        StringBuilder text, int count, bool truncated, UIElementTruncationReason? reason, ElementSearchScope scope,
        AccessibilityWarmupInfo? warmup) =>
        new(text.Length == 0 ? null : text.ToString(), count, truncated, reason, scope, warmup);

    private static void Append(StringBuilder text, string lines)
    {
        if (text.Length > 0)
        {
            text.Append('\n');
        }

        text.Append(lines);
    }

    /// <summary>A title in single quotes, exactly as the element lines quote names (nothing in it can start another line).</summary>
    private static string Quote(string value) => UIElementTextFormat.Quote(value);

    private DesktopFocus? FocusOf(FocusedElementResult focused, nint? named, WindowInfo? foreground, List<DesktopStateError> errors)
    {
        if (!focused.Success || focused.Element is not { } element)
        {
            errors.Add(new DesktopStateError("focus", focused.ErrorCode.ToString(), focused.Error ?? "No element has keyboard focus."));
            return null;
        }

        if (FocusRefusal(focused, named, foreground) is { } refusal)
        {
            errors.Add(refusal);
            return null;
        }

        // The value is left out: get_focused_element reads it when it is needed (never for a password field)
        return new DesktopFocus(element.Ref, element.ControlType, element.Name, AppText.Cut(NullIfEmpty(element.AutomationId)),
            element.Bounds, focused.WindowHandle, focused.ProcessName, element.IsPassword ? true : null);
    }

    /// <summary>
    /// Why the focused element is left out, checked again after the read (the focus can move, or be in a window other than the
    /// one in front): its window (the window in front when the read does not say) is a window the policy denies, named or
    /// not; it was not named and is the agent's host, or cannot be checked. The agent's host that was named is read, as its
    /// elements are.
    /// </summary>
    private DesktopStateError? FocusRefusal(FocusedElementResult focused, nint? named, WindowInfo? foreground)
    {
        var window = focused.WindowHandle is { } known and not 0 ? (nint)known : foreground?.Handle ?? nint.Zero;
        var isNamed = window != nint.Zero && window == named;
        var info = window == nint.Zero ? null : window == foreground?.Handle ? foreground : Read(() => _windows.GetWindowInfo(window));
        if (info is null)
        {
            // The window named was checked against the policy before anything was read
            return isNamed
                ? null
                : new DesktopStateError("focus", nameof(UIElementErrorCode.OperationException),
                    "The window that holds the keyboard focus could not be read, so it could not be checked against the user's " +
                    "target policy, and the focused element was left out. Read it with get_focused_element.");
        }

        if (info.IsAgentHost && !isNamed)
        {
            return FocusError(nameof(InputErrorCode.TargetIsAgentHost), $"your own host window ('{info.Title}', {info.ProcessName})");
        }

        return _policy.EvaluateFailClosed(info) is { Allowed: false }
            ? FocusError(nameof(InputErrorCode.TargetDeniedByPolicy), $"'{info.Title}' ({info.ProcessName})")
            : null;
    }

    private static DesktopStateError FocusError(string code, string window) => new("focus", code,
        code == nameof(InputErrorCode.TargetIsAgentHost)
            ? $"The keyboard focus is in {window}, which is not read unless you name it, so the focused element was left out."
            : $"The keyboard focus is in {window}, an app the user's target policy refuses, so the focused element was left out; " +
              "no argument lets you act on it.");

    private static IReadOnlyList<ActivePopup>? PopupsOf(ActivePopupsResult result, List<DesktopStateError> errors)
    {
        if (result.Success)
        {
            return result.Popups ?? [];
        }

        errors.Add(new DesktopStateError("popups", result.ErrorCode.ToString(), result.Error ?? "The popups could not be read."));
        return null;
    }

    private static DesktopWindowRow Row(WindowInfo window) => new(
        window.Handle, AppText.Cut(window.Title), window.ProcessName, window.State.ToString().ToLowerInvariant(), window.MonitorIndex,
        window.IsAgentHost ? true : null);

    /// <summary>
    /// The part's result, or null after adding why there is none: it did not finish by the deadline (OperationTimedOut) or it
    /// failed (OperationException).
    /// </summary>
    private static T? Collect<T>(Task<T>? part, string section, int timeoutMs, List<DesktopStateError> errors)
        where T : class
    {
        if (part is null)
        {
            return null;
        }

        if (part.IsCompletedSuccessfully)
        {
            return part.Result;
        }

        if (!part.IsCompleted || part.IsCanceled || part.Exception?.InnerException is OperationCanceledException or TimeoutException)
        {
            errors.Add(new DesktopStateError(section, nameof(UIElementErrorCode.OperationTimedOut),
                $"Not read within the call's deadline ({timeoutMs.ToString(CultureInfo.InvariantCulture)} ms, timeoutMs): an " +
                "application may be slow or not responding. The rest of the observation is complete; read this part again, " +
                "with a longer timeoutMs or its own tool."));
            return null;
        }

        errors.Add(new DesktopStateError(section, nameof(UIElementErrorCode.OperationException),
            $"Could not be read: {part.Exception?.InnerException?.Message ?? "unknown error"}"));
        return null;
    }

    private static readonly string[] SectionOrder = ["target", "windows", "focus", "popups", "elements", "screenshot"];

    private static IReadOnlyList<DesktopStateError> Ordered(List<DesktopStateError> errors) =>
        errors.OrderBy(e => Array.IndexOf(SectionOrder, e.Section)).ToList();

    private static DesktopState Failed(nint window, DesktopInputState input, TimeSpan elapsed) => new(
        Success: false,
        WindowHandle: window,
        WindowTitle: null,
        Foreground: null,
        Focus: null,
        Cursor: null,
        Input: input,
        Windows: null,
        WindowCount: null,
        Popups: null,
        Elements: null,
        Screenshot: null,
        Errors: null,
        ElapsedMilliseconds: Math.Round(elapsed.TotalMilliseconds, 1),
        Error: $"{(long)window} is no window (it closed, or the handle is wrong). Nothing was read. Call get_desktop_state without " +
               "handle or window for the window in front, or find the window again with find_windows.",
        ErrorCode: nameof(UIElementErrorCode.InvalidHandle));

    /// <summary>Starts an async read so that one that throws before its first await is reported like one that fails later.</summary>
    private static Task<T> Start<T>(Func<Task<T>> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }

    /// <summary>A cheap read that must not fail the observation: null when it throws.</summary>
    private static T? Read<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return default;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed record Target(nint Handle, string? Title, DesktopStateError? Refusal, bool Invalid = false);

    /// <param name="PopupsExcluded">The popups the walk left out: of the agent's host, or of an app the policy denies.</param>
    private sealed record ElementsWalk(DesktopElements? Elements, DesktopStateError? Error, int PopupsExcluded = 0);
}
