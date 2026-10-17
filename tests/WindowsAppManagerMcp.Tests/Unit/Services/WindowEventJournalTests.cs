namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The window event journal (roadmap M7.2): it records from the first read until it is idle, reads each event's top-level
/// window on its own thread, leaves out child windows, noise and the host's title changes, and reads back by sequence number
/// with limits, filters and missed counts. Over <see cref="FakeWindowEventSource"/> and a mocked window layer: no hook is
/// installed and no real window is read.
/// </summary>
public class WindowEventJournalTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private const uint Foreground = 0x0003, MinimizeStart = 0x0016, MinimizeEnd = 0x0017;
    private const uint Destroy = 0x8001, Show = 0x8002, Hide = 0x8003, NameChange = 0x800C, Create = 0x8000;
    private const uint GwOwner = 4;

    private const nint Notepad = 0x1A0B2C, SaveAs = 0x1A0C00, Edit = 0x1A0D00, Tooltip = 0x1A0E00, Terminal = 0x2B0000;

    private readonly FakeWindowEventSource _source = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<IHostGuard> _host = new();
    private readonly ManualClock _clock = new();
    private readonly Dictionary<nint, string> _titles = [];
    private readonly WindowEventJournal _sut;

    public WindowEventJournalTests()
    {
        Window(Notepad, "Untitled - Notepad", "Notepad", 4_100);
        Window(SaveAs, "Save As", "#32770", 4_100, owner: Notepad);
        Window(Edit, "", "Edit", 4_100, root: Notepad);
        Window(Tooltip, "", "tooltips_class32", 4_100);
        Window(Terminal, "pwsh", "CASCADIA_HOSTING_WINDOW_CLASS", 6_400);
        _windows.Setup(w => w.GetProcessName(4_100u)).Returns("notepad");
        _windows.Setup(w => w.GetProcessName(6_400u)).Returns("WindowsTerminal");
        _host.Setup(h => h.IsHostWindow(Terminal)).Returns(true);
        _sut = new WindowEventJournal(_source, _windows.Object, _host.Object, _clock);
    }

    public void Dispose() => _sut.Dispose();

    private void Window(nint handle, string title, string className, uint processId, nint owner = 0, nint? root = null)
    {
        _titles[handle] = title;
        _windows.Setup(w => w.IsWindow(handle)).Returns(true);
        _windows.Setup(w => w.IsWindowVisible(handle)).Returns(true);
        _windows.Setup(w => w.GetAncestor(handle, WindowAncestor.Root)).Returns(root ?? handle);
        _windows.Setup(w => w.GetClassName(handle)).Returns(className);
        _windows.Setup(w => w.GetWindowText(handle)).Returns(() => _titles[handle]);
        _windows.Setup(w => w.GetWindowThreadProcessId(handle)).Returns(processId);
        _windows.Setup(w => w.GetWindow(handle, GwOwner)).Returns(owner);
    }

    /// <summary>Starts the recording, as the agent's first call does.</summary>
    private long Start()
    {
        var first = _sut.Read(null, 50);
        first.StartedNow.Should().BeTrue();
        return first.NextSeq;
    }

    /// <summary>Raises the events, then waits until the journal's worker has recorded <paramref name="expected"/> more.</summary>
    private void RaiseAndWait(long since, int expected, params (uint Type, nint Window)[] events)
    {
        foreach (var (type, window) in events)
        {
            _source.Raise(type, window);
        }

        SpinWait.SpinUntil(() => _sut.Read(since, 200).Events.Count >= expected, HangGuard)
            .Should().BeTrue($"the worker records {expected} event(s)");
    }

    [Fact]
    public void BeforeTheFirstRead_NothingIsRecorded_AndNothingListens()
    {
        _source.Subscriptions.Should().Be(0);
        _sut.IsRecording.Should().BeFalse();
    }

    [Fact]
    public void TheFirstRead_StartsTheRecording_WithNoEvents()
    {
        // Act
        var first = _sut.Read(null, 50);
        var second = _sut.Read(first.NextSeq, 50);

        // Assert
        first.StartedNow.Should().BeTrue();
        first.Events.Should().BeEmpty();
        first.RecordingSince.Should().Be(_clock.Now);
        second.StartedNow.Should().BeFalse();
        _source.Active.Should().Be(1, "the journal listens once, for as long as it records");
    }

    [Fact]
    public void ADialogThatComesUp_IsRecorded_WithItsTitleClassProcessAndOwner()
    {
        // Arrange
        var since = Start();

        // Act
        RaiseAndWait(since, 2, (Show, SaveAs), (Foreground, SaveAs));
        var read = _sut.Read(since, 50);

        // Assert
        read.Events.Select(e => e.Type).Should().Equal(WindowEventType.Shown, WindowEventType.Foreground);
        var shown = read.Events[0];
        shown.Should().BeEquivalentTo(new
        {
            Seq = since + 1,
            Handle = (long)SaveAs,
            Title = "Save As",
            ClassName = "#32770",
            ProcessName = "notepad",
            ProcessId = 4_100,
            OwnerHandle = (long?)Notepad
        });
        read.NextSeq.Should().Be(since + 2);
        read.More.Should().BeFalse();
    }

    [Fact]
    public void ChildWindows_Tooltips_AndEventsTheJournalDoesNotRecord_AreLeftOut()
    {
        // Arrange
        var since = Start();

        // Act - a control inside Notepad, a tooltip, a create (a hidden window, usually), then one that counts
        RaiseAndWait(since, 1, (Show, Edit), (Show, Tooltip), (Create, SaveAs), (Show, SaveAs));

        // Assert
        _sut.Read(since, 50).Events.Should().ContainSingle().Which.Handle.Should().Be(SaveAs);
    }

    [Fact]
    public void AClosedWindow_IsRecordedWithItsLastTitle_OnlyWhenTheJournalKnewIt()
    {
        // Arrange - the dialog was seen; another window closes that the journal never saw
        var since = Start();
        RaiseAndWait(since, 1, (Show, SaveAs));
        _windows.Setup(w => w.IsWindow(SaveAs)).Returns(false);

        // Act
        RaiseAndWait(since, 2, (Destroy, 0x777777), (Destroy, SaveAs));

        // Assert
        var destroyed = _sut.Read(since, 50).Events[^1];
        destroyed.Type.Should().Be(WindowEventType.Destroyed);
        destroyed.Title.Should().Be("Save As");
        _sut.Read(since, 50).Events.Should().HaveCount(2);
    }

    [Fact]
    public void ATitleChange_IsRecordedOnlyWhenTheTitleChanged_AndNeverForTheAgentsOwnHost()
    {
        // Arrange - the host is known (it came to the front), and so is Notepad
        var since = Start();
        RaiseAndWait(since, 2, (Foreground, Terminal), (Show, Notepad));

        // Act - the terminal retitles itself; Notepad sends a name change without a new title, then with one
        _titles[Terminal] = "✳ working";
        _source.Raise(NameChange, Terminal);
        _source.Raise(NameChange, Notepad);
        _titles[Notepad] = "notes.txt - Notepad";
        RaiseAndWait(since, 3, (NameChange, Notepad));

        // Assert
        var renamed = _sut.Read(since, 50).Events[^1];
        renamed.Type.Should().Be(WindowEventType.TitleChanged);
        renamed.Handle.Should().Be(Notepad);
        renamed.Title.Should().Be("notes.txt - Notepad");
        _sut.Read(since, 50).Events.Should().HaveCount(3);
    }

    [Fact]
    public void MinimizedAndRestored_AreRecorded()
    {
        var since = Start();

        RaiseAndWait(since, 2, (MinimizeStart, Notepad), (MinimizeEnd, Notepad));

        _sut.Read(since, 50).Events.Select(e => e.Type).Should().Equal(WindowEventType.Minimized, WindowEventType.Restored);
    }

    [Fact]
    public void ALimit_ReturnsTheOldestFirst_WithMore_AndNextSeqContinuesFromThere()
    {
        // Arrange
        var since = Start();
        RaiseAndWait(since, 5, (Show, SaveAs), (Hide, SaveAs), (Show, SaveAs), (Hide, SaveAs), (Show, SaveAs));

        // Act
        var first = _sut.Read(since, 2);
        var rest = _sut.Read(first.NextSeq, 50);

        // Assert
        first.Events.Select(e => e.Seq).Should().Equal(since + 1, since + 2);
        first.More.Should().BeTrue();
        rest.Events.Select(e => e.Seq).Should().Equal(since + 3, since + 4, since + 5);
        rest.More.Should().BeFalse();
    }

    [Fact]
    public void WithoutSinceSeq_TheLatestEventsAreReturned()
    {
        var since = Start();
        RaiseAndWait(since, 4, (Show, SaveAs), (Hide, SaveAs), (Show, SaveAs), (Foreground, SaveAs));

        var latest = _sut.Read(null, 2);

        latest.Events.Select(e => e.Type).Should().Equal(WindowEventType.Shown, WindowEventType.Foreground);
        latest.NextSeq.Should().Be(since + 4);
    }

    [Fact]
    public void AFilter_KeepsOnlyTheEventsItAccepts_ButNextSeqPassesTheOthers()
    {
        var since = Start();
        RaiseAndWait(since, 3, (Show, SaveAs), (Foreground, SaveAs), (Hide, SaveAs));

        var read = _sut.Read(since, 50, e => e.Type == WindowEventType.Foreground);

        read.Events.Should().ContainSingle().Which.Type.Should().Be(WindowEventType.Foreground);
        read.NextSeq.Should().Be(since + 3);
    }

    [Fact]
    public void ACursorPastTheLastEvent_FromAnEarlierServer_StillGetsTheNewEvents()
    {
        // Arrange - a client kept nextSeq 300 from a server that ran before this one
        var since = Start();
        RaiseAndWait(since, 1, (Show, SaveAs));

        // Act
        var read = _sut.Read(300, 50);

        // Assert
        read.Events.Should().ContainSingle().Which.Handle.Should().Be(SaveAs);
        read.NextSeq.Should().Be(since + 1);
    }

    [Fact]
    public void ANegativeCursor_ReadsFromTheStart_WithoutMissed()
    {
        var since = Start();
        RaiseAndWait(since, 1, (Show, SaveAs));

        var read = _sut.Read(-5, 50);

        read.Events.Should().ContainSingle();
        read.Missed.Should().Be(0);
    }

    [Fact]
    public void EventsTheJournalNoLongerHolds_AreCountedAsMissed()
    {
        // Arrange - more events than the journal keeps
        var since = Start();
        var events = Enumerable.Range(0, WindowEventJournal.Capacity + 10).Select(i => (i % 2 == 0 ? Show : Hide, SaveAs)).ToArray();
        foreach (var (type, window) in events)
        {
            _source.Raise(type, window);
        }

        SpinWait.SpinUntil(() => _sut.Read(null, 1).NextSeq >= since + events.Length, HangGuard).Should().BeTrue();

        // Act
        var read = _sut.Read(since, 200);

        // Assert
        read.Missed.Should().Be(10);
        read.Events[0].Seq.Should().Be(since + 11);
    }

    [Fact]
    public void WithoutAReadFor10Minutes_TheRecordingStops_AndTheNextReadStartsItAgain()
    {
        // Arrange
        var since = Start();
        RaiseAndWait(since, 1, (Show, SaveAs));

        // Act - the next event after the idle time lets the worker see it
        _clock.Advance(WindowEventJournal.IdleStop);
        _source.Raise(Show, Notepad);
        SpinWait.SpinUntil(() => _source.Active == 0, HangGuard).Should().BeTrue("the idle journal stops listening");
        var again = _sut.Read(since, 50);

        // Assert
        again.StartedNow.Should().BeTrue();
        again.Missed.Should().Be(0, "a restarted journal says so with startedNow");
        _source.Active.Should().Be(1);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        Start();

        _sut.Dispose();

        _source.Active.Should().Be(0);
        _sut.IsRecording.Should().BeFalse();
    }
}
