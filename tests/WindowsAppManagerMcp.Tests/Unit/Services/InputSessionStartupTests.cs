using Microsoft.Extensions.Hosting;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The start-up and shut-down of the input session (roadmap M1.10): the pause hotkey is started when the
/// server starts and toggles the pause, and input the server still holds is released when the application
/// stops (which the SDK also triggers when stdin closes) and in StopAsync. The hotkey and the input layer are
/// fakes; nothing is registered or sent for real.
/// </summary>
public class InputSessionStartupTests
{
    private const uint KeyDown = 0x0000, KeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeInputWrapper _input = new();
    private readonly FakeHotkeyWrapper _native = new();
    private readonly InputSession _session;
    private readonly PauseHotkeyListener _hotkey;

    public InputSessionStartupTests()
    {
        _session = new InputSession(_input);
        _hotkey = new PauseHotkeyListener(_native, PauseHotkeyOptions.Default);
    }

    [Fact]
    public void Constructing_RegistersNothing()
    {
        using var startup = new InputSessionStartup(_session, _hotkey);

        _native.RegisteringThread.Should().BeNull();
        _hotkey.Status.State.Should().Be(PauseHotkeyState.NotStarted);
    }

    [Fact]
    public async Task TheHotkey_PausesAndResumesInput()
    {
        // Arrange
        using var startup = new InputSessionStartup(_session, _hotkey);
        await startup.StartAsync(CancellationToken.None);

        // Act & Assert - the hotkey always toggles
        _native.Press();
        SpinWait.SpinUntil(() => _session.IsPaused, Timeout).Should().BeTrue();
        _session.PauseState.PausedBy.Should().Be(InputControlSources.Hotkey);

        _native.Press();
        SpinWait.SpinUntil(() => !_session.IsPaused, Timeout).Should().BeTrue();
        _session.PauseState.ResumedBy.Should().Be(InputControlSources.Hotkey);

        await startup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplicationStopping_ReleasesWhatTheServerHolds()
    {
        // Arrange - a key the server pressed is still down (its release failed earlier)
        var lifetime = new FakeLifetime();
        using var startup = new InputSessionStartup(_session, _hotkey, lifetime);
        await startup.StartAsync(CancellationToken.None);
        _session.Send([InputEvent.Key(VkControl, KeyDown)]);

        // Act - stdin closed, or the host is shutting down
        lifetime.Stop();

        // Assert
        _input.Batches[^1].Should().Equal(InputEvent.Key(VkControl, KeyUp));
        _input.IsKeyDown(VkControl).Should().BeFalse();
        _session.LastRelease!.Reason.Should().Be("the server is stopping");

        await startup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_FreesTheHotkey_AndReleasesWhatTheServerHolds()
    {
        using var startup = new InputSessionStartup(_session, _hotkey);
        await startup.StartAsync(CancellationToken.None);
        _session.Send([InputEvent.Mouse(0x0002)]); // left button down

        await startup.StopAsync(CancellationToken.None);

        _native.Registered.Should().BeEmpty();
        _input.Batches[^1].Should().Equal(InputEvent.Mouse(0x0004));
        _session.ServerHeld.Should().BeEmpty();
    }

    [Fact]
    public async Task StopAsync_WithNothingHeld_SendsNothing()
    {
        using var startup = new InputSessionStartup(_session, _hotkey);
        await startup.StartAsync(CancellationToken.None);

        await startup.StopAsync(CancellationToken.None);

        _input.Batches.Should().BeEmpty();
    }

    /// <summary>A host lifetime the test stops by hand.</summary>
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void Stop() => _stopping.Cancel();

        public void StopApplication() => Stop();
    }
}
