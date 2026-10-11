namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for WindowWaitService (roadmap M2.6): new windows against a baseline, splash screens, owned dialogs,
/// reused and tabbed windows, claims, the timeout and cancellation. The desktop is a scripted fake whose windows
/// change at given times on a manual clock; nothing reads or touches the real desktop.
/// </summary>
public class WindowWaitServiceTests
{
    private const int TerminalHandle = 0x10;
    private const int ExistingNotepadHandle = 0x20;
    private const int OtherAppHandle = 0x30;
    private const int TerminalProcessId = 100;
    private const int NotepadProcessId = 200;
    private const int OtherProcessId = 300;
    private const int LaunchedProcessId = 400;

    private readonly FakeDesktop _desktop = new();

    public WindowWaitServiceTests()
    {
        _desktop.Add(TerminalHandle, "Terminal", TerminalProcessId);
        _desktop.Add(ExistingNotepadHandle, "notes.txt - Notepad", NotepadProcessId);
        _desktop.Add(OtherAppHandle, "Other app", OtherProcessId);
        _desktop.Foreground = TerminalHandle;
    }

    /// <summary>The launch's windows: those of the launched process and of Notepad.</summary>
    private static bool LaunchOwns(WindowCandidate window) =>
        window.ProcessId is LaunchedProcessId or NotepadProcessId;

    private Task<WindowWaitResult> WaitAsync(WindowWaitOptions? options = null, Func<WindowCandidate, bool>? isOwner = null,
        CancellationToken cancellationToken = default)
    {
        var sut = _desktop.CreateService();
        var baseline = sut.TakeBaseline();
        return sut.WaitForNewWindowAsync(baseline, isOwner ?? LaunchOwns, options ?? new WindowWaitOptions(), cancellationToken);
    }

    [Fact]
    public void TakeBaseline_RecordsEveryTopLevelWindowWithItsTitle_AndTheForegroundWindow()
    {
        // Act
        var baseline = _desktop.CreateService().TakeBaseline();

        // Assert
        baseline.Titles.Should().BeEquivalentTo(new Dictionary<nint, string>
        {
            [TerminalHandle] = "Terminal",
            [ExistingNotepadHandle] = "notes.txt - Notepad",
            [OtherAppHandle] = "Other app"
        });
        baseline.Foreground.Should().Be((nint)TerminalHandle);
    }

    [Fact]
    public async Task ANewWindowOfTheLaunch_IsReturnedOnceItHasStayedForStableMs()
    {
        // Arrange - the launched app's window appears 200 ms after the start
        _desktop.At(200, d => d.Add(0x100, "Untitled - App", LaunchedProcessId));

        // Act
        var result = await WaitAsync();

        // Assert - seen at 200 ms, stable at 500 ms; then WaitForInputIdle on its process
        result.Outcome.Should().Be(WindowWaitOutcome.NewWindow);
        result.Window!.Handle.Should().Be((nint)0x100);
        result.ElapsedMs.Should().BeInRange(500, 600);
        _desktop.InputIdleWaits.Should().ContainSingle().Which.ProcessId.Should().Be(LaunchedProcessId);
    }

    [Fact]
    public async Task ASplashScreenThatCloses_IsSkipped_AndTheMainWindowIsReturned()
    {
        // Arrange - a splash screen for 200 ms, then the main window
        _desktop.At(100, d => d.Add(0x100, "Loading...", LaunchedProcessId));
        _desktop.At(300, d => d.Remove(0x100));
        _desktop.At(400, d => d.Add(0x101, "App", LaunchedProcessId));

        // Act
        var result = await WaitAsync();

        // Assert
        result.Window!.Handle.Should().Be((nint)0x101);
    }

