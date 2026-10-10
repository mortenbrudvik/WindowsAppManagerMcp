using Microsoft.Extensions.DependencyInjection;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The server the E2E tests drive: the real registrations with every native wrapper guarded. These tests
/// build the container and call wrappers only with arguments that would be harmless even if a guard
/// failed (an empty input batch, a null window).
/// </summary>
public class ServerHarnessTests : IDisposable
{
    private readonly string _layoutsPath = Path.Combine(Path.GetTempPath(), "WamE2E-tests-" + Guid.NewGuid().ToString("N"));

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
        var services = ServerHarness.CreateServices(tier, 4242, new Win32DesktopProbe(), _layoutsPath, out var guarded);

        // Assert
        guarded.Should().Contain(new[]
        {
            typeof(INativeWindowWrapper),
            typeof(INativeInputWrapper),
            typeof(IUIAutomationWrapper),
            typeof(IScreenCaptureWrapper)
        });
        services.Where(d => WrapperGuard.IsNativeWrapper(d.ServiceType))
            .Should().OnlyContain(d => d.ImplementationFactory != null && d.ImplementationType == null,
                "every wrapper the server registers must be created behind the guard");
    }

    [Fact]
    public void NoInputServer_RefusesInputAndWindowChanges()
    {
        // Arrange
        using var provider = ServerHarness
            .CreateServices(DesktopTier.NoInput, 4242, new Win32DesktopProbe(), _layoutsPath, out _)
            .BuildServiceProvider();
        var input = provider.GetRequiredService<INativeInputWrapper>();
        var windows = provider.GetRequiredService<INativeWindowWrapper>();

        // Act
        var sendInput = () => input.SendInput(Array.Empty<InputEvent>());
        var setForeground = () => windows.SetForegroundWindow(0);

        // Assert
        sendInput.Should().Throw<E2EGuardException>();
        setForeground.Should().Throw<E2EGuardException>();
    }

    [Fact]
    public void InputServer_RefusesWindowChangesOutsideTheFixture()
    {
        // Arrange - process 4242 owns no window that a null handle could name
        using var provider = ServerHarness
            .CreateServices(DesktopTier.Input, 4242, new Win32DesktopProbe(), _layoutsPath, out _)
            .BuildServiceProvider();
        var windows = provider.GetRequiredService<INativeWindowWrapper>();

        // Act
        var setForeground = () => windows.SetForegroundWindow(0);

        // Assert
        setForeground.Should().Throw<E2EGuardException>().WithMessage("*not the fixture*");
    }

    [Theory]
    [InlineData(DesktopTier.NoInput)]
    [InlineData(DesktopTier.Input)]
    public void Create_WhenTheTierIsNotOptedIn_Refuses(DesktopTier tier)
    {
        var act = () => ServerHarness.Create(tier, 4242, new Win32DesktopProbe(), _ => null);

        act.Should().Throw<InvalidOperationException>().WithMessage("Refusing to touch the desktop*");
    }
}
