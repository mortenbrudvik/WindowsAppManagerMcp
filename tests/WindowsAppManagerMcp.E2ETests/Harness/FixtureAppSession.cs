using System.ComponentModel;
using System.Diagnostics;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>How a test wants the fixture started.</summary>
public sealed record FixtureLaunchOptions
{
    /// <summary>
    /// Show the main window activated. Off by default: a test that needs the fixture in front activates
    /// it through the server, and a <see cref="DesktopTier.NoInput"/> test may not activate it at all.
    /// </summary>
    public bool Activate { get; init; }

    /// <summary>Open the modal dialog right after the main window is ready.</summary>
    public bool OpenModal { get; init; }

    /// <summary>Open the document window (with unsaved changes) right after the main window is ready.</summary>
    public bool OpenDocument { get; init; }

    /// <summary>Open the table window (a WPF DataGrid, roadmap M3.F4) right after the main window is ready.</summary>
    public bool OpenTable { get; init; }

    /// <summary>Open the menu window (a Win32 menu bar, roadmap M3.F4) right after the main window is ready.</summary>
    public bool OpenMenuWindow { get; init; }

    /// <summary>
    /// Open the WebView2 window (roadmap M3.F4) right after the main window is ready. It needs the Edge WebView2 runtime: a
    /// scenario that opens it is gated with Requires = typeof(<see cref="WebView2Runtime"/>).
    /// </summary>
    public bool OpenWebView { get; init; }

    /// <summary>
    /// Open the main window on a monitor whose scale differs from the primary's (roadmap M3.F30). A scenario that needs it is
    /// gated with Requires = typeof(<see cref="MonitorAtAnotherScale"/>).
    /// </summary>
    public bool OtherScaleMonitor { get; init; }

    /// <summary>
    /// Open the canvas window (roadmap M6.3) right after the main window is ready: <see cref="FixtureContract.CanvasLines"/>
    /// drawn as pixels only. Wait for its <see cref="FixtureContract.Events.CanvasOpened"/> event and read it with
    /// <see cref="FixtureCanvas.From"/>.
    /// </summary>
    public bool OpenCanvas { get; init; }

    /// <summary>
    /// Add the change panel (<see cref="FixtureContract.Ids.ChangePanel"/>) to the main window, and recolor and relabel it
    /// once, this long after the ready event (roadmap M6.4, <c>--change-after-ms</c>). Null for no panel.
    /// </summary>
    public TimeSpan? ChangeAfter { get; init; }

    /// <summary>
    /// Add the spinner (<see cref="FixtureContract.Ids.Spinner"/>) to the main window and turn it for this long after the
    /// ready event, then stop it (roadmap M6.4, <c>--animate-for-ms</c>). Null for no spinner.
    /// </summary>
    public TimeSpan? AnimateFor { get; init; }

    /// <summary>
    /// Open the overlay (roadmap M6.7) right after the main window is ready: a small unowned WS_EX_TOPMOST tool window of the
    /// fixture, for z-order checks that must not touch any other window. It is centred on the primary monitor, so it covers
    /// the main window's middle only without <see cref="OtherScaleMonitor"/>, which it does not follow.
    /// </summary>
    public bool OpenOverlay { get; init; }

    /// <summary>
    /// Arguments the harness plants on the fixture's command line after <see cref="FixtureOptions.EndOfOptions"/>, where
    /// the fixture ignores them (roadmap M6.6: a secret-looking switch only a reader of the command line sees).
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>The fixture's process DPI awareness.</summary>
    public FixtureDpiAwareness DpiAwareness { get; init; } = FixtureDpiAwareness.PerMonitorV2;

    /// <summary>How long to wait for the fixture's ready event.</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The fixture ends itself after this long, even if the harness never kills it.</summary>
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// One running fixture app, started by the harness and killed in <see cref="Dispose"/>. The fixture also
/// ends itself when the test host exits or after <see cref="FixtureLaunchOptions.MaxLifetime"/>.
/// </summary>
public sealed class FixtureAppSession : IDisposable
{
    /// <summary>How long <see cref="Dispose"/> keeps trying to delete the fixture's folder while it is in use.</summary>
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;
    private readonly string _workDirectory;
    private readonly ITestOutputHelper? _output;
    private FixtureReady? _ready;
    private bool _disposed;

