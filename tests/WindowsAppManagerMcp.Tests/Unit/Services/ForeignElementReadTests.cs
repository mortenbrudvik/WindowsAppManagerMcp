using System.Text.Json;

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

        // Assert - the search (detail minimal) cannot tell its process, but the read checks it before anything is read
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
}
