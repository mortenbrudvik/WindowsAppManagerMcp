using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// What an E2E test works with: a running fixture, the server services guarded for the test's tier,
/// and the fixture's main window as the server's find_windows reports it.
/// </summary>
/// <example>
/// <code>
/// [DesktopNoInputFact]
/// public void Something()
/// {
///     using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
///     var tree = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);
/// }
/// </code>
/// </example>
public sealed class E2ESession : IDisposable
{
    /// <summary>How long find_windows gets to list a fixture window that has already rendered.</summary>
    public static readonly TimeSpan FindWindowTimeout = TimeSpan.FromSeconds(10);

    /// <summary>What to do when the agent's host is not what a run needs, for the failure messages.</summary>
    internal const string KeepTheTerminalInFront =
        "Keep the terminal (or IDE) that runs the tests in front, and do not click a fixture window, while the E2E tests run.";

    private E2ESession(DesktopTier tier, IHostGuard agentHost, FixtureAppSession fixture, ServerHarness server, WindowInfo mainWindow)
    {
        Tier = tier;
        AgentHost = agentHost;
        Fixture = fixture;
        Server = server;
        MainWindow = mainWindow;
    }

    public DesktopTier Tier { get; }

    /// <summary>
    /// The agent's host as the server recorded it, before the fixture started (<see cref="ServerHarness.RecordAgentHost(DesktopTier)"/>):
    /// the server's own <see cref="IHostGuard"/>. Pass it to a second server of the session
    /// (<see cref="ServerHarness.Create(DesktopTier, int, UiaEngine?, IHostGuard?, IReadOnlyCollection{string}?)"/>), so both see the same host.
    /// </summary>
    public IHostGuard AgentHost { get; }

    public FixtureAppSession Fixture { get; }

    public ServerHarness Server { get; }

    /// <summary>The fixture's main window, as find_windows (IWindowService.FindWindows) reports it.</summary>
    public WindowInfo MainWindow { get; }

