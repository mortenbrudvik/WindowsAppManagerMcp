using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: read_element_text and read_table (roadmap M3.9) through the production tool and filter registrations: the
/// result's JSON shape, the redacted password field, and the largest result either can return, measured end to end with the
/// real read service over an in-memory UI Automation tree (no real window is read).
/// </summary>
public class ElementReadContractTests
{
    /// <summary>The most tokens one read may cost, whatever the element holds: about a third of the largest walk's budget.</summary>
    private const int MaxResultTokens = 16_000;

    private static readonly nint Handle = (nint)TypicalDesktop.NotepadHandle;

    private static async Task<CallToolResult> CallAsync(string tool, Dictionary<string, object?> arguments, Action<ContractServices>? arrange = null)
    {
        using var services = new ContractServices(ContractMode.Typical);
        arrange?.Invoke(services);
        await using var server = await InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(),
            configureServices: services.Register);
        return await server.CallToolAsync(tool, arguments);
    }

    private static string Text(CallToolResult result) => result.Content.OfType<TextContentBlock>().Single().Text;

    private static JsonElement Json(CallToolResult result) => JsonDocument.Parse(Text(result)).RootElement.Clone();

    private static int Tokens(CallToolResult result) =>
        (Encoding.UTF8.GetByteCount(Text(result)) + ToolPayloadBudgetTests.BytesPerToken - 1) / ToolPayloadBudgetTests.BytesPerToken;

    /// <summary>
    /// The real read service over <paramref name="element"/> in a fake window, wired into the contract services, and the ref
    /// the element has.
    /// </summary>
    private static (Action<ContractServices> Arrange, string Ref) RealReads(FakeUiaNode element)
    {
        var tree = new FakeUiaTree().WithWindow(Handle, new FakeUiaNode("Window", "Notes").With(element));
        var worker = new InlineUiaWorker();
        var windows = new Mock<INativeWindowWrapper>();
        windows.Setup(w => w.IsWindow(Handle)).Returns(true);
        windows.Setup(w => w.GetWindowThreadProcessId(Handle)).Returns((uint)TypicalDesktop.NotepadProcessId);
        windows.Setup(w => w.RespondsWithin(Handle, It.IsAny<uint>())).Returns(true);
        var registry = new ElementRegistry(tree, worker, windows.Object, ElementRegistry.DefaultCapacity, 1, TimeProvider.System);
        var elementRef = registry.Register(Handle, tree.ReadElement(element), new ElementOrigin(registry.NextGeneration())) ??
                         throw new InvalidOperationException("The element got no ref.");
        var reads = new ElementReadService(tree, worker, registry, new ElementSearchService(tree, worker, registry, windows.Object), windows.Object);

        return (services =>
        {
            services.ElementReads.Setup(r => r.ReadTextAsync(It.IsAny<ElementTextRequest>(), It.IsAny<CancellationToken>()))
                .Returns((ElementTextRequest request, CancellationToken ct) => reads.ReadTextAsync(request, ct));
            services.ElementReads.Setup(r => r.ReadTableAsync(It.IsAny<ElementTableRequest>(), It.IsAny<CancellationToken>()))
                .Returns((ElementTableRequest request, CancellationToken ct) => reads.ReadTableAsync(request, ct));
        }, elementRef);
    }

    [Fact]
    public async Task ReadElementText_TakesTheRefParameter_AndNamesTheSource()
    {
        // Act
        var result = await CallAsync("read_element_text", new() { ["ref"] = "w4120035e57", ["offset"] = 0 });

        // Assert
        result.IsError.Should().NotBe(true);
        var json = Json(result);
        json.GetProperty("ref").GetString().Should().Be("w4120035e57");
        json.GetProperty("source").GetString().Should().Be("Text");
        json.GetProperty("totalLength").GetInt32().Should().Be(TypicalDesktop.MessageLength);
        json.TryGetProperty("isPassword", out _).Should().BeFalse();
        json.TryGetProperty("nextOffset", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ReadElementText_OfAPasswordField_SucceedsWithoutText()
    {
        // Arrange
        var (arrange, elementRef) = RealReads(new FakeUiaNode("Edit", "Password") { ActionPatterns = ["Value"], IsPassword = true, Value = "hunter2" });

        // Act
        var result = await CallAsync("read_element_text", new() { ["ref"] = elementRef }, arrange);

        // Assert
        result.IsError.Should().NotBe(true);
        Text(result).Should().NotContain("hunter2");
        var json = Json(result);
        json.GetProperty("source").GetString().Should().Be("Redacted");
        json.GetProperty("isPassword").GetBoolean().Should().BeTrue();
        json.TryGetProperty("text", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ReadElementText_OfAHugeDocument_StaysWithinTheLargestResult()
    {
        // Arrange - a million characters of prose in lines. The SDK's JSON escapes the line breaks, and also quotes, <, >, &
        // and every non-ASCII character (six bytes each), so a text full of those costs more per character than this
        var document = string.Concat(Enumerable.Repeat("The release ships on Monday, after the last review of the notes.\r\n", 16_000));
        var (arrange, elementRef) = RealReads(
            new FakeUiaNode("Document", "Log") { OtherPatterns = UiaPatterns.Text }.WithPattern<IUiaTextPattern>(new FakeTextPattern(document)));

        // Act - asking for everything gets the limit
        var result = await CallAsync("read_element_text", new() { ["ref"] = elementRef, ["maxLength"] = 10_000_000 }, arrange);

        // Assert
        Json(result).GetProperty("text").GetString()!.Length.Should().Be(ElementReadService.MaxTextLength);
        Json(result).GetProperty("truncated").GetBoolean().Should().BeTrue();
        Tokens(result).Should().BeLessThanOrEqualTo(MaxResultTokens);
    }

    [Fact]
    public async Task ReadTable_ResultShape_NamesTheSourceAndTheReason()
    {
        var result = await CallAsync("read_table", new() { ["ref"] = "w4120035e61", ["startRow"] = 0, ["maxRows"] = 50 });

        result.IsError.Should().NotBe(true);
        var json = Json(result);
        json.GetProperty("source").GetString().Should().Be("Grid");
        json.GetProperty("truncationReason").GetString().Should().Be("MaxRows");
        json.GetProperty("rows").GetArrayLength().Should().Be(TypicalDesktop.TableRows);
        json.GetProperty("rows")[0].GetArrayLength().Should().Be(5);
        json.GetProperty("nextStartRow").GetInt32().Should().Be(TypicalDesktop.TableRows);
    }

    [Theory]
    [InlineData(2_000)] // long cells: the size limit ends the page
    [InlineData(0)] // empty cells: the most rows and columns, where the JSON around the cells costs the most
    public async Task ReadTable_OfAHugeGrid_StaysWithinTheLargestResult(int cellLength)
    {
        // Arrange - 1000 rows of 100 columns
        var cells = Enumerable.Range(0, 1000)
            .Select(_ => (IReadOnlyList<FakeUiaNode?>)Enumerable.Range(0, 100)
                .Select(_ => (FakeUiaNode?)new FakeUiaNode("DataItem", new string('z', cellLength)))
                .ToList())
            .ToList();
        var (arrange, elementRef) = RealReads(
            new FakeUiaNode("DataGrid", "Big") { OtherPatterns = UiaPatterns.Grid }.WithPattern<IUiaGridPattern>(new FakeGridPattern(cells)));

        // Act - asking for everything gets the limits
        var result = await CallAsync("read_table",
            new() { ["ref"] = elementRef, ["maxRows"] = 100_000, ["maxColumns"] = 100_000, ["timeoutMs"] = 30_000 }, arrange);

        // Assert
        var json = Json(result);
        json.GetProperty("rows").GetArrayLength().Should().BeLessThanOrEqualTo(ElementReadService.MaxRowsLimit);
        json.GetProperty("rows")[0].GetArrayLength().Should().Be(ElementReadService.MaxColumnsLimit);
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
        Tokens(result).Should().BeLessThanOrEqualTo(MaxResultTokens);
    }

    [Fact]
    public async Task ReadTable_WithoutARef_IsRefusedByTheSchema()
    {
        var result = await CallAsync("read_table", new() { ["startRow"] = 0 });

        result.IsError.Should().BeTrue();
    }
}
