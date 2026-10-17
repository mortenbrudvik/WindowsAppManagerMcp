using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Resources;

namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: the wam:// resources (roadmap M6.9) over MCP, through the production registrations with the contract services.
/// Each returns, as application/json, exactly the JSON of the tool it mirrors (get_monitors, find_windows with no filter,
/// get_layout) on every revision a client may negotiate, written with the relaxed encoder; a layout name no preset has is
/// the error the SDK sends for a URI no resource matches; a resource is served exactly while the tool it mirrors is.
/// </summary>
public class ResourceContractTests
{
    public static TheoryData<string?> Revisions => InProcessMcpServer.Revisions();

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task EveryResource_ReturnsTheJsonOfTheToolItMirrors(string? revision)
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services, revision: revision);

        foreach (var (uri, tool, arguments) in new (string, string, Dictionary<string, object?>?)[]
                 {
                     (DesktopResources.MonitorsUri, "get_monitors", null),
                     (DesktopResources.WindowsUri, "find_windows", null),
                     ("wam://layouts/layout-1", "get_layout", new() { ["name"] = "layout-1" }),
                 })
        {
            // Act
            var content = await ReadTextAsync(server, uri);
            var toolResult = await server.CallToolAsync(tool, arguments);

            // Assert
            content.Uri.Should().Be(uri);
            content.MimeType.Should().Be("application/json");
            content.Text.Should().Be(toolResult.Content.OfType<TextContentBlock>().Single().Text,
                "{0} returns what {1} returns, through the same mapper ({2})", uri, tool, revision);
        }
    }

    [Fact]
    public async Task TheWindowsResource_AsksTheWindowService_WhatFindWindowsWithNoArgumentAsks()
    {
        // The fakes return the same windows for any query, so compare the queries themselves (WindowQuery is a record)
        using var services = new ContractServices(ContractMode.Typical);
        var queries = new List<WindowQuery>();
        services.Windows.Setup(w => w.FindWindows(Capture.In(queries))).Returns(TypicalDesktop.Windows);
        await using var server = await StartAsync(services);

        await ReadTextAsync(server, DesktopResources.WindowsUri);
        await server.CallToolAsync("find_windows");

        queries.Should().HaveCount(2);
        queries[0].Should().Be(queries[1]);
    }

    [Fact]
    public async Task TheWindowsResource_ListsADeniedAppsWindow_AsFindWindowsDoes()
    {
        // The target policy guards reading a window's content and acting on it; the window lists name it (TOOLS.md)
        using var services = new ContractServices(ContractMode.Typical);
        var keePass = TypicalDesktop.Notepad with { Handle = 0x5A0E10, Title = "Database.kdbx - KeePass", ProcessName = "KeePass", ProcessId = 7_100 };
        services.Windows.Setup(w => w.FindWindows(It.IsAny<WindowQuery>())).Returns([keePass, .. TypicalDesktop.Windows]);
        await using var server = await StartAsync(services);

        var windows = JsonNode.Parse((await ReadTextAsync(server, DesktopResources.WindowsUri)).Text)!.AsArray();

        windows.Select(w => (string?)w!["processName"]).Should().Contain("KeePass");
        windows.Should().HaveCount(TypicalDesktop.Windows.Count + 1);
    }

    [Fact]
    public async Task ALayout_WithANonAsciiName_RoundTripsWithoutEscapes()
    {
        // Arrange - the URI carries the name percent-encoded, and the JSON keeps the text readable (the relaxed encoder)
        using var services = new ContractServices(ContractMode.Typical);
        const string name = "Arbeid på kontoret – 作業";
        services.Layouts.Setup(l => l.GetPreset(name)).Returns(TypicalDesktop.Layouts[0] with { Name = name });
        await using var server = await StartAsync(services);

        // Act
        var content = await ReadTextAsync(server, "wam://layouts/" + Uri.EscapeDataString(name));

        // Assert
        content.Text.Should().Contain($"\"name\":\"{name}\"");
        services.Layouts.Verify(l => l.GetPreset(name), Times.Once());
    }

    [Theory]
    [MemberData(nameof(Revisions))]
    public async Task ALayoutNameNoPresetHas_IsTheErrorOfAUriNoResourceMatches(string? revision)
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        services.Layouts.Setup(l => l.GetPreset("nope")).Returns((LayoutPreset?)null);
        await using var server = await StartAsync(services, revision: revision);

        // Act
        var missingPreset = await ReadErrorAsync(server, "wam://layouts/nope");
        var missingResource = await ReadErrorAsync(server, "wam://nothing-here");

        // Assert
        missingPreset.ErrorCode.Should().Be(missingResource.ErrorCode, "a client tells both apart from a failed read the same way ({0})", revision);
        missingPreset.ErrorCode.Should().Be(revision == null ? McpErrorCode.InvalidParams : McpErrorCode.ResourceNotFound);
        missingPreset.Message.Should().Contain("Layout preset 'nope' was not found. Call list_layouts to see the available presets.");
    }

    [Fact]
    public async Task AnUnusableLayoutsFolder_IsAnErrorWithItsMessage()
    {
        using var services = new ContractServices(ContractMode.Typical);
        services.Layouts.Setup(l => l.GetStorageError()).Returns("The layouts folder cannot be written");
        await using var server = await StartAsync(services);

        var error = await ReadErrorAsync(server, "wam://layouts/layout-1");

        error.Message.Should().Contain("The layouts folder cannot be written");
    }

    [Fact]
    public async Task AnUnexpectedException_IsAnErrorThatNamesIt_AndEveryReadIsLogged()
    {
        // Arrange - not the SDK's generic error, and one call-log line per read, naming the URI
        using var services = new ContractServices(ContractMode.Typical);
        services.Monitors.Setup(m => m.GetAllMonitors()).Throws(new InvalidOperationException("No monitors found"));
        await using var server = await StartAsync(services);

        // Act
        var error = await ReadErrorAsync(server, DesktopResources.MonitorsUri);
        await ReadTextAsync(server, DesktopResources.WindowsUri);

        // Assert
        error.Message.Should().Contain("InvalidOperationException: No monitors found");
        var lines = server.Logs.Entries.Where(e => e.Category == ToolErrorReporting.CallLoggerCategory).Select(e => e.Message).ToList();
        lines.Should().Contain(line => line.StartsWith("Resource read 'wam://monitors'") && line.Contains("unhandled System.InvalidOperationException"));
        lines.Should().Contain(line => line.StartsWith("Resource read 'wam://windows'") && line.EndsWith(": ok"));
        server.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task ACancellationThatIsNotTheRequests_IsLoggedAsCancelled_NotAsAFailure()
    {
        // Arrange - a wait inside the read gives up (its own token, not the client's): not a failure of the server
        using var services = new ContractServices(ContractMode.Typical);
        services.Monitors.Setup(m => m.GetAllMonitors()).Throws(new OperationCanceledException("A wait was given up"));
        await using var server = await StartAsync(services);

        // Act
        var error = await ReadErrorAsync(server, DesktopResources.MonitorsUri);

        // Assert
        // The SDK answers a rethrown cancellation it did not ask for with its generic error; the filter adds no failure of its own
        error.Message.Should().NotContain("Resource read 'wam://monitors' failed").And.NotContain(nameof(OperationCanceledException));
        var lines = server.Logs.Entries.Where(e => e.Category == ToolErrorReporting.CallLoggerCategory).Select(e => e.Message).ToList();
        lines.Should().Contain(line => line.StartsWith("Resource read 'wam://monitors'") && line.EndsWith(": cancelled"));
        lines.Should().NotContain(line => line.Contains("unhandled"));
        server.Logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Error && e.Category == ToolErrorReporting.CallLoggerCategory);
    }

    [Fact]
    public async Task AUriWithLineBreaks_IsLoggedOnOneLine()
    {
        // Arrange - the URI is the client's text: a line break in it must not start a forged call-log line
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);
        const string forged = "Resource read 'wam://monitors' (request 7) finished in 1 ms: ok";

        // Act
        await ReadErrorAsync(server, "wam://nothing\r\n" + forged + "\n" + forged + "\r" + forged);

        // Assert
        var lines = server.Logs.Entries.Where(e => e.Category == ToolErrorReporting.CallLoggerCategory).Select(e => e.Message).ToList();
        lines.Should().ContainSingle(line => line.StartsWith("Resource read 'wam://nothing " + forged + " " + forged + " " + forged + "'"));
        lines.Should().NotContain(line => line.Contains('\r') || line.Contains('\n'));
    }

    [Fact]
    public async Task LayoutNames_CompleteInTheLayoutTemplate()
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        await using var server = await StartAsync(services);

        // Act
        var layouts = await CompleteAsync(server, DesktopResources.LayoutUriTemplate, "LAYOUT-");
        var none = await CompleteAsync(server, DesktopResources.LayoutUriTemplate, "work");
        var otherTemplate = await CompleteAsync(server, "wam://other/{name}", "layout");

        // Assert
        layouts.Values.Should().Equal(TypicalDesktop.Layouts.Select(l => l.Name));
        layouts.Total.Should().Be(TypicalDesktop.Layouts.Count);
        none.Values.Should().BeEmpty();
        otherTemplate.Values.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, null, null, new[] { DesktopResources.MonitorsUri, DesktopResources.WindowsUri, DesktopResources.LayoutUriTemplate })]
    [InlineData(null, null, "1", new[] { DesktopResources.MonitorsUri, DesktopResources.WindowsUri, DesktopResources.LayoutUriTemplate })]
    [InlineData("-monitors", null, null, new[] { DesktopResources.WindowsUri, DesktopResources.LayoutUriTemplate })]
    [InlineData("-layouts,-windows", null, null, new[] { DesktopResources.MonitorsUri })]
    [InlineData(null, "find_windows", null, new[] { DesktopResources.MonitorsUri, DesktopResources.LayoutUriTemplate })]
    [InlineData(null, "layouts", null, new[] { DesktopResources.MonitorsUri, DesktopResources.WindowsUri })]
    [InlineData("server", null, null, new string[0])]
    public async Task AResource_IsServedExactlyWhileTheToolItMirrorsIs(string? toolsets, string? exclude, string? readOnly, string[] expected)
    {
        // Arrange
        using var services = new ContractServices(ContractMode.Typical);
        var selection = Selection(toolsets, exclude, readOnly);
        await using var server = await StartAsync(services, selection);

        // Act
        var resources = (await server.Client.ListResourcesAsync()).Select(r => r.Uri);
        var templates = (await server.Client.ListResourceTemplatesAsync()).Select(t => t.UriTemplate);

        // Assert
        resources.Concat(templates).Should().BeEquivalentTo(expected);
        if (!expected.Contains(DesktopResources.MonitorsUri))
        {
            // A resource that is not served is a URI no resource matches, not a failed read: the code of an unknown URI on
            // the client's default revision (2026-07-28), even when no resource at all is served
            (await ReadErrorAsync(server, DesktopResources.MonitorsUri)).ErrorCode.Should().Be(McpErrorCode.InvalidParams);
            (await ReadErrorAsync(server, "wam://nothing-here")).ErrorCode.Should().Be(McpErrorCode.InvalidParams);
        }

        if (!expected.Contains(DesktopResources.LayoutUriTemplate))
        {
            (await CompleteAsync(server, DesktopResources.LayoutUriTemplate, "")).Values.Should().BeEmpty(
                "a template that is not served completes nothing");
        }
    }

    [Fact]
    public void TheCatalog_NamesEveryResource_AndEachMirrorsAReadOnlyToolOfTheServer()
    {
        var attributes = typeof(DesktopResources).GetMethods()
            .Select(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerResourceAttribute), false))
            .SelectMany(a => a.Cast<ModelContextProtocol.Server.McpServerResourceAttribute>())
            .Select(a => a.UriTemplate)
            .ToList();

        ResourcesAndPrompts.Resources.Select(r => r.UriTemplate).Should().BeEquivalentTo(attributes);
        ResourcesAndPrompts.Resources.Should().OnlyContain(r => r.UriTemplate!.StartsWith(ResourcesAndPrompts.Scheme + "://"));
        foreach (var resource in ResourcesAndPrompts.Resources)
        {
            ServerToolCatalog.Tools.Should().ContainKey(resource.Mirrors);
            var annotation = (ModelContextProtocol.Server.McpServerToolAttribute)ServerToolCatalog.Tools[resource.Mirrors]
                .GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Single();
            annotation.ReadOnly.Should().BeTrue("{0} mirrors {1}, which only reads", resource.UriTemplate, resource.Mirrors);
        }
    }

    internal static ToolsetSelection Selection(string? toolsets, string? exclude, string? readOnly) =>
        ToolsetSelection.FromEnvironment(name => name switch
        {
            ToolsetSelection.ToolsetsVariable => toolsets,
            ToolsetSelection.ExcludeToolsVariable => exclude,
            ToolsetSelection.ReadOnlyVariable => readOnly,
            _ => null
        }, Toolsets.Server);

    private static Task<InProcessMcpServer> StartAsync(ContractServices services, ToolsetSelection? selection = null, string? revision = null) =>
        InProcessMcpServer.StartAsync(
            [],
            builder => builder.AddWindowsAppManagerTools(selection),
            clientOptions: InProcessMcpServer.WithRevision(null, revision),
            configureServices: services.Register);

    private static async Task<TextResourceContents> ReadTextAsync(InProcessMcpServer server, string uri)
    {
        using var timeout = new CancellationTokenSource(InProcessMcpServer.DefaultCallTimeout);
        var result = await server.Client.ReadResourceAsync(uri, cancellationToken: timeout.Token);
        return result.Contents.Should().ContainSingle().Which.Should().BeOfType<TextResourceContents>().Subject;
    }

    private static async Task<McpProtocolException> ReadErrorAsync(InProcessMcpServer server, string uri)
    {
        using var timeout = new CancellationTokenSource(InProcessMcpServer.DefaultCallTimeout);
        var read = () => server.Client.ReadResourceAsync(uri, cancellationToken: timeout.Token).AsTask();
        return (await read.Should().ThrowAsync<McpProtocolException>()).Which;
    }

    private static async Task<Completion> CompleteAsync(InProcessMcpServer server, string uriTemplate, string typed)
    {
        using var timeout = new CancellationTokenSource(InProcessMcpServer.DefaultCallTimeout);
        var result = await server.Client.CompleteAsync(
            new CompleteRequestParams
            {
                Ref = new ResourceTemplateReference { Uri = uriTemplate },
                Argument = new Argument { Name = "name", Value = typed }
            },
            timeout.Token);
        return result.Completion;
    }
}
