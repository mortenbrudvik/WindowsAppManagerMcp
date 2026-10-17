using System.Drawing;
using System.Globalization;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// wait_for_visual's waits (roadmap M6.4, see <see cref="IVisualChangeService"/>). Each check reads the target's pixels
/// through <see cref="IScreenCaptureWrapper.WithPixels{T}"/>, which lends them without a copy, and keeps only their visual
/// signature (a hash per 16 x 16 tile). A window is read as capture_window reads it: refused by the target policy and the
/// capture health checks, drawn by itself (PrintWindow under the window-call deadline), copied from the screen for the rest
/// of the wait once it does not answer, and cut to its visible frame. A region or monitor is copied from the screen, and
/// the windows the policy denies over it are left out of the comparison and reported (owner decision D8). The text caret
/// the target's thread reports is left out too, so a blinking caret is no change. The last frame is registered as a capture
/// without an image, so its captureId can be the next wait's since.
/// </summary>
public sealed class VisualChangeService : IVisualChangeService
{
    /// <summary>The longest wait, as wait_for_window's.</summary>
    public const int MaxTimeoutMs = WindowWaitService.MaxTimeoutMs;

    /// <summary>The shortest time from one frame to the next.</summary>
    public const int MinPollMs = 100;

    /// <summary>The longest time from one frame to the next.</summary>
    public const int MaxPollMs = 2000;

    /// <summary>The default time from one frame to the next.</summary>
    public const int DefaultPollMs = 200;

    /// <summary>
    /// The largest area one check reads: 32 megapixels, an 8K monitor. Each check reads the whole area, so a larger virtual
    /// screen is watched one monitor at a time.
    /// </summary>
    public const long MaxPolledPixels = 32L * 1024 * 1024;

    /// <summary>
    /// The warning when the wait starts again from a frame because it was read another way than its baseline: a window
    /// that stopped answering is copied from the screen, or since's capture was.
    /// </summary>
    public const string BaselineResetWarning = "BaselineReset";

    /// <summary>How often the wait reports its progress.</summary>
    private const int ProgressIntervalMs = 1000;

    /// <summary>How far around the caret's rectangle is left out: a caret is drawn a pixel or two wider than it says.</summary>
    private const int CaretMargin = 2;

    private readonly IScreenCaptureWrapper _capture;
    private readonly IScreenshotService _screenshots;
    private readonly INativeWindowWrapper _windows;
    private readonly IMonitorService _monitors;
    private readonly ICaptureTargetPolicy _capturePolicy;
    private readonly IWindowCallRunner _windowCalls;
    private readonly ICaptureRegistry? _registry;
    private readonly INativeThreadInputWrapper? _threads;
    private readonly ITargetPolicy? _policy;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="capture">Lends the pixels.</param>
    /// <param name="screenshots">The capture health checks of a window (CheckWindow).</param>
    /// <param name="windows">Window rectangles, frames, processes and threads, and the window list of the policy's mask.</param>
    /// <param name="monitors">Monitor targets, the DPI warning and the monitors a screen frame registers.</param>
    /// <param name="capturePolicy">The target policy's check of a window capture.</param>
    /// <param name="windowCalls">Runs PrintWindow with a deadline; default a <see cref="WindowCallRunner"/> over <paramref name="windows"/>.</param>
    /// <param name="registry">Where since is found and the last frame is registered; without it there is neither.</param>
    /// <param name="threads">Reads where the text caret is; without it the caret counts like any pixel.</param>
    /// <param name="policy">The target policy: one that denies nothing masks nothing without listing windows. Null: every
    /// window over a region or monitor is checked.</param>
    /// <param name="time">The clock; default the system clock.</param>
    /// <param name="delay">Waits between two frames; default Task.Delay on <paramref name="time"/>. Tests pass one that moves
    /// a manual clock.</param>
    public VisualChangeService(
        IScreenCaptureWrapper capture,
        IScreenshotService screenshots,
        INativeWindowWrapper windows,
        IMonitorService monitors,
        ICaptureTargetPolicy capturePolicy,
        IWindowCallRunner? windowCalls = null,
        ICaptureRegistry? registry = null,
        INativeThreadInputWrapper? threads = null,
        ITargetPolicy? policy = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _screenshots = screenshots ?? throw new ArgumentNullException(nameof(screenshots));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _monitors = monitors ?? throw new ArgumentNullException(nameof(monitors));
        _capturePolicy = capturePolicy ?? throw new ArgumentNullException(nameof(capturePolicy));
        _windowCalls = windowCalls ?? new WindowCallRunner(windows);
        _registry = registry;
        _threads = threads;
        _policy = policy;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((wait, token) => Task.Delay(wait, _time, token));
    }

