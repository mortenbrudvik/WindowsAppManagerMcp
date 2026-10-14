using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Integration;

/// <summary>
/// scroll and the clipboard are wired the way Program.cs registers them (<see cref="ServiceRegistration"/>, roadmap M2.15
/// and M2.16): the input service gets the client area, the wheel routing and the clipboard origin the clipboard service
/// uses, so the agent's copy keys count as its copies. Resolving the services touches neither the clipboard nor the
/// desktop: the clipboard wrapper only starts a thread when it is called.
/// </summary>
public sealed class ScrollClipboardRegistrationTests : IDisposable
{
    private readonly ServiceProvider _provider = new ServiceCollection()
        .AddWindowsAppManagerServices(() => LayoutStorageOptions.ForPath(
            Path.Combine(Path.GetTempPath(), $"ScrollClipboardRegistrationTests_{Guid.NewGuid()}")))
        .AddSingleton<INativeWindowWrapper>(new ReadOnlyNativeWindowWrapper())
        .BuildServiceProvider();

    public void Dispose() => _provider.Dispose();

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    [Fact]
    public void TheInputService_GetsTheClientArea_TheWheelRouting_AndTheClipboardOrigin()
    {
        var input = _provider.GetRequiredService<IInputSimulationService>();

        Field(input, "_hitTest").Should().BeSameAs(_provider.GetRequiredService<INativeHitTestWrapper>());
        Field(input, "_systemInfo").Should().BeSameAs(_provider.GetRequiredService<INativeSystemInfoWrapper>());
        Field(input, "_clipboardOrigin").Should().BeSameAs(_provider.GetRequiredService<IClipboardOrigin>());
    }

    [Fact]
    public void TheInputService_GetsTheKeyboardLayoutQueries()
    {
        // Without them send_keys would map every character with the invariant layout (roadmap M4.1)
        var input = _provider.GetRequiredService<IInputSimulationService>();

        Field(input, "_keyboardLayout").Should().BeSameAs(_provider.GetRequiredService<INativeKeyboardLayoutWrapper>())
            .And.BeOfType<NativeKeyboardLayoutWrapper>();
    }

    [Fact]
    public void TheInputService_GetsThePasteGuard_AndTheOriginReadsWindows()
    {
        // Without them the agent could paste the user's clipboard, or a copy the user makes in another window of the
        // target's app would count as the agent's (review PRIVA-1, PRIVA-2)
        var input = _provider.GetRequiredService<IInputSimulationService>();
        var pasteGuard = _provider.GetRequiredService<IClipboardPasteGuard>();

        Field(input, "_pasteGuard").Should().BeSameAs(pasteGuard);
        pasteGuard.Should().BeOfType<ClipboardPasteGuard>();
        Field(pasteGuard, "_origin").Should().BeSameAs(_provider.GetRequiredService<IClipboardOrigin>());
        Field(pasteGuard, "_options").Should().BeSameAs(_provider.GetRequiredService<ClipboardOptions>());
        Field(_provider.GetRequiredService<IClipboardOrigin>(), "_windows").Should().BeSameAs(_provider.GetRequiredService<INativeWindowWrapper>());
    }

    [Fact]
    public void TheClipboardService_UsesTheSameOrigin_AndTheUsersSetting()
    {
        var clipboard = _provider.GetRequiredService<IClipboardService>();

        clipboard.Should().BeOfType<ClipboardService>();
        Field(clipboard, "_origin").Should().BeSameAs(_provider.GetRequiredService<IClipboardOrigin>());
        Field(clipboard, "_options").Should().BeSameAs(_provider.GetRequiredService<ClipboardOptions>());
        _provider.GetRequiredService<INativeClipboardWrapper>().Should().BeOfType<NativeClipboardWrapper>();
    }

    [Fact]
    public void TheClipboardTools_AreRegistered()
    {
        _provider.GetRequiredService<WindowsAppManagerMcp.Tools.ClipboardTools>().Should().NotBeNull();
    }
}