    private FixtureAppSession(Process process, FixtureOptions options, string workDirectory, ITestOutputHelper? output)
    {
        _process = process;
        Options = options;
        _workDirectory = workDirectory;
        _output = output;
        Events = new FixtureEventLogReader(options.EventLogPath!);
    }

    /// <summary>The fixture's command line.</summary>
    public FixtureOptions Options { get; }

    /// <summary>
    /// The test's own folder under the temp folder, deleted in <see cref="Dispose"/>: it holds the event log, and it is the
    /// one folder the fixture's Save As button writes into (<see cref="FixtureSaveFolder"/>).
    /// </summary>
    public string WorkDirectory => _workDirectory;

    /// <summary>The token in every window title of this fixture.</summary>
    public string Instance => Options.Instance;

    public int ProcessId => _process.Id;

    /// <summary>The fixture's event log.</summary>
    public FixtureEventLogReader Events { get; }

    /// <summary>What the fixture reported when its main window first rendered.</summary>
    public FixtureReady Ready => _ready ?? throw new InvalidOperationException("The fixture has not reported ready.");

    public nint MainWindowHandle => Ready.MainWindowHandle;

    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Starts the fixture and waits for its ready event. Refuses unless <paramref name="tier"/> is opted in,
    /// before it locates or starts anything.
    /// </summary>
    public static FixtureAppSession Launch(DesktopTier tier, FixtureLaunchOptions? launch = null, ITestOutputHelper? output = null) =>
        Launch(tier, launch ?? new FixtureLaunchOptions(), output, FixtureAppLocator.Locate, Environment.GetEnvironmentVariable);

