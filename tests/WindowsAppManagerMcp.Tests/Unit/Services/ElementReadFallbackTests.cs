using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// read_element_text and read_table when a pattern read fails: the text of the next source is still returned, as documented,
/// with a warning that names the pattern and the error, since the name it falls back to can be a label or a description
/// rather than the element's text. And read_element_text's own time for the read after its criteria search. Over an
/// in-memory UI Automation tree; nothing reads the real desktop.
/// </summary>
public class ElementReadFallbackTests
{
    private static readonly nint Handle = 0x7600;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeUiaTree _tree = new();
    private readonly InlineUiaWorker _worker = new();
    private readonly Mock<INativeWindowWrapper> _windows = new();
    private readonly ElementRegistry _registry;
    private readonly FakeUiaNode _window = new("Window", "Ledger");

    public ElementReadFallbackTests()
    {
        _windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns(300u);
        _windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        _registry = new ElementRegistry(_tree, _worker, _windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        _tree.WithWindow(Handle, _window);
    }

    private ElementReadService CreateSut(IUiaWorker? worker = null, IElementSearchService? search = null) =>
        new(_tree, worker ?? _worker, _registry, search ?? new ElementSearchService(_tree, _worker, _registry, _windows.Object),
            _windows.Object);

    private string Add(FakeUiaNode element)
    {
        _window.With(element);
        return _registry.Register(Handle, _tree.ReadElement(element), new ElementOrigin(_registry.NextGeneration()))!;
    }

    private Task<ElementTextResult> Read(string elementRef) =>
        CreateSut().ReadTextAsync(new ElementTextRequest(elementRef, null, 0, 1000, Timeout));

    private static COMException ProviderFailure() => new("The provider failed (0x80040201).");

    #region read_element_text

    [Fact]
    public async Task ReadText_WhenEveryPatternFails_ReturnsTheName_WithAWarningThatNamesEachFailure()
    {
        // Arrange - an amount field whose name is its label
        var elementRef = Add(new FakeUiaNode("Edit", "Amount")
        {
            ActionPatterns = ["Value"], OtherPatterns = UiaPatterns.Text, ValueException = ProviderFailure()
        }.WithPattern<IUiaTextPattern>(new FakeTextPattern("1250.00") { Failure = ProviderFailure() }));

        // Act
        var result = await Read(elementRef);

        // Assert - the documented fall-through, said
        result.Success.Should().BeTrue();
        result.Source.Should().Be(ElementTextSource.Name);
        result.Text.Should().Be("Amount");
        result.Warning.Should().Contain("the Text pattern failed (COMException: The provider failed (0x80040201).)")
            .And.Contain("the Value pattern failed (COMException: The provider failed (0x80040201).)")
            .And.Contain("label");
    }

    [Fact]
    public async Task ReadText_WhenTheValuePatternFails_ReturnsTheName_WithAWarning()
    {
        var elementRef = Add(new FakeUiaNode("Edit", "Amount") { ActionPatterns = ["Value"], ValueException = ProviderFailure() });

        var result = await Read(elementRef);

        result.Source.Should().Be(ElementTextSource.Name);
        result.Warning.Should().StartWith("The element has a pattern that should hold its text, but the Value pattern failed (COMException");
    }

    [Fact]
    public async Task ReadText_WhenTheTextPatternFails_ReturnsTheValue_WithAWarning()
    {
        var elementRef = Add(new FakeUiaNode("Document", "Intranet")
        {
            ActionPatterns = ["Value"], OtherPatterns = UiaPatterns.Text, Value = "https://intranet.example/"
        }.WithPattern<IUiaTextPattern>(new FakeTextPattern("Welcome") { Failure = new InvalidOperationException("Not supported") }));

        var result = await Read(elementRef);

        result.Source.Should().Be(ElementTextSource.Value);
        result.Text.Should().Be("https://intranet.example/");
        result.Warning.Should().StartWith("The Text pattern failed (InvalidOperationException: Not supported), so the text comes from the Value pattern");
    }

    [Fact]
    public async Task ReadText_WithoutAFailure_HasNoWarning()
    {
        var elementRef = Add(new FakeUiaNode("Text", "Saved"));

        var result = await Read(elementRef);

        result.Source.Should().Be(ElementTextSource.Name);
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task ReadText_OfAMovedRefWhosePatternFails_KeepsBothWarnings()
    {
        // Arrange - the status label was re-created, and its Value pattern now fails
        var status = new FakeUiaNode("Edit", "Status") { AutomationId = "Status" };
        _window.With(status);
        var statusRef = _registry.Register(Handle, _tree.ReadElement(status),
            new ElementOrigin(_registry.NextGeneration(), new ElementCriteria(AutomationId: "Status"), Unique: true))!;
        status.Vanish();
        _window.With(new FakeUiaNode("Edit", "Status") { AutomationId = "Status", ActionPatterns = ["Value"], ValueException = ProviderFailure() });

        // Act
        var result = await Read(statusRef);

        // Assert
        result.RefMoved.Should().BeTrue();
        result.Warning.Should().Contain($"Ref '{statusRef}' was moved").And.Contain("the Value pattern failed");
    }

    #endregion

    #region read_table

    [Fact]
    public async Task ReadTable_WhenACellsValuePatternFails_ReturnsItsName_WithAWarningThatSaysHowManyAndWhy()
    {
        // Arrange - a WinForms grid names its cells after their column and row
        var gridRef = Add(new FakeUiaNode("List", "Payments").With(
            new FakeUiaNode("ListItem", "Amount Row 0, Not sorted.") { ActionPatterns = ["Value"], Value = "1250.00" },
            new FakeUiaNode("ListItem", "Amount Row 1, Not sorted.") { ActionPatterns = ["Value"], ValueException = ProviderFailure() },
            new FakeUiaNode("ListItem", "Amount Row 2, Not sorted.") { ActionPatterns = ["Value"], ValueException = ProviderFailure() }));

        // Act
        var result = await CreateSut().ReadTableAsync(new ElementTableRequest(gridRef, 0, 50, 20, Timeout));

        // Assert - the documented fall-through, said
        result.Success.Should().BeTrue(result.Error);
        result.Rows!.Select(r => r[0]).Should().Equal("1250.00", "Amount Row 1, Not sorted.", "Amount Row 2, Not sorted.");
        result.Warning.Should().Contain("2 cells' Value pattern failed (first: the Value pattern failed (COMException: The provider failed")
            .And.Contain("description of the cell");
    }

    [Fact]
    public async Task ReadTable_WithoutAFailure_HasNoWarning()
    {
        var gridRef = Add(new FakeUiaNode("List", "Payments").With(
            new FakeUiaNode("ListItem", "Row 0") { ActionPatterns = ["Value"], Value = "1250.00" }));

        var result = await CreateSut().ReadTableAsync(new ElementTableRequest(gridRef, 0, 50, 20, Timeout));

        result.Warning.Should().BeNull();
    }

    #endregion

    #region read_element_text's read after its search

    /// <summary>
    /// A worker that runs a read on the calling thread, but, as the real worker does, gives up at once on a read whose deadline
    /// passed before it started.
    /// </summary>
    private sealed class DeadlineWorker : IUiaWorker
    {
        public List<bool> CancelledAtStart { get; } = [];

        public Task<T> RunAsync<T>(int targetProcessId, Func<CancellationToken, T> read, CancellationToken cancellationToken, TimeSpan grace)
        {
            CancelledAtStart.Add(cancellationToken.IsCancellationRequested);
            return cancellationToken.IsCancellationRequested
                ? Task.FromException<T>(new TimeoutException("UI Automation did not answer in time."))
                : Task.FromResult(read(cancellationToken));
        }
    }

    [Fact]
    public async Task ReadText_ByCriteria_AfterASearchThatUsedTheWholeTimeout_StillGetsTimeForTheRead()
    {
        // Arrange - a slow window: the search finds the one match only as the timeout passes
        var subject = new FakeUiaNode("Edit", "Subject") { AutomationId = "subject", ActionPatterns = ["Value"], Value = "Lunch on Friday" };
        var subjectRef = Add(subject);
        var search = new Mock<IElementSearchService>();
        search.Setup(s => s.FindAsync(It.IsAny<ElementQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (ElementQuery _, CancellationToken token) =>
            {
                try
                {
                    await Task.Delay(System.Threading.Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    // The search's deadline: it found its match just then
                }

                return new FindElementsResult(true, Handle, "Ledger", 1, false,
                    [new FoundElement(subjectRef, "Subject", "Edit", "subject", "Edit", null, null, true, false, ["Value"])], 1);
            });
        var worker = new DeadlineWorker();
        var timeout = TimeSpan.FromMilliseconds(300);

        // Act
        var result = await CreateSut(worker, search.Object).ReadTextAsync(
            new ElementTextRequest(null, new ElementQuery(Handle, AutomationId: "subject", MaxResults: 5, Timeout: timeout), 0, 1000, timeout));

        // Assert - the read had its own deadline, not the search's spent one
        result.Success.Should().BeTrue(result.Error);
        result.Text.Should().Be("Lunch on Friday");
        worker.CancelledAtStart.Should().Equal(false);
    }

    [Theory]
    [InlineData(5000, 500, 4500)] // most of the timeout left: all of it
    [InlineData(5000, 4900, 1000)] // a search that used most of it: at least a second
    [InlineData(5000, 9000, 1000)] // one that overran it
    [InlineData(300, 300, 300)] // a timeout shorter than a second: at most the timeout
    public void ReadTimeoutAfterSearch_IsWhatIsLeft_ButAtLeastASecond_OrTheTimeout(int timeoutMs, int elapsedMs, int expectedMs)
    {
        ElementReadService.ReadTimeoutAfterSearch(TimeSpan.FromMilliseconds(timeoutMs), TimeSpan.FromMilliseconds(elapsedMs))
            .Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    #endregion
}
