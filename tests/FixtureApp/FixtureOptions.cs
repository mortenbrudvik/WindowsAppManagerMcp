using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>The DPI awareness the fixture sets for its process before it creates a window.</summary>
public enum FixtureDpiAwareness
{
    PerMonitorV2,
    PerMonitor,
    System,
    Unaware
}

/// <summary>
/// The fixture's command line. The E2E harness builds it with <see cref="ToArguments"/> and the fixture
/// reads it with <see cref="Parse"/>; the E2E project compiles this file too, so both sides agree.
/// </summary>
/// <param name="Instance">Token in every window title, so tests find only the fixture they started.</param>
/// <param name="EventLogPath">Where the fixture writes its JSON-lines event log, or null for none.</param>
/// <param name="ParentProcessId">The fixture exits when this process exits (the test host), so a crashed
/// test run cannot leave it behind.</param>
/// <param name="MaxLifetime">The fixture exits after this long, whatever happens.</param>
/// <param name="NoActivate">Show the windows without activating them, so a test that only reads the
/// fixture does not take the foreground from the user.</param>
/// <param name="OpenModal">Open the modal dialog right after the main window is ready.</param>
/// <param name="OpenDocument">Open the document window (with unsaved changes) right after the main window is ready.</param>
/// <param name="DpiAwareness">The process DPI awareness.</param>
/// <param name="OpenTable">Open the table window (a WPF DataGrid) right after the main window is ready.</param>
/// <param name="OpenMenuWindow">Open the menu window (a Win32 menu bar) right after the main window is ready.</param>
/// <param name="OpenWebView">Open the WebView2 window right after the main window is ready. Only this option starts
/// WebView2, which needs the Edge WebView2 runtime.</param>
/// <param name="OtherScaleMonitor">Open the main window centred on a monitor whose scale differs from the primary's, instead
/// of on the primary (roadmap M3.F30); on a machine without one, on the primary. See <see cref="FixturePlacement"/>. The
/// canvas window follows it.</param>
/// <param name="OpenCanvas">Open the canvas window (roadmap M6.3) right after the main window is ready: known text drawn as
/// pixels only, which UI Automation cannot read. See <see cref="FixtureContract.CanvasLines"/>.</param>
/// <param name="ChangeAfter">Add the change panel to the main window and recolor and relabel it once, this long after the
/// ready event (roadmap M6.4); null for no panel. Positive, and due at least <see cref="TimedChangeMargin"/> before the end of
/// <see cref="EffectiveMaxLifetime"/>.</param>
/// <param name="AnimateFor">Add the spinner to the main window and turn it for this long after the ready event, then stop it
/// (roadmap M6.4); null for no spinner. Positive, and due at least <see cref="TimedChangeMargin"/> before the end of
/// <see cref="EffectiveMaxLifetime"/>.</param>
/// <param name="OpenOverlay">Open the overlay (roadmap M6.7) right after the main window is ready: a small unowned
/// WS_EX_TOPMOST tool window of the fixture itself, centred on the primary monitor. Unlike the canvas it does not follow
/// <paramref name="OtherScaleMonitor"/>.</param>
/// <remarks>
/// Every argument after <see cref="EndOfOptions"/> is ignored, so a test can plant arguments that only a reader of the
/// fixture's command line sees (roadmap M6.6: a secret-looking switch the server must redact). Before it, an unknown option
/// is refused, so a misspelt option still fails the start instead of being ignored.
/// </remarks>
public sealed record FixtureOptions(
    string Instance,
    string? EventLogPath = null,
    int? ParentProcessId = null,
    TimeSpan? MaxLifetime = null,
    bool NoActivate = false,
    bool OpenModal = false,
    bool OpenDocument = false,
    FixtureDpiAwareness DpiAwareness = FixtureDpiAwareness.PerMonitorV2,
    bool OpenTable = false,
    bool OpenMenuWindow = false,
    bool OpenWebView = false,
    bool OtherScaleMonitor = false,
    bool OpenCanvas = false,
    TimeSpan? ChangeAfter = null,
    TimeSpan? AnimateFor = null,
    bool OpenOverlay = false)
{
    public const string InstanceOption = "--instance";
    public const string EventLogOption = "--event-log";
    public const string ParentPidOption = "--parent-pid";
    public const string MaxLifetimeOption = "--max-lifetime-seconds";
    public const string NoActivateOption = "--no-activate";
    public const string OpenModalOption = "--open-modal";
    public const string OpenDocumentOption = "--open-document";
    public const string DpiAwarenessOption = "--dpi-awareness";
    public const string OpenTableOption = "--open-table";
    public const string OpenMenuWindowOption = "--open-menu-window";
    public const string OpenWebViewOption = "--open-web-view";
    public const string OtherScaleMonitorOption = "--other-scale-monitor";
    public const string OpenCanvasOption = "--open-canvas";
    public const string ChangeAfterOption = "--change-after-ms";
    public const string AnimateForOption = "--animate-for-ms";
    public const string OpenOverlayOption = "--open-overlay";

    /// <summary>Ends the options: every argument after it is ignored (see the remarks on <see cref="FixtureOptions"/>).</summary>
    public const string EndOfOptions = "--";

    /// <summary>The instance token when none is given, for starting the fixture by hand.</summary>
    public const string DefaultInstance = "manual";

    /// <summary>The lifetime when none is given.</summary>
    public static readonly TimeSpan DefaultMaxLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The longest lifetime the fixture accepts.</summary>
    public static readonly TimeSpan LongestMaxLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// How long before the end of the lifetime a timed change must be due. The watchdog counts the lifetime from the start,
    /// but <see cref="ChangeAfter"/> and <see cref="AnimateFor"/> count from the ready event, which the harness waits up to
    /// 30 s for (a cold WPF start, or WebView2); and the spinner stops on the first tick at or after
    /// <see cref="AnimateFor"/>, up to <see cref="FixtureContract.SpinnerIntervalMs"/> later.
    /// </summary>
    public static readonly TimeSpan TimedChangeMargin =
        TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(FixtureContract.SpinnerIntervalMs);

    private static readonly Regex InstancePattern = new("^[A-Za-z0-9-]{1,32}$");

    /// <summary>The lifetime that applies: <see cref="MaxLifetime"/> or <see cref="DefaultMaxLifetime"/>.</summary>
    public TimeSpan EffectiveMaxLifetime => MaxLifetime ?? DefaultMaxLifetime;

    /// <summary>Reads the command line.</summary>
    /// <exception cref="ArgumentException">An option before <see cref="EndOfOptions"/> is unknown, repeated, missing its
    /// value or invalid.</exception>
    public static FixtureOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new FixtureOptions(DefaultInstance);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == EndOfOptions)
            {
                break;
            }
            if (!seen.Add(name))
            {
                throw new ArgumentException($"Option {name} is given more than once.");
            }

            switch (name)
            {
                case InstanceOption:
                    options = options with { Instance = ValidInstance(ValueOf(args, ref i)) };
                    break;
                case EventLogOption:
                    options = options with { EventLogPath = ValueOf(args, ref i) };
                    break;
                case ParentPidOption:
                    options = options with { ParentProcessId = PositiveInt(name, ValueOf(args, ref i)) };
                    break;
                case MaxLifetimeOption:
                    var seconds = PositiveInt(name, ValueOf(args, ref i));
                    var lifetime = TimeSpan.FromSeconds(seconds);
                    if (lifetime > LongestMaxLifetime)
                    {
                        throw new ArgumentException(
                            $"{name} must be at most {LongestMaxLifetime.TotalSeconds:0} seconds, got {seconds}.");
                    }
                    options = options with { MaxLifetime = lifetime };
                    break;
                case NoActivateOption:
                    options = options with { NoActivate = true };
                    break;
                case OpenModalOption:
                    options = options with { OpenModal = true };
                    break;
                case OpenDocumentOption:
                    options = options with { OpenDocument = true };
                    break;
                case DpiAwarenessOption:
                    options = options with { DpiAwareness = ParseDpiAwareness(ValueOf(args, ref i)) };
                    break;
                case OpenTableOption:
                    options = options with { OpenTable = true };
                    break;
                case OpenMenuWindowOption:
                    options = options with { OpenMenuWindow = true };
                    break;
                case OpenWebViewOption:
                    options = options with { OpenWebView = true };
                    break;
                case OtherScaleMonitorOption:
                    options = options with { OtherScaleMonitor = true };
                    break;
                case OpenCanvasOption:
                    options = options with { OpenCanvas = true };
                    break;
                case ChangeAfterOption:
                    options = options with { ChangeAfter = Milliseconds(name, ValueOf(args, ref i)) };
                    break;
                case AnimateForOption:
                    options = options with { AnimateFor = Milliseconds(name, ValueOf(args, ref i)) };
                    break;
                case OpenOverlayOption:
                    options = options with { OpenOverlay = true };
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown option '{name}'. Arguments the fixture should ignore go after '{EndOfOptions}'.");
            }
        }

        // After the loop: the lifetime may come after the timed changes on the command line
        options.ThrowIfATimedChangeIsInvalid();
        return options;
    }

    /// <summary>
    /// Throws when <see cref="ChangeAfter"/> or <see cref="AnimateFor"/> is not positive (the fixture refuses it) or is due
    /// later than <see cref="TimedChangeMargin"/> before the end of <see cref="EffectiveMaxLifetime"/>: the lifetime runs
    /// from the start and the change from the ready event, so the watchdog could end the fixture first, the change would
    /// never be logged and a test waiting for it would only time out. The harness calls this when it builds the options,
    /// so the mistake fails the test where it is made.
    /// </summary>
    /// <exception cref="ArgumentException">A timed change is not positive or is not due within the lifetime less the
    /// margin.</exception>
    public void ThrowIfATimedChangeIsInvalid()
    {
        CheckTimedChange(nameof(ChangeAfter), ChangeAfterOption, ChangeAfter);
        CheckTimedChange(nameof(AnimateFor), AnimateForOption, AnimateFor);
    }

    private void CheckTimedChange(string property, string option, TimeSpan? duration)
    {
        if (duration is not { } value)
        {
            return;
        }
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentException($"{property} ({option}) must be positive, got {value.TotalMilliseconds} ms.");
        }
        // In the whole milliseconds the command line carries, so what passes here the fixture reads back as valid
        var latest = EffectiveMaxLifetime - TimedChangeMargin;
        if (Math.Ceiling(value.TotalMilliseconds) > latest.TotalMilliseconds)
        {
            throw new ArgumentException(
                $"{property} ({option}) must be at most {Math.Max(0, latest.TotalMilliseconds):0} ms, " +
                $"{TimedChangeMargin.TotalSeconds:0.##} s before the end of the fixture's lifetime of " +
                $"{EffectiveMaxLifetime.TotalSeconds:0} s, got {value.TotalMilliseconds:0} ms: the lifetime runs from the start " +
                "and the change from the ready event, so the fixture could end before it.");
        }
    }

    /// <summary>How many arguments <see cref="Parse"/> ignores: those after <see cref="EndOfOptions"/>.</summary>
    /// <remarks>
    /// No option's value can be "--" (a value may not start with it), so the first "--" is always the end of the options.
    /// </remarks>
    public static int IgnoredArgumentCount(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == EndOfOptions)
            {
                return args.Count - i - 1;
            }
        }

        return 0;
    }

    /// <summary>The command line that <see cref="Parse"/> reads back into these options.</summary>
    /// <exception cref="ArgumentException">The instance or a timed change is one the fixture would refuse, or a timed change
    /// is not due within the lifetime less <see cref="TimedChangeMargin"/> (see <see cref="ThrowIfATimedChangeIsInvalid"/>).</exception>
    public IReadOnlyList<string> ToArguments()
    {
        ThrowIfATimedChangeIsInvalid();
        var args = new List<string> { InstanceOption, ValidInstance(Instance) };
        if (EventLogPath != null)
        {
            args.AddRange([EventLogOption, EventLogPath]);
        }
        if (ParentProcessId is { } parent)
        {
            args.AddRange([ParentPidOption, parent.ToString(CultureInfo.InvariantCulture)]);
        }
        if (MaxLifetime is { } lifetime)
        {
            args.AddRange([MaxLifetimeOption, ((int)Math.Ceiling(lifetime.TotalSeconds)).ToString(CultureInfo.InvariantCulture)]);
        }
        if (NoActivate)
        {
            args.Add(NoActivateOption);
        }
        if (OpenModal)
        {
            args.Add(OpenModalOption);
        }
        if (OpenDocument)
        {
            args.Add(OpenDocumentOption);
        }
        if (DpiAwareness != FixtureDpiAwareness.PerMonitorV2)
        {
            args.AddRange([DpiAwarenessOption, FormatDpiAwareness(DpiAwareness)]);
        }
        if (OpenTable)
        {
            args.Add(OpenTableOption);
        }
        if (OpenMenuWindow)
        {
            args.Add(OpenMenuWindowOption);
        }
        if (OpenWebView)
        {
            args.Add(OpenWebViewOption);
        }
        if (OtherScaleMonitor)
        {
            args.Add(OtherScaleMonitorOption);
        }
        if (OpenCanvas)
        {
            args.Add(OpenCanvasOption);
        }
        if (ChangeAfter is { } changeAfter)
        {
            args.AddRange([ChangeAfterOption, FormatMilliseconds(changeAfter)]);
        }
        if (AnimateFor is { } animateFor)
        {
            args.AddRange([AnimateForOption, FormatMilliseconds(animateFor)]);
        }
        if (OpenOverlay)
        {
            args.Add(OpenOverlayOption);
        }
        return args;
    }

    /// <summary>The command-line spelling of <paramref name="awareness"/>.</summary>
    public static string FormatDpiAwareness(FixtureDpiAwareness awareness) => awareness switch
    {
        FixtureDpiAwareness.PerMonitorV2 => "permonitorv2",
        FixtureDpiAwareness.PerMonitor => "permonitor",
        FixtureDpiAwareness.System => "system",
        FixtureDpiAwareness.Unaware => "unaware",
        _ => throw new ArgumentOutOfRangeException(nameof(awareness), awareness, "Unknown DPI awareness.")
    };

    private static FixtureDpiAwareness ParseDpiAwareness(string value) => value switch
    {
        "permonitorv2" => FixtureDpiAwareness.PerMonitorV2,
        "permonitor" => FixtureDpiAwareness.PerMonitor,
        "system" => FixtureDpiAwareness.System,
        "unaware" => FixtureDpiAwareness.Unaware,
        _ => throw new ArgumentException(
            $"{DpiAwarenessOption} must be permonitorv2, permonitor, system or unaware, got '{value}'.")
    };

    private static string ValueOf(IReadOnlyList<string> args, ref int index)
    {
        var name = args[index];
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Option {name} needs a value.");
        }
        index++;
        return args[index];
    }

    private static int PositiveInt(string name, string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new ArgumentException($"{name} must be a positive whole number, got '{value}'.");

    /// <summary>
    /// A positive number of milliseconds. <see cref="Parse"/> then holds it to the lifetime, which a later option may set
    /// (<see cref="ThrowIfATimedChangeIsInvalid"/>).
    /// </summary>
    private static TimeSpan Milliseconds(string name, string value) => TimeSpan.FromMilliseconds(PositiveInt(name, value));

    /// <summary>Whole milliseconds, rounded up, as <see cref="ThrowIfATimedChangeIsInvalid"/> checks them.</summary>
    private static string FormatMilliseconds(TimeSpan duration) =>
        ((int)Math.Ceiling(duration.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);

    private static string ValidInstance(string value) =>
        InstancePattern.IsMatch(value)
            ? value
            : throw new ArgumentException(
                $"{InstanceOption} must be 1-32 letters, digits or hyphens, got '{value}'.");
}