    /// <summary>The condition as wait_for_visual names it.</summary>
    internal static string ConditionName(VisualWaitCondition condition) =>
        condition == VisualWaitCondition.Stable ? "stable" : "changed";

    /// <inheritdoc />
    public async Task<VisualWaitResult> WaitAsync(
        VisualWaitRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = _time.GetTimestamp();
        var wait = new Wait(this, request, () => (long)_time.GetElapsedTime(start).TotalMilliseconds);
        return await wait.RunAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A frame: its signature and what it leaves out, or why the window is no longer there to read. The geometry is built
    /// only for the frame that is registered, so a poll does not read the monitors.
    /// </summary>
    private sealed record Frame(
        VisualSignature? Signature,
        string? Method,
        IReadOnlyList<Rectangle> Excluded,
        Func<CaptureGeometry?> Geometry,
        string? GoneReason = null,
        string? GoneCode = null,
        string? GoneMessage = null)
    {
        public static Frame Gone(CaptureRefusal refusal) => new(null, null, [], () => null, refusal.Code switch
        {
            ScreenshotErrorCode.WindowMinimized => "minimized",
            ScreenshotErrorCode.WindowHidden or ScreenshotErrorCode.Cloaked => "hidden",
            _ => "closed"
        }, refusal.Code.ToString(), refusal.Message);
    }

    /// <summary>Why a target cannot be watched, with its errorCode.</summary>
    private sealed class Refused(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>
    /// The windows the policy denies over what a wait copies from the screen (owner decision D8), gathered for the whole
    /// wait: every rectangle a denied window had stays left out, so a denied window that moves shows at neither place.
    /// </summary>
    private sealed class MaskTracker(VisualChangeService service, nint target = 0)
    {
        private readonly DeniedWindowMask _mask = new(service._windows, service._capturePolicy, service._policy);
        private readonly Dictionary<long, MaskedWindow> _masked = [];
        private readonly List<Rectangle> _areas = [];

        public IReadOnlyList<MaskedWindow> Masked => _masked.Values.ToList();

        public IReadOnlyList<Rectangle> Areas => _areas;

        /// <summary>Adds the denied windows over <paramref name="area"/> now; the target window itself is never one.</summary>
        public void Add(Rectangle area)
        {
            foreach (var window in _mask.Over(area))
            {
                if (window.Handle == (long)target)
                {
                    continue;
                }

                _masked[window.Handle] = window;
                var rectangle = new Rectangle(window.X, window.Y, window.Width, window.Height);
                if (!_areas.Contains(rectangle))
                {
                    _areas.Add(rectangle);
                }
            }
        }
    }

    /// <summary>What one wait reads: a window, or a screen area.</summary>
    private abstract class Source
    {
        public List<string> Warnings { get; } = [];

        public virtual IReadOnlyList<MaskedWindow> Masked => [];

        /// <summary>Reads the next frame.</summary>
        /// <exception cref="Refused">The pixels could not be read (CaptureFailed).</exception>
        public abstract Frame Read();
    }

    private sealed class WindowSource(VisualChangeService service, nint handle, bool includeFrame, uint expectedProcessId) : Source
    {
        private bool _fromScreen;
        private MaskTracker? _mask;

        /// <summary>Only once the window is copied from the screen: the denied windows that covered it.</summary>
        public override IReadOnlyList<MaskedWindow> Masked => _mask?.Masked ?? [];

        public override Frame Read()
        {
            var windows = service._windows;
            if (service._screenshots.CheckWindow(handle) is { } refusal)
            {
                return Frame.Gone(refusal);
            }

            var processId = windows.GetWindowThreadProcessId(handle);
            if (expectedProcessId != 0 && processId != expectedProcessId)
            {
                // The window closed, and its handle was given to a window of another process
                return Frame.Gone(new CaptureRefusal(ScreenshotErrorCode.InvalidHandle,
                    "The window closed: its handle now belongs to a window of another process."));
            }

            var rect = windows.GetWindowRect(handle);
            var frame = includeFrame ? windows.GetExtendedFrameBounds(handle) : null;
            PixelReader<(VisualSignature Signature, string Method)> read = pixels =>
            {
                // As capture_window cuts a frame capture to the visible frame; a reader keeps nothing of the pixels, since an
                // abandoned PrintWindow can still call it after the deadline
                var visible = frame is { } f
                    ? Rectangle.Intersect(Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom),
                        new Rectangle(pixels.ScreenOriginX, pixels.ScreenOriginY, pixels.Width, pixels.Height))
                    : Rectangle.Empty;
                var view = visible.Width > 0 && visible.Height > 0 && (visible.Width != pixels.Width || visible.Height != pixels.Height)
                    ? pixels.Crop(visible.X, visible.Y, visible.Width, visible.Height)
                    : pixels;
                return (VisualSignature.Of(view), view.Method);
            };

            (VisualSignature Signature, string Method) pixels;
            try
            {
                pixels = ReadPixels(read, rect);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A window that closed or was minimized while it was read is gone, not a failure
                if (service._screenshots.CheckWindow(handle) is { } gone)
                {
                    return Frame.Gone(gone);
                }

                throw new Refused(nameof(ScreenshotErrorCode.CaptureFailed), $"The window's pixels could not be read: {ex.Message}");
            }

            var signature = pixels.Signature;
            var excluded = new List<Rectangle>(_mask?.Areas ?? []);
            excluded.AddRange(service.Caret(windows.GetWindowThreadId(handle)));
            return new Frame(signature, pixels.Method, excluded, () => rect is { } r
                ? new CaptureGeometry(signature.Width, signature.Height,
                    new ImageToScreenTransform(signature.OriginX, signature.OriginY, 1, 1), signature.Region,
                    new CapturedWindow((long)handle, (int)processId, WindowRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), includeFrame,
                        includeFrame ? null : new WindowRect(signature.OriginX, signature.OriginY, signature.Width, signature.Height)),
                    VirtualScreen: null)
                : null);
        }

        /// <summary>
        /// The window drawn by itself (PrintWindow under the window-call deadline), or copied from the screen for the rest of
        /// the wait once it does not answer.
        /// </summary>
        private (VisualSignature, string) ReadPixels(PixelReader<(VisualSignature, string)> read, (int Left, int Top, int Right, int Bottom)? rect)
        {
            if (!_fromScreen)
            {
                try
                {
                    return service._windowCalls.Run(handle, () => service._capture.WithPixels(new PixelSource.Window(handle, includeFrame), read));
                }
                catch (WindowNotRespondingException ex)
                {
                    _fromScreen = true;
                    Warnings.Add(CaptureWarnings.Format(CaptureWarnings.WindowNotResponding,
                        "the window could not be asked to draw itself, so the wait copies the screen there from now on and sees " +
                        $"any window that covers it (those the policy denies are left out). Reason: {ex.Message}"));
                }
            }

            // The denied windows over the window's rectangle are listed before and after the copy, as for a region (D8)
            _mask ??= new MaskTracker(service, handle);
            var area = rect is { } r ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;
            _mask.Add(area);
            var pixels = service._capture.WithPixels(new PixelSource.WindowFromScreen(handle, includeFrame), read);
            _mask.Add(area);
            return pixels;
        }
    }

