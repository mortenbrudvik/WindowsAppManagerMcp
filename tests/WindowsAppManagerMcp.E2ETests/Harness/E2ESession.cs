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

    private E2ESession(DesktopTier tier, FixtureAppSession fixture, ServerHarness server, WindowInfo mainWindow)
    {
        Tier = tier;
        Fixture = fixture;
        Server = server;
        MainWindow = mainWindow;
    }

    public DesktopTier Tier { get; }

    public FixtureAppSession Fixture { get; }

    public ServerHarness Server { get; }

    /// <summary>The fixture's main window, as find_windows (IWindowService.FindWindows) reports it.</summary>
    public WindowInfo MainWindow { get; }

    /// <summary>
    /// Starts the fixture, builds the guarded server and finds the fixture's main window through the
    /// server. Refuses unless <paramref name="tier"/> is opted in. Writes the server's UI Automation engine to the
    /// output, so every scenario's log names the engine it read with.
    /// </summary>
    /// <param name="uiaEngine">The UI Automation engine of the server (roadmap M3.2), or null for the server's own choice:
    /// WAM_UIA_ENGINE in the test process, by default UIA3. A value of WAM_UIA_ENGINE the server does not understand is
    /// refused, rather than run with the default engine, so a run meant for one engine cannot quietly read with the other.</param>
    public static E2ESession Start(DesktopTier tier, ITestOutputHelper output, FixtureLaunchOptions? launch = null, UiaEngine? uiaEngine = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        DesktopTestGate.ThrowIfNotAllowed(tier);
        if (uiaEngine == null && UiaEngineOptions.FromEnvironment().Warning is { } warning)
        {
            throw new InvalidOperationException($"{warning} Unset it, or set it to uia3 or uia2, for an E2E run.");
        }

        output.WriteLine($"Test host DPI awareness: {E2EProcess.EnsureServerDpiAwareness()}.");

        var fixture = FixtureAppSession.Launch(tier, launch, output);
        try
        {
            var server = ServerHarness.Create(tier, fixture.ProcessId, uiaEngine);
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
                return new E2ESession(tier, fixture, server, mainWindow);
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

    public void Dispose()
    {
        Server.Dispose();
        Fixture.Dispose();
    }
}
