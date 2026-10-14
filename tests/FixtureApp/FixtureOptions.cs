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
    bool OpenMenuWindow = false)
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

    /// <summary>The instance token when none is given, for starting the fixture by hand.</summary>
    public const string DefaultInstance = "manual";

    /// <summary>The lifetime when none is given.</summary>
    public static readonly TimeSpan DefaultMaxLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The longest lifetime the fixture accepts.</summary>
    public static readonly TimeSpan LongestMaxLifetime = TimeSpan.FromHours(1);

    private static readonly Regex InstancePattern = new("^[A-Za-z0-9-]{1,32}$");

    /// <summary>The lifetime that applies: <see cref="MaxLifetime"/> or <see cref="DefaultMaxLifetime"/>.</summary>
    public TimeSpan EffectiveMaxLifetime => MaxLifetime ?? DefaultMaxLifetime;

    /// <summary>Reads the command line.</summary>
    /// <exception cref="ArgumentException">An option is unknown, repeated, missing its value or invalid.</exception>
    public static FixtureOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new FixtureOptions(DefaultInstance);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
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
                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        return options;
    }

    /// <summary>The command line that <see cref="Parse"/> reads back into these options.</summary>
    public IReadOnlyList<string> ToArguments()
    {
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

    private static string ValidInstance(string value) =>
        InstancePattern.IsMatch(value)
            ? value
            : throw new ArgumentException(
                $"{InstanceOption} must be 1-32 letters, digits or hyphens, got '{value}'.");
}