    private sealed class ScreenSource(VisualChangeService service, Rectangle area) : Source
    {
        private readonly MaskTracker _mask = new(service);

        public override IReadOnlyList<MaskedWindow> Masked => _mask.Masked;

        public override Frame Read()
        {
            // The denied windows are listed before and after the pixels are copied, so one that opens or moves in between
            // is left out of this frame too
            _mask.Add(area);
            (VisualSignature Signature, string Method) read;
            try
            {
                read = service._capture.WithPixels(new PixelSource.Screen(area.X, area.Y, area.Width, area.Height),
                    static pixels => (VisualSignature.Of(pixels), pixels.Method));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new Refused(nameof(ScreenshotErrorCode.CaptureFailed), $"The screen could not be read: {ex.Message}");
            }

            _mask.Add(area);
            var excluded = new List<Rectangle>(_mask.Areas);
            excluded.AddRange(service.Caret(service._windows.GetWindowThreadId(service._windows.GetForegroundWindow())));
            var signature = read.Signature;
            return new Frame(signature, read.Method, excluded, () =>
            {
                var screen = service._capture.GetVirtualScreenBounds();
                return new CaptureGeometry(signature.Width, signature.Height,
                    new ImageToScreenTransform(signature.OriginX, signature.OriginY, 1, 1), signature.Region, Window: null,
                    new VirtualScreenBounds(screen.X, screen.Y, screen.Width, screen.Height), service.ReadMonitors());
            });
        }
    }

