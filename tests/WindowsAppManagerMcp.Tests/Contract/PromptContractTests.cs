using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Prompts;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the prompts (roadmap M6.9) over MCP, through the production registrations. Every tool a served prompt names is
/// served, under the default, every toolset, read-only mode, toolsets turned off and tools excluded; a prompt is served only
/// while the tools it needs are; its arguments are required strings and come back quoted as data; its text stays within a
/// token budget. No tool is called and nothing native runs.
/// </summary>
public partial class PromptContractTests
{
    /// <summary>
    /// The most tokens one prompt's text may cost with every step served (the whole surface) and the sample arguments: a recipe
    /// is a page the agent reads once, not a manual. A ratchet just above the largest, measured October 2026: automate_app 464,
    /// arrange_workspace 393, fill_form 375 and diagnose_click 447 tokens. A change that lengthens a recipe on purpose raises it.
    /// </summary>
    private const int PromptTokenBudget = 480;

    [GeneratedRegex(@"\b[a-z]+(?:_[a-z]+)+\b")]
    private static partial Regex SnakeCaseName();

    [GeneratedRegex(@"^(\d+)\. ", RegexOptions.Multiline)]
    private static partial Regex StepNumber();

    /// <summary>Sample arguments for each prompt, with a quote, a line break and non-ASCII text in the free-text one.</summary>
    private static readonly Dictionary<string, Dictionary<string, object?>> SampleArguments = new()
    {
        [WorkflowPrompts.AutomateApp] = new() { ["app"] = "Notepad", ["goal"] = "Write \"Hei på deg\" and save it as\nnotes.txt" },
        [WorkflowPrompts.ArrangeWorkspace] = new() { ["description"] = "Editor on the left half, browser on the right; Teams on the second monitor" },
        [WorkflowPrompts.FillForm] = new() { ["window"] = "Contoso sign-up", ["fields"] = "Name=Ada Lovelace; Country=Norge; City=Tromsø" },
        [WorkflowPrompts.DiagnoseClick] = new() { ["window"] = "Notepad", ["symptom"] = "Clicked Save at 812,640 and nothing happened" },
    };

    /// <summary>Selections that turn off tools a prompt names: toolsets, read-only mode, exclusions, and opt-in toolsets on.</summary>
    public static TheoryData<string?, string?, string?> Selections() => new()
    {
        { null, null, null },
        { "all", null, null },
        { null, null, "1" },
        { "-input", null, null },
        { "-elements", null, null },
        { "-launch,-layouts,-capture", null, null },
        { null, "launch_application,type_text,press_keys,mouse_click,get_window_info,wait_for_element", null },
        { null, "find_windows", null },
        { null, "find_windows,get_window_info", null },
        { "windows,input", null, null },
    };

