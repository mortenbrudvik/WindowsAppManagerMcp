using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// The input session (roadmap M1.10) is wired the way Program.cs registers it (<see cref="ServiceRegistration"/>):
/// one session for every injector of input, and the pause hotkey started by a hosted service. Resolving the
/// services only constructs them: no hotkey is registered and no thread is started (the hosted service is never
/// started here), and the window wrapper is the read-only one.
/// </summary>
public sealed class InputSessionRegistrationTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(
            Path.Combine(Path.GetTempPath(), $"InputSessionRegistrationTests_{Guid.NewGuid()}")))
        .AddSingleton<INativeWindowWrapper>(new ReadOnlyNativeWindowWrapper())
        .BuildServiceProvider();

    public void Dispose() => _provider.Dispose();

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    [Theory]
    [InlineData(typeof(IInputSession), typeof(InputSession))]
    [InlineData(typeof(IPauseHotkey), typeof(PauseHotkeyListener))]
    [InlineData(typeof(INativeHotkeyWrapper), typeof(NativeHotkeyWrapper))]
    [InlineData(typeof(PauseHotkeyOptions), typeof(PauseHotkeyOptions))]
    public void TheSessionServices_AreSingletons(Type serviceType, Type implementation)
    {
        var first = _provider.GetRequiredService(serviceType);

        first.Should().BeOfType(implementation);
        _provider.GetRequiredService(serviceType).Should().BeSameAs(first);
    }

    [Fact]
    public void EveryInjectorOfInput_UsesTheOneSession()
    {
        // A pause must stop the input tools and EnsureForeground's activation input alike
        var session = _provider.GetRequiredService<IInputSession>();

        Field(_provider.GetRequiredService<IInputSimulationService>(), "_session").Should().BeSameAs(session);
        Field(_provider.GetRequiredService<IForegroundService>(), "_session").Should().BeSameAs(session);
        Field(_provider.GetRequiredService<InputControlTools>(), "_session").Should().BeSameAs(session);
    }

    [Fact]
    public void TheSessionAndTheHotkey_UseTheConfiguredChord()
    {
        var options = _provider.GetRequiredService<PauseHotkeyOptions>();

        Field(_provider.GetRequiredService<IInputSession>(), "_hotkey").Should().BeSameAs(options);
        Field(_provider.GetRequiredService<IPauseHotkey>(), "_options").Should().BeSameAs(options);
    }

    [Fact]
    public void TheHotkeyIsStartedByAHostedService_AndNotByResolvingIt()
    {
        // Act
        var startup = _provider.GetServices<IHostedService>().OfType<InputSessionStartup>().Should().ContainSingle().Subject;

        // Assert
        Field(startup, "_session").Should().BeSameAs(_provider.GetRequiredService<IInputSession>());
        Field(startup, "_hotkey").Should().BeSameAs(_provider.GetRequiredService<IPauseHotkey>());
        _provider.GetRequiredService<IPauseHotkey>().Status.State.Should().BeOneOf(PauseHotkeyState.NotStarted, PauseHotkeyState.Disabled);
    }
}
