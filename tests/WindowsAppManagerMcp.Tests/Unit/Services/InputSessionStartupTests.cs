using Microsoft.Extensions.Hosting;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The start-up and shut-down of the input session (roadmap M1.10): the session's pause is shared with the other
/// servers on the desktop and the pause hotkey is started when the server starts and toggles the pause, and input
/// the server still holds is released when the application stops (which the SDK also triggers when stdin closes)
/// and in StopAsync. The hotkey, the shared pause and the input layer are fakes; nothing is registered, opened or
/// sent for real.
/// </summary>
public sealed class InputSessionStartupTests : IDisposable
{
    private const uint KeyDown = 0x0000, KeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeInputWrapper _input = new();
    private readonly FakeHotkeyWrapper _native = new();
    private readonly ManualClock _clock = new();
    private readonly InputSession _session;
    private readonly PauseHotkeyListener _hotkey;

    public InputSessionStartupTests()
    {
        _session = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        _hotkey = new PauseHotkeyListener(_native, PauseHotkeyOptions.Default, retryInterval: TimeSpan.FromMilliseconds(20));
    }

    public void Dispose()
    {
        _hotkey.Dispose();
        _session.Dispose();
    }

    [Fact]
    public void Constructing_RegistersAndOpensNothing()
    {
        using var startup = new InputSessionStartup(_session, _hotkey, desktop: _native);

        _native.RegisteringThread.Should().BeNull();
        _hotkey.Status.State.Should().Be(PauseHotkeyState.NotStarted);
        _session.SharesDesktopPause.Should().BeFalse();
    }

    [Fact]
    public async Task TheHotkey_PausesAndResumesInput()
    {
        // Arrange
        using var startup = new InputSessionStartup(_session, _hotkey, desktop: _native);
        await startup.StartAsync(CancellationToken.None);

        // Act & Assert - the hotkey always toggles (a second press right after the pause is ignored)
        _native.Press();
        SpinWait.SpinUntil(() => _session.IsPaused, Timeout).Should().BeTrue();
        _session.PauseState.PausedBy.Should().Be(InputControlSources.Hotkey);

        _clock.Advance(InputSession.HotkeyResumeDelay);
        _native.Press();
        SpinWait.SpinUntil(() => !_session.IsPaused, Timeout).Should().BeTrue();
        _session.PauseState.ResumedBy.Should().Be(InputControlSources.Hotkey);

        await startup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_SharesThePause_WithTheOtherServersOnTheDesktop()
    {
        using var startup = new InputSessionStartup(_session, _hotkey, desktop: _native);

        await startup.StartAsync(CancellationToken.None);

        _session.SharesDesktopPause.Should().BeTrue();
        _native.Press();
        SpinWait.SpinUntil(() => _native.Desktop.Pause.IsSet, Timeout).Should().BeTrue("the hotkey pauses every server");
        await startup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TwoServers_TheHotkeyOfTheFirst_PausesTheSecond()
    {
        // Arrange - two servers on one desktop: the first registers the chord (and is slow to mark itself as its
        // holder), the second finds it taken by a server
        var desktop = new FakeNamedObjects();
        var firstNative = new FakeHotkeyWrapper(desktop) { OnMark = () => Thread.Sleep(200) };
        var secondNative = new FakeHotkeyWrapper(desktop) { OnRegister = (_, _, _) => Win32CallResult.Failure(1409) };
        using var first = new InputSession(new FakeInputWrapper(), PauseHotkeyOptions.Default, _clock);
        using var second = new InputSession(_input, PauseHotkeyOptions.Default, _clock);
        using var firstHotkey = new PauseHotkeyListener(firstNative, PauseHotkeyOptions.Default);
        using var secondHotkey = new PauseHotkeyListener(secondNative, PauseHotkeyOptions.Default, retryInterval: TimeSpan.FromMilliseconds(20));
        using var firstStartup = new InputSessionStartup(first, firstHotkey, desktop: firstNative);
        using var secondStartup = new InputSessionStartup(second, secondHotkey, desktop: secondNative);
        await firstStartup.StartAsync(CancellationToken.None);
        await secondStartup.StartAsync(CancellationToken.None);
        var secondAtStart = secondHotkey.Status.State;
        var running = second.PauseToken;

        // Act - the user presses the chord, which reaches the first server only
        firstNative.Press();

        // Assert - the second server stops too
        SpinWait.SpinUntil(() => running.IsCancellationRequested, Timeout).Should().BeTrue();
        second.Send([InputEvent.Key(VkControl, KeyDown)]).PausedByUser.Should().BeTrue();
        _input.Batches.Should().BeEmpty();
        secondAtStart.Should().Be(PauseHotkeyState.OtherServer,
            "the first server's Start returns only once it has marked itself as the holder, so the second never reports another program");
        secondHotkey.Status.State.Should().Be(PauseHotkeyState.OtherServer);

        await secondStartup.StopAsync(CancellationToken.None);
        await firstStartup.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task APauseThatCannotBeShared_StaysThisServersOwn()
    {
        // For example: a server running as administrator created the shared pause
        _native.OpenFails = new UnauthorizedAccessException("Access to the path is denied.");
        using var startup = new InputSessionStartup(_session, _hotkey, desktop: _native);

        await startup.StartAsync(CancellationToken.None);

        _session.SharesDesktopPause.Should().BeFalse();
        _hotkey.Status.State.Should().Be(PauseHotkeyState.Registered);
        _native.Press();
        SpinWait.SpinUntil(() => _session.IsPaused, Timeout).Should().BeTrue("the hotkey still pauses this server");
        _native.Desktop.Pause.IsSet.Should().BeFalse();
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