    [Fact]
    public async Task AWindowOfAnotherApp_IsNotTheLaunchsWindow()
    {
        // Arrange - only an unrelated app opens a window during the wait
        _desktop.At(100, d => d.Add(0x100, "Something else", OtherProcessId));

        // Act
        var result = await WaitAsync(new WindowWaitOptions { TimeoutMs = 2000 });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.TimedOut);
        result.Window.Should().BeNull();
        result.ElapsedMs.Should().Be(2000);
    }

    [Fact]
    public async Task AWindowThatIsNotUserFacing_IsIgnored()
    {
        // Arrange - an untitled (or cloaked, tool, ...) window of the launched process
        _desktop.At(100, d => d.Add(0x100, "", LaunchedProcessId, userFacing: false));

        // Act
        var result = await WaitAsync(new WindowWaitOptions { TimeoutMs = 1000 });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.TimedOut);
    }

    [Fact]
    public async Task AWindowThatWasThereBeforeTheStart_IsNotNew()
    {
        // Arrange - nothing changes: the existing Notepad window belongs to the launch's app but is in the baseline
        // Act
        var result = await WaitAsync(new WindowWaitOptions { TimeoutMs = 1000 });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.TimedOut);
    }

    [Fact]
    public async Task ADialogOwnedByANewMainWindow_WaitsForTheMainWindow()
    {
        // Arrange - a dialog owned by the main window shows first, the main window right after
        _desktop.At(100, d => d.Add(0x100, "Open", LaunchedProcessId, owner: 0x101));
        _desktop.At(200, d => d.Add(0x101, "App", LaunchedProcessId));

        // Act
        var result = await WaitAsync();

        // Assert
        result.Window!.Handle.Should().Be((nint)0x101);
    }

    [Fact]
    public async Task AnUnownedWindow_GoesBeforeAnOwnedOneThatBecameStableAtTheSameTime()
    {
        // Arrange - a new window and a dialog owned by an existing window, both at once
        _desktop.At(100, d =>
        {
            d.Add(0x100, "Tip of the day", LaunchedProcessId, owner: ExistingNotepadHandle);
            d.Add(0x101, "App", LaunchedProcessId);
        });

        // Act
        var result = await WaitAsync();

        // Assert
        result.Window!.Handle.Should().Be((nint)0x101);
    }

    [Fact]
    public async Task AnOwnedDialogAlone_IsTheLaunchsWindow()
    {
        // Arrange - the launch only shows a dialog owned by an existing window (a document opened in a running app asks a question)
        _desktop.At(100, d => d.Add(0x100, "Convert file?", NotepadProcessId, owner: ExistingNotepadHandle));

        // Act
        var result = await WaitAsync();

        // Assert
        result.Window!.Handle.Should().Be((nint)0x100);
    }

    [Fact]
    public async Task AnExistingWindowOfTheAppThatCameToTheFront_IsAReusedWindow_OnceThatHolds()
    {
        // Arrange - a single-instance app brings its existing window to the front
        _desktop.At(200, d => d.Foreground = ExistingNotepadHandle);

        // Act
        var result = await WaitAsync();

        // Assert - seen at 200 ms, held for ReuseSettleMs (1000 ms)
        result.Outcome.Should().Be(WindowWaitOutcome.ReusedExistingWindow);
        result.Window!.Handle.Should().Be((nint)ExistingNotepadHandle);
        result.ElapsedMs.Should().BeInRange(1200, 1300);
    }

    [Fact]
    public async Task AnExistingWindowThatCameToTheFrontWithANewTitle_OpenedInATab()
    {
        // Arrange - Notepad opens the file in a new tab of its window and comes to the front
        _desktop.At(200, d =>
        {
            d.SetTitle(ExistingNotepadHandle, "README.md - Notepad");
            d.Foreground = ExistingNotepadHandle;
        });

        // Act
        var result = await WaitAsync();

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.OpenedInTab);
        result.Window!.Handle.Should().Be((nint)ExistingNotepadHandle);
    }

    [Fact]
    public async Task AnExistingWindowWhoseTitleNowShowsTheDocument_OpenedInATab_EvenInTheBackground()
    {
        // Arrange - Windows did not let the app come to the front, but its title shows the launched file
        _desktop.At(200, d => d.SetTitle(ExistingNotepadHandle, "README.md - Notepad"));

        // Act
        var result = await WaitAsync(new WindowWaitOptions { DocumentHints = ["README.md"] });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.OpenedInTab);
    }

    [Fact]
    public async Task AnExistingWindowWhoseTitleChangedWithoutTheDocument_IsNoSignOfTheLaunch()
    {
        // Arrange - a browser tab's title changes on its own; the window stays in the background
        _desktop.At(200, d => d.SetTitle(ExistingNotepadHandle, "(1) notes.txt - Notepad"));

        // Act
        var result = await WaitAsync(new WindowWaitOptions { TimeoutMs = 2000, DocumentHints = ["README.md"] });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.TimedOut);
    }

    [Fact]
    public async Task ANewWindow_WinsOverAReuseSignal()
    {
        // Arrange - the existing window comes to the front first, then the new window appears
        _desktop.At(100, d => d.Foreground = ExistingNotepadHandle);
        _desktop.At(500, d => d.Add(0x100, "New window", LaunchedProcessId));

        // Act
        var result = await WaitAsync();

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.NewWindow);
        result.Window!.Handle.Should().Be((nint)0x100);
    }

    [Fact]
    public async Task AnExistingWindowOfAnotherApp_ThatCameToTheFront_IsNotReused()
    {
        // Arrange - the user switched to another app
        _desktop.At(200, d => d.Foreground = OtherAppHandle);

        // Act
        var result = await WaitAsync(new WindowWaitOptions { TimeoutMs = 2000 });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.TimedOut);
    }

    [Fact]
    public async Task AWindowAnotherLaunchClaimed_IsSkipped()
    {
        // Arrange - two new windows; the first is claimed by another launch of the same batch
        _desktop.At(100, d => d.Add(0x100, "App 1", LaunchedProcessId));
        _desktop.At(200, d => d.Add(0x101, "App 2", LaunchedProcessId));
        var claimed = new HashSet<nint> { 0x100 };

        // Act
        var result = await WaitAsync(new WindowWaitOptions { TryClaim = h => claimed.Add(h) });

        // Assert
        result.Window!.Handle.Should().Be((nint)0x101);
        claimed.Should().Contain((nint)0x101);
    }

    [Fact]
    public async Task AUwpFrame_IsMatchedByTheProcessOfTheAppItHosts()
    {
        // Arrange - Calculator's frame belongs to ApplicationFrameHost; its CoreWindow child to CalculatorApp
        const int frameHost = 500;
        const int calculator = 501;
        _desktop.At(100, d =>
        {
            d.Add(0x100, "Calculator", frameHost, className: "ApplicationFrameWindow");
            d.AddChild(0x100, 0x200, "Windows.UI.Core.CoreWindow", calculator);
        });
        WindowCandidate? seen = null;

        // Act
        var result = await WaitAsync(isOwner: w =>
        {
            seen = w;
            return w.ProcessId == calculator;
        });

        // Assert
        result.Window!.Handle.Should().Be((nint)0x100);
        seen!.ProcessId.Should().Be(calculator);
        seen.HostProcessId.Should().Be(frameHost);
        _desktop.InputIdleWaits.Should().ContainSingle().Which.ProcessId.Should().Be(calculator);
    }

    [Fact]
    public async Task TheInputIdleWait_IsBoundedByTheTimeLeft()
    {
        // Arrange - the window becomes stable 100 ms before the deadline
        _desktop.At(100, d => d.Add(0x100, "App", LaunchedProcessId));

        // Act
        var result = await WaitAsync(new WindowWaitOptions { TimeoutMs = 500, InputIdleMs = 1000 });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.NewWindow);
        _desktop.InputIdleWaits.Single().Milliseconds.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task ATimeoutAboveTheMaximum_IsClamped()
    {
        // Act
        var result = await WaitAsync(new WindowWaitOptions { TimeoutMs = 600_000 });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.TimedOut);
        result.ElapsedMs.Should().Be(WindowWaitService.MaxTimeoutMs);
    }

    [Fact]
    public async Task AZeroTimeout_LooksOnce()
    {
        // Act
        var result = await WaitAsync(new WindowWaitOptions { TimeoutMs = 0 });

        // Assert
        result.Outcome.Should().Be(WindowWaitOutcome.TimedOut);
        result.ElapsedMs.Should().Be(0);
    }

    [Fact]
    public async Task ACancelledWait_Throws()
    {
        // Arrange - the call is cancelled after 300 ms
        using var call = new CancellationTokenSource();
        _desktop.At(300, _ => call.Cancel());

        // Act
        var act = () => WaitAsync(cancellationToken: call.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        _desktop.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// A desktop of top-level windows (with titles, processes, owners and UWP children) that changes at given times on a
    /// manual clock, which the wait's delay moves forward.
    /// </summary>
    private sealed class FakeDesktop
    {
        private readonly ManualClock _clock = new();
        private readonly long _start;
        private readonly List<(long AtMs, Action<FakeDesktop> Change)> _changes = [];
        private readonly List<nint> _windows = [];
        private readonly Dictionary<nint, string> _titles = [];
        private readonly Dictionary<nint, uint> _processes = [];
        private readonly Dictionary<nint, string> _classes = [];
        private readonly Dictionary<nint, nint> _owners = [];
        private readonly HashSet<nint> _userFacing = [];
        private readonly Dictionary<nint, List<nint>> _children = [];

        public FakeDesktop()
        {
            _start = _clock.GetTimestamp();
        }

        public nint Foreground { get; set; }

        public List<(int ProcessId, int Milliseconds)> InputIdleWaits { get; } = [];

        public TimeSpan Elapsed => _clock.GetElapsedTime(_start);

        public void At(long atMs, Action<FakeDesktop> change) => _changes.Add((atMs, change));

        public void Add(nint handle, string title, int processId, bool? userFacing = null, nint owner = 0, string className = "AppWindow")
        {
            _windows.Insert(0, handle);
            _titles[handle] = title;
            _processes[handle] = (uint)processId;
            _classes[handle] = className;
            _owners[handle] = owner;
            if (userFacing ?? title.Length > 0)
                _userFacing.Add(handle);
        }

        public void AddChild(nint parent, nint child, string className, int processId)
        {
            if (!_children.TryGetValue(parent, out var children))
                _children[parent] = children = [];
            children.Add(child);
            _classes[child] = className;
            _processes[child] = (uint)processId;
        }

        public void Remove(nint handle)
        {
            _windows.Remove(handle);
            _userFacing.Remove(handle);
        }

        public void SetTitle(nint handle, string title) => _titles[handle] = title;

        public WindowWaitService CreateService()
        {
            var native = new Mock<INativeWindowWrapper>();
            native.Setup(n => n.EnumWindows(It.IsAny<Func<nint, bool>>()))
                .Callback<Func<nint, bool>>(callback =>
                {
                    foreach (var handle in _windows.ToList())
                        if (!callback(handle))
                            break;
                });
            native.Setup(n => n.GetWindowText(It.IsAny<nint>())).Returns<nint>(h => _titles.GetValueOrDefault(h, string.Empty));
            native.Setup(n => n.GetForegroundWindow()).Returns(() => Foreground);
            native.Setup(n => n.GetWindowThreadProcessId(It.IsAny<nint>())).Returns<nint>(h => _processes.GetValueOrDefault(h));
            native.Setup(n => n.GetClassName(It.IsAny<nint>())).Returns<nint>(h => _classes.GetValueOrDefault(h, string.Empty));
            native.Setup(n => n.GetWindow(It.IsAny<nint>(), 4u)).Returns<nint, uint>((h, _) => _owners.GetValueOrDefault(h));
            native.Setup(n => n.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()))
                .Callback<nint, Func<nint, bool>>((parent, callback) =>
                {
                    foreach (var child in _children.GetValueOrDefault(parent) ?? [])
                        if (!callback(child))
                            break;
                });

            var windows = new Mock<IWindowService>(MockBehavior.Strict);
            windows.Setup(w => w.IsUserFacingAppWindow(It.IsAny<nint>())).Returns<nint>(h => _windows.Contains(h) && _userFacing.Contains(h));
            windows.Setup(w => w.GetWindowInfo(It.IsAny<nint>())).Returns<nint>(h => _windows.Contains(h)
                ? TestDataFactory.CreateWindowInfo((int)h, _titles[h], "app", (int)_processes[h])
                : null);

            var processes = new Mock<INativeProcessWrapper>(MockBehavior.Strict);
            processes.Setup(p => p.WaitForInputIdle(It.IsAny<int>(), It.IsAny<int>()))
                .Returns<int, int>((processId, milliseconds) =>
                {
                    InputIdleWaits.Add((processId, milliseconds));
                    return true;
                });

            return new WindowWaitService(native.Object, windows.Object, processes.Object, _clock, DelayAsync);
        }

        /// <summary>Lets the time pass and applies the changes due by then.</summary>
        private Task DelayAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _clock.Advance(wait);
            var now = (long)Elapsed.TotalMilliseconds;
            foreach (var change in _changes.Where(c => c.AtMs <= now).ToList())
            {
                _changes.Remove(change);
                change.Change(this);
            }

            return Task.CompletedTask;
        }
    }
}
