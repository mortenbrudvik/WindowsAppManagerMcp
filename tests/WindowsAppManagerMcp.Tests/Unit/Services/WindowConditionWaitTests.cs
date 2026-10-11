namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for WindowWaitService.WaitForConditionAsync (roadmap M3.7, wait_for_window): each condition, stableMs,
/// newOnly and excludeHandles, ambiguity, the early ends, the timeout, cancellation, progress and the poll interval. The
/// windows are a <see cref="FakeWindowDesktop"/> read through the real WindowService (so the selector rules are the
/// resolver's), changed at given times on a manual clock; nothing reads or touches the real desktop.
/// </summary>
public class WindowConditionWaitTests
{
    private readonly FakeWindowDesktop _desktop = new();
    private readonly ManualClock _clock = new();
    private readonly long _start;
    private readonly List<(long AtMs, Action Change)> _changes = [];
    private readonly Mock<INativeProcessWrapper> _processes = new();

    public WindowConditionWaitTests()
    {
        _start = _clock.GetTimestamp();
        _processes.Setup(p => p.WaitForInputIdle(It.IsAny<int>(), It.IsAny<int>())).Returns(true);
    }

    /// <summary>The delays the wait asked for, in order.</summary>
    private List<TimeSpan> Delays { get; } = [];

    private long NowMs => (long)_clock.GetElapsedTime(_start).TotalMilliseconds;

    private void At(long atMs, Action change) => _changes.Add((atMs, change));

    private WindowWaitService CreateService() =>
        new(_desktop.Native.Object, _desktop.CreateService(), _processes.Object, _clock, DelayAsync);

    private Task<WindowConditionResult> WaitAsync(
        WindowSelector window,
        WindowWaitCondition condition = WindowWaitCondition.Exists,
        int timeoutMs = 5000,
        int stableMs = 300,
        bool newOnly = false,
        IReadOnlyCollection<nint>? excludeHandles = null,
        string? previousTitle = null,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        CreateService().WaitForConditionAsync(new WindowConditionRequest
        {
            Window = window,
            Condition = condition,
            TimeoutMs = timeoutMs,
            StableMs = stableMs,
            NewOnly = newOnly,
            ExcludeHandles = excludeHandles ?? [],
            PreviousTitle = previousTitle
        }, progress, cancellationToken);

    /// <summary>Lets the time pass and applies the changes due by then.</summary>
    private Task DelayAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(wait);
        _clock.Advance(wait);
        foreach (var change in _changes.Where(c => c.AtMs <= NowMs).ToList())
        {
            _changes.Remove(change);
            change.Change();
        }

        return Task.CompletedTask;
    }

    #region exists

    [Fact]
    public async Task Exists_ADialogThatOpens_IsReturnedOnceItHasStayedForStableMs()
    {
        // Arrange - Notepad is open; its Save As dialog opens 300 ms into the wait
        _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        nint dialog = 0;
        At(300, () => dialog = _desktop.Add("Save As", "#32770", processName: "notepad"));

        // Act
        var result = await WaitAsync(new WindowSelector { TitleEquals = "Save As" });

        // Assert - seen at 300 ms, stable at 600 ms
        result.Satisfied.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().BeNull();
        result.Window!.Handle.Should().Be(dialog);
        result.Window.Title.Should().Be("Save As");
        result.ElapsedMs.Should().Be(600);
    }

    [Fact]
    public async Task Exists_AWindowThatIsAlreadyOpen_IsReturnedAfterStableMs()
    {
        var notepad = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");

        var result = await WaitAsync(new WindowSelector { ProcessName = "notepad" });

        result.Satisfied.Should().BeTrue();
        result.Window!.Handle.Should().Be(notepad);
        result.ElapsedMs.Should().Be(300);
    }

    [Fact]
    public async Task Exists_WithStableMsZero_IsAnsweredByTheFirstRead_WithoutWaiting()
    {
        _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");

        var result = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, stableMs: 0);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(0);
        Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Exists_WithTimeoutZero_IsOneCheck_WhateverStableMs()
    {
        _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");

        var open = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, timeoutMs: 0);
        var missing = await WaitAsync(new WindowSelector { ProcessName = "calc" }, timeoutMs: 0);

        open.Satisfied.Should().BeTrue();
        missing.TimedOut.Should().BeTrue();
        Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Exists_ASplashScreenThatClosesBeforeStableMs_DoesNotCount()
    {
        // Arrange - a splash screen for 200 ms, the main window from 500 ms
        nint splash = 0, main = 0;
        At(100, () => splash = _desktop.Add("App", "Splash", processName: "app"));
        At(300, () => _desktop.Close(splash));
        At(500, () => main = _desktop.Add("App", "MainWindow", processName: "app"));

        // Act
        var result = await WaitAsync(new WindowSelector { TitleEquals = "App" });

        // Assert
        result.Window!.Handle.Should().Be(main);
        result.ElapsedMs.Should().Be(800);
    }

