using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// The input guard and the target policy are wired the way Program.cs registers them
/// (<see cref="ServiceRegistration"/>): every consumer gets the one policy, and input goes through the
/// guard. Resolving the services only reads (the process list, the console and foreground windows, the
/// environment); nothing is changed, and the window wrapper is the read-only one.
/// </summary>
public sealed class InputGuardRegistrationTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(
            Path.Combine(Path.GetTempPath(), $"InputGuardRegistrationTests_{Guid.NewGuid()}")))
        .AddSingleton<INativeWindowWrapper>(new ReadOnlyNativeWindowWrapper())
        .BuildServiceProvider();

    public void Dispose() => _provider.Dispose();

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    [Theory]
    [InlineData(typeof(ITargetPolicy), typeof(TargetPolicy))]
    [InlineData(typeof(IInputTargetGuard), typeof(InputTargetGuard))]
    [InlineData(typeof(IInputSafetyInfo), typeof(InputSafetyInfo))]
    [InlineData(typeof(InputGuardOptions), typeof(InputGuardOptions))]
    public void TheGuardServices_AreSingletons(Type serviceType, Type implementation)
    {
        var first = _provider.GetRequiredService(serviceType);

        first.Should().BeOfType(implementation);
        _provider.GetRequiredService(serviceType).Should().BeSameAs(first);
    }

    [Fact]
    public void InputGoesThroughTheGuard()
    {
        var input = _provider.GetRequiredService<IInputSimulationService>();

        Field(input, "_guard").Should().BeSameAs(_provider.GetRequiredService<IInputTargetGuard>());
    }

    [Fact]
    public void CloseAndKill_EnforceTheSamePolicy()
    {
        // The policy parameters are optional for tests, so check that the container fills them in
        var policy = _provider.GetRequiredService<ITargetPolicy>();

        Field(_provider.GetRequiredService<IWindowService>(), "_targetPolicy").Should().BeSameAs(policy);
        Field(_provider.GetRequiredService<IProcessService>(), "_targetPolicy").Should().BeSameAs(policy);
        Field(_provider.GetRequiredService<WindowsAppManagerMcp.Tools.ScreenshotTools>(), "_targetPolicy").Should().BeSameAs(policy);
    }

    [Fact]
    public void TheSafetySettings_AreLoggedWhenTheServerStarts()
    {
        _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<InputSafetyStartup>().Should().ContainSingle();
    }

    [Fact]
    public void SafetyInfo_ReportsTheHostThePolicyAndTheChords()
    {
        // Act
        var snapshot = _provider.GetRequiredService<IInputSafetyInfo>().Read();

        // Assert
        snapshot.Host.Should().BeSameAs(_provider.GetRequiredService<IHostGuard>().Host);
        snapshot.Policy.Should().BeSameAs(_provider.GetRequiredService<ITargetPolicy>().Settings);
        snapshot.DestructiveChords.Should().Equal(
            _provider.GetRequiredService<InputGuardOptions>().DestructiveChords.Select(c => c.Spec));
    }
}
