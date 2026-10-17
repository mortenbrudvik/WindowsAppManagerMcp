using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
/// <item>the services in <see cref="RefusedServices"/> refuse every call;</item>
/// <item>no hosted service runs, so the host guard is created when the harness is built, or recorded before the fixture
/// starts and passed in (<see cref="RecordAgentHost(DesktopTier)"/>).</item>
/// </list>
/// ServerHarnessTests resolves every service and fails if any of them holds a native wrapper that is not
/// behind the guard.
/// </summary>
/// <remarks>
/// Tests call the services directly. The in-process MCP host (<see cref="InProcessMcpServer"/>, shared with the contract
/// tests) can run the real MCP pipeline in this tier too; a scenario that hosts the server's tools through it must hand
/// them this harness's guarded services, not the defaults of <see cref="ServiceRegistration.AddWindowsAppManagerServices"/>:
/// <see cref="StartMcpServerAsync"/> does (run_steps, roadmap M6.2, whose steps only the pipeline can run).
/// </remarks>
public sealed class ServerHarness : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IReadOnlyList<ServiceDescriptor> _registrations;
    private readonly string _layoutsPath;

    private ServerHarness(ServiceCollection services, string layoutsPath, IReadOnlyList<Type> guardedWrappers)
    {
        _registrations = services.ToList();
        _provider = services.BuildServiceProvider();
        _layoutsPath = layoutsPath;
        GuardedWrappers = guardedWrappers;
    }

    /// <summary>The native wrapper interfaces that sit behind the guard.</summary>
    public IReadOnlyList<Type> GuardedWrappers { get; }

    public IWindowService Windows => Get<IWindowService>();

    /// <summary>Verified activation: what focus_window and apply_layout use.</summary>
    public IForegroundService Foreground => Get<IForegroundService>();

    public IUIElementService UiElements => Get<IUIElementService>();

    public IScreenshotService Screenshots => Get<IScreenshotService>();

    public IInputSimulationService Input => Get<IInputSimulationService>();

    public IMonitorService Monitors => Get<IMonitorService>();

    public T Get<T>() where T : notnull => _provider.GetRequiredService<T>();

    /// <summary>
    /// Starts the in-process MCP host with the server's own tools and call filters
    /// (<see cref="ServiceRegistration.AddWindowsAppManagerTools"/>, every toolset) over this harness's services
    /// (<see cref="Forward"/>), so a call goes through the real pipeline (binding, the audit log, error reporting, the
    /// undeclared-argument refusal, the input gate) and acts only through this harness's guard. A scenario that calls
    /// run_steps (roadmap M6.2) needs it: its steps are tool calls inside the server.
    /// </summary>
    public Task<InProcessMcpServer> StartMcpServerAsync() =>
        InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: host => Forward(_registrations, _provider, host));

    /// <summary>
    /// Registers in <paramref name="host"/> (an MCP host's service collection) every service type of
    /// <paramref name="registrations"/> that the host does not register itself, resolved from <paramref name="provider"/>:
    /// the host's tools and filters then get the harness's own instances, behind its guard, and the harness and the host
    /// share one input session, one element registry and one audit log. Hosted services are left out, as the harness runs
    /// none (no pause hotkey is registered, no audit file is pruned); so are keyed and open generic registrations.
    /// </summary>
    internal static void Forward(IEnumerable<ServiceDescriptor> registrations, IServiceProvider provider, IServiceCollection host)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(host);

        var types = registrations
            .Where(d => !d.IsKeyedService && !d.ServiceType.IsGenericTypeDefinition && d.ServiceType != typeof(IHostedService))
            .Select(d => d.ServiceType)
            .Distinct()
            .ToList();
        foreach (var type in types)
        {
            if (!host.Any(d => d.ServiceType == type))
            {
                host.AddSingleton(type, _ => provider.GetRequiredService(type));
            }
        }
    }

    /// <summary>
    /// Builds the server for a test of <paramref name="tier"/> against the fixture process
    /// <paramref name="fixtureProcessId"/>. Refuses unless the tier is opted in.
    /// </summary>
    /// <param name="uiaEngine">The UI Automation engine the server reads with (roadmap M3.2); null for the server's own
    /// choice (WAM_UIA_ENGINE, by default UIA3), so a scenario can run its reads against both engines.</param>
    /// <param name="agentHost">The agent's host as <see cref="RecordAgentHost(DesktopTier)"/> recorded it before the fixture
    /// started, which the server then uses as its own; null to let the server record it now, as it does when it starts.</param>
    public static ServerHarness Create(DesktopTier tier, int fixtureProcessId, UiaEngine? uiaEngine = null, IHostGuard? agentHost = null) =>
        Create(tier, fixtureProcessId, new Win32DesktopProbe(), Environment.GetEnvironmentVariable, uiaEngine, agentHost);

    internal static ServerHarness Create(
        DesktopTier tier,
        int fixtureProcessId,
        IDesktopProbe probe,
        Func<string, string?> readVariable,
        UiaEngine? uiaEngine = null,
        IHostGuard? agentHost = null)
    {
        DesktopTestGate.ThrowIfNotAllowed(tier, readVariable);
        E2EProcess.EnsureServerDpiAwareness();

        var layoutsPath = Path.Combine(Path.GetTempPath(), "WamE2E", "layouts-" + Guid.NewGuid().ToString("N"));
        var services = CreateServices(tier, fixtureProcessId, probe, layoutsPath, wrapperSubstitute: null, out var guarded, uiaEngine, agentHost);
        var harness = new ServerHarness(services, layoutsPath, guarded);
        try
        {
            // The server records its host when it starts (HostGuardStartup); the harness runs no hosted service, so it
            // records it here, before any call, rather than at the first call that asks
            harness.Get<IHostGuard>();
            return harness;
        }
        catch
        {
            harness.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Records the agent's host as the server records it when it starts (<see cref="HostGuardStartup"/>): a
    /// <see cref="HostGuard"/> over the server's own window and process wrappers, behind the read-only guard in every tier
    /// (it only reads). Among other things it records the window in front as part of the host, so
    /// <see cref="E2ESession.Start"/> records it before it starts the fixture: the window in front is then the terminal that
    /// runs the tests, never the fixture. Pass the result to <see cref="Create(DesktopTier, int, UiaEngine?, IHostGuard?)"/>.
    /// Refuses unless <paramref name="tier"/> is opted in.
    /// </summary>
    public static IHostGuard RecordAgentHost(DesktopTier tier) =>
        RecordAgentHost(tier, Environment.GetEnvironmentVariable, wrapperSubstitute: null);

    /// <summary>
    /// As <see cref="RecordAgentHost(DesktopTier)"/>. <paramref name="wrapperSubstitute"/>, when given, creates what stands
    /// behind each wrapper's guard instead of the real wrapper, so the harness's own tests can check it with mocks.
    /// </summary>
    internal static IHostGuard RecordAgentHost(DesktopTier tier, Func<string, string?> readVariable, Func<Type, object>? wrapperSubstitute)
    {
        DesktopTestGate.ThrowIfNotAllowed(tier, readVariable);

        // Handles and process ids only: the DPI awareness does not change what is recorded
        var policy = new ReadOnlyCallPolicy();
        var windows = (INativeWindowWrapper)(wrapperSubstitute?.Invoke(typeof(INativeWindowWrapper)) ?? new NativeWindowWrapper());
        var processes = (INativeProcessWrapper)(wrapperSubstitute?.Invoke(typeof(INativeProcessWrapper)) ?? new NativeProcessWrapper());
        return new HostGuard(WrapperGuard.Create(windows, policy), WrapperGuard.Create(processes, policy));
    }

    /// <summary>
    /// Services the E2E tests must not reach at all; they refuse every call in every tier. CDP talks to a
    /// browser's debug port over the network, outside the native wrappers, and could reach the user's own
    /// browser; the fixture's WebView2 window (roadmap M3.F4) opens no debug port. Both the CDP service and the
    /// transport under it (roadmap M5.2) are refused, so a service that takes the transport directly cannot reach it
    /// either; every registered ICdp* service must be listed here. launch_browser_with_debug's readiness check
    /// (IDebugBrowserReadiness, roadmap M5.1) reaches the browser only through ICdpService, so it is refused with it. The
    /// tab tools' CDP side (ICdpTabService, roadmap M5.5) navigates and activates tabs, and the DOM bridge's (ICdpPageService,
    /// roadmap M5.8) clicks, types and runs scripts in pages, so both are refused too.
    /// </summary>
    public static IReadOnlyList<Type> RefusedServices { get; } =
        [typeof(ICdpService), typeof(ICdpTransport), typeof(ICdpTabService), typeof(ICdpPageService)];

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
    /// <paramref name="agentHost"/>, when given, is the server's host guard (<see cref="RecordAgentHost(DesktopTier)"/>).
    /// </summary>
    internal static ServiceCollection CreateServices(
        DesktopTier tier,
        int fixtureProcessId,
        IDesktopProbe probe,
        string layoutsPath,
        Func<Type, object>? wrapperSubstitute,
        out IReadOnlyList<Type> guardedWrappers,
        UiaEngine? uiaEngine = null,
        IHostGuard? agentHost = null)
    {
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(
            () => LayoutStorageOptions.ForPath(layoutsPath),
            () => AuditLogOptions.ForFolder(AuditFolderFor(layoutsPath)));
        AddWrappersTheServerCreatesItself(services);
        if (uiaEngine is { } engine)
        {
            // The server builds its IUiaTree from these options, so the engine's tree sits behind the guard as usual
            services.Replace(ServiceDescriptor.Singleton(new UiaEngineOptions(engine, "chosen by the test")));
        }

        if (agentHost != null)
        {
            // Recorded before the fixture started; its wrappers sit behind a read-only guard of their own
            services.Replace(ServiceDescriptor.Singleton(agentHost));
        }

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
    /// The audit log folder of a harness (roadmap M2.17): inside its temporary <paramref name="layoutsPath"/>, which
    /// <see cref="Dispose"/> deletes, so a test that calls an audited tool through the server never writes or prunes
    /// the user's own audit folder (%LOCALAPPDATA% or WAM_AUDIT_PATH). Layout storage reads only the top-level
    /// <c>*.json</c> files, so the subfolder is invisible to it.
    /// </summary>
    public static string AuditFolderFor(string layoutsPath) => Path.Combine(layoutsPath, AuditLogOptions.FolderName);

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
        // What the server's app manifest declares: per-monitor v2, or per-monitor where v2 is unknown. Each call fails
        // harmlessly when the awareness is already set (the test host has no manifest of the server's).
        if (!E2ENative.SetProcessDpiAwarenessContext(E2ENative.DpiAwarenessContextPerMonitorAwareV2))
        {
            E2ENative.SetProcessDpiAwareness(E2ENative.ProcessPerMonitorDpiAware);
        }

        return E2ENative.CurrentDpiAwareness();
    });

    /// <summary>
    /// Makes the test host per-monitor DPI aware, as the server is, so window rectangles, UI Automation
    /// bounds and captures come back in physical pixels as they would in the server. Returns the
    /// awareness in effect.
    /// </summary>
    public static string EnsureServerDpiAwareness() => DpiAwareness.Value;
}
