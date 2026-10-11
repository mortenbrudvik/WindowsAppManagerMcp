namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Window selectors and the richer find_windows query (roadmap M2.4): ResolveWindow finds exactly one window or says
/// why not (WindowNotFound, AmbiguousWindow with candidates, HandleMismatch, InvalidHandle, InvalidArguments), and never
/// silently picks the first of several matches. Every window is a fake on a mocked native wrapper.
/// </summary>
public class WindowServiceSelectorTests
{
    private readonly FakeWindowDesktop _desktop = new();

    private WindowService CreateSut() => _desktop.CreateService();

    #region find_windows query

    [Fact]
    public void FindWindows_ProcessName_IsTheWholeName_UnlessContainsIsAsked()
    {
        // Arrange - "Code" must not match "Code - Insiders" (roadmap M2.4: exact by default, contains opt-in)
        var code = _desktop.Add("main.cs - Code", processName: "Code");
        var insiders = _desktop.Add("main.cs - Code - Insiders", processName: "Code - Insiders");
        var sut = CreateSut();

        // Act & Assert
        sut.FindWindows(new WindowQuery { ProcessName = "code" }).Select(w => w.Handle).Should().Equal(code);
        sut.FindWindows(new WindowQuery { ProcessName = "Code.exe" }).Select(w => w.Handle).Should().Equal(code);
        sut.FindWindows(new WindowQuery { ProcessName = "cod" }).Should().BeEmpty();
        sut.FindWindows(new WindowQuery { ProcessName = "cod", ProcessNameContains = true }).Select(w => w.Handle)
            .Should().Equal(code, insiders);
    }

    [Fact]
    public void FindWindows_TitleFilters_UseTheBrowserAwareNormalizer()
    {
        // Arrange - an Edge window: a zero-width space in "Microsoft Edge" and the more-pages segment
        var edge = _desktop.Add("Inbox and 2 more pages - Personal - Microsoft​ Edge", processName: "msedge");
        _desktop.Add("Untitled - Notepad", processName: "notepad");
        var sut = CreateSut();

        // Act & Assert
        sut.FindWindows(new WindowQuery { TitleContains = "Microsoft Edge" }).Select(w => w.Handle).Should().Equal(edge);
        sut.FindWindows(new WindowQuery { TitleEquals = "Inbox - Personal - Microsoft Edge" }).Select(w => w.Handle).Should().Equal(edge);
        sut.FindWindows(new WindowQuery { TitleRegex = "^inbox - .* edge$" }).Select(w => w.Handle).Should().Equal(edge);
        sut.FindWindows(new WindowQuery { TitleEquals = "Untitled" }).Should().BeEmpty("titleEquals compares whole titles");
    }

    [Fact]
    public void FindWindows_IncludeUntitled_KeepsUntitledWindowsInTheListing()
    {
        var untitled = _desktop.Add(string.Empty, processName: "splash");
        var sut = CreateSut();

        sut.FindWindows(new WindowQuery { ProcessName = "splash" }).Should().BeEmpty();
        sut.FindWindows(new WindowQuery { ProcessName = "splash", IncludeUntitled = true }).Select(w => w.Handle).Should().Equal(untitled);
    }

    [Fact]
    public void FindWindows_SortAndLimit()
    {
        // Arrange - z-order: front to back as added
        var b = _desktop.Add("Beta", processName: "zeta");
        var a = _desktop.Add("alpha", processName: "omega");
        var c = _desktop.Add("Gamma", processName: "alpha");
        var sut = CreateSut();

        // Act & Assert
        sut.FindWindows(new WindowQuery()).Select(w => w.Handle).Should().Equal(b, a, c);
        sut.FindWindows(new WindowQuery { Sort = WindowSort.Title }).Select(w => w.Handle).Should().Equal(a, b, c);
        sut.FindWindows(new WindowQuery { Sort = WindowSort.Process }).Select(w => w.Handle).Should().Equal(c, a, b);
        sut.FindWindows(new WindowQuery { Limit = 2 }).Select(w => w.Handle).Should().Equal(b, a);
        sut.FindWindows(new WindowQuery { Sort = WindowSort.Title, Limit = 1 }).Select(w => w.Handle).Should().Equal(a);
    }

