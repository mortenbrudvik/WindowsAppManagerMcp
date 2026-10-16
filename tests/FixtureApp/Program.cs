using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// Entry point of WamFixtureApp. See <see cref="FixtureOptions"/> for the command line and
/// <see cref="FixtureContract"/> for what the tests can rely on.
/// </summary>
internal static class Program
{
    /// <summary>Exit code for a command line the fixture cannot read.</summary>
    public const int InvalidArgumentsExitCode = 2;

    /// <remarks>
    /// Main names no WPF type, directly or through a lambda: compiling a method that does loads WPF, which makes the
    /// process system DPI aware before the method's first line runs, so ApplyDpiAwareness's call would fail with
    /// ERROR_ACCESS_DENIED. Everything WPF is in <see cref="Run"/>, compiled only once the awareness is set.
    /// </remarks>
    [STAThread]
    private static int Main(string[] args)
    {
        FixtureOptions options;
        try
        {
            options = FixtureOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            // A WinExe has no console: report the problem through the exit code (and stderr for a debugger).
            Console.Error.WriteLine(ex.Message);
            return InvalidArgumentsExitCode;
        }

        // Before any window exists: the awareness of a process is fixed once its first window is created.
        var dpiAwareness = ApplyDpiAwareness(options.DpiAwareness);

        using var events = FixtureEventLog.Open(options.EventLogPath);
        StartWatchdog(options, events);

        // Planted for a reader of the command line (roadmap M6.6); only their number is logged
        var ignored = FixtureOptions.IgnoredArgumentCount(args);
        if (ignored > 0)
        {
            events.Write(FixtureContract.Events.ArgumentsIgnored, ("count", ignored));
        }

        var exitCode = Run(options, events, dpiAwareness);
        events.Write(FixtureContract.Events.Exiting, ("reason", "closed"));
        return exitCode;
    }

    /// <summary>Runs the WPF app. Not inlined into Main: see Main's remarks.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(FixtureOptions options, FixtureEventLog events, string dpiAwareness)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) => OnUnhandledException(events, e);
        return app.Run(new MainWindow(options, events, dpiAwareness));
    }

    private static void OnUnhandledException(FixtureEventLog events, DispatcherUnhandledExceptionEventArgs e)
    {
        // Report it and keep running, so the test sees the problem in the log instead of a vanished window.
        events.Write(FixtureContract.Events.UnhandledException, ("message", e.Exception.ToString()));
        e.Handled = true;
    }

    /// <summary>
    /// Sets the process DPI awareness and returns the awareness in effect (unaware, system, perMonitor or
    /// perMonitorV2). The fixture has no manifest DPI setting, so the call is not overridden by one. When the call fails
    /// (the awareness was already set) the awareness in effect is still reported, and the harness refuses a fixture
    /// whose awareness is not the one it asked for.
    /// </summary>
    private static string ApplyDpiAwareness(FixtureDpiAwareness requested)
    {
        var context = requested switch
        {
            FixtureDpiAwareness.Unaware => NativeMethods.DpiAwarenessContextUnaware,
            FixtureDpiAwareness.System => NativeMethods.DpiAwarenessContextSystemAware,
            FixtureDpiAwareness.PerMonitor => NativeMethods.DpiAwarenessContextPerMonitorAware,
            _ => NativeMethods.DpiAwarenessContextPerMonitorAwareV2
        };
        NativeMethods.SetProcessDpiAwarenessContext(context);

        var current = NativeMethods.GetThreadDpiAwarenessContext();
        if (NativeMethods.AreDpiAwarenessContextsEqual(current, NativeMethods.DpiAwarenessContextPerMonitorAwareV2))
        {
            return "perMonitorV2";
        }

        return NativeMethods.GetAwarenessFromDpiAwarenessContext(current) switch
        {
            0 => "unaware",
            1 => "system",
            2 => "perMonitor",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Ends the fixture when the test host that started it exits, or after its maximum lifetime, so a
    /// crashed or killed test run cannot leave fixture windows behind. It runs on its own thread and
    /// terminates the process directly, so a modal dialog or a save prompt cannot keep the fixture alive.
    /// </summary>
    private static void StartWatchdog(FixtureOptions options, FixtureEventLog events)
    {
        Process? parent = null;
        if (options.ParentProcessId is { } parentId)
        {
            try
            {
                parent = Process.GetProcessById(parentId);
            }
            catch (ArgumentException)
            {
                Terminate(events, "parentExited");
            }
        }

        var lifetime = Stopwatch.StartNew();
        var maxLifetime = options.EffectiveMaxLifetime;
        var thread = new Thread(() =>
        {
            while (true)
            {
                if (parent != null)
                {
                    if (parent.WaitForExit(500))
                    {
                        Terminate(events, "parentExited");
                    }
                }
                else
                {
                    Thread.Sleep(500);
                }

                if (lifetime.Elapsed >= maxLifetime)
                {
                    Terminate(events, "maxLifetime");
                }
            }
        })
        {
            IsBackground = true,
            Name = "Fixture watchdog"
        };
        thread.Start();
    }

    private static void Terminate(FixtureEventLog events, string reason)
    {
        events.Write(FixtureContract.Events.Exiting, ("reason", reason));

        // TerminateProcess on itself: no Closing handlers, no save prompt, nothing that can block.
        using var self = Process.GetCurrentProcess();
        self.Kill();
    }
}
