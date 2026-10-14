namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="AccessibilityWarmup"/> (roadmap M3.3, M3.F23), over an in-memory UI Automation tree, mocked
/// window and process reads and a manual clock: no real window is read and no test waits.
/// </summary>
public class AccessibilityWarmupTests
{
    private const nint Browser = 0x100;
    private const nint RenderWidget = 0x101;
    private const nint HiddenTab = 0x102;
    private const uint BrowserProcess = 4000;
    private const uint WebViewProcess = 5000;
    private const nint App = 0x300;
    private const uint AppProcess = 6000;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    private readonly FakeUiaTree _tree = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
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

    private AccessibilityWarmup CreateSut() => new(_tree, _windows.Object, _processes.Object, _clock, (delay, token) =>
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

    [Fact]
    public void WarmUp_OfARenderWidgetWhoseElementHoldsThePageItself_TakesItAsThePage()
    {
        // Arrange - WebView2 runtime 155 on a real desktop (roadmap M3.F33): the render widget's element is the Win32 pane
        // "Chrome Legacy Window", and its children are the page's own elements, with no Document element between them
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);
        _desktop[RenderWidget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        _tree.WithWindow(RenderWidget, new FakeUiaNode("Pane", "Chrome Legacy Window") { FrameworkId = "Win32" }.With(
            new FakeUiaNode("Text", "WAM fixture web content") { FrameworkId = "Chrome" },
            new FakeUiaNode("Text", "Local HTML") { FrameworkId = "Chrome" },
            new FakeUiaNode("Button", "Web button") { FrameworkId = "Chrome" }));

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        info.DocumentChildCount.Should().Be(3);
    }

    [Fact]
    public void WarmUp_OfARenderWidgetElementThatFillsWithThePageAfterTheFirstQuery_WaitsForIt()
    {
        // Arrange - the first query turns Chromium's accessibility on; the page's elements arrive under the pane a moment later
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);
        _desktop[RenderWidget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        var pane = new FakeUiaNode("Pane", "Chrome Legacy Window") { FrameworkId = "Win32" };
        _tree.WithWindow(RenderWidget, pane);
        var waits = 0;
        _afterWait = () =>
        {
            if (++waits == 1)
            {
                pane.With(new FakeUiaNode("Text", "Heading") { FrameworkId = "Chrome" }, new FakeUiaNode("Button", "Go") { FrameworkId = "Chrome" });
            }
        };

        // Act
        var info = CreateSut().WarmUp(Browser, Budget, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        info.DocumentChildCount.Should().Be(2);
        info.Warning.Should().BeNull();
    }

    [Fact]
    public void WarmUp_OfARenderWidgetElementWhoseChildrenAreNotChromiumContent_DoesNotTakeItAsThePage()
    {
        // Arrange - a pane below the widget that holds only native controls is not a page
        _desktop[Browser] = new FakeWindow("Chrome_WidgetWin_1", BrowserProcess, 0);
        _desktop[RenderWidget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, BrowserProcess, Browser);
        _tree.WithWindow(RenderWidget, new FakeUiaNode("Pane", "Chrome Legacy Window") { FrameworkId = "Win32" }.With(
            new FakeUiaNode("Button", "Native") { FrameworkId = "Win32" }));

        // Act
        var info = CreateSut().WarmUp(Browser, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
        info.Warning.Should().Contain("no web document was found");
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

    #region Web content hosted as a visual (roadmap M3.F23)

    /// <summary>
    /// A WinUI 3 window with a WebView2 hosted as a visual: no render widget, an msedgewebview2.exe child of the app's
    /// process, and the page's Document (frameworkId Chrome) a few panes below the window's element.
    /// </summary>
    private FakeUiaNode AppWithVisualWebView(int documentChildren)
    {
        _desktop[App] = new FakeWindow("WinUIDesktopWin32WindowClass", AppProcess, 0);
        _desktop[0x301] = new FakeWindow("Microsoft.UI.Content.DesktopChildSiteBridge", AppProcess, App);
        HostsWebView2(AppProcess);
        var document = new FakeUiaNode("Document", "Page") { FrameworkId = "Chrome" };
        AddChildren(document, documentChildren);
        _tree.WithWindow(App, new FakeUiaNode("Window", "App") { FrameworkId = "Win32" }.With(
            new FakeUiaNode("Pane", "Bridge") { FrameworkId = "XAML" }.With(
                new FakeUiaNode("Button", "Not a container") { FrameworkId = "XAML" },
                new FakeUiaNode("Pane", "WebView2") { FrameworkId = "XAML" }.With(document))));
        return document;
    }

    /// <summary>The process list: an msedgewebview2.exe browser process that <paramref name="host"/> started, and its renderer.</summary>
    private void HostsWebView2(uint host) =>
        _processes.Setup(p => p.GetProcessList()).Returns(new Dictionary<int, (int ParentProcessId, string ImageName)>
        {
            [(int)host] = (1, "App.exe"),
            [(int)WebViewProcess] = ((int)host, "msedgewebview2.exe"),
            [(int)WebViewProcess + 1] = ((int)WebViewProcess, "msedgewebview2.exe")
        });

    [Fact]
    public void WarmUp_OfAWebView2HostedAsAVisual_FindsItsDocument_AndReportsWebView2()
    {
        // Arrange
        AppWithVisualWebView(documentChildren: 3);

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info.Should().Be(new AccessibilityWarmupInfo(AccessibilityWarmupStatus.Ready, WebContentHost.WebView2, 0, 1, 3));
        _tree.CachedFetches.Should().NotContain("Not a container", "only panes and groups are searched");
        _waits.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_OfAnEmptyVisualWebView2_WaitsUntilItsElementsArriveAndStopChanging()
    {
        // Arrange - the page is read cold: empty, then 4 elements, then still 4
        var document = AppWithVisualWebView(documentChildren: 0);
        _afterWait = () => AddChildren(document, document.Children.Count == 0 ? 4 : 0);

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        info.Host.Should().Be(WebContentHost.WebView2);
        info.DocumentChildCount.Should().Be(4);
        info.Polls.Should().Be(3);
        _waits.Should().Equal(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void WarmUp_OfAVisualWebView2ThatStaysEmpty_StopsAtTheBudget_AndWarnsNotReady()
    {
        // Arrange
        AppWithVisualWebView(documentChildren: 0);

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.NotReady);
        info.Host.Should().Be(WebContentHost.WebView2);
        info.WaitedMs.Should().Be(1500);
        info.DocumentChildCount.Should().Be(0);
        info.Warning.Should().Contain("no elements").And.Contain("use a screenshot").And.NotContain("forceAccessibility");
    }

    [Fact]
    public void WarmUp_OfAVisualWebView2ThatStayedEmpty_OnlyChecksAfterThreeAttempts()
    {
        // Arrange
        AppWithVisualWebView(documentChildren: 0);
        var sut = CreateSut();

        // Act
        var attempts = Enumerable.Range(0, AccessibilityWarmup.MaxWaitingAttempts + 1)
            .Select(_ => sut.WarmUp(App, Budget, CancellationToken.None)!)
            .ToList();

        // Assert
        attempts.Should().OnlyContain(i => i.Status == AccessibilityWarmupStatus.NotReady);
        attempts.Take(AccessibilityWarmup.MaxWaitingAttempts).Should().OnlyContain(i => i.WaitedMs == 1500);
        attempts[^1].WaitedMs.Should().Be(0);
        attempts[^1].Warning.Should().Contain("did not wait");
    }

    [Fact]
    public void WarmUp_OfAVisualWebView2WarmedUpBefore_DoesNotReadAgain()
    {
        // Arrange
        AppWithVisualWebView(documentChildren: 2);
        var sut = CreateSut();
        sut.WarmUp(App, Budget, CancellationToken.None)!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        var fetchesBefore = _tree.CachedFetches.Count;
        var readsBefore = _tree.Reads.Count;

        // Act
        var info = sut.WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.CachedFetches.Count.Should().Be(fetchesBefore, "a remembered window is not searched again");
        _tree.Reads.Count.Should().Be(readsBefore);
        _processes.Verify(p => p.GetProcessList(), Times.Once(), "nor is the process list read again");
    }

    [Fact]
    public void WarmUp_OfAWindowWhoseProcessStartedNoWebView2_MakesNoUiAutomationCall()
    {
        // Arrange - the same tree, but the msedgewebview2.exe processes belong to another app
        AppWithVisualWebView(documentChildren: 3);
        HostsWebView2(AppProcess + 1);

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.Reads.Should().BeEmpty("a window without web content costs a process list, not a UI Automation call");
        _tree.CachedFetches.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_WhenTheProcessListCannotBeRead_DoesNothing()
    {
        // Arrange
        AppWithVisualWebView(documentChildren: 3);
        _processes.Setup(p => p.GetProcessList()).Returns((IReadOnlyDictionary<int, (int ParentProcessId, string ImageName)>?)null);

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.CachedFetches.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_OfAWindowOfAWebView2HostWithoutAChromeDocument_DoesNothing()
    {
        // Arrange - a dialog of an app that shows WebView2 elsewhere: its only Document is a WPF rich text box
        _desktop[App] = new FakeWindow("HwndWrapper[App;;1]", AppProcess, 0);
        HostsWebView2(AppProcess);
        _tree.WithWindow(App, new FakeUiaNode("Window", "Settings") { FrameworkId = "WPF" }.With(
            new FakeUiaNode("Document", "Notes") { FrameworkId = "WPF" }.With(new FakeUiaNode("Text", "line"))));

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull("only a Document of Chromium (frameworkId Chrome) is web content");
        _waits.Should().BeEmpty();
    }

    [Fact]
    public void WarmUp_LooksForAVisualDocumentOnlyAFewLevelsDeep()
    {
        // Arrange - the Document lies one level below the deepest level searched
        _desktop[App] = new FakeWindow("WinUIDesktopWin32WindowClass", AppProcess, 0);
        HostsWebView2(AppProcess);
        var root = new FakeUiaNode("Window", "App");
        var level = root;
        for (var depth = 1; depth < AccessibilityWarmup.VisualDocumentSearchDepth; depth++)
        {
            var pane = new FakeUiaNode("Pane", $"Pane {depth}");
            level.With(pane);
            level = pane;
        }

        var document = new FakeUiaNode("Document", "Page") { FrameworkId = "Chrome" };
        AddChildren(document, 2);
        level.With(new FakeUiaNode("Pane", "Too deep").With(document));
        _tree.WithWindow(App, root);

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.CachedFetches.Should().HaveCount(AccessibilityWarmup.VisualDocumentSearchDepth).And.NotContain("Too deep");
    }

    [Fact]
    public void WarmUp_ListsAtMostTheBoundOfLevels_WhenLookingForAVisualDocument()
    {
        // Arrange - a wide window of panes, none with web content
        _desktop[App] = new FakeWindow("WinUIDesktopWin32WindowClass", AppProcess, 0);
        HostsWebView2(AppProcess);
        var root = new FakeUiaNode("Window", "App");
        for (var i = 0; i < 40; i++)
        {
            root.With(new FakeUiaNode("Pane", $"Pane {i}").With(new FakeUiaNode("Group", $"Group {i}")));
        }

        _tree.WithWindow(App, root);

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info.Should().BeNull();
        _tree.CachedFetches.Should().HaveCount(AccessibilityWarmup.VisualDocumentSearchListings);
    }

    [Fact]
    public void WarmUp_OfAWindowWithARenderWidget_DoesNotLookForAVisualDocument()
    {
        // Arrange - a WebView2 pane hosted in a window (HWND hosting): the render widget path, as before
        _desktop[App] = new FakeWindow("HwndWrapper[App;;1]", AppProcess, 0);
        _desktop[RenderWidget] = new FakeWindow(AccessibilityWarmup.RenderWidgetHostClass, WebViewProcess, App);
        HostsWebView2(AppProcess);
        var document = new FakeUiaNode("Document", "Mail");
        AddChildren(document, 2);
        _tree.WithWindow(RenderWidget, document);

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info!.Host.Should().Be(WebContentHost.WebView2);
        _processes.Verify(p => p.GetProcessList(), Times.Never());
    }

    [Fact]
    public void WarmUp_OfAVisualWebView2WhoseDocumentWasReplaced_FindsTheNewOne()
    {
        // Arrange - the page navigates during the first wait: the old Document is gone, and a new one has elements
        var document = AppWithVisualWebView(documentChildren: 0);
        var webView = document.Parent!;
        _afterWait = () =>
        {
            if (document.ChildrenException == null)
            {
                document.Vanish();
                document.ChildrenException = new UiaElementUnavailableException("gone");
                var next = new FakeUiaNode("Document", "Next") { FrameworkId = "Chrome" };
                AddChildren(next, 2);
                webView.With(next);
            }
        };

        // Act
        var info = CreateSut().WarmUp(App, Budget, CancellationToken.None);

        // Assert
        info!.Status.Should().Be(AccessibilityWarmupStatus.Ready);
        info.DocumentChildCount.Should().Be(2);
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