    /// <summary>The text caret of <paramref name="thread"/> on the screen, widened by a margin; none without one.</summary>
    private IReadOnlyList<Rectangle> Caret(uint thread)
    {
        if (_threads == null || thread == 0 || _threads.GetGuiThreadInfo(thread) is not { Caret: { } caret })
        {
            return [];
        }

        var rectangle = new Rectangle(caret.X, caret.Y, caret.Width, caret.Height);
        rectangle.Inflate(CaretMargin, CaretMargin);
        return [rectangle];
    }

    /// <summary>The monitors a screen frame registers, or null when they cannot be read.</summary>
    private IReadOnlyList<CapturedMonitor>? ReadMonitors()
    {
        try
        {
            return CapturedMonitors.Of(_monitors.GetAllMonitors());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>One wait: its target, its baseline and what it saw so far.</summary>
    private sealed class Wait(VisualChangeService service, VisualWaitRequest request, Func<long> elapsed)
    {
        private readonly string _condition = ConditionName(request.Condition);
        private Source? _source;
        private Frame? _last;
        private int _checks;

        public async Task<VisualWaitResult> RunAsync(IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
        {
            VisualSignature? reference;
            try
            {
                (_source, reference) = Open();
            }
            catch (Refused refused)
            {
                return Failure(refused.Code, refused.Message);
            }

            var changed = request.Condition == VisualWaitCondition.Changed;
            var since = reference;
            IReadOnlyList<Rectangle> referenceExcluded = reference?.Excluded ?? [];
            long deadline = request.TimeoutMs;
            VisualChange? lastChange = null;
            IReadOnlyList<BoundsDto>? motion = null;
            double? motionPercent = null;
            var runStart = 0L;
            var runHolds = false;
            var lastProgress = 0L;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readAt = elapsed();
                Frame frame;
                try
                {
                    frame = _source.Read();
                }
                catch (Refused refused)
                {
                    return Failure(refused.Code, refused.Message);
                }

                _checks++;
                if (frame.GoneReason is { } gone)
                {
                    // A window that closed or left the screen is a visible change, but nothing is left to be still. Its last
                    // frame is not registered: a capture of a window that is not on the screen has nothing to click on
                    var where = (_last?.Signature ?? since) is { } before ? VisualDiff.Whole(before) : null;
                    _last = null;
                    return changed ? Satisfied(gone, changedPercent: null, where) : Failure(frame.GoneCode!, frame.GoneMessage!);
                }

                var signature = frame.Signature!;
                _last = frame;
                if (reference != null && ReadAnotherWay(reference, signature, fromSince: ReferenceEquals(reference, since)))
                {
                    // Frames read by PrintWindow and by a screen copy differ where nothing changed, so the wait starts again
                    // from this one
                    reference = null;
                    lastChange = null;
                }

                if (reference == null)
                {
                    reference = signature;
                    referenceExcluded = frame.Excluded;
                    runStart = readAt;
                    runHolds = false;
                    if (!changed && _checks == 1)
                    {
                        // A stable wait counts its time from its first frame, so stableMs up to timeoutMs can be met
                        deadline += readAt;
                    }
                }
                else
                {
                    var change = VisualDiff.Compare(reference, signature, [.. referenceExcluded, .. frame.Excluded]);
                    if (changed)
                    {
                        if (change == null)
                        {
                            return Satisfied("resized", 100, VisualDiff.Whole(signature));
                        }

                        if (Counts(change))
                        {
                            return Satisfied("pixels", change.ChangedPercent, change.Regions);
                        }

                        lastChange = change;
                    }
                    else
                    {
                        if (change == null || Counts(change))
                        {
                            runStart = readAt;
                            runHolds = false;
                            motion = change?.Regions ?? VisualDiff.Whole(signature);
                            motionPercent = change?.ChangedPercent ?? 100;
                        }
                        else
                        {
                            runHolds = true;
                        }

                        reference = signature;
                        referenceExcluded = frame.Excluded;
                        if (runHolds && readAt - runStart >= request.StableMs)
                        {
                            return Satisfied(reason: null, changedPercent: null, regions: null) with { StableForMs = readAt - runStart };
                        }
                    }
                }

                var now = elapsed();
                var remaining = deadline - now;
                if (remaining <= 0)
                {
                    return changed
                        ? TimedOut(lastChange is { ChangedTiles: > 0 } ? lastChange.ChangedPercent : 0, lastChange is { ChangedTiles: > 0 } ? lastChange.Regions : null)
                        : TimedOut(motionPercent, motion);
                }

                if (progress != null && now - lastProgress >= ProgressIntervalMs)
                {
                    lastProgress = now;
                    progress.Report(new OperationProgress((int)Math.Min(now, request.TimeoutMs), request.TimeoutMs,
                        $"waiting for {_condition}: {now} of {request.TimeoutMs} ms, {_checks} checks"));
                }

                var pause = Math.Min(Math.Max(request.PollMs - (now - readAt), 0), remaining);
                await service._delay(TimeSpan.FromMilliseconds(pause), cancellationToken).ConfigureAwait(false);
            }
        }

        private bool Counts(VisualChange change) => change.ChangedTiles > 0 && change.ChangedPercent > request.MinChangedPercent;

        /// <summary>
        /// True, with a BaselineReset warning, when <paramref name="frame"/> was read another way than
        /// <paramref name="reference"/> (PrintWindow, a screen copy): such frames differ where nothing changed.
        /// </summary>
        private bool ReadAnotherWay(VisualSignature reference, VisualSignature frame, bool fromSince)
        {
            if (reference.Method is not { } before || frame.Method is not { } now || before == now)
            {
                return false;
            }

            _source!.Warnings.Add(CaptureWarnings.Format(BaselineResetWarning, fromSince
                ? $"since's capture was read by {before} and this wait reads the target by {now}, which differ where nothing " +
                  "changed, so the wait compares with its first frame instead: a change made before it is missed."
                : $"the wait now reads the target by {now} instead of {before}, which differ where nothing changed, so it " +
                  "compares with its first frame read that way: a change made in between is missed."));
            return true;
        }

        /// <summary>The source of the target and, for since, the capture's signature as the first frame.</summary>
        /// <exception cref="Refused">The target cannot be watched.</exception>
        private (Source Source, VisualSignature? Baseline) Open()
        {
            switch (request.Target)
            {
                case VisualTarget.Window window:
                    ThrowIfRefused(service._capturePolicy.Check(window.Handle));
                    ThrowIfRefused(service._screenshots.CheckWindow(window.Handle));
                    // The window's process now, so a handle given to another window mid-wait reads as closed
                    return (OpenWindow(window.Handle, window.IncludeFrame, service._windows.GetWindowThreadProcessId(window.Handle)), null);
                case VisualTarget.Region region:
                    return (OpenRegion(region), null);
                case VisualTarget.Monitor monitor:
                    return (OpenArea(MonitorArea(monitor.Index), clipped: null), null);
                case VisualTarget.Since since:
                    return OpenSince(since.CaptureId);
                default:
                    throw new ArgumentException($"Unknown visual target {request.Target.GetType().Name}", nameof(request));
            }
        }

        private Source OpenWindow(nint handle, bool includeFrame, uint expectedProcessId)
        {
            var source = new WindowSource(service, handle, includeFrame, expectedProcessId);
            if (service._monitors.GetMonitorForWindow(handle) is { } monitor)
            {
                var windowDpi = (int)service._windows.GetDpiForWindow(handle);
                var monitorDpi = (int)Math.Round(monitor.ScaleFactor * 96);
                if (windowDpi != 0 && Math.Abs(windowDpi - monitorDpi) > 1)
                {
                    source.Warnings.Add(CaptureWarnings.Format(CaptureWarnings.DpiScaledWindow,
                        $"the window works at {windowDpi} DPI on a {monitorDpi} DPI monitor, so Windows scales it. The frames can show it " +
                        "at its unscaled size, so changedRegions can be offset from the screen; watch the region of the window instead."));
                }
            }

            return source;
        }

        private Source OpenRegion(VisualTarget.Region region)
        {
            if (region.Width <= 0 || region.Height <= 0)
            {
                throw new Refused(nameof(ScreenshotErrorCode.InvalidRegion), "Width and height must be positive values. Nothing was waited for.");
            }

            var requested = new Rectangle(region.X, region.Y, region.Width, region.Height);
            var screen = service._capture.GetVirtualScreenBounds();
            var virtualScreen = new Rectangle(screen.X, screen.Y, screen.Width, screen.Height);
            var area = Rectangle.Intersect(requested, virtualScreen);
            if (area.Width <= 0 || area.Height <= 0)
            {
                throw new Refused(nameof(ScreenshotErrorCode.RegionOffScreen),
                    $"No part of the region ({region.X}, {region.Y}, {region.Width}x{region.Height}) is on the screen: the monitors cover " +
                    $"({virtualScreen.X}, {virtualScreen.Y}, {virtualScreen.Width}x{virtualScreen.Height}). Nothing was waited for.");
            }

            return OpenArea(area, clipped: area != requested ? requested : null);
        }

        private Rectangle MonitorArea(int index)
        {
            if (index == ScreenshotService.VirtualScreenIndex)
            {
                var screen = service._capture.GetVirtualScreenBounds();
                return new Rectangle(screen.X, screen.Y, screen.Width, screen.Height);
            }

            var monitors = service._monitors.GetAllMonitors();
            if (index < 0 || index >= monitors.Count)
            {
                throw new Refused(nameof(ScreenshotErrorCode.InvalidMonitorIndex),
                    $"Monitor index {index} is out of range. Available monitors: 0-{monitors.Count - 1}, or " +
                    $"{ScreenshotService.VirtualScreenIndex} for all of them. Nothing was waited for.");
            }

            var bounds = monitors[index].Bounds;
            return new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        private Source OpenArea(Rectangle area, Rectangle? clipped)
        {
            if ((long)area.Width * area.Height > MaxPolledPixels)
            {
                throw new Refused(nameof(ScreenshotErrorCode.InvalidRegion),
                    $"The area is {area.Width}x{area.Height} pixels, more than the {MaxPolledPixels / (1024 * 1024)} megapixels one check " +
                    "reads. Watch one monitor, a window or a smaller region. Nothing was waited for.");
            }

            var source = new ScreenSource(service, area);
            if (clipped is { } requested)
            {
                source.Warnings.Add(CaptureWarnings.Format(CaptureWarnings.RegionClipped,
                    $"the region ({requested.X}, {requested.Y}, {requested.Width}x{requested.Height}) is partly off the screen; only " +
                    $"({area.X}, {area.Y}, {area.Width}x{area.Height}) is watched (capturedRegion)."));
            }

            return source;
        }

        private (Source Source, VisualSignature? Baseline) OpenSince(string captureId)
        {
            if (service._registry?.Find(captureId) is not { } record)
            {
                throw new Refused(nameof(InputErrorCode.CaptureNotFound),
                    $"No capture has the id '{captureId}': it expired (captures are kept for {CaptureRegistry.DefaultTimeToLive.TotalMinutes:0} " +
                    $"minutes), {CaptureRegistry.DefaultCapacity} newer captures pushed it out, or it is from an earlier server run. " +
                    "Take a new capture with output 'metadata' before you act, and pass its captureId. Nothing was waited for.");
            }

            if (record.Signature is not { } signature)
            {
                throw new Refused(nameof(InputErrorCode.CaptureNotFound),
                    $"The capture '{captureId}' has no visual signature to compare with: it was taken while wait_for_visual was not " +
                    "served, it is a crop to a ref, or the server dropped its signature to stay within its memory. Take a new capture " +
                    "of the whole window, region or monitor with output 'metadata' before you act, and pass its captureId. Nothing was waited for.");
            }

            if (record.Geometry.Window is { } window)
            {
                var handle = (nint)window.Handle;
                ThrowIfRefused(service._capturePolicy.Check(handle));
                return (OpenWindow(handle, window.IncludeFrame, (uint)window.ProcessId), signature);
            }

            // A monitor or region capture: the same screen rectangle, wherever the monitors are now
            return (OpenArea(new Rectangle(signature.OriginX, signature.OriginY, signature.Width, signature.Height), clipped: null), signature);
        }

        private static void ThrowIfRefused(CaptureRefusal? refusal)
        {
            if (refusal != null)
            {
                throw new Refused(refusal.Code.ToString(), refusal.Message + " Nothing was waited for.");
            }
        }

        private VisualWaitResult Satisfied(string? reason, double? changedPercent, IReadOnlyList<BoundsDto>? regions) =>
            Result(true, reason, changedPercent, regions);

        private VisualWaitResult TimedOut(double? changedPercent, IReadOnlyList<BoundsDto>? regions)
        {
            var error = request.Condition == VisualWaitCondition.Changed
                ? $"Nothing changed within {request.TimeoutMs} ms ({_checks} checks)" +
                  (regions is { Count: > 0 }
                      ? string.Create(CultureInfo.InvariantCulture,
                          $": {changedPercent}% of the tiles changed, not more than minChangedPercent {request.MinChangedPercent} (changedRegions).")
                      : ".") +
                  " A change before the first check is missed: take a capture with output 'metadata' before you act and pass its captureId as since."
                : regions == null
                    ? _checks <= 1
                        ? $"Only one frame was read within {request.TimeoutMs} ms, so there was nothing to compare it with. Wait longer."
                        : $"No change was seen, but not yet for stableMs {request.StableMs} within {request.TimeoutMs} ms ({_checks} checks). Wait longer."
                    : $"The target kept changing for {request.TimeoutMs} ms ({_checks} checks); changedRegions shows the last change. " +
                      "Raise minChangedPercent to ignore a small animation, or watch a smaller region.";
            return Result(false, reason: null, changedPercent, regions) with
            {
                TimedOut = true,
                Error = error,
                ErrorCode = nameof(WindowErrorCode.WaitTimedOut)
            };
        }

        private VisualWaitResult Failure(string code, string error) => Result(false, reason: null, changedPercent: null, regions: null) with
        {
            Error = error,
            ErrorCode = code
        };

        private VisualWaitResult Result(bool satisfied, string? reason, double? changedPercent, IReadOnlyList<BoundsDto>? regions)
        {
            var warnings = _source?.Warnings.ToList() ?? [];
            var signature = _last?.Signature;
            if (signature is { Uniform: true })
            {
                warnings.Add(CaptureWarnings.Format(CaptureWarnings.ImageBlank,
                    "every pixel of the last frame has one color: GPU or protected content this read cannot see, or a window that has " +
                    "not painted yet. Such a target never changes."));
            }

            var masked = _source?.Masked;
            return new VisualWaitResult(
                Success: satisfied,
                Satisfied: satisfied,
                TimedOut: false,
                Condition: _condition,
                ElapsedMs: elapsed(),
                Checks: _checks,
                Reason: reason,
                ChangedPercent: changedPercent,
                ChangedRegions: regions,
                CaptureId: RegisterLastFrame(),
                CapturedRegion: signature?.Region,
                CaptureMethod: _last?.Method,
                MaskedWindows: masked is { Count: > 0 } ? masked : null,
                Warnings: warnings.Count > 0 ? warnings : null);
        }

        /// <summary>The last frame as a capture without an image (its signature for since, a 1:1 transform), or null.</summary>
        /// <remarks>The frame's signature keeps what it left out (the denied windows, the caret), so a since on it leaves them
        /// out too, even once a denied window closed or moved (owner decision D8).</remarks>
        private string? RegisterLastFrame() =>
            service._registry != null && _last is { Signature: { } signature } frame && frame.Geometry() is { } geometry
                ? service._registry.Register(geometry, signature.WithExcluded(frame.Excluded)).CaptureId
                : null;
    }
}
