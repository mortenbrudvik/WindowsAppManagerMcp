using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// The real server services, registered as Program.cs registers them
/// (<see cref="ServiceRegistration.AddWindowsAppManagerServices"/>), with these differences:
/// <list type="bullet">
/// <item>layouts live in a temporary folder;</item>
/// <item>the native wrappers some services create themselves are registered, so the container passes
/// them in (<see cref="AddWrappersTheServerCreatesItself"/>);</item>
/// <item>every native wrapper sits behind the tier's guard (<see cref="WrapperGuard"/>). A
/// <see cref="DesktopTier.NoInput"/> server can only read; a <see cref="DesktopTier.Input"/> server can
/// act only on the fixture process, and neither can start a process;</item>
/// <item>the services in <see cref="RefusedServices"/> refuse every call.</item>
/// </list>
/// ServerHarnessTests resolves every service and fails if any of them holds a native wrapper that is not
/// behind the guard.
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
    /// Services the E2E tests must not reach at all; they refuse every call in every tier. CDP talks to a
    /// browser's debug port over the network, outside the native wrappers, and could reach the user's own
    /// browser; no fixture has a debug port yet (a WebView2 pane comes with the M3 Chromium work).
    /// </summary>
    public static IReadOnlyList<Type> RefusedServices { get; } = [typeof(ICdpService)];

    /// <summary>
    /// The server's registrations for <paramref name="tier"/>. Building the collection resolves nothing and
    /// touches nothing, so it can be checked by ordinary tests.
    /// </summary>
    public static ServiceCollection CreateServices(
        DesktopTier tier,
        int fixtureProcessId,
        IDesktopProbe probe,
        string layoutsPath,
        out IReadOnlyList<Type> guardedWrappers) =>
        CreateServices(tier, fixtureProcessId, probe, layoutsPath, wrapperSubstitute: null, out guardedWrappers);

    /// <summary>
    /// As <see cref="CreateServices(DesktopTier, int, IDesktopProbe, string, out IReadOnlyList{Type})"/>.
    /// <paramref name="wrapperSubstitute"/>, when given, creates what stands behind each wrapper's guard
    /// instead of the real wrapper, so the harness's own tests can check the composition with mocks.
    /// </summary>
    internal static ServiceCollection CreateServices(
        DesktopTier tier,
        int fixtureProcessId,
        IDesktopProbe probe,
        string layoutsPath,
        Func<Type, object>? wrapperSubstitute,
        out IReadOnlyList<Type> guardedWrappers)
    {
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(layoutsPath));
        AddWrappersTheServerCreatesItself(services);

        if (wrapperSubstitute != null)
        {
            for (var i = 0; i < services.Count; i++)
            {
                if (!services[i].IsKeyedService && WrapperGuard.IsNativeWrapper(services[i].ServiceType))
                {
                    services[i] = ServiceDescriptor.Singleton(services[i].ServiceType, wrapperSubstitute(services[i].ServiceType));
                }
            }
        }

        IWrapperCallPolicy policy = tier switch
        {
            DesktopTier.NoInput => new ReadOnlyCallPolicy(),
            DesktopTier.Input => new FixtureOnlyCallPolicy(probe, fixtureProcessId),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown desktop tier.")
        };
        guardedWrappers = WrapperGuard.GuardNativeWrappers(services, policy);
        foreach (var refused in RefusedServices)
        {
            WrapperGuard.RefuseService(services, refused,
                $"the E2E tests have no {refused.Name} target; add one to the fixture and a policy for it first.");
        }
        return services;
    }

    /// <summary>
    /// Registers the native wrappers that services create themselves when the container has none:
    /// ProcessService (its optional INativeProcessWrapper parameter) and MonitorService (its
    /// parameterless constructor). The container then passes the registered, guarded wrapper in. TryAdd
    /// keeps a registration the server makes itself.
    /// </summary>
    internal static void AddWrappersTheServerCreatesItself(IServiceCollection services)
    {
        services.TryAddSingleton<INativeProcessWrapper, NativeProcessWrapper>();
        services.TryAddSingleton<INativeMonitorWrapper, NativeMonitorWrapper>();
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
