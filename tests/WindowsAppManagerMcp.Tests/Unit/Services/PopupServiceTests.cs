namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The popup enumeration (roadmap M3.5) over a mocked desktop: which top-level windows are popups of a window (owned, same
/// process, child process), their kinds and order, the foreground rule of the default scope, the desktop scope's windows
/// with the target policy and the host guard, get_active_popups' refs, and the wait for a new popup. No real window is read.
/// </summary>
public class PopupServiceTests
{
    // Win32 values (NativeEnums is internal to the server assembly)
    private const long WsCaption = 0x00C00000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExAppWindow = 0x00040000;
    private const uint GwEnabledPopup = 6;

    private readonly FakeWindowDesktop _desktop = new();
    private readonly Mock<INativeProcessWrapper> _processes = new();
    private readonly Mock<ITargetPolicy> _policy = new();
    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();

    public PopupServiceTests()
    {
        _policy.Setup(p => p.Evaluate(It.IsAny<IReadOnlyCollection<string?>>())).Returns(TargetPolicyVerdict.Allow);
        _desktop.Native.Setup(n => n.RespondsWithin(It.IsAny<nint>(), It.IsAny<uint>())).Returns(true);
    }

    private ElementRegistry Registry { get; set; } = null!;

    private PopupService CreateSut()
    {
        Registry = new ElementRegistry(_tree, _worker, _desktop.Native.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        return new PopupService(_desktop.Native.Object, _processes.Object, _desktop.HostGuard.Object, _policy.Object, _tree, _worker, Registry);
    }

    /// <summary>An app's main window: a title bar, no owner.</summary>
    private nint AddMain(string title, string processName = "app", uint? processId = null, string className = "AppWindow") =>
        _desktop.Add(title, className, processId, processName, style: WsCaption);

    private nint AddPopup(string title, string className, string processName = "app", uint? processId = null, long style = 0,
        long extendedStyle = 0, nint owner = 0, bool visible = true, bool cloaked = false)
    {
        var hWnd = _desktop.Add(title, className, processId, processName, visible, cloaked, extendedStyle, style);
        if (owner != 0)
        {
            Own(hWnd, owner);
        }

        return hWnd;
    }

    private void Own(nint window, nint owner) =>
        _desktop.Native.Setup(n => n.GetWindow(window, FakeWindowDesktop.GwOwner)).Returns(owner);

    #region Which windows

    [Fact]
    public void GetPopups_ListsTheMenusDropdownsAndOwnedDialogsOfTheWindow_FrontFirst()
    {
        // Arrange - z-order: the menu in front, then the dropdown, the owned dialog, the window, another app
        var menu = AddPopup("", PopupService.MenuClass);
        var dropdown = AddPopup("", "ComboLBox");
        var main = AddMain("Editor");
        var dialog = AddPopup("Save As", "#32770", style: WsCaption);
        Own(dialog, main);
        AddMain("Other app", processName: "other");

        // Act
        var popups = CreateSut().GetPopups(main);

        // Assert
        popups.Select(p => (p.Handle, p.Kind, p.Relation)).Should().Equal(
            (menu, PopupKind.Menu, PopupRelation.SameProcess),
            (dropdown, PopupKind.Dropdown, PopupRelation.SameProcess),
            (dialog, PopupKind.Dialog, PopupRelation.Owned));
        popups[0].Title.Should().BeNull("a menu has no window text");
        popups[2].Title.Should().Be("Save As");
        popups[2].OwnerHandle.Should().Be(main);
        popups[2].Bounds.Should().Be(new BoundsDto(0, 0, 800, 600));
    }

    [Fact]
    public void GetPopups_LeavesOutAnotherMainWindowOfTheSameProcess_AndAnUnownedDialogBoxNobodyCanAttribute()
    {
        // Arrange - one process shows two main windows (a second browser or Explorer window), a tool palette and an unowned
        // message box, which could be either window's
        var main = AddMain("Window A", processId: 500);
        AddMain("Window B", processId: 500);
        var palette = AddPopup("Colors", "Palette", processId: 500, style: WsCaption, extendedStyle: WsExToolWindow);
        AddPopup("Error", "#32770", processId: 500, style: WsCaption);
        AddPopup("Forced into the taskbar", "Splash", processId: 500, extendedStyle: WsExAppWindow);

        // Act
        var popups = CreateSut().GetPopups(main);

        // Assert
        popups.Select(p => (p.Handle, p.Kind)).Should().Equal((palette, PopupKind.Window));
    }

    [Fact]
    public void GetPopups_KeepsAnUnownedDialogBoxOfTheProcess_WhenTheWindowIsItsOnlyMainWindow()
    {
        var main = AddMain("Editor", processId: 500);
        var messageBox = AddPopup("Error", "#32770", processId: 500, style: WsCaption);
        AddMain("Other app", processName: "other");

        var popups = CreateSut().GetPopups(main);

        popups.Should().ContainSingle().Which.Should().Match<PopupWindow>(p =>
            p.Handle == messageBox && p.Kind == PopupKind.Dialog && p.Relation == PopupRelation.SameProcess);
    }

    [Fact]
    public void GetPopups_LeavesOutTheDialogsAndPopupsAnotherMainWindowOfTheSameProcessOwns()
    {
        // Arrange - Word document B's dialog and its tooltip, and the Properties dialog of a dialog B owns, are B's: a search
        // of document A must not find their buttons
        var documentA = AddMain("Report.docx", processId: 500);
        var documentB = AddMain("Letter.docx", processId: 500);
        var dialogOfB = AddPopup("Font", "#32770", processId: 500, style: WsCaption, owner: documentB);
        AddPopup("", "tooltips_class32", processId: 500, owner: documentB);
        AddPopup("Properties", "#32770", processId: 500, style: WsCaption, owner: dialogOfB);
        var dialogOfA = AddPopup("Paragraph", "#32770", processId: 500, style: WsCaption, owner: documentA);

        // Act
        var popups = CreateSut().GetPopups(documentA);

        // Assert
        popups.Should().ContainSingle().Which.Should().Match<PopupWindow>(p => p.Handle == dialogOfA && p.Relation == PopupRelation.Owned);
    }

    [Fact]
    public void GetPopups_KeepsAWindowOfTheProcessOwnedByAHiddenOrPopupStyledWindow()
    {
        // Arrange - a WinForms dialog owned by the hidden parking window, and a window owned by the app's tool palette
        var main = AddMain("Editor", processId: 500);
        AddMain("Editor 2", processId: 500);
        var parking = AddPopup("", "WindowsForms10.Window.8.app", processId: 500, visible: false);
        var options = AddPopup("Options", "WindowsForms10.Window.8.app", processId: 500, style: WsCaption, owner: parking);
        var palette = AddPopup("Colors", "Palette", processId: 500, style: WsCaption, extendedStyle: WsExToolWindow);
        var picker = AddPopup("Pick a color", "Picker", processId: 500, style: WsCaption, owner: palette);

        // Act
        var popups = CreateSut().GetPopups(main);

        // Assert
        popups.Select(p => (p.Handle, p.Relation)).Should().Equal(
            (options, PopupRelation.SameProcess), (palette, PopupRelation.SameProcess), (picker, PopupRelation.SameProcess));
    }

    [Fact]
    public void GetPopups_KeepsAnOwnedWindowOfAnotherProcess_AndOneOwnedThroughAnotherOwnedWindow()
    {
        var main = AddMain("Editor");
        var dialog = AddPopup("Options", "WindowsForms10.Window", processName: "helper", style: WsCaption, owner: main);
        var nested = AddPopup("", "Popup", processName: "helper", owner: dialog);

        var popups = CreateSut().GetPopups(main);

        popups.Select(p => (p.Handle, p.Relation)).Should().BeEquivalentTo(new[]
        {
            (dialog, PopupRelation.Owned), (nested, PopupRelation.Owned)
        });
        popups.Single(p => p.Handle == dialog).Kind.Should().Be(PopupKind.Dialog, "an owned window with a title bar");
    }

    [Fact]
    public void GetPopups_LeavesOutHiddenCloakedEmptyAndHelperWindows_AndTheDesktop()
    {
        // Arrange
        var main = AddMain("Editor");
        AddPopup("", PopupService.MenuClass, visible: false);
        AddPopup("", PopupService.MenuClass, cloaked: true);
        var empty = AddPopup("", PopupService.MenuClass);
        _desktop.Native.Setup(n => n.GetWindowRect(empty)).Returns((10, 10, 10, 10));
        AddPopup("Default IME", "IME");
        AddPopup("", "MSCTFIME UI");
        var progman = AddPopup("Program Manager", "Progman", processName: "explorer");
        _desktop.Native.Setup(n => n.GetShellWindow()).Returns(progman);

        // Act + Assert
        CreateSut().GetPopups(main).Should().BeEmpty();
    }

    [Fact]
    public void GetPopups_MarksTheModalDialogOfADisabledWindowAsBlocking()
    {
        // Arrange
        var main = _desktop.Add("Editor", style: WsCaption | FakeWindowDesktop.WsDisabled);
        var prompt = AddPopup("Save changes?", "#32770", style: WsCaption, owner: main);
        var tooltip = AddPopup("", "tooltips_class32", owner: main);
        _desktop.Native.Setup(n => n.GetWindow(main, GwEnabledPopup)).Returns(prompt);

        // Act
        var popups = CreateSut().GetPopups(main);

        // Assert
        popups.Single(p => p.Handle == prompt).IsBlocking.Should().BeTrue();
        popups.Single(p => p.Handle == tooltip).Should().Match<PopupWindow>(p => !p.IsBlocking && p.Kind == PopupKind.Tooltip);
    }

    [Fact]
    public void GetPopups_ListsAtMostMaxPopups()
    {
        var main = AddMain("Editor");
        for (var i = 0; i < PopupService.MaxPopups + 3; i++)
        {
            AddPopup("", "Popup");
        }

        CreateSut().GetPopups(main).Should().HaveCount(PopupService.MaxPopups);
        CreateSut().GetPopupList(main).Should().Match<PopupList>(l => l.Popups.Count == PopupService.MaxPopups && l.Truncated);
    }

    [Fact]
    public void GetPopups_OfANonWindow_IsEmpty()
    {
        CreateSut().GetPopups(0x9999).Should().BeEmpty();
        CreateSut().GetPopups(0).Should().BeEmpty();
    }

    #endregion

    #region Child processes (WebView2)

    private nint ArrangeWebViewPopup(DateTime? hostStart, DateTime? childStart, out nint host)
    {
        host = AddMain("Outlook", processName: "olk", processId: 700);
        var popup = AddPopup("", "Chrome_WidgetWin_1", processName: "msedgewebview2", processId: 710);
        _processes.Setup(p => p.GetProcessList()).Returns(new Dictionary<int, (int, string)>
        {
            [700] = (4, "olk.exe"),
            [710] = (700, "msedgewebview2.exe"),
            [720] = (710, "msedgewebview2.exe")
        });
        _processes.Setup(p => p.GetProcessStartTime(700)).Returns(hostStart);
        _processes.Setup(p => p.GetProcessStartTime(710)).Returns(childStart);
        return popup;
    }

    [Fact]
    public void GetPopups_KeepsThePopupOfADirectChildProcess()
    {
        var popup = ArrangeWebViewPopup(new DateTime(2026, 1, 1, 9, 0, 0), new DateTime(2026, 1, 1, 9, 0, 1), out var host);

        var popups = CreateSut().GetPopups(host);

        popups.Should().ContainSingle().Which.Should().Match<PopupWindow>(p =>
            p.Handle == popup && p.Relation == PopupRelation.ChildProcess && p.ProcessId == 710);
    }

    [Fact]
    public void GetPopups_LeavesOutAChildThatStartedBeforeItsParent_ItsParentIdWasReused()
    {
        ArrangeWebViewPopup(new DateTime(2026, 1, 1, 9, 0, 0), new DateTime(2026, 1, 1, 8, 0, 0), out var host);

        CreateSut().GetPopups(host).Should().BeEmpty();
    }

    [Fact]
    public void GetPopups_LeavesOutAMainWindowOfAChildProcess_AndAGrandchildsPopup()
    {
        var host = AddMain("Launcher", processName: "launcher", processId: 800);
        AddMain("Started app", processName: "child", processId: 810);
        AddPopup("", "Popup", processName: "grandchild", processId: 820);
        _processes.Setup(p => p.GetProcessList()).Returns(new Dictionary<int, (int, string)>
        {
            [800] = (4, "launcher.exe"), [810] = (800, "child.exe"), [820] = (810, "grandchild.exe")
        });

        CreateSut().GetPopups(host).Should().BeEmpty();
    }

    [Fact]
    public void GetPopups_NeverCountsTheChildrenOfTheDesktopShell()
    {
        // Arrange - explorer.exe starts the programs the user opens: their popups are not File Explorer's
        var explorer = AddMain("Documents", processName: "explorer", processId: 900, className: "CabinetWClass");
        AddPopup("", "Popup", processName: "app", processId: 910);
        _processes.Setup(p => p.GetProcessList()).Returns(new Dictionary<int, (int, string)>
        {
            [900] = (4, "explorer.exe"), [910] = (900, "app.exe")
        });

        // Act + Assert
        CreateSut().GetPopups(explorer).Should().BeEmpty();
        _processes.Verify(p => p.GetProcessList(), Times.Never);
    }

    [Fact]
    public void GetPopups_TakesNoProcessSnapshot_WhenNoWindowOfAnotherProcessLooksLikeAPopup()
    {
        var main = AddMain("Editor");
        AddPopup("", PopupService.MenuClass);
        AddMain("Other", processName: "other");

        CreateSut().GetPopups(main);

        _processes.Verify(p => p.GetProcessList(), Times.Never);
    }

    #endregion

    #region UWP

    [Fact]
    public void GetPopups_OfAUwpFrame_CountsTheAppsProcess_NotTheFrameHostsOtherWindows()
    {
        // Arrange - the frame belongs to ApplicationFrameHost, its CoreWindow child to the app
        var frame = _desktop.Add("Calculator", "ApplicationFrameWindow", processId: 1000, processName: "ApplicationFrameHost", style: WsCaption);
        var core = new nint(0x7777);
        _desktop.Native.Setup(n => n.EnumChildWindows(frame, It.IsAny<Func<nint, bool>>()))
            .Callback<nint, Func<nint, bool>>((_, callback) => callback(core));
        _desktop.Native.Setup(n => n.GetClassName(core)).Returns("Windows.UI.Core.CoreWindow");
        _desktop.Native.Setup(n => n.GetWindowThreadProcessId(core)).Returns(1010u);
        var flyout = AddPopup("", "Xaml_WindowedPopupClass", processName: "CalculatorApp", processId: 1010);
        AddPopup("", "Popup", processName: "ApplicationFrameHost", processId: 1000);

        // Act
        var popups = CreateSut().GetPopups(frame);

        // Assert
        popups.Should().ContainSingle().Which.Should().Match<PopupWindow>(p => p.Handle == flyout && p.Kind == PopupKind.Flyout);
    }

    #endregion

    #region Kinds

    [Theory]
    [InlineData("#32768", 0L, 0L, false, PopupKind.Menu)]
    [InlineData("ComboLBox", 0L, 0L, false, PopupKind.Dropdown)]
    [InlineData("tooltips_class32", 0L, 0L, false, PopupKind.Tooltip)]
    [InlineData("Xaml_WindowedPopupClass", 0L, 0L, false, PopupKind.Flyout)]
    [InlineData("Microsoft.UI.Content.PopupWindowSiteBridge", 0L, 0L, false, PopupKind.Flyout)]
    [InlineData("#32770", WsCaption, 0L, true, PopupKind.Dialog)]
    [InlineData("HwndWrapper[App;;1]", WsCaption, 0L, true, PopupKind.Dialog)]
    [InlineData("HwndWrapper[App;;1]", WsCaption, WsExToolWindow, true, PopupKind.Window)]
    [InlineData("Palette", WsCaption, 0L, false, PopupKind.Window)]
    [InlineData("Chrome_WidgetWin_1", 0L, 0L, true, PopupKind.Popup)]
    public void Classify_NamesTheKindFromTheClassAndStyle(string className, long style, long exStyle, bool owned, PopupKind expected)
    {
        PopupService.Classify(className, style, exStyle, owned).Should().Be(expected);
    }

    [Theory]
    [InlineData(PopupKind.Popup, "Menu", PopupKind.Menu)]
    [InlineData(PopupKind.Popup, "List", PopupKind.Dropdown)]
    [InlineData(PopupKind.Popup, "ToolTip", PopupKind.Tooltip)]
    [InlineData(PopupKind.Popup, "Pane", PopupKind.Popup)]
    [InlineData(PopupKind.Dialog, "Menu", PopupKind.Dialog)]
    public void Sharpen_UsesTheUiAutomationControlType_OnlyForAPlainPopup(PopupKind kind, string controlType, PopupKind expected)
    {
        PopupService.Sharpen(kind, controlType).Should().Be(expected);
    }

    #endregion

    #region Foreground (the default scope)

    [Fact]
    public void IsInForeground_WhenTheWindowItsDialogOrAnotherWindowOfItsProcessIsInFront()
    {
        // Arrange
        var main = AddMain("Editor", processId: 500);
        var dialog = AddPopup("Save As", "#32770", processId: 500, style: WsCaption, owner: main);
        var second = AddMain("Editor 2", processId: 500);
        var other = AddMain("Other", processName: "other");
        var sut = CreateSut();

        // Act + Assert
        _desktop.SetForeground(main);
        sut.IsInForeground(main).Should().BeTrue();
        _desktop.SetForeground(dialog);
        sut.IsInForeground(main).Should().BeTrue();
        _desktop.SetForeground(second);
        sut.IsInForeground(main).Should().BeTrue();
        _desktop.SetForeground(other);
        sut.IsInForeground(main).Should().BeFalse();
        _desktop.SetForeground(0);
        sut.IsInForeground(main).Should().BeFalse();
    }

    #endregion

    #region Desktop scope

    [Fact]
    public void GetDesktopWindows_ListsTheVisibleWindowsFrontFirst_LeavingOutDeniedAndHostWindows()
    {
        // Arrange
        var terminal = AddMain("Terminal", processName: "WindowsTerminal");
        var notepad = AddMain("Notepad", processName: "notepad");
        AddMain("Bank", processName: "bankapp");
        var menu = AddPopup("", PopupService.MenuClass, processName: "notepad");
        AddMain("Hidden", processName: "hidden").Also(h => _desktop.SetVisible(h, false));
        _desktop.Add("Server window", processId: FakeWindowDesktop.ServerProcessId, style: WsCaption);
        _desktop.HostGuard.Setup(g => g.IsHostWindow(terminal)).Returns(true);
        _policy.Setup(p => p.Evaluate(It.Is<IReadOnlyCollection<string?>>(names => names.Contains("bankapp"))))
            .Returns(TargetPolicyVerdict.Deny("bankapp", "denied"));

        // Act
        var set = CreateSut().GetDesktopWindows(0, PopupService.DesktopWindowLimit);

        // Assert
        set.Windows.Should().Equal(notepad, menu);
        set.Excluded.Should().Be(2);
        set.OverLimit.Should().Be(0);
    }

    [Fact]
    public void GetDesktopWindows_PutsTheNamedWindowFirst_EvenAHostOrDeniedOne_AndCountsTheWindowsOverTheLimit()
    {
        // Arrange
        var first = AddMain("A", processName: "a");
        var second = AddMain("B", processName: "b");
        AddMain("C", processName: "c");
        var named = AddMain("Named", processName: "denied");
        _policy.Setup(p => p.Evaluate(It.Is<IReadOnlyCollection<string?>>(names => names.Contains("denied"))))
            .Returns(TargetPolicyVerdict.Deny("denied", "denied"));
        _desktop.HostGuard.Setup(g => g.IsHostWindow(named)).Returns(true);

        // Act
        var set = CreateSut().GetDesktopWindows(named, limit: 3);

        // Assert
        set.Windows.Should().Equal(named, first, second);
        set.OverLimit.Should().Be(1);
        set.Excluded.Should().Be(0);
    }

    [Fact]
    public void GetDesktopWindows_LeavesOutAWindowWhoseProcessCannotBeNamed_WhileThePolicyDeniesAnything()
    {
        // Arrange - the process exited, or cannot be read: it cannot be checked against the deny list
        AddMain("A", processName: "a", processId: 777);
        _desktop.Native.Setup(n => n.GetProcessName(777u)).Returns((string?)null);

        // Act
        var set = CreateSut().GetDesktopWindows(0, 10);

        // Assert
        set.Windows.Should().BeEmpty();
        set.Excluded.Should().Be(1);
    }

    [Fact]
    public void GetDesktopWindows_LeavesOutAWindowTheHostGuardCannotJudge()
    {
        var window = AddMain("A");
        _desktop.HostGuard.Setup(g => g.IsHostWindow(window)).Throws(new InvalidOperationException("no snapshot"));

        var set = CreateSut().GetDesktopWindows(0, 10);

        set.Windows.Should().BeEmpty();
        set.Excluded.Should().Be(1);
    }

    #endregion

    #region get_active_popups

    [Fact]
    public async Task GetActivePopups_RegistersEachPopupsElementUnderItsOwnWindow_AndSharpensItsKind()
    {
        // Arrange - a Chromium-style menu (no menu class: its element is a Menu) and a dialog
        var menu = AddPopup("", "Chrome_WidgetWin_1");
        var main = AddMain("Editor");
        var dialog = AddPopup("Save As", "#32770", style: WsCaption, owner: main);
        _tree.WithWindow(menu, new FakeUiaNode("Menu", "Context"));
        _tree.WithWindow(dialog, new FakeUiaNode("Window", "Save As"));
        _desktop.SetForeground(main);
        var sut = CreateSut();

        // Act
        var result = await sut.GetActivePopupsAsync(main, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.WindowHandle.Should().Be((long)main);
        result.WindowTitle.Should().Be("Editor");
        result.IsForeground.Should().BeTrue();
        result.Popups!.Select(p => (p.Handle, p.Kind, p.ControlType, p.ProcessName)).Should().Equal(
            ((long)menu, PopupKind.Menu, "Menu", "app"),
            ((long)dialog, PopupKind.Dialog, "Window", "app"));
        result.Popups!.Select(p => p.Ref).Should().OnlyContain(r => r != null);
        Registry.Lookup(result.Popups![0].Ref!).WindowHandle.Should().Be(menu, "a popup's refs are registered under its own window");
        Registry.Lookup(result.Popups![1].Ref!).WindowHandle.Should().Be(dialog);
        _worker.LastTargetProcessId.Should().Be(100);
    }

    [Fact]
    public async Task GetActivePopups_WithoutAWindow_ListsThePopupsOfTheAppThatOwnsTheWindowInFront()
    {
        // Arrange - the dialog is in front: the app is its owner
        var main = AddMain("Editor");
        var dialog = AddPopup("Save As", "#32770", style: WsCaption, owner: main);
        _desktop.SetForeground(dialog);

        // Act
        var result = await CreateSut().GetActivePopupsAsync(0, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert
        result.WindowHandle.Should().Be((long)main);
        result.Popups!.Should().ContainSingle().Which.Handle.Should().Be((long)dialog);
        result.Popups![0].Ref.Should().BeNull("UI Automation has no element for it in this fake");
    }

    [Fact]
    public async Task GetActivePopups_ListsAPopupThatDoesNotRespond_WithoutReadingIt()
    {
        var main = AddMain("Editor");
        var menu = AddPopup("", PopupService.MenuClass, owner: main);
        _desktop.SetHung(menu, true);
        _tree.WithWindow(menu, new FakeUiaNode("Menu", "Context"));

        var result = await CreateSut().GetActivePopupsAsync(main, TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Popups!.Should().ContainSingle().Which.Should().Match<ActivePopup>(p => p.Ref == null && p.Kind == PopupKind.Menu);
        _tree.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActivePopups_OfANonWindow_FailsWithInvalidHandle_AndWithNothingInFront_WithWindowNotFound()
    {
        var sut = CreateSut();

        (await sut.GetActivePopupsAsync(0x9999, TimeSpan.FromSeconds(1), CancellationToken.None)).ErrorCode
            .Should().Be(UIElementErrorCode.InvalidHandle);
        (await sut.GetActivePopupsAsync(0, TimeSpan.FromSeconds(1), CancellationToken.None)).ErrorCode
            .Should().Be(UIElementErrorCode.WindowNotFound);
        _worker.Calls.Should().Be(0);
    }

    private void Deny(string processName) =>
        _policy.Setup(p => p.Evaluate(It.Is<IReadOnlyCollection<string?>>(names => names.Contains(processName))))
            .Returns(TargetPolicyVerdict.Deny(processName, $"'{processName}' is on the server's default deny list"));

    [Fact]
    public void GetPopupList_MarksThePopupsOfTheAgentsHostAndOfADeniedApp()
    {
        // Arrange
        var main = AddMain("Inbox - Chrome", processName: "chrome");
        var prompt = AddPopup("Windows Security", "Credential Dialog Xaml Host", processName: "CredentialUIBroker", style: WsCaption,
            owner: main);
        var menu = AddPopup("", PopupService.MenuClass, processName: "chrome");
        var hostTip = AddPopup("", "tooltips_class32", processName: "WindowsTerminal", owner: main);
        _desktop.HostGuard.Setup(g => g.IsHostWindow(hostTip)).Returns(true);
        Deny("CredentialUIBroker");

        // Act
        var list = CreateSut().GetPopupList(main);

        // Assert - the dialog tools' GetPopups keeps them unmarked; the reads leave the marked ones out
        list.Popups.Select(p => (p.Handle, p.IsAgentHost, p.IsDeniedByPolicy)).Should().BeEquivalentTo(
            [(prompt, false, true), (menu, false, false), (hostTip, true, false)]);
        list.ForReading().Should().Match<(IReadOnlyList<PopupWindow> Popups, int Excluded)>(r =>
            r.Popups.Count == 1 && r.Popups[0].Handle == menu && r.Excluded == 2);
        list.ForReading(includeHost: true).Excluded.Should().Be(1);
        CreateSut().GetPopups(main).Should().HaveCount(3).And.OnlyContain(p => !p.IsAgentHost && !p.IsDeniedByPolicy);
    }

    [Fact]
    public async Task GetActivePopups_LeavesOutAPopupOfADeniedApp_WithoutReadingIt_AndCountsIt()
    {
        // Arrange - Chrome, named, owns a credential prompt of another process that the policy denies
        var main = AddMain("Inbox - Chrome", processName: "chrome");
        var prompt = AddPopup("Windows Security", "Credential Dialog Xaml Host", processName: "CredentialUIBroker", style: WsCaption,
            owner: main);
        var menu = AddPopup("", PopupService.MenuClass, processName: "chrome");
        _tree.WithWindow(prompt, new FakeUiaNode("Window", "Windows Security"));
        _tree.WithWindow(menu, new FakeUiaNode("Menu", "Context"));
        Deny("CredentialUIBroker");
        var sut = CreateSut();

        // Act
        var result = await sut.GetActivePopupsAsync(main, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert - a popup is never named: as a desktop search, the call leaves it out and counts it
        result.Success.Should().BeTrue();
        result.Popups!.Should().ContainSingle().Which.Handle.Should().Be((long)menu);
        result.PopupsExcluded.Should().Be(1);
        _tree.Reads.Should().NotContain(r => r.Contains("Windows Security"));
        Registry.Count.Should().Be(1, "only the menu's element was registered");
    }

    [Fact]
    public async Task GetActivePopups_WithoutAWindow_RefusesADeniedAppInFront_WithNothingOfItListedOrRead()
    {
        // Arrange - a dialog of a password manager is in front
        var main = AddMain("Vault", processName: "KeePass");
        var dialog = AddPopup("Edit Entry", "#32770", processName: "KeePass", style: WsCaption, owner: main);
        _tree.WithWindow(dialog, new FakeUiaNode("Window", "Edit Entry"));
        _desktop.SetForeground(dialog);
        Deny("KeePass");

        // Act
        var result = await CreateSut().GetActivePopupsAsync(0, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert - get_desktop_state's rule for the window in front: named or not, a denied app is not read
        AssertRefused(result, UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("KeePass").And.NotContain("Vault").And.NotContain("Edit Entry");
    }

    [Fact]
    public async Task GetActivePopups_WithoutAWindow_RefusesADeniedPromptInFront_ThatAnAllowedAppOwns()
    {
        // Arrange - Chrome owns a credential prompt of another process, and the prompt is in front
        var main = AddMain("Inbox - Chrome", processName: "chrome");
        var prompt = AddPopup("Windows Security", "Credential Dialog Xaml Host", processName: "CredentialUIBroker", style: WsCaption,
            owner: main);
        var menu = AddPopup("", PopupService.MenuClass, processName: "chrome");
        _tree.WithWindow(prompt, new FakeUiaNode("Window", "Windows Security"));
        _tree.WithWindow(menu, new FakeUiaNode("Menu", "Context"));
        _desktop.SetForeground(prompt);
        Deny("CredentialUIBroker");

        // Act
        var result = await CreateSut().GetActivePopupsAsync(0, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert - the window in front is checked itself, not only the app that owns it
        AssertRefused(result, UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().NotContain("Windows Security");
    }

    [Fact]
    public async Task GetActivePopups_WithoutAWindow_RefusesTheDeniedAppThatOwnsTheWindowInFront()
    {
        // Arrange - a print dialog of another process, owned by a password manager's window, is in front
        var main = AddMain("Vault", processName: "KeePass");
        var dialog = AddPopup("Print", "#32770", processName: "splwow64", style: WsCaption, owner: main);
        _tree.WithWindow(dialog, new FakeUiaNode("Window", "Print"));
        _desktop.SetForeground(dialog);
        Deny("KeePass");

        // Act
        var result = await CreateSut().GetActivePopupsAsync(0, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert - the app whose popups would be listed is the denied one
        AssertRefused(result, UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().NotContain("Vault");
    }

    [Fact]
    public async Task GetActivePopups_WithoutAWindow_RefusesTheAppInFront_WhenItsProcessCannotBeNamed_WhileThePolicyDeniesAnything()
    {
        // Arrange - the process exited, or cannot be read: it cannot be checked against the deny list
        var main = AddMain("Editor", processId: 777);
        var menu = AddPopup("", PopupService.MenuClass, processId: 777, owner: main);
        _tree.WithWindow(menu, new FakeUiaNode("Menu", "Context"));
        _desktop.Native.Setup(n => n.GetProcessName(777u)).Returns((string?)null);
        _desktop.SetForeground(main);

        // Act
        var result = await CreateSut().GetActivePopupsAsync(0, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert
        AssertRefused(result, UIElementErrorCode.TargetDeniedByPolicy);
    }

    [Fact]
    public async Task GetActivePopups_WithoutAWindow_RefusesTheAgentsHostInFront_WithNothingOfItListedOrRead()
    {
        // Arrange - the agent's terminal is in front, with a context menu open
        var terminal = AddMain("pwsh", processName: "WindowsTerminal");
        var menu = AddPopup("", PopupService.MenuClass, processName: "WindowsTerminal", owner: terminal);
        _tree.WithWindow(menu, new FakeUiaNode("Menu", "Context"));
        _desktop.HostGuard.Setup(g => g.IsHostWindow(It.IsAny<nint>())).Returns(true);
        _desktop.SetForeground(terminal);

        // Act
        var result = await CreateSut().GetActivePopupsAsync(0, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert - get_desktop_state's rule: the host in front is not read unless it is named
        AssertRefused(result, UIElementErrorCode.TargetIsAgentHost);
        result.Error.Should().Contain("host").And.Contain("Name the window").And.NotContain("pwsh");
    }

    [Fact]
    public async Task GetActivePopups_OfTheAgentsHostNamed_ReadsIt_ButLeavesOutItsPopups()
    {
        // Arrange - the agent's terminal, with a context menu open
        var terminal = AddMain("pwsh", processName: "WindowsTerminal");
        var menu = AddPopup("", PopupService.MenuClass, processName: "WindowsTerminal", owner: terminal);
        _tree.WithWindow(menu, new FakeUiaNode("Menu", "Context"));
        _desktop.HostGuard.Setup(g => g.IsHostWindow(It.IsAny<nint>())).Returns(true);
        _desktop.SetForeground(terminal);

        // Act
        var result = await CreateSut().GetActivePopupsAsync(terminal, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert - a popup is never named: the desktop scope's rule leaves the host's popups out
        result.Success.Should().BeTrue();
        result.WindowTitle.Should().Be("pwsh");
        result.Popups.Should().BeEmpty();
        result.PopupsExcluded.Should().Be(1);
        _tree.Reads.Should().BeEmpty();
    }

    /// <summary>A refusal of the app in front: none of its titles, popups or elements is returned or read.</summary>
    private void AssertRefused(ActivePopupsResult result, UIElementErrorCode code)
    {
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(code);
        result.WindowTitle.Should().BeNull();
        result.Popups.Should().BeNull();
        result.PopupsExcluded.Should().BeNull();
        _tree.Reads.Should().BeEmpty();
        _worker.Calls.Should().Be(0);
        Registry.Count.Should().Be(0);
    }

    [Fact]
    public async Task GetActivePopups_OfANamedWindowOfADeniedApp_IsRefused_WithNothingListedOrRead()
    {
        // Arrange - a password manager's main window and its entry dialog
        var main = AddMain("Vault", processName: "KeePass");
        var dialog = AddPopup("Edit Entry", "#32770", processName: "KeePass", style: WsCaption, owner: main);
        _tree.WithWindow(dialog, new FakeUiaNode("Window", "Edit Entry"));
        _policy.Setup(p => p.Evaluate(It.Is<IReadOnlyCollection<string?>>(names => names.Contains("KeePass"))))
            .Returns(TargetPolicyVerdict.Deny("KeePass", "'KeePass' is on the server's default deny list"));

        // Act
        var result = await CreateSut().GetActivePopupsAsync(main, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("KeePass");
        result.Popups.Should().BeNull();
        _tree.Reads.Should().BeEmpty();
        _worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task GetActivePopups_WithAStopGrace_GivesUpOnAStuckRead_AfterIt_AndStillListsThePopup()
    {
        // Arrange - the menu's provider does not answer; the real worker abandons the read after the grace
        using var release = new ManualResetEventSlim();
        var main = AddMain("Editor");
        var menu = AddPopup("", PopupService.MenuClass, owner: main);
        _tree.WithWindow(menu, new FakeUiaNode("Menu", "Context") { OnRead = () => release.Wait(TimeSpan.FromSeconds(3)) });
        Registry = new ElementRegistry(_tree, _worker, _desktop.Native.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        var sut = new PopupService(_desktop.Native.Object, _processes.Object, _desktop.HostGuard.Object, _policy.Object, _tree,
            new UiaWorker(), Registry);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Act
            var result = await sut.GetActivePopupsAsync(main, TimeSpan.FromMilliseconds(100), CancellationToken.None,
                stopGrace: TimeSpan.FromMilliseconds(50));

            // Assert - without the grace it would wait a whole second after the timeout
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(900));
            result.Success.Should().BeTrue();
            result.Popups.Should().ContainSingle().Which.Ref.Should().BeNull();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task GetActivePopups_WithNoPopup_ReadsNoUiAutomation()
    {
        var main = AddMain("Editor");

        var result = await CreateSut().GetActivePopupsAsync(main, TimeSpan.FromSeconds(1), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Popups.Should().BeEmpty();
        _worker.Calls.Should().Be(0);
    }

    #endregion

    #region Waiting for a new popup (element_action's openedPopup)

    [Fact]
    public async Task WaitForNewPopup_ReturnsThePopupThatWasNotThereBefore()
    {
        // Arrange - a menu was open before; a submenu opens while the wait runs
        var main = AddMain("Editor");
        var before = AddPopup("", PopupService.MenuClass, owner: main);
        var sut = CreateSut();
        var seen = sut.GetPopups(main).Select(p => p.Handle).ToHashSet();
        var submenu = AddPopup("", PopupService.MenuClass, owner: main);

        // Act
        var opened = await sut.WaitForNewPopupAsync(main, seen, TimeSpan.FromSeconds(2), CancellationToken.None);

        // Assert
        seen.Should().Equal(before);
        opened!.Handle.Should().Be(submenu);
    }

    [Fact]
    public async Task WaitForNewPopup_ReturnsNull_WhenNoneOpensInTime()
    {
        var main = AddMain("Editor");

        var opened = await CreateSut().WaitForNewPopupAsync(main, new HashSet<nint>(), TimeSpan.FromMilliseconds(120), CancellationToken.None);

        opened.Should().BeNull();
    }

    [Fact]
    public async Task WaitForNewPopup_IgnoresATooltip_AndReturnsTheMenuThatOpened()
    {
        // Arrange - the pointer rests on the button, so its tooltip shows in front of the menu the click opened
        var main = AddMain("Editor");
        AddPopup("", "tooltips_class32", owner: main);
        var menu = AddPopup("", PopupService.MenuClass, owner: main);

        // Act
        var opened = await CreateSut().WaitForNewPopupAsync(main, new HashSet<nint>(), TimeSpan.FromSeconds(1), CancellationToken.None);

        // Assert
        opened!.Handle.Should().Be(menu);
    }

    [Fact]
    public async Task WaitForNewPopup_ReadsTheProcessSnapshotOnce_NotOnEveryCheck()
    {
        // Arrange - a WebView2 popup of the app is already open, so every check needs the app's child processes
        var popup = ArrangeWebViewPopup(new DateTime(2026, 1, 1, 9, 0, 0), new DateTime(2026, 1, 1, 9, 0, 1), out var host);

        // Act - several checks (every 50 ms) within one refresh period
        var opened = await CreateSut().WaitForNewPopupAsync(host, new HashSet<nint> { popup }, TimeSpan.FromMilliseconds(300),
            CancellationToken.None);

        // Assert
        opened.Should().BeNull();
        _processes.Verify(p => p.GetProcessList(), Times.Once);
    }

    [Fact]
    public async Task WaitForNewPopup_HonoursCancellation()
    {
        var main = AddMain("Editor");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => CreateSut().WaitForNewPopupAsync(main, new HashSet<nint>(), TimeSpan.FromSeconds(5), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion
}

internal static class PopupServiceTestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
