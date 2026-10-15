using Microsoft.Extensions.DependencyInjection;
using Moq;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The server the E2E tests drive: the real registrations with every native wrapper guarded. These tests
/// put strict mocks behind the guards and a fake desktop under the input policy, so nothing here calls a
/// real wrapper, reaches a real window or process, or sends input, even if a guard fails.
/// </summary>
public class ServerHarnessTests : IDisposable
{
    private const int FixturePid = FakeDesktop.FixturePid;

    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), "WamE2E-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<Type, Mock> _wrappers = new();

    public void Dispose()
    {
        if (Directory.Exists(_layoutsPath))
        {
            Directory.Delete(_layoutsPath, recursive: true);
        }
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void CreateServices_GuardsEveryNativeWrapperOfTheServer(DesktopTier tier)
    {
        // Act
        var services = ServerHarness.CreateServices(tier, FixturePid, new FakeDesktop(), _layoutsPath, out var guarded);

        // Assert - including the wrappers ProcessService and MonitorService would otherwise create themselves
        guarded.Should().Contain(new[]
        {
            typeof(INativeWindowWrapper),
            typeof(INativeInputWrapper),
            typeof(IUIAutomationWrapper),
            typeof(IScreenCaptureWrapper),
            typeof(INativeProcessWrapper),
            typeof(INativeMonitorWrapper)
        });
        services.Where(d => WrapperGuard.IsNativeWrapper(d.ServiceType))
            .Should().OnlyContain(d => d.ImplementationFactory != null && d.ImplementationType == null,
                "every wrapper the server uses must be created behind the guard");
    }

    [Theory]
    [InlineData(UiaEngine.Uia3)]
    [InlineData(UiaEngine.Uia2)]
    public void CreateServices_WithAnEngine_BuildsTheTreeOfThatEngine_BehindTheGuard(UiaEngine engine)
    {
        // Act - resolving creates the adapter; neither adapter touches UI Automation before its first read
        var services = ServerHarness.CreateServices(
            DesktopTier.NoInput, FixturePid, new FakeDesktop(), _layoutsPath, wrapperSubstitute: null, out var guarded, engine);
        using var provider = services.BuildServiceProvider();
        var tree = provider.GetRequiredService<IUiaTree>();

        // Assert - a scenario can read the fixture with both engines (roadmap M3.2)
        guarded.Should().Contain(typeof(IUiaTree));
        provider.GetRequiredService<WindowsAppManagerMcp.Services.UiaEngineOptions>().Engine.Should().Be(engine);
        tree.Should().BeAssignableTo<GuardProxy>();
        tree.Engine.Should().Be(engine);
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void EveryServerService_ReachesNativeWrappersOnlyThroughTheGuard(DesktopTier tier)
    {
        // Arrange
        var services = CreateServicesWithMockedWrappers(tier);
        using var provider = services.BuildServiceProvider();
        var serviceTypes = services
            .Where(d => !d.IsKeyedService && !d.ServiceType.ContainsGenericParameters)
            .Select(d => d.ServiceType)
            .Distinct()
            .ToList();

        // Act - resolving only constructs; no service method runs
        var leaks = new List<string>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var serviceType in serviceTypes)
        {
            NativeReferenceWalker.Walk(provider.GetRequiredService(serviceType), serviceType.Name, leaks, visited);
        }

        // Assert
        serviceTypes.Should().Contain(new[] { typeof(IProcessService), typeof(IMonitorService), typeof(IWindowService) });
        leaks.Should().BeEmpty(
            "a service that creates its own native wrapper bypasses the E2E guard; register that wrapper in " +
            $"{nameof(ServerHarness)}.{nameof(ServerHarness.AddWrappersTheServerCreatesItself)}");
    }

    [Fact]
    public void TheWalk_FindsTheWrappersServicesCreateThemselves()
    {
        // Arrange - built the way the container would build them if the wrappers were not registered
        var processService = new ProcessService(
            Mock.Of<IWindowService>(MockBehavior.Strict),
            Mock.Of<IInputValidationService>(MockBehavior.Strict),
            Mock.Of<IBrowserDetectionService>(MockBehavior.Strict));
        var monitorService = new MonitorService();

        // Act
        var processLeaks = NativeReferenceWalker.FindUnguardedWrappers(processService, nameof(ProcessService));
        var monitorLeaks = NativeReferenceWalker.FindUnguardedWrappers(monitorService, nameof(MonitorService));

        // Assert - without IWindowWaitService, ProcessService builds a WindowWaitService over a NativeWindowWrapper of its own
        processLeaks.Should().HaveCount(2);
        processLeaks.Should().ContainSingle(leak => leak.Contains(nameof(NativeProcessWrapper)));
        processLeaks.Should().ContainSingle(leak => leak.Contains(nameof(NativeWindowWrapper)));
        monitorLeaks.Should().ContainSingle().Which.Should().Contain(nameof(NativeMonitorWrapper));
    }

    [Fact]
    public void NoInputServer_RefusesInputAndWindowChanges()
    {
        // Arrange
        using var provider = CreateServicesWithMockedWrappers(DesktopTier.NoInput).BuildServiceProvider();
        var input = provider.GetRequiredService<INativeInputWrapper>();
        var windows = provider.GetRequiredService<INativeWindowWrapper>();

        // Act
        var sendInput = () => input.SendInput([InputEvent.Unicode('x', 0)]);
        var setForeground = () => windows.SetForegroundWindow(FakeDesktop.FixtureWindow);

        // Assert
        sendInput.Should().Throw<E2EGuardException>();
        setForeground.Should().Throw<E2EGuardException>();
        Wrapper<INativeInputWrapper>().VerifyNoOtherCalls();
        Wrapper<INativeWindowWrapper>().VerifyNoOtherCalls();
    }

    [Fact]
    public void InputServer_RefusesWindowChangesOutsideTheFixture()
    {
        // Arrange
        using var provider = CreateServicesWithMockedWrappers(DesktopTier.Input).BuildServiceProvider();
        var windows = provider.GetRequiredService<INativeWindowWrapper>();

        // Act
        var setForeground = () => windows.SetForegroundWindow(FakeDesktop.OtherWindow);

        // Assert
        setForeground.Should().Throw<E2EGuardException>().WithMessage("*not the fixture*");
        Wrapper<INativeWindowWrapper>().VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public async Task Server_CannotStartAProcess(DesktopTier tier)
    {
        // Arrange - a fully qualified path needs no lookup, so the launch goes straight to the process wrapper.
        // It names a file that does not exist, in a folder that is never created.
        using var provider = CreateServicesWithMockedWrappers(tier).BuildServiceProvider();
        var processService = ResolveWithoutRealWrappers<IProcessService>(provider);
        var executable = Path.Combine(_layoutsPath, "app.exe");

        // Resolving ProcessService creates WindowService and with it the host guard, which reads the
        // ancestor processes and the console window once. Only what the launch does is under test.
        Wrapper<INativeProcessWrapper>().Invocations.Clear();

        // Act
        var result = await processService.LaunchApplicationAsync(executable);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("E2E guard refused INativeProcessWrapper.StartProcess");
        Wrapper<INativeProcessWrapper>().VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public async Task Server_CannotKillAProcessThatIsNotTheFixture(DesktopTier tier)
    {
        // Arrange - kill_process opens the process, reads its name, then kills it
        var processId = Environment.ProcessId + 1;
        var handle = new Mock<IProcessHandle>(MockBehavior.Strict);
        handle.SetupGet(h => h.Id).Returns(processId);
        handle.SetupGet(h => h.ProcessName).Returns("notepad");
        handle.SetupGet(h => h.HasExited).Returns(false);
        handle.Setup(h => h.Dispose());
        using var provider = CreateServicesWithMockedWrappers(tier).BuildServiceProvider();
        var processService = ResolveWithoutRealWrappers<IProcessService>(provider);
        var processWrapper = Wrapper<INativeProcessWrapper>();
        processWrapper.Setup(w => w.OpenProcess(processId)).Returns(handle.Object);
        processWrapper.Setup(w => w.IsAncestorOfCurrentProcess(processId)).Returns(false);
        // forceKill reads the process tree for the target policy first (a read the guard lets through)
        processWrapper.Setup(w => w.GetDescendantsOf(processId)).Returns([]);

        // Act
        var result = await processService.KillProcessAsync(processId, confirm: true, forceKill: true);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("E2E guard refused IProcessHandle.Kill");
        handle.Verify(h => h.Kill(It.IsAny<bool>()), Times.Never);
        handle.Verify(h => h.Dispose(), Times.Once);
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void Server_CannotReachCdp(DesktopTier tier)
    {
        // Arrange
        using var provider = CreateServicesWithMockedWrappers(tier).BuildServiceProvider();
        var cdp = provider.GetRequiredService<ICdpService>();

        // Act
        Action targets = () => _ = cdp.GetPageTargetsAsync(9222, TimeSpan.FromSeconds(1));

        // Assert
        cdp.Should().BeAssignableTo<GuardProxy>();
        targets.Should().Throw<E2EGuardException>().WithMessage("*ICdpService*");
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void Server_CannotReachTheCdpTransport(DesktopTier tier)
    {
        // Arrange
        using var provider = CreateServicesWithMockedWrappers(tier).BuildServiceProvider();
        var transport = provider.GetRequiredService<ICdpTransport>();

        // Act
        Action list = () => _ = transport.GetJsonAsync(9222, "/json/list", TimeSpan.FromSeconds(1), CancellationToken.None);

        // Assert
        transport.Should().BeAssignableTo<GuardProxy>();
        list.Should().Throw<E2EGuardException>().WithMessage("*ICdpTransport*");
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void EveryRegisteredCdpService_IsRefused(DesktopTier tier)
    {
        // A CDP service added later (a browser-level session, a debug-browser lifecycle) must be refused too
        var cdpServices = CreateServicesWithMockedWrappers(tier)
            .Select(d => d.ServiceType)
            .Where(t => t.Name.StartsWith("ICdp", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        cdpServices.Should().NotBeEmpty();
        cdpServices.Should().BeSubsetOf(ServerHarness.RefusedServices);
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void TheAuditLog_WritesInsideTheHarnesssTemporaryFolder_NeverTheUsersOwn(DesktopTier tier)
    {
        // Arrange
        using var provider = CreateServicesWithMockedWrappers(tier).BuildServiceProvider();

        // Act - creating the log touches no file
        var audit = provider.GetRequiredService<IAuditLog>();

        // Assert - Dispose deletes the layouts folder, and the audit folder with it
        audit.Folder.Should().Be(ServerHarness.AuditFolderFor(_layoutsPath));
        audit.Folder.Should().StartWith(_layoutsPath + Path.DirectorySeparatorChar);
        _layoutsPath.Should().StartWith(Path.GetTempPath());
        Directory.Exists(audit.Folder).Should().BeFalse();
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void Create_WhenTheTierIsNotOptedIn_Refuses(DesktopTier tier)
    {
        var act = () => ServerHarness.Create(tier, FixturePid, new FakeDesktop(), _ => null);

        act.Should().Throw<InvalidOperationException>().WithMessage("Refusing to touch the desktop*");
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void RecordAgentHost_WhenTheTierIsNotOptedIn_RefusesBeforeCreatingAWrapper(DesktopTier tier)
    {
        // Arrange - if the gate ever let this through, creating a wrapper would throw, so nothing is read
        object Substitute(Type type) => throw new InvalidOperationException("no wrapper may be created");

        // Act
        var act = () => ServerHarness.RecordAgentHost(tier, _ => null, Substitute);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("Refusing to touch the desktop*");
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void RecordAgentHost_RecordsTheWindowInFrontNow_ThroughReadOnlyGuards(DesktopTier tier)
    {
        // Arrange - mocks behind the guards: the terminal is in front while the host is recorded
        const nint terminal = 0x3000;
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.GetForegroundWindow()).Returns(terminal);
        windows.Setup(w => w.GetWindowThreadProcessId(terminal)).Returns(5150u);
        var processes = new Mock<INativeProcessWrapper>();
        object Substitute(Type type) => type == typeof(INativeWindowWrapper) ? windows.Object : processes.Object;

        // Act - opted in through the reader passed in; no variable is set and no real wrapper is created
        var host = ServerHarness.RecordAgentHost(tier, OptedIn, Substitute);

        // Assert - the read reached the wrapper through the guard (the read-only policy in every tier), so a fixture started
        // after this cannot be the window recorded in front
        host.Host.StartupForegroundWindow.Should().NotBeNull();
        host.Host.StartupForegroundWindow!.Handle.Should().Be(terminal);
        host.IsHostWindow(terminal).Should().BeTrue();
        NativeReferenceWalker.FindUnguardedWrappers(host, nameof(HostGuard)).Should().BeEmpty();
        windows.Verify(w => w.GetForegroundWindow(), Times.Once);
        windows.Verify(w => w.SetForegroundWindow(It.IsAny<nint>()), Times.Never);
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void CreateServices_WithARecordedHost_GivesTheServerThatHostGuard(DesktopTier tier)
    {
        // Arrange
        var recorded = new Mock<IHostGuard>(MockBehavior.Strict).Object;

        // Act
        var services = ServerHarness.CreateServices(
            tier, FixturePid, new FakeDesktop(), _layoutsPath, StrictWrapper, out _, uiaEngine: null, agentHost: recorded);
        using var provider = services.BuildServiceProvider();

        // Assert - the services that ask whether a window is the host ask this one, recorded before the fixture started
        provider.GetRequiredService<IHostGuard>().Should().BeSameAs(recorded);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IHostGuard));
        ResolveWithoutRealWrappers<IInputTargetGuard>(provider).Should().NotBeNull();
    }

    private static string? OptedIn(string variable) =>
        variable is DesktopTestGate.EnvironmentVariable or DesktopTestGate.NoInputEnvironmentVariable ? "1" : null;

    /// <summary>The harness's composition with a strict mock behind each wrapper's guard.</summary>
    private ServiceCollection CreateServicesWithMockedWrappers(DesktopTier tier) =>
        ServerHarness.CreateServices(tier, FixturePid, new FakeDesktop(), _layoutsPath, StrictWrapper, out _);

    /// <summary>
    /// Resolves <typeparamref name="T"/> and stops the test unless every wrapper it holds is a guarded mock,
    /// so a regression in the harness fails here instead of reaching a real wrapper.
    /// </summary>
    private static T ResolveWithoutRealWrappers<T>(IServiceProvider provider) where T : notnull
    {
        var service = provider.GetRequiredService<T>();
        NativeReferenceWalker.FindUnguardedWrappers(service, typeof(T).Name)
            .Should().BeEmpty("precondition: only guarded mocks may stand behind the service under test");
        return service;
    }

    private object StrictWrapper(Type wrapperType)
    {
        if (!_wrappers.TryGetValue(wrapperType, out var mock))
        {
            mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(wrapperType), MockBehavior.Strict)!;
            _wrappers[wrapperType] = mock;
        }
        return mock.Object;
    }

    private Mock<T> Wrapper<T>() where T : class => (Mock<T>)_wrappers[typeof(T)];
}
