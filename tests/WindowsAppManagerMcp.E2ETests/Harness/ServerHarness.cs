using Microsoft.Extensions.DependencyInjection;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// The real server services, registered exactly as Program.cs registers them
/// (<see cref="ServiceRegistration.AddWindowsAppManagerServices"/>), with two differences: layouts live
/// in a temporary folder, and every native wrapper sits behind the tier's guard
/// (<see cref="WrapperGuard"/>). A <see cref="DesktopTier.NoInput"/> server can only read; a
/// <see cref="DesktopTier.Input"/> server can act only on the fixture process.
/// </summary>
/// <remarks>
/// Tests call the services directly until the in-process MCP client path exists (roadmap M2.12); then
/// the same harness can host the MCP server over in-memory pipes.
/// </remarks>
public sealed class ServerHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly string _layoutsPath;

    private ServerHarness(ServiceProvider provider, string layoutsPath, IReadOnlyList<Type> guardedWrappers)
    {
        _provider = provider;
        _layoutsPath = layoutsPath;
        GuardedWrappers = guardedWrappers;
    }

    /// <summary>The native wrapper interfaces that sit behind the guard.</summary>
    public IReadOnlyList<Type> GuardedWrappers { get; }

    public IWindowService Windows => Get<IWindowService>();

    public IUIElementService UiElements => Get<IUIElementService>();

    public IScreenshotService Screenshots => Get<IScreenshotService>();

    public IInputSimulationService Input => Get<IInputSimulationService>();

    public IMonitorService Monitors => Get<IMonitorService>();

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>
    /// Builds the server for a test of <paramref name="tier"/> against the fixture process
    /// <paramref name="fixtureProcessId"/>. Refuses unless the tier is opted in.
    /// </summary>
    public static ServerHarness Create(DesktopTier tier, int fixtureProcessId) =>
        Create(tier, fixtureProcessId, new Win32DesktopProbe(), Environment.GetEnvironmentVariable);

    internal static ServerHarness Create(
        DesktopTier tier,
        int fixtureProcessId,
        IDesktopProbe probe,
        Func<string, string?> readVariable)
    {
        DesktopTestGate.ThrowIfNotAllowed(tier, readVariable);
        E2EProcess.EnsureServerDpiAwareness();

        var layoutsPath = Path.Combine(Path.GetTempPath(), "WamE2E", "layouts-" + Guid.NewGuid().ToString("N"));
        var services = CreateServices(tier, fixtureProcessId, probe, layoutsPath, out var guarded);
        return new ServerHarness(services.BuildServiceProvider(), layoutsPath, guarded);
    }

    /// <summary>
    /// The server's registrations for <paramref name="tier"/>. Building the collection resolves nothing and
    /// touches nothing, so it can be checked by ordinary tests.
    /// </summary>
    public static ServiceCollection CreateServices(
        DesktopTier tier,
        int fixtureProcessId,
        IDesktopProbe probe,
        string layoutsPath,
        out IReadOnlyList<Type> guardedWrappers)
    {
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(layoutsPath));

        IWrapperCallPolicy policy = tier switch
        {
            DesktopTier.NoInput => new ReadOnlyCallPolicy(),
            DesktopTier.Input => new FixtureOnlyCallPolicy(probe, fixtureProcessId),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown desktop tier.")
        };
        guardedWrappers = WrapperGuard.GuardNativeWrappers(services, policy);
        return services;
    }

    public void Dispose()
    {
        _provider.Dispose();
        try
        {
            if (Directory.Exists(_layoutsPath))
            {
                Directory.Delete(_layoutsPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Process-wide setup that makes the test host behave like the server process.</summary>
public static class E2EProcess
{
    private static readonly Lazy<string> DpiAwareness = new(() =>
    {
        // What the server's Program.cs does first. It fails harmlessly when the awareness is already set.
        E2ENative.SetProcessDpiAwareness(E2ENative.ProcessPerMonitorDpiAware);
        return E2ENative.CurrentDpiAwareness();
    });

    /// <summary>
    /// Makes the test host per-monitor DPI aware, as the server is, so window rectangles, UI Automation
    /// bounds and captures come back in physical pixels as they would in the server. Returns the
    /// awareness in effect.
    /// </summary>
    public static string EnsureServerDpiAwareness() => DpiAwareness.Value;
}
