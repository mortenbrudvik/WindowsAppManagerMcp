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

        // Assert
        processLeaks.Should().ContainSingle().Which.Should().Contain(nameof(NativeProcessWrapper));
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
    public void Create_WhenTheTierIsNotOptedIn_Refuses(DesktopTier tier)
    {
        var act = () => ServerHarness.Create(tier, FixturePid, new FakeDesktop(), _ => null);

        act.Should().Throw<InvalidOperationException>().WithMessage("Refusing to touch the desktop*");
    }

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
