namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The pause hotkey's thread (roadmap M1.10): it registers the chord on its own message-loop thread, calls
/// the action on every press, reports a refused registration (telling a chord another server holds from one
/// another program holds), tries again while the chord is taken, and frees the hotkey when stopped. The native
/// side is <see cref="FakeHotkeyWrapper"/>: no real hotkey is registered and no named object opened.
/// </summary>
public class PauseHotkeyListenerTests
{
    private const uint ModCtrlAltShiftNoRepeat = 0x0002 | 0x0001 | 0x0004 | 0x4000;
    private const uint VkPause = 0x13, VkCancel = 0x03;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeHotkeyWrapper _native = new();

    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(20);

    /// <summary>How long a slow <see cref="FakeHotkeyWrapper.OnMark"/> takes: long enough that a race with it would lose.</summary>
    private static readonly TimeSpan SlowMark = TimeSpan.FromMilliseconds(200);

    private PauseHotkeyListener CreateSut(PauseHotkeyOptions? options = null, FakeHotkeyWrapper? native = null) =>
        new(native ?? _native, options ?? PauseHotkeyOptions.Default, retryInterval: Retry);

    private static string DefaultChordId => PauseHotkeyListener.ChordId(PauseHotkeyOptions.Default.Chord!);

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
        sut.Status.Error.Should().Contain("another program already uses this chord").And.Contain("tries again").And.Contain("WAM_PAUSE_HOTKEY");
        _native.Registered.Should().BeEmpty();
    }

    [Fact]
    public void AChordAnotherServerHolds_IsReportedAsOtherServer()
    {
        // Arrange - the first server on the desktop registered the chord and marked itself as its holder
        var desktop = new FakeNamedObjects();
        using var holder = desktop.Mark(DefaultChordId);
        var native = new FakeHotkeyWrapper(desktop) { OnRegister = (_, _, _) => Win32CallResult.Failure(1409) };
        using var sut = CreateSut(native: native);

        // Act
        sut.Start(() => { });

        // Assert
        sut.Status.State.Should().Be(PauseHotkeyState.OtherServer);
        sut.Status.Error.Should().Contain("Another Windows App Manager server").And.Contain("pauses and resumes this server too");
    }

    [Fact]
    public void AChordThatIsTaken_IsRegisteredOnceItIsFree()
    {
        // Arrange - another server holds the chord, then stops; marking this server as the holder is slow
        var free = false;
        _native.OnRegister = (_, _, _) => Volatile.Read(ref free) ? Win32CallResult.Ok : Win32CallResult.Failure(1409);
        _native.OnMark = () => Thread.Sleep(SlowMark);
        using var sut = CreateSut();
        var pressed = new ManualResetEventSlim();
        sut.Start(pressed.Set);
        var whileTaken = sut.Status.State;

        // Act
        Volatile.Write(ref free, true);

        // Assert - the next attempt registers it, marks this server as its holder before it reports Registered,
        // and the hotkey works
        SpinWait.SpinUntil(() => sut.Status.State == PauseHotkeyState.Registered, Timeout).Should().BeTrue();
        whileTaken.Should().Be(PauseHotkeyState.Failed);
        _native.Desktop.IsMarked(DefaultChordId).Should().BeTrue();
        _native.Press();
        pressed.Wait(Timeout).Should().BeTrue();
    }

    [Fact]
    public void ARefusalForAnotherReason_IsNotRetried()
    {
        _native.OnRegister = (_, _, _) => Win32CallResult.Failure(87); // ERROR_INVALID_PARAMETER
        using var sut = CreateSut();

        sut.Start(() => { });
        Thread.Sleep(Retry * 10);

        sut.Status.State.Should().Be(PauseHotkeyState.Failed);
        sut.Status.Error.Should().Contain("Windows error 87").And.NotContain("tries again");
        _native.RegisterCalls.Should().Be(2, "one attempt per key of the chord, and no retry");
    }

    [Fact]
    public void Stop_WhileWaitingToTryAgain_EndsTheThread()
    {
        _native.OnRegister = (_, _, _) => Win32CallResult.Failure(1409);
        var sut = CreateSut();
        sut.Start(() => { });
        var thread = (Thread)typeof(PauseHotkeyListener)
            .GetField("_thread", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(sut)!;

        sut.Stop();

        thread.IsAlive.Should().BeFalse();
        _native.Registered.Should().BeEmpty();
    }

    [Fact]
    public void Registering_MarksThisServerAsTheHolder_AndStopRemovesTheMark()
    {
        // A slow mark: Start returns only once the mark exists, so a server that starts right after this one
        // finds the chord taken by a server (OtherServer), not by another program (Failed)
        _native.OnMark = () => Thread.Sleep(SlowMark);
        var sut = CreateSut();
        sut.Start(() => { });
        var markedWhileRunning = _native.Desktop.IsMarked(DefaultChordId);

        sut.Stop();

        markedWhileRunning.Should().BeTrue();
        _native.Desktop.IsMarked(DefaultChordId).Should().BeFalse();
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
