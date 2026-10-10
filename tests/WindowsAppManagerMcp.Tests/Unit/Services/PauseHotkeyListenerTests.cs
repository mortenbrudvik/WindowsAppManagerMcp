namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The pause hotkey's thread (roadmap M1.10): it registers the chord on its own message-loop thread, calls
/// the action on every press, reports a refused registration, and frees the hotkey when stopped. The native
/// side is <see cref="FakeHotkeyWrapper"/>: no real hotkey is registered.
/// </summary>
public class PauseHotkeyListenerTests
{
    private const uint ModCtrlAltShiftNoRepeat = 0x0002 | 0x0001 | 0x0004 | 0x4000;
    private const uint VkPause = 0x13, VkCancel = 0x03;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeHotkeyWrapper _native = new();

    private PauseHotkeyListener CreateSut(PauseHotkeyOptions? options = null) =>
        new(_native, options ?? PauseHotkeyOptions.Default);

    [Fact]
    public void Start_RegistersTheChordOnItsOwnThread_ForPauseAndBreak()
    {
        using var sut = CreateSut();

        sut.Start(() => { });

        sut.Status.Should().Be(new PauseHotkeyStatus("Ctrl+Alt+Shift+Pause", PauseHotkeyState.Registered, "default"));
        _native.QueueCreated.Should().BeTrue();
        _native.RegisteringThread.Should().NotBe(Environment.CurrentManagedThreadId);
        _native.Registered.Values.Should().BeEquivalentTo(new[] { (ModCtrlAltShiftNoRepeat, VkCancel), (ModCtrlAltShiftNoRepeat, VkPause) });
    }

    [Fact]
    public void Pressing_CallsTheAction_OnTheHotkeyThread()
    {
        // Arrange
        using var sut = CreateSut();
        var presses = new BlockingCollectionOfThreads();
        sut.Start(presses.Record);

        // Act - the keyboard sends Break; a remote session sends Pause; another message is ignored
        _native.Press(PauseHotkeyListener.FirstHotkeyId);
        _native.Post(new HotkeyMessage(HotkeyMessageKind.Other));
        _native.Post(new HotkeyMessage(HotkeyMessageKind.Hotkey, 0x1234));
        _native.Press(PauseHotkeyListener.FirstHotkeyId + 1);

        // Assert
        presses.Take(2, Timeout).Should().OnlyContain(thread => thread == _native.RegisteringThread);
        presses.Count.Should().Be(0, "a hotkey ID the listener did not register is not the pause hotkey");
    }

    [Fact]
    public void AnActionThatThrows_DoesNotStopTheHotkey()
    {
        using var sut = CreateSut();
        var calls = 0;
        var second = new ManualResetEventSlim();
        sut.Start(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("handler failed");
            }

            second.Set();
        });

        _native.Press();
        _native.Press();

        second.Wait(Timeout).Should().BeTrue();
        sut.Status.State.Should().Be(PauseHotkeyState.Registered);
    }

    [Fact]
    public void ARefusedRegistration_IsReportedWithTheReason()
    {
        // Another program already has the chord
        _native.OnRegister = (_, _, _) => Win32CallResult.Failure(1409);
        using var sut = CreateSut();

        sut.Start(() => { });

        sut.Status.State.Should().Be(PauseHotkeyState.Failed);
        sut.Status.Error.Should().Contain("another program already uses this chord").And.Contain("WAM_PAUSE_HOTKEY");
        _native.Registered.Should().BeEmpty();
    }

    [Fact]
    public void ARegistrationForOneOfTheKeys_StillWorks_AndSaysWhichKeyFailed()
    {
        _native.OnRegister = (_, _, vk) => vk == VkPause ? Win32CallResult.Failure(1409) : Win32CallResult.Ok;
        using var sut = CreateSut();
        var pressed = new ManualResetEventSlim();

        sut.Start(pressed.Set);
        _native.Press(PauseHotkeyListener.FirstHotkeyId);

        sut.Status.State.Should().Be(PauseHotkeyState.Registered);
        sut.Status.Error.Should().Contain("Pause");
        pressed.Wait(Timeout).Should().BeTrue();
    }

    [Fact]
    public void TurnedOff_RegistersNothing()
    {
        using var sut = CreateSut(PauseHotkeyOptions.FromEnvironment(_ => "none"));

        sut.Start(() => { });

        sut.Status.Should().Be(new PauseHotkeyStatus(null, PauseHotkeyState.Disabled, PauseHotkeyOptions.EnvironmentVariable));
        _native.RegisteringThread.Should().BeNull();
    }

    [Fact]
    public void AnInvalidSetting_IsReportedNextToTheDefaultChord()
    {
        using var sut = CreateSut(PauseHotkeyOptions.FromEnvironment(_ => "Ctrl+Nope"));

        sut.Start(() => { });

        sut.Status.Chord.Should().Be(PauseHotkeyOptions.DefaultChord);
        sut.Status.State.Should().Be(PauseHotkeyState.Registered);
        sut.Status.Error.Should().Contain("Ctrl+Nope");
    }

    [Fact]
    public void Stop_FreesTheHotkey_AndEndsTheThread()
    {
        var sut = CreateSut();
        sut.Start(() => { });
        var registered = _native.Registered.Keys.ToList();

        sut.Stop();

        _native.StopPostedTo.Should().Be((uint)_native.RegisteringThread!.Value);
        _native.Unregistered.Should().BeEquivalentTo(registered);
        _native.Registered.Should().BeEmpty();
    }

    [Fact]
    public void Stop_BeforeStart_DoesNothing()
    {
        var sut = CreateSut();

        sut.Stop();

        _native.StopPostedTo.Should().BeNull();
    }

    [Fact]
    public void Start_Twice_RegistersOnce()
    {
        using var sut = CreateSut();
        var registrations = 0;
        _native.OnRegister = (_, _, _) =>
        {
            Interlocked.Increment(ref registrations);
            return Win32CallResult.Ok;
        };

        sut.Start(() => { });
        sut.Start(() => { });

        registrations.Should().Be(2, "one registration per key of the chord, from the first Start only");
    }

    [Fact]
    public void AFailingMessageLoop_IsReported_AndTheHotkeyFreed()
    {
        using var sut = CreateSut();
        sut.Start(() => { });

        _native.Post(new HotkeyMessage(HotkeyMessageKind.Failed, Win32Error: 1400));

        SpinWait.SpinUntil(() => _native.Registered.Count == 0, Timeout).Should().BeTrue();
        sut.Status.State.Should().Be(PauseHotkeyState.Failed);
        sut.Status.Error.Should().Contain("1400");
    }

    /// <summary>Records the thread of each call.</summary>
    private sealed class BlockingCollectionOfThreads
    {
        private readonly System.Collections.Concurrent.BlockingCollection<int> _threads = new();

        public int Count => _threads.Count;

        public void Record() => _threads.Add(Environment.CurrentManagedThreadId);

        public List<int> Take(int count, TimeSpan timeout)
        {
            var taken = new List<int>();
            for (var i = 0; i < count; i++)
            {
                _threads.TryTake(out var thread, timeout).Should().BeTrue($"press {i + 1} should reach the action");
                taken.Add(thread);
            }

            return taken;
        }
    }
}