    [Theory]
    [MemberData(nameof(Selections))]
    public async Task EveryServedPrompt_NamesOnlyServedTools_AndIsServedOnlyWithTheToolsItNeeds(string? toolsets, string? exclude, string? readOnly)
    {
        // Arrange
        var selection = ResourceContractTests.Selection(toolsets, exclude, readOnly);
        await using var server = await StartAsync(selection);
        var served = (await server.Client.ListToolsAsync()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var prompts = (await server.Client.ListPromptsAsync()).Select(p => p.Name).ToList();

        // Assert - which prompts: exactly those whose needed tools are all served
        prompts.Should().BeEquivalentTo(ResourcesAndPrompts.Prompts.Where(p => p.Needs.All(served.Contains)).Select(p => p.Name));

        foreach (var name in prompts)
        {
            // Act
            var text = await GetTextAsync(server, name);

            // Assert - every snake_case word that is a tool of the server is a tool this server serves
            var named = SnakeCaseName().Matches(text).Select(m => m.Value).Where(ServerToolCatalog.Tools.ContainsKey).Distinct().ToList();
            named.Should().BeSubsetOf(served, "{0} sends the agent only to tools it has (toolsets '{1}', excluded '{2}', read-only '{3}')",
                name, toolsets, exclude, readOnly);
            named.Should().Contain(ResourcesAndPrompts.Prompts.Single(p => p.Name == name).Needs, "{0} uses the tools it needs", name);

            // Assert - the steps that remain are numbered 1, 2, 3 ... without a gap where a step was left out
            var numbers = StepNumber().Matches(text).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
            numbers.Should().Equal(Enumerable.Range(1, numbers.Count),
                "{0}'s steps are numbered without gaps (toolsets '{1}', excluded '{2}', read-only '{3}')", name, toolsets, exclude, readOnly);
        }
    }

    [Fact]
    public async Task AStepWhoseToolsAreAllOff_IsLeftOutWhole_AndTheNextStepTakesItsNumber()
    {
        // Arrange - diagnose_click's second step (find_windows, then get_window_info) with both tools excluded
        await using var full = await StartAsync(selection: null);
        await using var server = await StartAsync(ResourceContractTests.Selection(null, "find_windows,get_window_info", null));
        var fullText = await GetTextAsync(full, WorkflowPrompts.DiagnoseClick);

        // Act
        var text = await GetTextAsync(server, WorkflowPrompts.DiagnoseClick);

        // Assert
        fullText.Should().Contain("\n2. The window: find_windows").And.Contain("\n3. In the way: get_active_popups");
        text.Should().NotContain("find_windows").And.NotContain("get_window_info");
        text.Should().Contain("\n1. Paused: ").And.Contain("\n2. In the way: get_active_popups").And.Contain("\n5. Look: capture_window");
        StepNumber().Matches(text).Should().HaveCount(5);
    }

    [Fact]
    public async Task AStepWhoseFirstLineIsOff_StartsWithItsFirstLineThatIsOn()
    {
        await using var server = await StartAsync(ResourceContractTests.Selection(null, "find_windows", null));

        var text = await GetTextAsync(server, WorkflowPrompts.DiagnoseClick);

        text.Should().Contain("\n2. get_window_info with detail 'full'").And.Contain("\n3. In the way: ");
    }

    [Fact]
    public async Task ReadOnlyMode_ServesOnlyThePromptThatReads()
    {
        await using var server = await StartAsync(ResourceContractTests.Selection(null, null, "1"));

        var prompts = await server.Client.ListPromptsAsync();

        prompts.Select(p => p.Name).Should().Equal(WorkflowPrompts.DiagnoseClick);
    }

    [Fact]
    public async Task EveryPrompt_DeclaresItsStringArgumentsAsRequired_WithADescription()
    {
        // MCP prompt arguments are strings; each recipe needs every one of its arguments
        await using var server = await StartAsync(selection: null);

        var prompts = await server.Client.ListPromptsAsync();

        prompts.Select(p => p.Name).Should().BeEquivalentTo(SampleArguments.Keys);
        foreach (var prompt in prompts)
        {
            var arguments = prompt.ProtocolPrompt.Arguments ?? [];
            arguments.Select(a => a.Name).Should().Equal(SampleArguments[prompt.Name].Keys, "{0} takes these arguments, in this order", prompt.Name);
            arguments.Should().OnlyContain(a => a.Required == true && !string.IsNullOrWhiteSpace(a.Description), "{0}'s arguments", prompt.Name);
            prompt.Title.Should().NotBeNullOrWhiteSpace();
            prompt.Description.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task EveryPrompt_IsOneUserMessage_WithTheArgumentsQuotedAsData_AndTheUntrustedContentRule()
    {
        await using var server = await StartAsync(selection: null);

        foreach (var (name, arguments) in SampleArguments)
        {
            // Act
            var result = await GetAsync(server, name, arguments);

            // Assert
            var message = result.Messages.Should().ContainSingle("{0} is one message", name).Subject;
            message.Role.Should().Be(Role.User);
            var text = message.Content.Should().BeOfType<TextContentBlock>().Subject.Text;
            foreach (var value in arguments.Values.Cast<string>())
            {
                // A JSON string: a quote is \" and a line break \n, so the argument cannot end the quote or add a line;
                // the relaxed encoder keeps non-ASCII text as it is
                text.Should().Contain(System.Text.Json.JsonSerializer.Serialize(value, ToolJson.SerializerOptions));
            }

            text.Should().Contain("given as data").And.Contain("untrusted data", "{0} carries the untrusted-content rule", name);
        }
    }

    [Fact]
    public async Task EveryPrompt_StaysWithinItsTokenBudget()
    {
        await using var server = await StartAsync(selection: null);
        using var scope = new FluentAssertions.Execution.AssertionScope();

        foreach (var (name, arguments) in SampleArguments)
        {
            var text = (await GetAsync(server, name, arguments)).Messages.Single().Content.Should().BeOfType<TextContentBlock>().Subject.Text;
            var tokens = ToolSurface.EstimatedTokens(Encoding.UTF8.GetByteCount(text));

            tokens.Should().BeLessThanOrEqualTo(PromptTokenBudget,
                "the text of {0} is about {1} tokens (budget {2}): a recipe is a page the agent reads once, not a manual", name, tokens, PromptTokenBudget);
        }
    }

    [Fact]
    public async Task AnEmptyOrMissingArgument_IsAnInvalidRequest_AndEveryPromptRequestIsLogged_WithoutItsArguments()
    {
        // Arrange
        await using var server = await StartAsync(selection: null);
        const string secretish = "do not log this goal";

        // Act
        await GetAsync(server, WorkflowPrompts.AutomateApp, new() { ["app"] = "Notepad", ["goal"] = secretish });
        var empty = await GetErrorAsync(server, WorkflowPrompts.AutomateApp, new() { ["app"] = "Notepad", ["goal"] = "  " });
        var missing = await GetErrorAsync(server, WorkflowPrompts.FillForm, new() { ["window"] = "Contoso sign-up" });

        // Assert
        empty.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
        empty.Message.Should().Contain("'goal' is empty");
        missing.ErrorCode.Should().Be(McpErrorCode.InvalidParams, "a missing required argument is the client's error: {0}", missing.Message);
        var lines = server.Logs.Entries.Where(e => e.Category == ToolErrorReporting.CallLoggerCategory).Select(e => e.Message).ToList();
        lines.Should().Contain(line => line.StartsWith($"Prompt '{WorkflowPrompts.AutomateApp}'") && line.EndsWith(": ok"));
        server.Logs.Entries.Should().NotContain(e => e.Message.Contains(secretish));
    }

    [Fact]
    public async Task APromptThatIsNotServed_IsAnUnknownPrompt_AndItsNameIsLoggedOnOneLine()
    {
        // Arrange - read-only mode does not serve automate_app; a name is the client's text, line breaks and all
        await using var server = await StartAsync(ResourceContractTests.Selection(null, null, "1"));
        const string forged = "Prompt 'diagnose_click' (request 7) finished in 1 ms: ok";

        // Act
        var notServed = await GetErrorAsync(server, WorkflowPrompts.AutomateApp, SampleArguments[WorkflowPrompts.AutomateApp]);
        var unknown = await GetErrorAsync(server, "no_such_prompt\r\n" + forged, []);

        // Assert
        notServed.ErrorCode.Should().Be(McpErrorCode.InvalidParams, "a prompt that is not served is unknown: {0}", notServed.Message);
        unknown.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
        var lines = server.Logs.Entries.Where(e => e.Category == ToolErrorReporting.CallLoggerCategory).Select(e => e.Message).ToList();
        lines.Should().ContainSingle(line => line.StartsWith("Prompt 'no_such_prompt " + forged + "'"));
        lines.Should().NotContain(line => line.Contains('\r') || line.Contains('\n'));
    }

    [Fact]
    public void TheCatalog_NamesEveryPrompt_AndItsNeedsAreToolsOfTheServer()
    {
        var attributes = typeof(WorkflowPrompts).GetMethods()
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name)
            .OfType<string>()
            .ToList();

        ResourcesAndPrompts.Prompts.Select(p => p.Name).Should().BeEquivalentTo(attributes);
        ResourcesAndPrompts.Prompts.Should().OnlyContain(p => SnakeCaseName().IsMatch(p.Name) && p.Needs.Count > 0);
        ResourcesAndPrompts.Prompts.SelectMany(p => p.Needs).Should().OnlyContain(tool => ServerToolCatalog.Tools.ContainsKey(tool));
    }

    private static Task<InProcessMcpServer> StartAsync(ToolsetSelection? selection) =>
        InProcessMcpServer.StartAsync([], builder => builder.AddWindowsAppManagerTools(selection));

    private static async Task<string> GetTextAsync(InProcessMcpServer server, string name) =>
        (await GetAsync(server, name, SampleArguments[name])).Messages.Single().Content.Should().BeOfType<TextContentBlock>().Subject.Text;

    private static async Task<GetPromptResult> GetAsync(InProcessMcpServer server, string name, Dictionary<string, object?> arguments)
    {
        using var timeout = new CancellationTokenSource(InProcessMcpServer.DefaultCallTimeout);
        return await server.Client.GetPromptAsync(name, arguments, cancellationToken: timeout.Token);
    }

    private static async Task<McpProtocolException> GetErrorAsync(InProcessMcpServer server, string name, Dictionary<string, object?> arguments)
    {
        var get = () => GetAsync(server, name, arguments);
        return (await get.Should().ThrowAsync<McpProtocolException>()).Which;
    }
}