    [Fact]
    public async Task Exists_NothingAppears_TimesOut_WithWaitTimedOut()
    {
        _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");

        var result = await WaitAsync(new WindowSelector { TitleEquals = "Save As" }, timeoutMs: 2000);

        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.WaitTimedOut));
        result.Error.Should().Contain("titleEquals 'Save As'").And.Contain("2000 ms");
        result.Window.Should().BeNull();
        result.ElapsedMs.Should().Be(2000);
    }

    [Fact]
    public async Task Exists_ExcludeHandles_IgnoresTheWindowsSeenBefore()
    {
        // Arrange - one Notepad window was open before the action; the action opens a second
        var before = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        nint second = 0;
        At(400, () => second = _desktop.Add("Untitled - Notepad", "Notepad", processName: "notepad"));

        // Act
        var result = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, excludeHandles: [before]);

        // Assert
        result.Window!.Handle.Should().Be(second);
        result.ElapsedMs.Should().Be(700);
    }

    [Fact]
    public async Task Exists_NewOnly_IgnoresTheWindowsOpenWhenTheWaitStarted()
    {
        _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        nint second = 0;
        At(200, () => second = _desktop.Add("Untitled - Notepad", "Notepad", processName: "notepad"));

        var result = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, newOnly: true);

        result.Window!.Handle.Should().Be(second);
    }

    [Fact]
    public async Task Exists_SeveralMatches_IsSatisfied_WithTheCandidatesAndNoWindow()
    {
        // Arrange - two Chrome windows: something exists, but not one window to act on
        var inbox = _desktop.Add("Inbox - Google Chrome", "Chrome_WidgetWin_1", processName: "chrome");
        var docs = _desktop.Add("Docs - Google Chrome", "Chrome_WidgetWin_1", processName: "chrome");

        // Act
        var result = await WaitAsync(new WindowSelector { ProcessName = "chrome" });

        // Assert
        result.Satisfied.Should().BeTrue();
        result.Window.Should().BeNull("a selector never silently picks the first of several windows");
        result.MatchCount.Should().Be(2);
        result.Candidates.Select(c => c.Handle).Should().Equal(inbox, docs);
    }

    [Fact]
    public async Task Exists_Index_PicksOneOfTheWindowsThatCount()
    {
        _desktop.Add("Inbox - Google Chrome", "Chrome_WidgetWin_1", processName: "chrome");
        var docs = _desktop.Add("Docs - Google Chrome", "Chrome_WidgetWin_1", processName: "chrome");

        var result = await WaitAsync(new WindowSelector { ProcessName = "chrome", Index = 1 });

        result.Window!.Handle.Should().Be(docs);
        result.MatchCount.Should().Be(1);
    }

    [Fact]
    public async Task Exists_AnUntitledDialog_IsFoundByItsClass()
    {
        // Arrange - an untitled dialog, which the listing leaves out unless the selector names its class
        _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        nint dialog = 0;
        At(100, () => dialog = _desktop.Add("", "#32770", processName: "notepad"));

        // Act
        var byClass = await WaitAsync(new WindowSelector { ProcessName = "notepad", ClassName = "#32770" });

        // Assert
        byClass.Window!.Handle.Should().Be(dialog);
    }

    [Fact]
    public async Task Exists_AnUntitledWindow_CountsOnlyWithIncludeUntitled()
    {
        _desktop.Add("", "Popup", processName: "app");

        var without = await WaitAsync(new WindowSelector { ProcessName = "app" }, timeoutMs: 500);
        var with = await WaitAsync(new WindowSelector { ProcessName = "app", IncludeUntitled = true }, timeoutMs: 500);

        without.TimedOut.Should().BeTrue();
        without.Error.Should().Contain("includeUntitled");
        with.Satisfied.Should().BeTrue();
    }

    [Fact]
    public async Task Exists_AHandleThatIsNoWindow_EndsAtOnce_WithInvalidHandle()
    {
        // A closed handle never comes back, so the wait does not sit out its timeout
        var result = await WaitAsync(new WindowSelector { Handle = 0x7777, ProcessId = 100 });

        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidHandle));
        result.ElapsedMs.Should().Be(0);
    }

    [Fact]
    public async Task Exists_AHandleWithATitleFilter_WaitsForTheWindowToMatch()
    {
        // Arrange - wait until this Notepad window shows the saved file's name
        var notepad = _desktop.Add("Untitled - Notepad", "Notepad", processName: "notepad");
        At(500, () => _desktop.SetTitle(notepad, "report.txt - Notepad"));

        // Act
        var result = await WaitAsync(new WindowSelector { Handle = notepad, TitleContains = "report.txt" });

        // Assert
        result.Satisfied.Should().BeTrue();
        result.Window!.Title.Should().Be("report.txt - Notepad");
        result.ElapsedMs.Should().Be(800);
    }

    [Fact]
    public async Task ASelectorThatCannotBeUsed_EndsAtOnce_WithInvalidArguments()
    {
        var result = await WaitAsync(new WindowSelector { TitleContains = " " });

        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be("InvalidArguments");
        result.Error.Should().Contain("titleContains");
    }

    #endregion

    #region gone

    [Fact]
    public async Task Gone_AWindowThatCloses_IsGoneOnceItHasStayedClosedForStableMs()
    {
        var dialog = _desktop.Add("Save As", "#32770", processName: "notepad");
        At(400, () => _desktop.Close(dialog));

        var result = await WaitAsync(new WindowSelector { TitleEquals = "Save As" }, WindowWaitCondition.Gone);

        result.Satisfied.Should().BeTrue();
        result.Window.Should().BeNull();
        result.ElapsedMs.Should().Be(700);
    }

    [Fact]
    public async Task Gone_AWindowThatReopensWithinStableMs_IsNotGone()
    {
        // Arrange - the dialog closes and a new one with the same title opens 100 ms later
        var first = _desktop.Add("Connecting...", "#32770", processName: "app");
        nint second = 0;
        At(200, () => _desktop.Close(first));
        At(300, () => second = _desktop.Add("Connecting...", "#32770", processName: "app"));

        // Act
        var result = await WaitAsync(new WindowSelector { TitleEquals = "Connecting..." }, WindowWaitCondition.Gone, timeoutMs: 1500);

        // Assert - still open at the timeout: the window as last seen
        result.TimedOut.Should().BeTrue();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.WaitTimedOut));
        result.Window!.Handle.Should().Be(second);
        result.Error.Should().Contain("still open");
    }

    [Fact]
    public async Task Gone_ByHandle_IsGoneWhenTheHandleIsNoWindow()
    {
        var notepad = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        At(200, () => _desktop.Close(notepad));

        var result = await WaitAsync(new WindowSelector { Handle = notepad }, WindowWaitCondition.Gone);

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(500);
    }

    [Fact]
    public async Task Gone_WhenNothingMatchedAtTheStart_IsSatisfied_ButSaysSo()
    {
        // A misspelled selector must not read as "the window closed"
        var result = await WaitAsync(new WindowSelector { TitleEquals = "Sav As" }, WindowWaitCondition.Gone);

        result.Satisfied.Should().BeTrue();
        result.MatchCount.Should().Be(0);
        result.ErrorCode.Should().BeNull();
        result.Warning.Should().Contain("No window matched").And.Contain("when the wait started");
    }

    #endregion

    #region foreground and visible

    [Fact]
    public async Task Foreground_IsSatisfiedOnceAMatchingWindowIsInFront()
    {
        var terminal = _desktop.Add("Terminal", "CASCADIA_HOSTING_WINDOW_CLASS", processName: "WindowsTerminal");
        var notepad = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        _desktop.SetForeground(terminal);
        At(300, () => _desktop.SetForeground(notepad));

        var result = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, WindowWaitCondition.Foreground);

        result.Satisfied.Should().BeTrue();
        result.Window!.Handle.Should().Be(notepad);
        result.ElapsedMs.Should().Be(600);
    }

    [Fact]
    public async Task Foreground_TimesOut_WithTheMatchingWindow_AndSaysWhatIsInFront()
    {
        var terminal = _desktop.Add("Terminal", "CASCADIA_HOSTING_WINDOW_CLASS", processName: "WindowsTerminal");
        var notepad = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        _desktop.SetForeground(terminal);

        var result = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, WindowWaitCondition.Foreground, timeoutMs: 1000);

        result.TimedOut.Should().BeTrue();
        result.Window!.Handle.Should().Be(notepad);
        result.Error.Should().Contain("'Terminal'");
    }

    [Fact]
    public async Task Visible_AMinimizedWindow_CountsOnceRestored()
    {
        var notepad = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        _desktop.SetMinimized(notepad, true);
        At(500, () => _desktop.SetMinimized(notepad, false));

        var exists = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, stableMs: 0);
        var visible = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, WindowWaitCondition.Visible);

        exists.Satisfied.Should().BeTrue("a minimized window exists");
        visible.Satisfied.Should().BeTrue();
        visible.ElapsedMs.Should().Be(800);
    }

    [Fact]
    public async Task Visible_TimesOut_WithTheWindowThatIsNotOnScreen()
    {
        var notepad = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");
        _desktop.SetMinimized(notepad, true);

        var result = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, WindowWaitCondition.Visible, timeoutMs: 1000);

        result.TimedOut.Should().BeTrue();
        result.Window!.Handle.Should().Be(notepad);
        result.Error.Should().Contain("minimized");
    }

    #endregion

    #region title_changed

    [Fact]
    public async Task TitleChanged_IsSatisfiedOnceTheNewTitleHasHeld()
    {
        // Arrange - the title passes through "Saving..." before it settles
        var notepad = _desktop.Add("*notes.txt - Notepad", "Notepad", processName: "notepad");
        At(200, () => _desktop.SetTitle(notepad, "Saving..."));
        At(300, () => _desktop.SetTitle(notepad, "notes.txt - Notepad"));

        // Act
        var result = await WaitAsync(new WindowSelector { Handle = notepad }, WindowWaitCondition.TitleChanged);

        // Assert - the settled title, 300 ms after it appeared
        result.Satisfied.Should().BeTrue();
        result.Window!.Title.Should().Be("notes.txt - Notepad");
        result.PreviousTitle.Should().Be("*notes.txt - Notepad");
        result.ElapsedMs.Should().Be(600);
    }

    [Fact]
    public async Task TitleChanged_ComparesWithPreviousTitle_WhenTheTitleChangedBeforeTheCall()
    {
        // The save finished before the wait started: the title the agent saw is what counts
        var notepad = _desktop.Add("notes.txt - Notepad", "Notepad", processName: "notepad");

        var result = await WaitAsync(new WindowSelector { Handle = notepad }, WindowWaitCondition.TitleChanged,
            previousTitle: "*notes.txt - Notepad");

        result.Satisfied.Should().BeTrue();
        result.ElapsedMs.Should().Be(300);
    }

    [Fact]
    public async Task TitleChanged_TimesOut_WithTheWindowAndItsUnchangedTitle()
    {
        var notepad = _desktop.Add("*notes.txt - Notepad", "Notepad", processName: "notepad");

        var result = await WaitAsync(new WindowSelector { Handle = notepad }, WindowWaitCondition.TitleChanged, timeoutMs: 1000);

        result.TimedOut.Should().BeTrue();
        result.Window!.Handle.Should().Be(notepad);
        result.PreviousTitle.Should().Be("*notes.txt - Notepad");
        result.Error.Should().Contain("still '*notes.txt - Notepad'");
    }

    [Fact]
    public async Task TitleChanged_TheWindowCloses_EndsWithInvalidHandle()
    {
        var notepad = _desktop.Add("*notes.txt - Notepad", "Notepad", processName: "notepad");
        At(400, () => _desktop.Close(notepad));

        var result = await WaitAsync(new WindowSelector { Handle = notepad }, WindowWaitCondition.TitleChanged);

        result.Satisfied.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.InvalidHandle));
        result.ElapsedMs.Should().Be(400);
    }

    [Fact]
    public async Task TitleChanged_ASelectorThatNamesSeveralWindows_EndsAtOnce_WithTheCandidates()
    {
        _desktop.Add("Inbox - Google Chrome", "Chrome_WidgetWin_1", processName: "chrome");
        _desktop.Add("Docs - Google Chrome", "Chrome_WidgetWin_1", processName: "chrome");

        var result = await WaitAsync(new WindowSelector { ProcessName = "chrome" }, WindowWaitCondition.TitleChanged);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.AmbiguousWindow));
        result.Candidates.Should().HaveCount(2);
        result.ElapsedMs.Should().Be(0);
    }

    [Fact]
    public async Task TitleChanged_ASelectorThatNamesNoWindow_EndsAtOnce_WithWindowNotFound()
    {
        var result = await WaitAsync(new WindowSelector { ProcessName = "notepad" }, WindowWaitCondition.TitleChanged);

        result.ErrorCode.Should().Be(nameof(WindowErrorCode.WindowNotFound));
    }

    #endregion

    #region input_idle

    [Fact]
    public async Task InputIdle_WaitsForInputIdleOnTheWindowsProcess_ForAtMost250MsAPoll()
    {
        var app = _desktop.Add("App", "MainWindow", processId: 4100, processName: "app");

        var result = await WaitAsync(new WindowSelector { ProcessName = "app" }, WindowWaitCondition.InputIdle);

        result.Satisfied.Should().BeTrue();
        result.Window!.Handle.Should().Be(app);
        result.IdleCheck.Should().Be(IdleCheck.InputIdle);
        _processes.Verify(p => p.WaitForInputIdle(4100, It.IsInRange(1, 250, Moq.Range.Inclusive)), Times.Once);
    }

    [Fact]
    public async Task InputIdle_AProcessThatIsNotIdleYet_IsWaitedFor()
    {
        _desktop.Add("App", "MainWindow", processId: 4100, processName: "app");
        _processes.SetupSequence(p => p.WaitForInputIdle(4100, It.IsAny<int>())).Returns(false).Returns(false).Returns(true);

        var result = await WaitAsync(new WindowSelector { ProcessName = "app" }, WindowWaitCondition.InputIdle);

        result.Satisfied.Should().BeTrue();
        _processes.Verify(p => p.WaitForInputIdle(4100, It.IsAny<int>()), Times.Exactly(3));
    }

    [Fact]
    public async Task InputIdle_AProcessWithoutAMessageLoop_CountsWhenTheWindowResponds()
    {
        // A console program: WaitForInputIdle cannot say, so only the window's responsiveness is checked
        _desktop.Add("Command Prompt", "ConsoleWindowClass", processId: 4100, processName: "cmd");
        _processes.Setup(p => p.WaitForInputIdle(4100, It.IsAny<int>())).Returns((bool?)null);

        var result = await WaitAsync(new WindowSelector { ProcessName = "cmd" }, WindowWaitCondition.InputIdle);

        result.Satisfied.Should().BeTrue();
        result.IdleCheck.Should().Be(IdleCheck.RespondingOnly);
    }

    [Fact]
    public async Task InputIdle_AHungWindow_IsNotReady()
    {
        var app = _desktop.Add("App", "MainWindow", processName: "app", hung: true);

        var result = await WaitAsync(new WindowSelector { ProcessName = "app" }, WindowWaitCondition.InputIdle, timeoutMs: 1000);

        result.TimedOut.Should().BeTrue();
        result.Window!.Handle.Should().Be(app);
        result.Error.Should().Contain("not responding");
    }

    [Fact]
    public async Task InputIdle_SeveralWindows_EndsWithAmbiguousWindow()
    {
        _desktop.Add("Inbox - Google Chrome", "Chrome_WidgetWin_1", processName: "chrome");
        _desktop.Add("Docs - Google Chrome", "Chrome_WidgetWin_1", processName: "chrome");

        var result = await WaitAsync(new WindowSelector { ProcessName = "chrome" }, WindowWaitCondition.InputIdle);

        result.Satisfied.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(WindowErrorCode.AmbiguousWindow));
        result.Candidates.Should().HaveCount(2);
        _processes.Verify(p => p.WaitForInputIdle(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    #endregion

    #region Timing, cancellation and progress

    [Fact]
    public async Task ThePollInterval_Is100Ms_ThenBacksOffTo250MsAfterTwoSeconds()
    {
        await WaitAsync(new WindowSelector { TitleEquals = "Never" }, timeoutMs: 3000);

        Delays.Take(20).Should().OnlyContain(d => d == TimeSpan.FromMilliseconds(100));
        Delays.Skip(20).Should().OnlyContain(d => d == TimeSpan.FromMilliseconds(250));
        Delays.Sum(d => d.TotalMilliseconds).Should().Be(3000);
    }

    [Fact]
    public async Task TheTimeout_IsClampedTo60Seconds()
    {
        var result = await WaitAsync(new WindowSelector { TitleEquals = "Never" }, timeoutMs: 600_000);

        result.ElapsedMs.Should().Be(60_000);
    }

    [Fact]
    public async Task Cancellation_EndsTheWait()
    {
        using var cancel = new CancellationTokenSource();
        At(500, cancel.Cancel);

        var act = () => WaitAsync(new WindowSelector { TitleEquals = "Never" }, cancellationToken: cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        NowMs.Should().BeLessThan(1000);
    }

    [Fact]
    public async Task Progress_IsReportedAboutOnceASecond_AsTheTimeWaited()
    {
        var progress = new RecordingProgress<OperationProgress>();

        await WaitAsync(new WindowSelector { TitleEquals = "Save As" }, timeoutMs: 3000, progress: progress);

        progress.Reports.Select(p => p.Completed).Should().Equal(1000, 2000, 3000);
        progress.Reports.Should().OnlyContain(p => p.Total == 3000 && p.Message.Contains("exists"));
    }

    #endregion
}