    internal static FixtureAppSession Launch(
        DesktopTier tier,
        FixtureLaunchOptions launch,
        ITestOutputHelper? output,
        Func<string> locateFixture,
        Func<string, string?> readVariable)
    {
        // The gate comes first: a test that is not opted in gets nothing located, created or started.
        DesktopTestGate.ThrowIfNotAllowed(tier, readVariable);
        var exePath = locateFixture();

        var instance = Guid.NewGuid().ToString("N")[..12];
        var workDirectory = Path.Combine(Path.GetTempPath(), "WamE2E", instance);
        var options = BuildOptions(tier, launch, instance, Path.Combine(workDirectory, "events.jsonl"), Environment.ProcessId);
        Directory.CreateDirectory(workDirectory);

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = workDirectory
        };
        foreach (var argument in CommandLine(options, launch.ExtraArguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start the fixture app '{exePath}'.");
        var session = new FixtureAppSession(process, options, workDirectory, output);
        try
        {
            var ready = session.Events.WaitFor(
                e => e.Name == FixtureContract.Events.Ready,
                launch.ReadyTimeout,
                "its ready event",
                session.ExitReason);
            session._ready = FixtureReady.From(ready);
            if (session._ready.ProcessId != process.Id)
            {
                throw new InvalidOperationException(
                    $"The ready event names process {session._ready.ProcessId}, but the harness started {process.Id}.");
            }
            if (DpiAwarenessMismatch(launch.DpiAwareness, session._ready.DpiAwareness) is { } mismatch)
            {
                throw new InvalidOperationException(mismatch);
            }

            output?.WriteLine(
                $"Fixture {instance} started: {exePath}, pid {process.Id}, main window 0x{session._ready.MainWindowHandle:X}, " +
                $"window {session._ready.WindowRect}, client {session._ready.ClientRect}, " +
                $"DPI {session._ready.Dpi} ({session._ready.DpiAwareness}).");
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The fixture's command line for a test of <paramref name="tier"/>. Pure, so the rules can be tested:
    /// the fixture always watches the test host and has a maximum lifetime, a no-input test never
    /// activates it, and a timed change is positive and due within that lifetime.
    /// </summary>
    public static FixtureOptions BuildOptions(
        DesktopTier tier,
        FixtureLaunchOptions launch,
        string instance,
        string eventLogPath,
        int parentProcessId)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (tier == DesktopTier.NoInput && launch.Activate)
        {
            throw new ArgumentException(
                "A DesktopNoInput test cannot activate the fixture: activation takes the foreground from the user.",
                nameof(launch));
        }

        var options = new FixtureOptions(
            Instance: instance,
            EventLogPath: eventLogPath,
            ParentProcessId: parentProcessId,
            MaxLifetime: launch.MaxLifetime,
            NoActivate: !launch.Activate,
            OpenModal: launch.OpenModal,
            OpenDocument: launch.OpenDocument,
            DpiAwareness: launch.DpiAwareness,
            OpenTable: launch.OpenTable,
            OpenMenuWindow: launch.OpenMenuWindow,
            OpenWebView: launch.OpenWebView,
            OtherScaleMonitor: launch.OtherScaleMonitor,
            OpenCanvas: launch.OpenCanvas,
            ChangeAfter: launch.ChangeAfter,
            AnimateFor: launch.AnimateFor,
            OpenOverlay: launch.OpenOverlay);

        // Here, not as the fixture's exit code 2 or a wait that times out after the watchdog ended it
        options.ThrowIfATimedChangeIsInvalid();
        return options;
    }

    /// <summary>
    /// The fixture's whole command line: <paramref name="options"/>, then, when there are any,
    /// <see cref="FixtureOptions.EndOfOptions"/> and <paramref name="extraArguments"/>, which the fixture ignores.
    /// </summary>
    public static IReadOnlyList<string> CommandLine(FixtureOptions options, IReadOnlyList<string> extraArguments)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(extraArguments);
        if (extraArguments.Any(a => a is null))
        {
            throw new ArgumentException("An extra argument is null.", nameof(extraArguments));
        }

        var args = options.ToArguments().ToList();
        if (extraArguments.Count > 0)
        {
            args.Add(FixtureOptions.EndOfOptions);
            args.AddRange(extraArguments);
        }

        return args;
    }

    /// <summary>
    /// Why the fixture's reported awareness is not the one the test asked for, or null when it is. A fixture with another
    /// awareness reports window and element rectangles that are scaled on a monitor whose scale differs from the
    /// primary's, so every coordinate a test compares with the server's would be off.
    /// </summary>
    public static string? DpiAwarenessMismatch(FixtureDpiAwareness requested, string reported)
    {
        var expected = FixtureOptions.FormatDpiAwareness(requested);
        return string.Equals(expected, reported, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The fixture asked for DPI awareness {expected} but runs {reported}: its coordinates would not be the " +
              "server's physical pixels. Check that nothing in the fixture sets the awareness before Program.Main does.";
    }

    /// <summary>Waits for the first event named <paramref name="name"/> that matches <paramref name="match"/>.</summary>
    public FixtureEvent WaitForEvent(string name, Func<FixtureEvent, bool>? match = null, TimeSpan? timeout = null) =>
        Events.WaitFor(
            e => e.Name == name && (match == null || match(e)),
            timeout ?? TimeSpan.FromSeconds(10),
            $"a '{name}' event",
            ExitReason);

    /// <summary>
    /// Waits for the first event named one of <paramref name="names"/>, for something that ends in one of several events
    /// (a window that opens, or the event that says why it could not).
    /// </summary>
    public FixtureEvent WaitForAnyEvent(IReadOnlyCollection<string> names, TimeSpan? timeout = null) =>
        Events.WaitFor(
            e => names.Contains(e.Name),
            timeout ?? TimeSpan.FromSeconds(10),
            $"one of the events {string.Join(", ", names.Select(n => $"'{n}'"))}",
            ExitReason);

    private string? ExitReason() =>
        _process.HasExited ? $"the fixture exited with code {_process.ExitCode}" : null;

    /// <summary>Kills the fixture (and anything it started) and deletes its working folder.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // It exited between the check and the kill.
        }
        catch (Win32Exception ex)
        {
            _output?.WriteLine($"Could not kill fixture {Instance} (pid {_process.Id}): {ex.Message}");
        }
        finally
        {
            try
            {
                _output?.WriteLine($"Fixture {Instance} event log:{Environment.NewLine}{Events.Describe()}");
            }
            catch (IOException)
            {
                // Diagnostics only.
            }

            _process.Dispose();
            TryDeleteDirectory(_workDirectory);
        }
    }

    /// <summary>
    /// Deletes the fixture's folder, trying again for a moment while it is in use: the WebView2 window's browser processes,
    /// killed with the fixture, can hold their user data folder in it for a moment after the kill. Left behind if it stays
    /// in use.
    /// </summary>
    private static void TryDeleteDirectory(string path) =>
        Wait.Until(() => TryDeleteDirectoryOnce(path), DeleteTimeout, TimeSpan.FromMilliseconds(100));

    private static bool TryDeleteDirectoryOnce(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