    [Fact]
    public void FindWindows_RefusesAnInvalidRegexAndLimit()
    {
        var sut = CreateSut();

        sut.Invoking(s => s.FindWindows(new WindowQuery { TitleRegex = "(" })).Should().Throw<ArgumentException>()
            .WithMessage("*titleRegex*");
        sut.Invoking(s => s.FindWindows(new WindowQuery { Limit = 0 })).Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region ResolveWindow

    [Fact]
    public void Resolve_OneMatch_IsFound()
    {
        _desktop.Add("Inbox - Outlook", processName: "OUTLOOK");
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad", processId: 200);
        var sut = CreateSut();

        var resolution = sut.ResolveWindow(new WindowSelector { ProcessName = "notepad" });

        resolution.Success.Should().BeTrue();
        resolution.Handle.Should().Be(notepad);
        resolution.Window!.ProcessId.Should().Be(200);
    }

    [Fact]
    public void Resolve_SeveralMatches_IsAmbiguous_WithAtMostFiveCandidatesInZOrder_NeverTheFirst()
    {
        // Arrange - seven Chrome windows
        var chrome = Enumerable.Range(0, 7).Select(i => _desktop.Add($"Tab {i} - Google Chrome", processName: "chrome")).ToList();
        var sut = CreateSut();

        // Act
        var resolution = sut.ResolveWindow(new WindowSelector { ProcessName = "chrome" });

        // Assert
        resolution.Success.Should().BeFalse("several windows match and none was named: the call must not guess");
        resolution.Handle.Should().Be(nint.Zero);
        resolution.ErrorCode.Should().Be("AmbiguousWindow");
        resolution.MatchCount.Should().Be(7);
        resolution.Candidates.Select(w => w.Handle).Should().Equal(chrome.Take(5));
        resolution.Error.Should().Contain("7 windows match").And.Contain("{handle, processId}");
    }

    [Fact]
    public void Resolve_Ambiguous_DoesNotOfferACandidatesPositionAsIndex()
    {
        // index counts the z-order of the next call, which a focus or a click in between changes: mapping a candidate's
        // position to index can silently act on another window
        _desktop.Add("Docs - Google Chrome", processName: "chrome");
        _desktop.Add("Mail - Google Chrome", processName: "chrome");

        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = "chrome" });

