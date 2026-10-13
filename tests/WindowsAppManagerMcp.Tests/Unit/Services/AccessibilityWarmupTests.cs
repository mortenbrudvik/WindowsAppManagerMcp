namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="AccessibilityWarmup"/> (roadmap M3.3), over an in-memory UI Automation tree, mocked window
/// reads and a manual clock: no real window is read and no test waits.
/// </summary>
public class AccessibilityWarmupTests
{
    private const nint Browser = 0x100;
    private const nint RenderWidget = 0x101;
    private const nint HiddenTab = 0x102;
    private const uint BrowserProcess = 4000;
    private const uint WebViewProcess = 5000;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    private readonly FakeUiaTree _tree = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ManualClock _clock = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly Dictionary<nint, FakeWindow> _desktop = [];

    /// <summary>Runs after every wait between two checks, before the next one.</summary>
    private Action? _afterWait;

    public AccessibilityWarmupTests()
    {
        _windows.Setup(w => w.IsWindow(It.IsAny<nint>())).Returns((nint h) => _desktop.TryGetValue(h, out var w) && w.Alive);
        _windows.Setup(w => w.GetClassName(It.IsAny<nint>())).Returns((nint h) => _desktop.TryGetValue(h, out var w) ? w.ClassName : string.Empty);
        _windows.Setup(w => w.IsWindowVisible(It.IsAny<nint>())).Returns((nint h) => _desktop.TryGetValue(h, out var w) && w.Visible);
        _windows.Setup(w => w.GetWindowThreadProcessId(It.IsAny<nint>()))
            .Returns((nint h) => _desktop.TryGetValue(h, out var w) && w.Alive ? w.ProcessId : 0u);
        _windows.Setup(w => w.GetProcessName(BrowserProcess)).Returns("chrome");
        _windows.Setup(w => w.GetProcessName(WebViewProcess)).Returns("msedgewebview2");
        _windows.Setup(w => w.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()))
            .Callback((nint parent, Func<nint, bool> callback) =>
            {
                foreach (var child in Descendants(parent))
                {
                    if (!callback(child))
                    {
                        return;
                    }
                }
            });
    }

    private sealed class FakeWindow(string className, uint processId, nint parent, bool visible = true)
    {
        public string ClassName { get; } = className;
        public uint ProcessId { get; set; } = processId;
        public nint Parent { get; } = parent;
        public bool Visible { get; set; } = visible;
        public bool Alive { get; set; } = true;
    }

    private IEnumerable<nint> Descendants(nint parent)
    {
        foreach (var (handle, window) in _desktop.Where(w => w.Value.Parent == parent && w.Key != parent).ToList())
        {
            yield return handle;
            foreach (var below in Descendants(handle))
            {
                yield return below;
            }
        }
    }

    private AccessibilityWarmup CreateSut() => new(_tree, _windows.Object, _clock, (delay, token) =>
    {
        _waits.Add(delay);
        _clock.Advance(delay);
        _afterWait?.Invoke();
    });

    /// <summary>A Chrome window with one shown page, whose render widget's element is the page's Document.</summary>
    private FakeUiaNode ChromeWithPage(int documentChildren)
    {
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);
        _desktop[RenderWidget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        var document = new FakeUiaNode("Document", "Page");
        AddChildren(document, documentChildren);
        _tree.WithWindow(Browser, new FakeUiaNode("Window", "Page - Google Chrome"));
        _tree.WithWindow(RenderWidget, document);
        return document;
    }

    private static void AddChildren(FakeUiaNode node, int count)
    {
        for (var i = 0; i < count; i++)
        {
            node.With(new FakeUiaNode("Text", $"text {node.Children.Count}"));
        }
    }

    #region Which windows are warmed up

    [Fact]
    public void WarmUp_OfAWindowWithoutWebContent_DoesNothing()
    {
        // Arrange
        _desktop[Browser] = new FakeWindow("Notepad", BrowserProcess, 0);
        _desktop[RenderWidget] = new FakeWindow("RichEditD2DPT", BrowserProcess, Browser);

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.Reads.Should().BeEmpty("no UI Automation call is made for a window without web content");
        _tree.ChildListings.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_OfAChromiumWindowWithoutAPage_SuchAsAMenu_DoesNothing()
    {
        // Arrange - a popup of the browser frame: Chrome_WidgetWin_1, but no render widget inside
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _waits.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_OfAWindowThatIsGone_DoesNothing()
    {
        // Act
        var info = CreateSut().WarmUp(0x999, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
    }

    [Fact]
    public void WarmUp_OfTheDesktopWindow_DoesNotTouchTheApplicationsBelowIt()
    {
        // Arrange - every top-level window is a child of the desktop window, a Chrome window among them
        ChromeWithPage(documentChildren: 0);
        _desktop[0x10] = new FakeWindow("#32769", 0, 0);
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0x10);

        // Act
        var info = CreateSut().WarmUp(0x10, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.Reads.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_IgnoresHiddenPages_SuchAsBackgroundTabs()
    {
        // Arrange - only a hidden render widget: no shown page to wait for
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);
        _desktop[HiddenTab] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser, visible: false);

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
    }

    [Fact]
    public void WarmUp_OfARenderWidgetItself_WarmsItUp()
    {
        // Arrange
        ChromeWithPage(documentChildren: 2);

        // Act
        var info = CreateSut().WarmUp(RenderWidget, Budget, CancellationToken.None);

        // Assert
        info.Should().NotBeNull();
        info!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
    }

    [Fact]
    public void WarmUp_OfAWebView2PaneInAnotherApp_ReportsWebView2()
    {
        // Arrange - a WPF window hosting a WebView2: the render widget belongs to msedgewebview2.exe
        _desktop[Browser] = new FakeWindow("HwndWrapper[Outlook;;1]", BrowserProcess, 0);
        _desktop[0x200] = new FakeWindow("Chrome_WidgetWin_0", WebViewProcess, Browser);
        _desktop[RenderWidget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, WebViewProcess, 0x200);
        var document = new FakeUiaNode("Document", "Mail");
        AddChildren(document, 3);
        _tree.WithWindow(RenderWidget, document);

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().NotBeNull();
        info!.Host.Should().Be(WebContentHost.WebView2);
        info.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        info.DocumentChildCount.Should().Be(3);
    }

    [Fact]
    public void WarmUp_FindsTheDocumentAFewPanesBelowTheRenderWidget()
    {
        // Arrange - the render widget's element is a pane; the Document is two levels below it
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);
        _desktop[RenderWidget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        var document = new FakeUiaNode("Document", "Page");
        AddChildren(document, 4);
        _tree.WithWindow(RenderWidget, new FakeUiaNode("Pane", "Chrome Legacy Window").With(
            new FakeUiaNode("Button", "Not a container"),
            new FakeUiaNode("Pane").With(document)));

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        info.DocumentChildCount.Should().Be(4);
        _tree.CachedFetches.Should().NotContain("Not a container", "only panes and groups are searched");
    }

    #endregion

    #region Waiting for the page

    [Fact]
    public void WarmUp_OfAPageThatIsAlreadyBuilt_DoesNotWait()
    {
        // Arrange
        ChromeWithPage(documentChildren: 5);

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().Be(new AccessibilityWarmupInfo(AccessibilityWarmupStatus.Ready, WebContentHost.Chromium, 0, 1, 5));
        _waits.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_OfAnEmptyPage_WaitsUntilItsElementsArriveAndStopChanging()
    {
        // Arrange - Chromium builds the tree after the first query: empty, then 3 elements, then 6, then still 6
        var document = ChromeWithPage(documentChildren: 0);
        var waits = 0;
        _afterWait = () =>
        {
            waits++;
            if (waits <= 2)
            {
                AddChildren(document, 3);
            }
        };

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().NotBeNull();
        info!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        info.DocumentChildCount.Should().Be(6);
        info.Polls.Should().Be(4);
        info.Warning.Should().BeNull();
        _waits.Should().Equal(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400));
        info.WaitedMs.Should().Be(900);
    }

    [Fact]
    public void WarmUp_OfAPageThatStaysEmpty_StopsAtTheBudget_AndWarnsNotReady()
    {
        // Arrange
        ChromeWithPage(documentChildren: 0);

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
        info.WaitedMs.Should().Be(1500);
        info.DocumentChildCount.Should().Be(0);
        _waits.Sum(w => w.TotalMilliseconds).Should().Be(1500, "the last wait is cut to the budget");
        _waits.Should().OnlyContain(w => w >= TimeSpan.Zero && w <= TimeSpan.FromMilliseconds(500));
        info.Warning.Should().Contain("no elements").And.Contain("forceAccessibility=true").And.Contain("Read again");
    }

    [Fact]
    public void WarmUp_WhenNoDocumentAppears_SaysSo()
    {
        // Arrange - the render widget's element has no Document yet
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);
        _desktop[RenderWidget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        _tree.WithWindow(RenderWidget, new FakeUiaNode("Pane", "Chrome Legacy Window"));

        // Act
        var info = CreateSut().WarmUp(Browser, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
        info.DocumentChildCount.Should().BeNull();
        info.Warning.Should().Contain("no web document was found");
    }

    [Fact]
    public void WarmUp_OfAPageStillGrowingAtTheBudget_IsPartial()
    {
        // Arrange - a new element after every wait
        var document = ChromeWithPage(documentChildren: 0);
        _afterWait = () => AddChildren(document, 1);

        // Act
        var info = CreateSut().WarmUp(Browser, TimeSpan.FromMilliseconds(600), CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.Partial);
        info.DocumentChildCount.Should().BeGreaterThan(0);
        info.Warning.Should().Contain("read it again");
    }

    [Fact]
    public void WarmUp_NeverWaitsLongerThanMaxWait_WhateverTheBudget()
    {
        // Arrange
        ChromeWithPage(documentChildren: 0);

        // Act
        var info = CreateSut().WarmUp(Browser, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Assert
        info!.WaitedMs.Should().Be((int)AccessibilityWarmup.MaxWait.TotalMilliseconds);
    }

    [Fact]
    public void WarmUp_WithAZeroBudget_ChecksOnceWithoutWaiting()
    {
        // Arrange
        ChromeWithPage(documentChildren: 0);

        // Act
        var info = CreateSut().WarmUp(Browser, TimeSpan.Zero, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
        info.Polls.Should().Be(1);
        _waits.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_WhenTheCallIsCancelled_StopsWaiting()
    {
        // Arrange - the deadline passes during the first wait
        ChromeWithPage(documentChildren: 0);
        using var deadline = new CancellationTokenSource();
        _afterWait = deadline.Cancel;

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, deadline.Token);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
        info.Polls.Should().Be(1);
        _waits.Should().ContainSingle();
    }

    [Fact]
    public void WarmUp_WhenTheProviderTimesOut_StopsWaitingWithoutThrowing()
    {
        // Arrange
        var document = ChromeWithPage(documentChildren: 0);
        document.ChildrenException = new TimeoutException("UIA_E_TIMEOUT");

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
        _waits.Should().BeEmpty("every further call would wait as long");
    }

    [Fact]
    public void WarmUp_WhenTheTreeFails_ReturnsNullInsteadOfThrowing()
    {
        // Arrange
        ChromeWithPage(documentChildren: 0);
        _windows.Setup(w => w.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>())).Throws(new InvalidOperationException("boom"));

        // Act
        var act = () => CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        act.Should().NotThrow().Which.Should().BeNull();
    }

    #endregion

    #region Remembering windows

    [Fact]
    public void WarmUp_OfAWindowWarmedUpBefore_DoesNothing()
    {
        // Arrange
        ChromeWithPage(documentChildren: 2);
        var sut = CreateSut();
        sut.WarmUp(Browser, Budget, CancellationToken.None);
        var readsBefore = _tree.Reads.Count;

        // Act
        var info = sut.WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.Reads.Count.Should().Be(readsBefore, "a remembered window is not read again");
    }

    [Fact]
    public void WarmUp_OfAWarmedWindow_WarmsUpATabThatWasShownSince()
    {
        // Arrange - the first tab was warmed up; then another tab is shown, whose page Chromium builds only now
        ChromeWithPage(documentChildren: 2);
        var sut = CreateSut();
        sut.WarmUp(Browser, Budget, CancellationToken.None)!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        _desktop[RenderWidget].Visible = false;
        _desktop[HiddenTab] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        var shownTab = new FakeUiaNode("Document", "Other page");
        _tree.WithWindow(HiddenTab, shownTab);
        _afterWait = () => AddChildren(shownTab, shownTab.Children.Count == 0 ? 3 : 0);

        // Act
        var info = sut.WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().NotBeNull("the page that is shown now was never warmed up");
        info!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        info.DocumentChildCount.Should().Be(3);
        info.Polls.Should().BeGreaterThan(1);
    }

    [Fact]
    public void WarmUp_OfAWarmedWindow_WhoseShownTabWasWarmedUp_DoesNotReadAgain()
    {
        // Arrange - the second tab was warmed up while it was shown
        ChromeWithPage(documentChildren: 2);
        var sut = CreateSut();
        sut.WarmUp(Browser, Budget, CancellationToken.None);
        _desktop[RenderWidget].Visible = false;
        _desktop[HiddenTab] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        var shownTab = new FakeUiaNode("Document", "Other page");
        AddChildren(shownTab, 4);
        _tree.WithWindow(HiddenTab, shownTab);
        sut.WarmUp(Browser, Budget, CancellationToken.None).Should().NotBeNull();
        var readsBefore = _tree.Reads.Count;

        // Act
        var info = sut.WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.Reads.Count.Should().Be(readsBefore);
    }

    [Fact]
    public void WarmUp_OfATabShownInAWarmedWindowThatStaysEmpty_IsWaitedForOnlyThreeTimes()
    {
        // Arrange - the first tab was warmed up; the tab shown now stays empty
        ChromeWithPage(documentChildren: 2);
        var sut = CreateSut();
        sut.WarmUp(Browser, Budget, CancellationToken.None);
        _desktop[RenderWidget].Visible = false;
        _desktop[HiddenTab] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        _tree.WithWindow(HiddenTab, new FakeUiaNode("Document", "Empty page"));

        // Act
        var attempts = Enumerable.Range(0, AccessibilityWarmup.MaxWaitingAttempts + 1)
            .Select(_ => sut.WarmUp(Browser, Budget, CancellationToken.None)!)
            .ToList();

        // Assert
        attempts.Should().OnlyContain(i => i.Status == AccessibilityWarmupStatus.NotReady);
        attempts.Take(AccessibilityWarmup.MaxWaitingAttempts).Should().OnlyContain(i => i.WaitedMs == 1500);
        attempts[^1].WaitedMs.Should().Be(0);
    }

    [Fact]
    public void WarmUp_OfAPageThatStayedEmpty_WaitsAgain_ButOnlyChecksAfterThreeAttempts()
    {
        // Arrange
        ChromeWithPage(documentChildren: 0);
        var sut = CreateSut();

        // Act
        var attempts = Enumerable.Range(0, AccessibilityWarmup.MaxWaitingAttempts + 1)
            .Select(_ => sut.WarmUp(Browser, Budget, CancellationToken.None)!)
            .ToList();

        // Assert
        attempts.Take(AccessibilityWarmup.MaxWaitingAttempts).Should().OnlyContain(i => i.WaitedMs == 1500);
        var last = attempts[^1];
        last.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
        last.WaitedMs.Should().Be(0);
        last.Polls.Should().Be(1);
        last.Warning.Should().Contain("did not wait");
    }

    [Fact]
    public void WarmUp_OfAHandleNowOwnedByAnotherProcess_WarmsItUpAgain()
    {
        // Arrange
        ChromeWithPage(documentChildren: 2);
        var sut = CreateSut();
        sut.WarmUp(Browser, Budget, CancellationToken.None);

        // Act - the window closed and its handle went to a window of another Chromium process
        _desktop[Browser].ProcessId = 4001;
        var info = sut.WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info.Should().NotBeNull();
    }

    [Fact]
    public void Remembering_ForgetsWindowsThatAreGone_AndKeepsAtMostTheCapacity()
    {
        // Arrange
        ChromeWithPage(documentChildren: 2);
        var sut = CreateSut();
        sut.WarmUp(Browser, Budget, CancellationToken.None);

        // Act - the first window dies, then more windows than the cache holds are warmed up
        _desktop[Browser].Alive = false;
        for (var i = 0; i < AccessibilityWarmup.CacheCapacity + 10; i++)
        {
            var window = (nint)(0x10000 + (i * 2));
            var widget = window + 1;
            _desktop[window] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);
            _desktop[widget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, window);
            var document = new FakeUiaNode("Document");
            AddChildren(document, 1);
            _tree.WithWindow(widget, document);
            sut.WarmUp(window, Budget, CancellationToken.None).Should().NotBeNull();
        }

        // Assert
        sut.RememberedCount.Should().Be(AccessibilityWarmup.CacheCapacity);
    }

    #endregion

    [Theory]
    [InlineData(10_000, 1_500)]
    [InlineData(3_000, 1_000)]
    [InlineData(1_000, 333)]
    [InlineData(0, 0)]
    public void BudgetFor_IsAThirdOfTheTimeout_AtMostMaxWait(int timeoutMs, int expectedMs)
    {
        AccessibilityWarmup.BudgetFor(TimeSpan.FromMilliseconds(timeoutMs)).TotalMilliseconds
            .Should().BeApproximately(expectedMs, 1);
    }
}