    /// <summary>
    /// Records the agent's host, starts the fixture, builds the guarded server and finds the fixture's main window through
    /// the server. Refuses unless <paramref name="tier"/> is opted in. Writes the server's UI Automation engine to the
    /// output, so every scenario's log names the engine it read with.
    /// </summary>
    /// <remarks>
    /// The server records the window in front as part of the agent's host when it starts (HostGuard). It is recorded here
    /// before the fixture starts, so that window is the terminal that runs the tests, whatever the fixture does with the
    /// foreground once it runs. A session whose server still counts the fixture's main window as the host fails at once
    /// (<see cref="FixtureIsHostError"/>): the server would refuse input to the fixture and leave its popups out of reads,
    /// and the scenarios would test the host guard instead of what they are about.
    /// </remarks>
    /// <param name="uiaEngine">The UI Automation engine of the server (roadmap M3.2), or null for the server's own choice:
    /// WAM_UIA_ENGINE in the test process, by default UIA3. A value of WAM_UIA_ENGINE the server does not understand is
    /// refused, rather than run with the default engine, so a run meant for one engine cannot quietly read with the other.</param>
    /// <param name="allowedActivations">Input tier only: the packaged apps the scenario may activate, without arguments
    /// (roadmap M6.5); the scenario ends each one itself, by the process ID the launch returned.</param>
    /// <param name="uiaActionProcesses">Input tier only: the processes, by image name, whose elements the scenario may act on
    /// through UI Automation besides the fixture's (roadmap M7.6): an app it activated and checked was not running before.</param>
    public static E2ESession Start(
        DesktopTier tier, ITestOutputHelper output, FixtureLaunchOptions? launch = null, UiaEngine? uiaEngine = null,
        IReadOnlyCollection<string>? allowedActivations = null, IReadOnlyCollection<string>? uiaActionProcesses = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        DesktopTestGate.ThrowIfNotAllowed(tier);
        if (uiaEngine == null && UiaEngineOptions.FromEnvironment().Warning is { } warning)
        {
            throw new InvalidOperationException($"{warning} Unset it, or set it to uia3 or uia2, for an E2E run.");
        }

        output.WriteLine($"Test host DPI awareness: {E2EProcess.EnsureServerDpiAwareness()}.");

        // Before the fixture exists: the window in front now is the one the run was started from
        var agentHost = ServerHarness.RecordAgentHost(tier);
        output.WriteLine($"Agent host, recorded before the fixture started: {DescribeHost(agentHost.Host)}.");

        var fixture = FixtureAppSession.Launch(tier, launch, output);
        try
        {
            var server = ServerHarness.Create(tier, fixture.ProcessId, uiaEngine, agentHost, allowedActivations, uiaActionProcesses);
            try
            {
                output.WriteLine($"UI Automation engine: {server.Get<IUiaTree>().Engine} ({server.Get<UiaEngineOptions>().Source}).");
                var mainWindow = Wait.ForValue(
                    () => server.Windows.FindWindows(processId: fixture.ProcessId)
                        .FirstOrDefault(w => w.Handle == fixture.MainWindowHandle),
                    FindWindowTimeout)
                    ?? throw new InvalidOperationException(
                        $"find_windows did not list the fixture's main window 0x{fixture.MainWindowHandle:X} " +
                        $"(process {fixture.ProcessId}) within {FindWindowTimeout.TotalSeconds:0} s.");

                output.WriteLine($"find_windows: {mainWindow}");
                if (FixtureIsHostError(mainWindow, fixture.ProcessId, server.Get<IHostGuard>()) is { } fixtureIsHost)
                {
                    throw new InvalidOperationException(fixtureIsHost);
                }

                return new E2ESession(tier, agentHost, fixture, server, mainWindow);
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The window in front, for a scenario whose meaning depends on the agent's host being there (the terminal that runs
    /// the tests, as the server recorded it). Throws when no window, a fixture window or any other window is in front, so the
    /// scenario fails and says what to do, instead of passing without checking what it is about.
    /// </summary>
    public nint RequireAgentHostInFront()
    {
        var foreground = E2ENative.GetForegroundWindow();
        var processId = foreground == 0 ? 0 : E2ENative.GetProcessId(foreground);
        if (AgentHostNotInFrontError(foreground, processId, Fixture.ProcessId, AgentHost) is { } error)
        {
            throw new InvalidOperationException(
                $"{error} In front: {UiaReads.DescribeWindow(foreground)}; the host: {DescribeHost(AgentHost.Host)}.");
        }

        return foreground;
    }

    /// <summary>
    /// Why a session cannot run, or null: the server counts the fixture's main window (or the fixture's process) as the
    /// agent's own host. Pure, so the rule can be tested.
    /// </summary>
    internal static string? FixtureIsHostError(WindowInfo mainWindow, int fixtureProcessId, IHostGuard host)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(host);
        if (!mainWindow.IsAgentHost && !host.IsHostWindow(mainWindow.Handle) && !host.IsHostProcess(fixtureProcessId))
        {
            return null;
        }

        return $"The server counts the fixture's main window 0x{mainWindow.Handle:X} (process {fixtureProcessId}) as the agent's " +
               "own host, so it would refuse input to it and leave its popups out of reads, and every scenario would test the host " +
               $"guard instead of what it is about. The host recorded: {DescribeHost(host.Host)}. {KeepTheTerminalInFront}";
    }

    /// <summary>
    /// Why the window in front is not the agent's host as a scenario needs it, or null: there is none, it is a fixture
    /// window, or the server does not count it as the host. Pure, so the rule can be tested.
    /// </summary>
    internal static string? AgentHostNotInFrontError(nint foreground, int foregroundProcessId, int fixtureProcessId, IHostGuard host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (foreground == 0)
        {
            return $"This scenario needs the agent's host in front, but no window is in front. {KeepTheTerminalInFront}";
        }

        if (foregroundProcessId == fixtureProcessId)
        {
            return "This scenario needs the agent's host in front, but a fixture window is: the fixture took the foreground. " +
                   KeepTheTerminalInFront;
        }

        return host.IsHostWindow(foreground)
            ? null
            : "This scenario needs the agent's host in front (the terminal that runs the tests, as the server recorded it), but " +
              $"another window is. {KeepTheTerminalInFront}";
    }

    /// <summary>The recorded host for the logs: the window that was in front, the console's window and the ancestors.</summary>
    internal static string DescribeHost(HostSnapshot host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var foreground = host.StartupForegroundWindow is { } window
            ? $"0x{window.Handle:X} (process {window.Process.ProcessId})"
            : "none";
        var console = host.ConsoleHostWindow is { } consoleHost
            ? $"0x{consoleHost.Handle:X} (process {consoleHost.Process.ProcessId})"
            : "none";
        return $"window in front {foreground}, console window {console}, " +
               $"ancestors {string.Join(", ", host.Ancestors.Select(a => a.ProcessId))}";
    }

    public void Dispose()
    {
        Server.Dispose();
        Fixture.Dispose();
    }
}