        resolution.Error.Should().NotContain("pick one in z-order").And.Contain("Do not turn a candidate's position into window.index");
    }

    [Fact]
    public void Resolve_Ambiguous_MarksTheAgentsHostAmongTheCandidates()
    {
        // Arrange - two terminals, one of them the agent's own
        var host = _desktop.Add("claude - Windows PowerShell", processName: "WindowsTerminal");
        var other = _desktop.Add("build - Windows PowerShell", processName: "WindowsTerminal");
        _desktop.HostGuard.Setup(g => g.IsHostWindow(host)).Returns(true);

        // Act
        var resolution = CreateSut().ResolveWindow(new WindowSelector { ProcessName = "WindowsTerminal" });
        var candidates = resolution.Candidates.Select(WindowCandidateDto.From).ToList();

        // Assert
        resolution.ErrorCode.Should().Be("AmbiguousWindow");
        resolution.Error.Should().Contain("isAgentHost");
        candidates.Single(c => c.Handle == host.ToInt64()).IsAgentHost.Should().BeTrue();
        candidates.Single(c => c.Handle == other.ToInt64()).IsAgentHost.Should().BeNull("only the host is marked; the field is omitted otherwise");
    }

    [Fact]
    public void Resolve_Index_PicksOneMatchInZOrder_AndPastTheEndIsNotFound()
    {
        var first = _desktop.Add("Docs - Google Chrome", processName: "chrome");
        var second = _desktop.Add("Mail - Google Chrome", processName: "chrome");
        var sut = CreateSut();

        sut.ResolveWindow(new WindowSelector { ProcessName = "chrome", Index = 0 }).Handle.Should().Be(first);
        sut.ResolveWindow(new WindowSelector { ProcessName = "chrome", Index = 1 }).Handle.Should().Be(second);

        var past = sut.ResolveWindow(new WindowSelector { ProcessName = "chrome", Index = 2 });
        past.ErrorCode.Should().Be("WindowNotFound");
        past.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public void Resolve_NoMatch_IsWindowNotFound_WithHints()
    {
        _desktop.Add("Untitled - Notepad", processName: "notepad");
        var sut = CreateSut();

        var resolution = sut.ResolveWindow(new WindowSelector { ProcessName = "note" });

        resolution.ErrorCode.Should().Be("WindowNotFound");
        resolution.Candidates.Should().BeEmpty();
        resolution.Error.Should().Contain("processName 'note'").And.Contain("processNameContains");
    }

    [Fact]
    public void Resolve_FollowsTheListingRules_CloakedOnlyWhenAsked()
    {
        var cloaked = _desktop.Add("Settings", processName: "SystemSettings", cloaked: true);
        var sut = CreateSut();

        sut.ResolveWindow(new WindowSelector { ProcessName = "SystemSettings" }).ErrorCode.Should().Be("WindowNotFound");
        sut.ResolveWindow(new WindowSelector { ProcessName = "SystemSettings", IncludeCloaked = true }).Handle.Should().Be(cloaked);
    }

    [Fact]
    public void Resolve_Handle_WithMatchingExpectations_IsFound()
    {
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad", processId: 200);
        var sut = CreateSut();

        var resolution = sut.ResolveWindow(new WindowSelector { Handle = notepad, ProcessId = 200, TitleContains = "notepad" });

        resolution.Success.Should().BeTrue();
        resolution.Handle.Should().Be(notepad);
    }

    [Fact]
    public void Resolve_Handle_ThatWasReusedByAnotherWindow_IsHandleMismatch()
    {
        // Arrange - the agent found Notepad, then Notepad closed and a terminal got its handle
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad", processId: 200);
        _desktop.Reuse(notepad, "Windows PowerShell", 300, "WindowsTerminal");
        var sut = CreateSut();

        // Act
        var resolution = sut.ResolveWindow(new WindowSelector { Handle = notepad, ProcessId = 200 });

        // Assert
        resolution.Success.Should().BeFalse();
        resolution.ErrorCode.Should().Be("HandleMismatch");
        resolution.Error.Should().Contain("PID 300, not 200").And.Contain("reused");
    }

    [Theory]
    [InlineData("processName")]
    [InlineData("titleEquals")]
    [InlineData("className")]
    public void Resolve_Handle_ChecksEveryExpectation(string field)
    {
        var notepad = _desktop.Add("Untitled - Notepad", className: "Notepad", processName: "notepad");
        var sut = CreateSut();
        var selector = field switch
        {
            "processName" => new WindowSelector { Handle = notepad, ProcessName = "chrome" },
            "titleEquals" => new WindowSelector { Handle = notepad, TitleEquals = "Budget.xlsx - Excel" },
            _ => new WindowSelector { Handle = notepad, ClassName = "XLMAIN" }
        };

        sut.ResolveWindow(selector).ErrorCode.Should().Be("HandleMismatch");
    }

    [Fact]
    public void Resolve_Handle_ThatIsNoWindow_IsInvalidHandle()
    {
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");
        _desktop.Close(notepad);
        var sut = CreateSut();

        sut.ResolveWindow(new WindowSelector { Handle = notepad, ProcessName = "notepad" }).ErrorCode.Should().Be("InvalidHandle");
    }

    [Fact]
    public void Resolve_Handle_SkipsTheListingFilter()
    {
        // An untitled, cloaked window named by its handle is still that window
        var popup = _desktop.Add(string.Empty, className: "#32768", cloaked: true);
        var sut = CreateSut();

        sut.ResolveWindow(new WindowSelector { Handle = popup, ClassName = "#32768" }).Handle.Should().Be(popup);
    }

    [Fact]
    public void Resolve_Foreground_IsTheWindowInFront_WhenItMatches()
    {
        _desktop.Add("Inbox - Outlook", processName: "OUTLOOK");
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");
        _desktop.SetForeground(notepad);
        var sut = CreateSut();

        sut.ResolveWindow(new WindowSelector { Foreground = true }).Handle.Should().Be(notepad);
        sut.ResolveWindow(new WindowSelector { Foreground = true, ProcessName = "notepad" }).Handle.Should().Be(notepad);
        sut.ResolveWindow(new WindowSelector { Foreground = true, ProcessName = "OUTLOOK" }).ErrorCode.Should().Be("WindowNotFound");
    }

    [Fact]
    public void Resolve_Foreground_WithNoWindowInFront_IsWindowNotFound()
    {
        _desktop.SetForeground(nint.Zero);

        CreateSut().ResolveWindow(new WindowSelector { Foreground = true }).ErrorCode.Should().Be("WindowNotFound");
    }

    public static TheoryData<WindowSelector> InvalidSelectors() => new()
    {
        new WindowSelector(), // names nothing: never every window
        new WindowSelector { IncludeCloaked = true, IncludeUntitled = true, ProcessNameContains = true },
        new WindowSelector { TitleContains = "" },
        new WindowSelector { Handle = 0 },
        new WindowSelector { ProcessId = -1 },
        new WindowSelector { ProcessName = "chrome", Index = -1 },
        new WindowSelector { Handle = 4096, Foreground = true },
        new WindowSelector { Handle = 4096, Index = 0 },
        new WindowSelector { Foreground = true, Index = 1 },
        new WindowSelector { TitleRegex = "([a-" },
        // Text that normalizes to nothing would count as a filter while matching every window
        new WindowSelector { TitleContains = " " },
        new WindowSelector { TitleContains = "\u200B" },
        new WindowSelector { TitleEquals = "  " },
        new WindowSelector { ProcessName = " " },
        new WindowSelector { ProcessName = ".exe", ProcessNameContains = true },
        new WindowSelector { ClassName = " " },
        new WindowSelector { ProcessName = "notepad", TitleContains = " ", Index = 0 },
        new WindowSelector { Handle = 4096, TitleContains = " " },
    };

    [Theory]
    [MemberData(nameof(InvalidSelectors))]
    public void Resolve_ASelectorThatCannotBeUsed_IsInvalidArguments(WindowSelector selector)
    {
        _desktop.Add("Untitled - Notepad", processName: "notepad");

        var resolution = CreateSut().ResolveWindow(selector);

        resolution.Success.Should().BeFalse();
        resolution.ErrorCode.Should().Be("InvalidArguments");
        resolution.Error.Should().Contain("Nothing was done");
    }

    [Fact]
    public void Resolve_AnEmptyFilterString_IsAFilterLeftOut()
    {
        // "" is what a client sends for a field it does not use; only text that is blank once normalized is refused
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad");

        CreateSut().ResolveWindow(new WindowSelector { ProcessName = "notepad", TitleContains = "" }).Handle.Should().Be(notepad);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_ARegexThatTimesOut_CheckingAHandleOrTheForeground_IsInvalidArguments_NotAMismatch(bool foreground)
    {
        // Arrange - the pattern's fault is reported as such, never as a reused handle or a different window in front
        var app = _desktop.Add(new string('a', 5000) + "!", processName: "app");
        _desktop.SetForeground(app);
        var selector = foreground
            ? new WindowSelector { Foreground = true, TitleRegex = "^(a+)+$" }
            : new WindowSelector { Handle = app, TitleRegex = "^(a+)+$" };

        // Act
        var resolution = CreateSut().ResolveWindow(selector);

        // Assert
        resolution.ErrorCode.Should().Be("InvalidArguments");
        resolution.Error.Should().Contain("100 ms").And.NotContain("reused");
    }

    [Fact]
    public void Resolve_ARegexThatTimesOut_IsInvalidArguments()
    {
        _desktop.Add(new string('a', 5000) + "!", processName: "app");

        var resolution = CreateSut().ResolveWindow(new WindowSelector { TitleRegex = "^(a+)+$" });

        resolution.ErrorCode.Should().Be("InvalidArguments");
        resolution.Error.Should().Contain("100 ms");
    }

    #endregion

    #region GetWindowDetails

    [Fact]
    public void GetWindowDetails_ReadsTheFullState()
    {
        // Arrange
        _desktop.Add("Inbox - Outlook", processName: "OUTLOOK");
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad", extendedStyle: FakeWindowDesktop.WsExTopmost,
            style: FakeWindowDesktop.WsDisabled, hung: true, dpi: 144);
        _desktop.SetForeground(notepad);
        var sut = CreateSut();

        // Act
        var details = sut.GetWindowDetails(notepad)!;

        // Assert
        details.Window.Handle.Should().Be(notepad);
        details.IsForeground.Should().BeTrue();
        details.ZOrder.Should().Be(1);
        details.IsTopmost.Should().BeTrue();
        details.IsEnabled.Should().BeFalse();
        details.IsResponding.Should().BeFalse();
        details.Dpi.Should().Be(144);
    }

    [Fact]
    public void GetWindowDetails_OfANormalWindow_AndOfNoWindow()
    {
        var notepad = _desktop.Add("Untitled - Notepad", processName: "notepad", dpi: 0);
        var sut = CreateSut();

        var details = sut.GetWindowDetails(notepad)!;
        details.IsForeground.Should().BeFalse();
        details.ZOrder.Should().Be(0);
        details.IsTopmost.Should().BeFalse();
        details.IsEnabled.Should().BeTrue();
        details.IsResponding.Should().BeTrue();
        details.Dpi.Should().BeNull("0 means the DPI could not be read");

        sut.GetWindowDetails(new nint(0x7777)).Should().BeNull();
    }

    #endregion
}
