using System.Reflection;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The pause every server on a desktop shares (roadmap M1.10, review F7), over the real named event of
/// <see cref="NativeHotkeyWrapper.OpenDesktopPause"/>: two wrappers on one name stand for two server processes. Each test
/// uses a name of its own, never the servers' (Local\WindowsAppManagerMcp.InputPaused), so no running server is paused or
/// resumed. No hotkey is registered (RegisterHotKey is never called), and the sessions' input layer is
/// <see cref="FakeInputWrapper"/>, so nothing reaches the desktop.
/// </summary>
public class NativeHotkeyWrapperSharedPauseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string UniqueName() => $@"Local\WamTests.SharedPause.{Guid.NewGuid():N}";

    private static void Close(params IDesktopPauseFlag[] flags)
    {
        foreach (var flag in flags)
        {
            (flag as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void TheServersWrapper_OpensThePauseEveryServerOnTheDesktopOpens()
    {
        // The default stays the shared name: only the tests' wrappers use another one
        var name = (string)typeof(NativeHotkeyWrapper).GetField("DesktopPauseName", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var used = typeof(NativeHotkeyWrapper).GetField("_desktopPauseName", BindingFlags.NonPublic | BindingFlags.Instance)!;

        name.Should().Be(@"Local\WindowsAppManagerMcp.InputPaused");
        used.GetValue(new NativeHotkeyWrapper()).Should().Be(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void AWrapperWithoutAName_IsRefused(string name)
    {
        var create = () => new NativeHotkeyWrapper(name);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void APauseSetByOneServer_IsSeenByTheOther_AndClearedByItsReset()
    {
        // Arrange - two servers open the same pause
        var name = UniqueName();
        var first = new NativeHotkeyWrapper(name).OpenDesktopPause();
        var second = new NativeHotkeyWrapper(name).OpenDesktopPause();
        try
        {
            second.IsSet.Should().BeFalse("a new pause is not set");

            // Act & Assert - the hotkey in the first server pauses; the second sees it and can wait on it
            first.Set();
            second.IsSet.Should().BeTrue();
            second.WaitHandle.WaitOne(0).Should().BeTrue();

            // A confirmed resume_input in the second server resumes both
            second.Reset();
            first.IsSet.Should().BeFalse();
        }
        finally
        {
            Close(first, second);
        }
    }

    [Fact]
    public void AServerThatOpensThePauseWhileItIsSet_SeesItSet()
    {
        // Opening the event of a running server must not reset it (initialState applies only to a new event)
        var name = UniqueName();
        var running = new NativeHotkeyWrapper(name).OpenDesktopPause();
        try
        {
            running.Set();
            var started = new NativeHotkeyWrapper(name).OpenDesktopPause();
            try
            {
                started.IsSet.Should().BeTrue();
            }
            finally
            {
                Close(started);
            }
        }
        finally
        {
            Close(running);
        }
    }

    [Fact]
    public void PausesOfDifferentNames_AreSeparate()
    {
        // What keeps these tests away from the servers' pause
        var one = new NativeHotkeyWrapper(UniqueName()).OpenDesktopPause();
        var other = new NativeHotkeyWrapper(UniqueName()).OpenDesktopPause();
        try
        {
            one.Set();

            other.IsSet.Should().BeFalse();
        }
        finally
        {
            Close(one, other);
        }
    }

    [Fact]
    public void TwoInputSessions_OnTheNamedPause_PauseAndResumeEachOther()
    {
        // Arrange - two servers' sessions, each with its own wrapper on the same name, as two processes would be
        var name = UniqueName();
        using var first = new InputSession(new FakeInputWrapper());
        using var second = new InputSession(new FakeInputWrapper());
        first.ShareDesktopPause(new NativeHotkeyWrapper(name).OpenDesktopPause());
        second.ShareDesktopPause(new NativeHotkeyWrapper(name).OpenDesktopPause());
        var running = second.PauseToken;

        // Act - the user presses the hotkey, which the first server registered
        first.TogglePauseByHotkey();

        // Assert - the second server's watcher sees the event at once and stops its running call
        SpinWait.SpinUntil(() => running.IsCancellationRequested, Timeout).Should().BeTrue();
        second.IsPaused.Should().BeTrue();
        second.PauseState.PausedBy.Should().Be(InputControlSources.Hotkey);

        // The user confirms resume_input in the second server: the first one resumes too
        second.Resume(InputControlSources.ResumeInput).Should().BeTrue();
        first.IsPaused.Should().BeFalse();
        first.PauseState.ResumedBy.Should().Be(InputControlSources.OtherServer);
    }
}
