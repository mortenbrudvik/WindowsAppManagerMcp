using System.Text.Json;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The target policy's check of the elements of another process inside a window it allows (an embedded app, a preview handler,
/// a WebView2: <see cref="ForeignElements"/>), on the reads that return an element's text or value: read_element_text and
/// read_table refuse such an element of a process the policy denies, read_table, find_ui_elements and wait_for_element leave
/// it out, as element_action refuses to act on it. The window belongs to an allowed app (process 300, "outlook"); the
/// embedded element to a password manager (process 777, "KeePass", on the default deny list) or to an allowed process (778,
/// "msedgewebview2"). Over an in-memory UI Automation tree; nothing reads the real desktop.
/// </summary>
public class ForeignElementReadTests
{
    private static readonly nint Handle = 0x7500;
    private static readonly nint VaultWindow = 0x7501;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const int AppPid = 300;
    private const int DeniedPid = 777;
    private const int AllowedPid = 778;

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;
    private readonly TargetPolicy _policy = new(TargetPolicySettings.Default);
    private readonly FakeUiaNode _window = new("Window", "Inbox - Outlook") { ProcessId = AppPid };

    public ForeignElementReadTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns((uint)AppPid);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _windows.Setup(w => w.GetProcessName((uint)AppPid)).Returns("outlook");
        _windows.Setup(w => w.GetProcessName((uint)DeniedPid)).Returns("KeePass");
        _windows.Setup(w => w.GetProcessName((uint)AllowedPid)).Returns("msedgewebview2");
        _windows.Setup(w => w.IsWindow(VaultWindow)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(VaultWindow)).Returns((uint)DeniedPid);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        _tree.WithWindow(Handle, _window);
    }

    private ElementSearchService Search(ITargetPolicy? policy) =>
        new(_tree, _worker, _registry, _windows.Object, policy: policy);

    private ElementReadService Reads(ITargetPolicy? policy = null) =>
        new(_tree, _worker, _registry, Search(policy ?? _policy), _windows.Object, policy: policy ?? _policy);

    /// <summary>Puts <paramref name="element"/> in the window (or in <paramref name="parent"/>) and gives it a ref.</summary>
    private string Add(FakeUiaNode element, FakeUiaNode? parent = null)
    {
        (parent ?? _window).With(element);
        return _registry.Register(Handle, _tree.ReadElement(element), new ElementOrigin(_registry.NextGeneration()))!;
    }

    /// <summary>The password manager's entry, shown inside the window as a child window of its own process.</summary>
    private static FakeUiaNode VaultEntry(int processId = DeniedPid) =>
        new FakeUiaNode("Document", "Entry notes") { ProcessId = processId, OtherPatterns = UiaPatterns.Text, ActionPatterns = ["Value"], Value = "4829-1337" }
            .WithPattern<IUiaTextPattern>(new FakeTextPattern("Recovery code 4829-1337"));

    private static bool ReadsAValueOrText(string read) =>
        read.StartsWith("Value:", StringComparison.Ordinal) || read.StartsWith("Pattern:IUiaTextPattern", StringComparison.Ordinal);

    #region ForeignElements

    [Fact]
    public void Verdict_OfTheWindowsOwnProcess_IsAllowed_WithTheWindow()
    {
        // Arrange - the window's own verdict is the read's first check (UiaTargets.ReadVerdict), not this one's
        _windows.Setup(w => w.GetProcessName((uint)AppPid)).Returns("KeePass");
        var foreign = UiaTargets.ForeignElementsOf(_windows.Object, _policy, Handle);

        // Act and assert
        foreign.Verdict(AppPid).Allowed.Should().BeTrue();
        foreign.LeavesOut(AppPid).Should().BeFalse();
        foreign.LeftOutCount.Should().Be(0);
    }

    [Fact]
    public void Verdict_OfAnotherProcess_IsTheVerdictOnItsName_NamedOncePerRead()
    {
        var foreign = UiaTargets.ForeignElementsOf(_windows.Object, _policy, Handle);

        foreign.Verdict(DeniedPid).Should().Match<TargetPolicyVerdict>(v => !v.Allowed && v.Reason!.Contains("KeePass"));
        foreign.Verdict(DeniedPid).Allowed.Should().BeFalse();
        foreign.Verdict(AllowedPid).Allowed.Should().BeTrue();
        _windows.Verify(w => w.GetProcessName((uint)DeniedPid), Times.Once);
    }

    [Theory]
    [InlineData(0)] // UI Automation did not say
    [InlineData(4040)] // a process whose name cannot be read
    public void Verdict_OfAProcessThatCannotBeNamed_IsRefused_WhileThePolicyDeniesAnything(int processId)
    {
        UiaTargets.ForeignElementsOf(_windows.Object, _policy, Handle).Verdict(processId).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Verdict_WithoutAPolicyThatDeniesAnything_IsAllowed_WithoutAnyRead()
    {
        var open = new TargetPolicy(new TargetPolicySettings([], null, "test"));

        var foreign = UiaTargets.ForeignElementsOf(_windows.Object, open, Handle);

        foreign.Active.Should().BeFalse();
        foreign.Verdict(0).Allowed.Should().BeTrue();
        foreign.LeavesOutWindow(VaultWindow).Should().BeFalse();
        _windows.Verify(w => w.GetProcessName(It.IsAny<uint>()), Times.Never);
    }

    #endregion

    #region read_element_text

    [Fact]
    public async Task ReadText_OfAnElementOfADeniedProcess_InAnAllowedWindow_IsRefused_WithNothingRead()
    {
        // Arrange - KeePass shown inside Outlook's window (as a child window or embedded control)
        var entryRef = Add(VaultEntry());

        // Act
        var result = await Reads().ReadTextAsync(new ElementTextRequest(entryRef, null, 0, 1000, Timeout));

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("KeePass").And.Contain("process 777").And.Contain("Nothing was read");
        result.Text.Should().BeNull();
        _tree.Reads.Should().NotContain(r => ReadsAValueOrText(r));
        JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    [Fact]
    public async Task ReadText_OfAnElementOfAnAllowedProcess_InTheWindow_IsRead()
    {
        // Arrange - a WebView2's document in Outlook's window
        var pageRef = Add(VaultEntry(AllowedPid));

        // Act
        var result = await Reads().ReadTextAsync(new ElementTextRequest(pageRef, null, 0, 1000, Timeout));

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Text.Should().Be("Recovery code 4829-1337");
    }

    [Fact]
    public async Task ReadText_OfAnElementOfTheWindowsOwnProcess_ReadsItsIdentityOnce()
    {
        var subjectRef = Add(new FakeUiaNode("Edit", "Subject") { ProcessId = AppPid, ActionPatterns = ["Value"], Value = "Lunch" });

        var result = await Reads().ReadTextAsync(new ElementTextRequest(subjectRef, null, 0, 1000, Timeout));

        result.Text.Should().Be("Lunch");
        _tree.Reads.Count(r => r == "Identity:Subject").Should().Be(1, "the policy check's identity read gives the password flag too");
    }

    [Fact]
    public async Task ReadText_OfAnElementWhoseProcessCannotBeRead_IsRefused_FailingClosed()
    {
        var fieldRef = Add(new FakeUiaNode("Edit", "Field") { ProcessId = 4040, ActionPatterns = ["Value"], Value = "maybe secret" });

        var result = await Reads().ReadTextAsync(new ElementTextRequest(fieldRef, null, 0, 1000, Timeout));

        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        _tree.Reads.Should().NotContain(r => ReadsAValueOrText(r));
    }

    [Fact]
    public async Task ReadText_ByCriteria_OfAnElementOfADeniedProcess_IsNotRead()
    {
        // Arrange - the criteria find only the embedded vault's document
        _window.With(VaultEntry());

        // Act
        var result = await Reads().ReadTextAsync(new ElementTextRequest(null,
            new ElementQuery(Handle, Name: "Entry notes", MaxResults: 5, Timeout: Timeout), 0, 1000, Timeout));

        // Assert - the search leaves it out by the process it read with it, so nothing of it is read
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Text.Should().BeNull();
        _tree.Reads.Should().NotContain(r => ReadsAValueOrText(r));
    }

    #endregion

    #region read_table

    /// <summary>A list whose second row is served by the password manager (its process), each row its own cell.</summary>
    private string AddMixedList(out FakeUiaNode vaultRow)
    {
        vaultRow = new FakeUiaNode("ListItem", "Bank login") { ProcessId = DeniedPid, ActionPatterns = ["Value"], Value = "hunter2" };
        return Add(new FakeUiaNode("List", "Messages") { ProcessId = AppPid }.With(
            new FakeUiaNode("ListItem", "Row 1") { ProcessId = AppPid, ActionPatterns = ["Value"], Value = "Lunch on Friday" },
            vaultRow));
    }

    [Fact]
    public async Task ReadTable_ACellOfADeniedProcess_IsNull_AndTheWarningSaysWhose_WithItsValueUnread()
    {
        // Arrange
        var listRef = AddMixedList(out _);

        // Act
        var result = await Reads().ReadTableAsync(new ElementTableRequest(listRef, 0, 50, 20, Timeout));

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Rows!.Select(r => r[0]).Should().Equal("Lunch on Friday", null);
        result.Warning.Should().Contain("KeePass").And.Contain("those cells are null");
        _tree.Reads.Should().NotContain("Value:Bank login");
        JsonSerializer.Serialize(result).Should().NotContain("hunter2").And.NotContain("Bank login");
    }

    [Fact]
    public async Task ReadTable_ACellThatIsAWindowOfADeniedProcess_IsNull()
    {
        // Arrange - a grid cell that hosts the vault's child window (no Value pattern: its name would be its text)
        var listRef = Add(new FakeUiaNode("List", "Messages") { ProcessId = AppPid }.With(
            new FakeUiaNode("ListItem", "Row 1") { ProcessId = AppPid },
            new FakeUiaNode("ListItem", "Vault 4829-1337") { ProcessId = DeniedPid, NativeWindowHandle = VaultWindow }));

        // Act
        var result = await Reads().ReadTableAsync(new ElementTableRequest(listRef, 0, 50, 20, Timeout));

        // Assert
        result.Rows!.Select(r => r[0]).Should().Equal("Row 1", null);
        JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    [Fact]
    public async Task ReadTable_OfATableOfADeniedProcess_IsRefused()
    {
        var listRef = Add(new FakeUiaNode("List", "Entries") { ProcessId = DeniedPid }.With(
            new FakeUiaNode("ListItem", "Bank login") { ProcessId = DeniedPid }));

        var result = await Reads().ReadTableAsync(new ElementTableRequest(listRef, 0, 50, 20, Timeout));

        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Rows.Should().BeNull();
        _tree.CachedFetches.Should().BeEmpty("no row is read");
    }

    #endregion

    #region find_ui_elements

    [Fact]
    public async Task Find_DetailStandard_LeavesOutAMatchOfADeniedProcess_WithItsValueUnread_AsAPartSkipped()
    {
        // Arrange
        _window.With(
            new FakeUiaNode("Edit", "Subject") { ProcessId = AppPid, ActionPatterns = ["Value"], Value = "Lunch" },
            new FakeUiaNode("Edit", "Master password hint") { ProcessId = DeniedPid, ActionPatterns = ["Value"], Value = "dog's name" });

        // Act
        var result = await Search(_policy).FindAsync(new ElementQuery(Handle, ControlTypes: ["Edit"], Detail: ElementDetail.Standard,
            Timeout: Timeout));

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Select(e => e.Name).Should().Equal("Subject");
        result.Elements![0].Value.Should().Be("Lunch");
        result.MatchCount.Should().Be(1);
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.Warning.Should().Contain("KeePass").And.Contain("left out of the matches");
        _tree.Reads.Should().NotContain("Value:Master password hint");
        JsonSerializer.Serialize(result).Should().NotContain("dog's name");
    }

    [Fact]
    public async Task Find_DetailMinimal_LeavesOutAMatchThatIsAWindowOfADeniedProcess()
    {
        // Arrange - the vault's child window inside the window
        _window.With(new FakeUiaNode("Pane", "Vault") { ProcessId = DeniedPid, NativeWindowHandle = VaultWindow });

        // Act
        var result = await Search(_policy).FindAsync(new ElementQuery(Handle, ControlTypes: ["Pane"], Timeout: Timeout));

        // Assert
        result.Elements.Should().BeEmpty();
        result.MatchCount.Should().Be(0);
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
    }

    [Fact]
    public async Task Find_DetailStandard_KeepsAMatchOfAnAllowedProcess()
    {
        _window.With(new FakeUiaNode("Edit", "Search the web") { ProcessId = AllowedPid, ActionPatterns = ["Value"], Value = "weather" });

        var result = await Search(_policy).FindAsync(new ElementQuery(Handle, ControlTypes: ["Edit"], Detail: ElementDetail.Standard,
            Timeout: Timeout));

        result.Elements!.Single().Value.Should().Be("weather");
        result.TruncationReason.Should().BeNull();
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task Find_WithoutAPolicy_LeavesNothingOut()
    {
        _window.With(new FakeUiaNode("Edit", "Master password hint") { ProcessId = DeniedPid, ActionPatterns = ["Value"], Value = "dog's name" });

        var result = await Search(null).FindAsync(new ElementQuery(Handle, ControlTypes: ["Edit"], Detail: ElementDetail.Standard,
            Timeout: Timeout));

        result.Elements!.Single().Value.Should().Be("dog's name");
    }

    #endregion

    #region wait_for_element

    private ElementWaitService Waits()
    {
        var windowWait = new Mock<IWindowWaitService>();
        return new ElementWaitService(Search(_policy), _registry, windowWait.Object, _tree, _worker, _windows.Object,
            delay: (_, _) => Task.CompletedTask, policy: _policy);
    }

    [Fact]
    public async Task Wait_RefValueEquals_OnAnElementOfADeniedProcess_EndsAtOnce_WithItsValueUnread()
    {
        // Arrange
        var hintRef = Add(new FakeUiaNode("Edit", "Master password hint") { ProcessId = DeniedPid, ActionPatterns = ["Value"], Value = "dog's name" });

        // Act
        var result = await Waits().WaitAsync(new ElementWaitRequest
        {
            Ref = hintRef, Condition = ElementWaitCondition.ValueEquals, Expected = "x", TimeoutMs = 5000
        });

        // Assert
        result.Success.Should().BeFalse();
        result.TimedOut.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain("KeePass");
        result.Polls.Should().Be(1);
        _tree.Reads.Should().NotContain("Value:Master password hint");
        JsonSerializer.Serialize(result).Should().NotContain("dog's name");
    }

    [Fact]
    public async Task Wait_CriteriaValueEquals_NeverQuotesTheValueOfAnElementOfADeniedProcess()
    {
        // Arrange
        var bodyRef = Add(new FakeUiaNode("Pane", "Body") { ProcessId = AppPid }.With(
            new FakeUiaNode("Edit", "Master password hint") { ProcessId = DeniedPid, ActionPatterns = ["Value"], Value = "dog's name" }));

        // Act
        var result = await Waits().WaitAsync(new ElementWaitRequest
        {
            Criteria = new ElementQuery(nint.Zero, WithinRef: bodyRef, ControlTypes: ["Edit"]),
            Condition = ElementWaitCondition.ValueEquals, Expected = "x", TimeoutMs = 0
        });

        // Assert - the search leaves it out, so the wait never sees it (a part skipped)
        result.Satisfied.Should().BeFalse();
        result.LastObservedState.Should().Contain("PartsSkipped");
        _tree.Reads.Should().NotContain("Value:Master password hint");
        JsonSerializer.Serialize(result).Should().NotContain("dog's name");
    }

    #endregion

    #region Elements with no window of their own (the process read with each element)

    /// <summary>An element as a read returned it, with no window of its own; <paramref name="processId"/> as read with it.</summary>
    private static UiaElement Read(int? processId, nint window = 0, IReadOnlyList<int>? runtimeId = null) =>
        new(new FakeUiaNode("Text", "Recovery code"), new UiaNodeProperties("Recovery code", "Text", null, UiaRect.Empty, true, false, "Chrome"),
            null, false, UiaPatterns.None, runtimeId ?? FakeUiaNode.NewRuntimeId(), 50020, window, ProcessId: processId);

    /// <summary>A page's text served by the password manager's process (a WebView2's elements have no window of their own).</summary>
    private static FakeUiaNode PageText(string name = "Recovery code 4829-1337") =>
        new("Text", name) { ProcessId = DeniedPid, FrameworkId = "Chrome", ActionPatterns = ["Value"], Value = "4829-1337" };

    [Theory]
    [InlineData(DeniedPid, true)]
    [InlineData(AllowedPid, false)]
    [InlineData(AppPid, false)]
    [InlineData(0, true)] // UI Automation did not say: refused while the policy denies anything
    public void LeavesOut_AnElementWithNoWindowOfItsOwn_ByTheProcessReadWithIt(int processId, bool leftOut)
    {
        var foreign = UiaTargets.ForeignElementsOf(_windows.Object, _policy, Handle);

        foreign.LeavesOut(Read(processId)).Should().Be(leftOut);
        foreign.LeftOutCount.Should().Be(leftOut ? 1 : 0);
    }

    [Fact]
    public void LeavesOut_CountsAnElementOnce_HoweverOftenItIsCheckedOrRead()
    {
        // Arrange - the same element, read twice (a new node each time, the same runtime id)
        var foreign = UiaTargets.ForeignElementsOf(_windows.Object, _policy, Handle);
        var element = Read(DeniedPid);

        // Act
        foreign.LeavesOut(element).Should().BeTrue();
        foreign.LeavesOut(element, DeniedPid).Should().BeTrue();
        foreign.LeavesOut(element with { Node = new FakeUiaNode("Text", "Recovery code") }).Should().BeTrue();

        // Assert
        foreign.LeftOutCount.Should().Be(1);
        foreign.LeftOutWarning(null, "x").Should().StartWith("1 element of window");
    }

    [Fact]
    public void LeavesOut_AnElementBuiltWithoutItsProcess_IsCheckedByItsOwnWindow_ElseAllowedWithTheWindow()
    {
        var foreign = UiaTargets.ForeignElementsOf(_windows.Object, _policy, Handle);

        foreign.LeavesOut(Read(null, VaultWindow)).Should().BeTrue();
        foreign.LeavesOut(Read(null)).Should().BeFalse();
        foreign.LeavesOut(Read(null), DeniedPid).Should().BeTrue("its identity names a process the policy refuses");
    }

    [Fact]
    public void Refusal_IsGiven_WhenTheProcessReadWithIt_OrTheOneItsIdentityNames_IsRefused()
    {
        var foreign = UiaTargets.ForeignElementsOf(_windows.Object, _policy, Handle);

        foreign.Refusal(Read(AppPid), DeniedPid).Should().Contain("KeePass").And.Contain("Nothing was read");
        foreign.Refusal(Read(DeniedPid), AppPid).Should().Contain("KeePass");
        foreign.Refusal(Read(AppPid), AppPid).Should().BeNull();
        foreign.Refusal(Read(AllowedPid)).Should().BeNull();
    }

    [Fact]
    public async Task Find_DetailMinimal_LeavesOutAMatchOfADeniedProcess_WithNoWindowOfItsOwn()
    {
        // Arrange - find_ui_elements' default detail, on a page the password manager shows inside the window
        _window.With(new FakeUiaNode("Text", "Inbox") { ProcessId = AppPid }, PageText());

        // Act
        var result = await Search(_policy).FindAsync(new ElementQuery(Handle, ControlTypes: ["Text"], Timeout: Timeout));

        // Assert - left out with nothing more of it read than the search's own read (the provider's answer), and said
        result.Success.Should().BeTrue(result.Error);
        result.Elements!.Select(e => e.Name).Should().Equal("Inbox");
        result.MatchCount.Should().Be(1);
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        result.Warning.Should().Contain("KeePass").And.Contain("left out of the matches");
        result.PolicyLeftOut.Should().Be(new PolicyLeftOutMatches(1, result.Warning!, OtherwiseComplete: true));
        _tree.Reads.Where(r => r.EndsWith(":Recovery code 4829-1337", StringComparison.Ordinal))
            .Should().Equal("Read:Recovery code 4829-1337");
        JsonSerializer.Serialize(result).Should().NotContain("4829").And.NotContain("PolicyLeftOut");
    }

    [Fact]
    public async Task Find_DetailStandard_OfAMatchOfADeniedProcess_WhoseIdentityReadFailsOnce_LeavesItOut_WithItsValueUnread()
    {
        // Arrange - a provider that fails one call now and then (a page that is loading)
        var text = PageText();
        text.IdentityFailures = 1;
        _window.With(text);

        // Act
        var result = await Search(_policy).FindAsync(new ElementQuery(Handle, ControlTypes: ["Text"], Detail: ElementDetail.Standard,
            Timeout: Timeout));

        // Assert
        result.Elements.Should().BeEmpty();
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
        _tree.Reads.Should().NotContain(r => ReadsAValueOrText(r));
        JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    [Fact]
    public async Task Find_DetailStandard_OfAMatchNotReadWithItsProcess_WhoseIdentityReadFails_LeavesItsValueUnread()
    {
        // Arrange - an element read without its process (the fake's default): the identity the check reads fails once, and
        // is not read again, so the value is not read past a check that could not be made
        _window.With(new FakeUiaNode("Edit", "Notes") { ActionPatterns = ["Value"], Value = "maybe secret", IdentityFailures = 1 });

        // Act
        var result = await Search(_policy).FindAsync(new ElementQuery(Handle, ControlTypes: ["Edit"], Detail: ElementDetail.Standard,
            Timeout: Timeout));

        // Assert
        result.Elements!.Single().Value.Should().BeNull();
        _tree.Reads.Should().NotContain("Value:Notes");
        _tree.Reads.Count(r => r == "Identity:Notes").Should().Be(1);
    }

    [Fact]
    public async Task Find_WhenTheWorkerAbandonsTheDescription_CountsAMatchLeftOutOnce()
    {
        // Arrange - the description of the second match gets stuck (a provider that does not answer an ancestor's read) after
        // the first, of the password manager, was left out; the worker gives up and the matches are described again
        var stuckParent = new FakeUiaNode("Pane", "Toolbar") { ProcessId = AppPid };
        _window.With(
            new FakeUiaNode("Button", "Vault entry") { ProcessId = DeniedPid },
            stuckParent.With(new FakeUiaNode("Button", "Send") { ProcessId = AppPid }));
        using var stuck = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        stuckParent.OnRead = () =>
        {
            // Its read as the second match's ancestor, after the search's own read of it
            if (Interlocked.Increment(ref reads) == 2)
            {
                stuck.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var sut = new ElementSearchService(_tree, new AbandoningWorker(stuck), _registry, _windows.Object, policy: _policy);

        try
        {
            // Act
            var result = await sut.FindAsync(new ElementQuery(Handle, ControlTypes: ["Button"], Timeout: Timeout));

            // Assert
            result.Success.Should().BeTrue(result.Error);
            result.Elements!.Select(e => e.Name).Should().Equal("Send");
            result.MatchCount.Should().Be(1);
            result.Warning.Should().StartWith("1 element of window");
            result.PolicyLeftOut!.Count.Should().Be(1);
            result.PolicyLeftOut.OtherwiseComplete.Should().BeFalse();
            result.TruncationReason.Should().Be(ElementSearchTruncation.Timeout);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// Runs the read on a thread of its own and gives up once <paramref name="stuck"/> is set, as the real worker gives up on
    /// a read stuck past its deadline: the read is abandoned, and the caller gets a TimeoutException.
    /// </summary>
    private sealed class AbandoningWorker(ManualResetEventSlim stuck) : IUiaWorker
    {
        public async Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
        {
            var reading = Task.Run(() => read(cancellationToken));
            var gaveUp = Task.Run(() => stuck.Wait(TimeSpan.FromSeconds(10)));
            if (await Task.WhenAny(reading, gaveUp) == reading)
            {
                return await reading;
            }

            throw new TimeoutException("UI Automation did not answer in time.");
        }
    }

    [Fact]
    public async Task Find_SearchVirtualized_LeavesOutAnItemOfADeniedProcess_ThatTheListHasNotRealized()
    {
        // Arrange - an allowed list whose item 500 is served by the password manager
        var list = new FakeVirtualList(1000, realized: 30, i => $"Entry {i:D4}", AppPid);
        list.Items[500].ProcessId = DeniedPid;
        _window.With(list.List);

        // Act
        var result = await Search(_policy).FindAsync(new ElementQuery(Handle, Name: "Entry 0500", Timeout: Timeout) { SearchVirtualized = true });

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Elements.Should().BeEmpty();
        result.MatchCount.Should().Be(0);
        result.Virtualized!.ItemsFound.Should().Be(0);
        result.PolicyLeftOut!.Count.Should().Be(1);
        result.Warning.Should().Contain("KeePass");
    }

    [Fact]
    public async Task Find_SearchVirtualized_DoesNotAskAListOfADeniedProcess_ForItsItems_NorListIt()
    {
        // Arrange - the whole list is the password manager's, shown inside the window
        var list = new FakeVirtualList(1000, realized: 30, i => $"Entry {i:D4}", DeniedPid);
        _window.With(list.List);

        // Act
        var result = await Search(_policy).FindAsync(new ElementQuery(Handle, Name: "Entry 0500", Timeout: Timeout) { SearchVirtualized = true });

        // Assert - a part not searched, and said
        result.Success.Should().BeTrue(result.Error);
        result.Elements.Should().BeEmpty();
        list.Probes.Should().BeEmpty();
        result.Virtualized!.Containers.Should().BeEmpty();
        result.Virtualized.Note.Should().Contain("1 list of another process that the server's target policy refuses was not searched");
        result.TruncationReason.Should().Be(ElementSearchTruncation.PartsSkipped);
    }

    [Theory]
    [InlineData(ElementWaitCondition.Exists, null)]
    [InlineData(ElementWaitCondition.NameEquals, "x")]
    [InlineData(ElementWaitCondition.Enabled, null)]
    [InlineData(ElementWaitCondition.Gone, null)]
    public async Task Wait_ARef_OfAnElementOfADeniedProcess_WithNoWindowOfItsOwn_EndsAtOnce_UnderEveryCondition(
        ElementWaitCondition condition, string? expected)
    {
        // Arrange
        var textRef = Add(PageText());

        // Act
        var result = await Waits().WaitAsync(new ElementWaitRequest
        {
            Ref = textRef, Condition = condition, Expected = expected, TimeoutMs = 5000
        });

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain("KeePass");
        result.Polls.Should().Be(1);
        result.Element.Should().BeNull();
        JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    [Fact]
    public async Task Wait_CriteriaExists_NeverMatchesAnElementOfADeniedProcess_WithNoWindowOfItsOwn()
    {
        // Arrange
        var bodyRef = Add(new FakeUiaNode("Pane", "Body") { ProcessId = AppPid }.With(PageText()));

        // Act
        var result = await Waits().WaitAsync(new ElementWaitRequest
        {
            Criteria = new ElementQuery(nint.Zero, WithinRef: bodyRef, ControlTypes: ["Text"]), Condition = ElementWaitCondition.Exists,
            TimeoutMs = 0
        });

        // Assert
        result.Satisfied.Should().BeFalse();
        result.Element.Should().BeNull();
        result.LastObservedState.Should().Contain("PartsSkipped");
        JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    [Fact]
    public async Task Wait_RefValueEquals_OfAnElementNotReadWithItsProcess_NeverReadsTheValuePastAnIdentityReadThatFailed()
    {
        // Arrange - an element read without its process (the fake's default), whose identity read fails once
        var notes = new FakeUiaNode("Edit", "Notes") { ActionPatterns = ["Value"], Value = "maybe secret" };
        var notesRef = Add(notes);
        notes.IdentityFailures = 1;

        // Act - one check
        var result = await Waits().WaitAsync(new ElementWaitRequest
        {
            Ref = notesRef, Condition = ElementWaitCondition.ValueEquals, Expected = "x", TimeoutMs = 0
        });

        // Assert
        result.Satisfied.Should().BeFalse();
        _tree.Reads.Should().NotContain("Value:Notes");
        _tree.Reads.Count(r => r == "Identity:Notes").Should().Be(1);
        JsonSerializer.Serialize(result).Should().NotContain("maybe secret");
    }

    [Fact]
    public async Task ReadTable_ACellOfADeniedProcess_WithNeitherTheValuePatternNorAWindowOfItsOwn_IsNull()
    {
        // Arrange - its name would be its text
        var listRef = Add(new FakeUiaNode("List", "Messages") { ProcessId = AppPid }.With(
            new FakeUiaNode("ListItem", "Row 1") { ProcessId = AppPid },
            new FakeUiaNode("ListItem", "Vault 4829-1337") { ProcessId = DeniedPid }));

        // Act
        var result = await Reads().ReadTableAsync(new ElementTableRequest(listRef, 0, 50, 20, Timeout));

        // Assert
        result.Rows!.Select(r => r[0]).Should().Equal("Row 1", null);
        result.Warning.Should().StartWith("1 element of window").And.Contain("those cells are null");
        JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    [Fact]
    public async Task ReadTable_AHeaderOfADeniedProcess_IsNull_AndCountedOnce_ThoughTheCornerCheckReadsItToo()
    {
        // Arrange - a WinForms DataGridView's header row with no data rows: its first two headers are read to tell whether
        // the first is the corner above the row headers, then each header for its text
        var listRef = Add(new FakeUiaNode("Table", "Payments") { ProcessId = AppPid }.With(
            new FakeUiaNode("Custom", "Top Row") { ProcessId = AppPid }.With(
                new FakeUiaNode("Header", "Vault 4829") { ProcessId = DeniedPid, ActionPatterns = ["Value"], Value = "4829" },
                new FakeUiaNode("Header", "Amount") { ProcessId = AppPid })));

        // Act
        var result = await Reads().ReadTableAsync(new ElementTableRequest(listRef, 0, 50, 20, Timeout));

        // Assert
        result.Success.Should().BeTrue(result.Error);
        result.Headers.Should().Equal(null, "Amount");
        result.Warning.Should().StartWith("1 element of window");
        JsonSerializer.Serialize(result).Should().NotContain("4829");
    }

    #endregion

    #region Criteria whose only matches the policy refuses

    [Fact]
    public async Task ReadText_ByCriteria_MatchingOnlyAWindowOfADeniedProcess_IsTargetDeniedByPolicy_NotATimeout()
    {
        // Arrange - the vault's child window inside the window
        _window.With(new FakeUiaNode("Pane", "Vault") { ProcessId = DeniedPid, NativeWindowHandle = VaultWindow });

        // Act
        var result = await Reads().ReadTextAsync(new ElementTextRequest(null,
            new ElementQuery(Handle, Name: "Vault", MaxResults: 5, Timeout: Timeout), 0, 1000, Timeout));

        // Assert - the policy's refusal, with whose it is, rather than "give more time"
        result.ErrorCode.Should().Be(UIElementErrorCode.TargetDeniedByPolicy);
        result.Error.Should().Contain("match only an element of an app the server's target policy refuses").And.Contain("KeePass")
            .And.Contain("ask the user").And.NotContain("more time");
        _tree.Reads.Should().NotContain(r => ReadsAValueOrText(r));
    }

    [Fact]
    public async Task ReadText_ByCriteria_MatchingAnAllowedAndADeniedElement_IsAmbiguous_WithTheAllowedOneAsTheCandidate()
    {
        // Arrange
        _window.With(
            new FakeUiaNode("Edit", "Notes") { ProcessId = AppPid, ActionPatterns = ["Value"], Value = "Lunch" },
            new FakeUiaNode("Edit", "Notes") { ProcessId = DeniedPid, ActionPatterns = ["Value"], Value = "4829-1337" });

        // Act
        var result = await Reads().ReadTextAsync(new ElementTextRequest(null,
            new ElementQuery(Handle, Name: "Notes", MaxResults: 5, Timeout: Timeout), 0, 1000, Timeout));

        // Assert
        result.ErrorCode.Should().Be(UIElementErrorCode.AmbiguousElement);
        result.Error.Should().StartWith("2 elements match the criteria, 1 of them of an app the server's target policy refuses");
        result.Candidates.Should().ContainSingle().Which.Ref.Should().NotBeNull();
        _tree.Reads.Should().NotContain(r => ReadsAValueOrText(r));
    }

    private ElementActionTools ActionTools(Mock<IElementActionService> actions) =>
        new(actions.Object, Search(_policy), _registry, new Mock<IWindowService>().Object);

    [Fact]
    public async Task ElementAction_ByCriteria_MatchingOnlyAnElementOfADeniedProcess_IsTargetDeniedByPolicy_AndNothingIsDone()
    {
        // Arrange - the page's button, served by the password manager's process
        _window.With(new FakeUiaNode("Button", "Reveal") { ProcessId = DeniedPid, ActionPatterns = ["Invoke"] });
        var actions = new Mock<IElementActionService>();

        // Act
        var result = await ActionTools(actions).ElementAction(ElementActionKind.Invoke, handle: (long)Handle, name: "Reveal")
            .Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.TargetDeniedByPolicy));
        result.Error.Should().Contain("nothing was done").And.Contain("KeePass");
        actions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ElementAction_ByCriteria_MatchingAnAllowedAndADeniedElement_IsAmbiguous_AndNothingIsDone()
    {
        _window.With(
            new FakeUiaNode("Button", "Send") { ProcessId = AppPid, ActionPatterns = ["Invoke"] },
            new FakeUiaNode("Button", "Send") { ProcessId = DeniedPid, ActionPatterns = ["Invoke"] });
        var actions = new Mock<IElementActionService>();

        var result = await ActionTools(actions).ElementAction(ElementActionKind.Invoke, handle: (long)Handle, name: "Send")
            .Payload<ElementActionResult>();

        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.AmbiguousElement));
        result.Error.Should().StartWith("2 elements match the criteria, 1 of them of an app the server's target policy refuses");
        actions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region A page of a denied WebView2 that Chromium builds only once it is asked for

    private static readonly nint WebViewHost = 0x7510;
    private static readonly nint RenderWidget = 0x7511;

    /// <summary>The default deny list with the WebView2 runtime on it: process 778 is refused.</summary>
    private static readonly TargetPolicy DeniesWebView2 =
        new(new TargetPolicySettings([.. TargetPolicySettings.DefaultDeniedProcesses, "msedgewebview2"], null, "test"));

    /// <summary>
    /// The window hosts a WebView2 (process 778) whose render widget's pane holds <paramref name="page"/>. Chromium builds a
    /// page's tree only once a UI Automation client asks for the render widget's element, as the warm-up does: until then the
    /// pane is empty, and a search of the window finds none of the page's elements.
    /// </summary>
    private void HostsAWebView2PageNotBuiltYet(params FakeUiaNode[] page)
    {
        _windows.Setup(w => w.EnumChildWindows(Handle, It.IsAny<Func<nint, bool>>()))
            .Callback((nint _, Func<nint, bool> callback) =>
            {
                if (callback(WebViewHost))
                {
                    callback(RenderWidget);
                }
            });
        _windows.Setup(w => w.GetClassName(WebViewHost)).Returns("Chrome_WidgetWin_1");
        _windows.Setup(w => w.GetClassName(RenderWidget)).Returns(AccessibilityWarmup.RenderWidgetHostClass);
        _windows.Setup(w => w.IsWindowVisible(RenderWidget)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(RenderWidget)).Returns((uint)AllowedPid);
        var pane = new FakeUiaNode("Pane", "Chrome Legacy Window") { ProcessId = AllowedPid, FrameworkId = "Win32", NativeWindowHandle = RenderWidget };
        _window.With(new FakeUiaNode("Pane", "WebView2") { ProcessId = AllowedPid, NativeWindowHandle = WebViewHost }.With(pane));
        _tree.WithWindow(RenderWidget, pane);
        _tree.FromHandleFailure = handle =>
        {
            if (handle == RenderWidget && pane.Children.Count == 0)
            {
                pane.With(page);
            }

            return null;
        };
    }

    /// <summary>The page's button, served by the WebView2's process.</summary>
    private static FakeUiaNode PageButton(string name) =>
        new("Button", name) { ProcessId = AllowedPid, FrameworkId = "Chrome", ActionPatterns = ["Invoke"] };

    /// <summary>The search with the warm-up as the server wires it: one warm-up, given the target policy too.</summary>
    private ElementSearchService SearchAsTheServerWires(ITargetPolicy policy) =>
        new(_tree, _worker, _registry, _windows.Object,
            new AccessibilityWarmup(_tree, _windows.Object, new Mock<INativeProcessWrapper>().Object, wait: (_, _) => { }, policy: policy),
            policy: policy);

    [Fact]
    public async Task ElementAction_ByCriteria_MatchingAnAllowedElement_AndOneOfADeniedWebView2sPageNotBuiltYet_IsAmbiguous()
    {
        // Arrange - the search's own provider search reaches the page's elements anyway (and leaves its matches out): its
        // warm-up asks for the page, so the match there is found and said, rather than missed on the first call
        HostsAWebView2PageNotBuiltYet(PageButton("Send"));
        _window.With(new FakeUiaNode("Button", "Send") { ProcessId = AppPid, ActionPatterns = ["Invoke"] });
        var actions = new Mock<IElementActionService>();
        actions.Setup(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ElementActionResult(true, "invoke"));
        var tools = new ElementActionTools(actions.Object, SearchAsTheServerWires(DeniesWebView2), _registry, new Mock<IWindowService>().Object);

        // Act
        var result = await tools.ElementAction(ElementActionKind.Invoke, handle: (long)Handle, name: "Send").Payload<ElementActionResult>();

        // Assert
        result.ErrorCode.Should().Be(nameof(UIElementErrorCode.AmbiguousElement));
        result.Error.Should().StartWith("2 elements match the criteria, 1 of them of an app the server's target policy refuses")
            .And.Contain("msedgewebview2");
        actions.Verify(a => a.ActAsync(It.IsAny<ElementActionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Wait_Gone_IsNotMet_WhileADeniedWebView2sPageNotBuiltYet_HoldsAMatch()
    {
        // Arrange
        HostsAWebView2PageNotBuiltYet(PageButton("Send"));
        var windowWait = new Mock<IWindowWaitService>();
        windowWait.Setup(w => w.WaitForConditionAsync(It.IsAny<WindowConditionRequest>(), It.IsAny<IProgress<OperationProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WindowConditionResult
            {
                Satisfied = true, MatchCount = 1, ElapsedMs = 0, Window = TestDataFactory.CreateWindowInfo((int)Handle, "Inbox - Outlook", "outlook", AppPid)
            });
        var waits = new ElementWaitService(SearchAsTheServerWires(DeniesWebView2), _registry, windowWait.Object,
            _tree, _worker, _windows.Object, delay: (_, _) => Task.CompletedTask, policy: DeniesWebView2);

        // Act
        var result = await waits.WaitAsync(new ElementWaitRequest
        {
            Window = new WindowSelector { Handle = (long)Handle },
            Criteria = new ElementQuery(nint.Zero, Name: "Send", Scope: ElementSearchScope.Window),
            Condition = ElementWaitCondition.Gone,
            TimeoutMs = 0
        });

        // Assert - its first check saw the match it left out, so the element is not taken for gone
        result.Satisfied.Should().BeFalse();
        result.LastObservedState.Should().Contain("PartsSkipped");
    }

    #endregion
}
