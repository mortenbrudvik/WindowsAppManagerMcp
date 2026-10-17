using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Prompts;
using WindowsAppManagerMcp.Resources;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>A resource of the server (roadmap M6.9) and the tool whose data it returns.</summary>
/// <param name="UriTemplate">Its URI, or its URI template ({name} for a part of the URI).</param>
/// <param name="Mirrors">The tool that returns the same data: the resource is served exactly while that tool is.</param>
public sealed record ServedResource(string UriTemplate, string Mirrors);

/// <summary>A prompt of the server (roadmap M6.9) and the tools its recipe cannot do without.</summary>
/// <param name="Name">The prompt's name.</param>
/// <param name="Needs">The tools the recipe is built on: the prompt is served only while every one of them is.</param>
public sealed record ServedPrompt(string Name, IReadOnlyList<string> Needs);

/// <summary>
/// The server's MCP resources and prompts (roadmap M6.9, owner decision D16): three read-only wam:// resources that return
/// what get_monitors, find_windows and get_layout return, and four prompts that write out the locate, act, verify loop of
/// the plugin skill for clients without it. Tools stay the primary API: nothing here acts, and tools/list is unchanged.
/// </summary>
/// <remarks>
/// <para>They follow the user's toolsets through the tools they rest on, not through toolsets of their own: a resource is
/// served exactly while the tool it mirrors is, and a prompt only while every tool it needs is (<see cref="Prompts"/>), so
/// WAM_TOOLSETS, WAM_EXCLUDE_TOOLS and WAM_READ_ONLY hide them as they hide those tools. A prompt also leaves out each step
/// whose tool is not served (<see cref="WorkflowPrompts"/>).</para>
/// <para>resources/read and prompts/get pass none of the call-tool filters (audit, error reporting, undeclared arguments,
/// input gate). They only read, as get_monitors and get_layout do, which are not audited either; this class logs each
/// request in the call log and turns an unexpected exception into an error that names it.</para>
/// </remarks>
public static class ResourcesAndPrompts
{
    /// <summary>The URI scheme of every resource of the server.</summary>
    public const string Scheme = "wam";

    /// <summary>The resources, in the order the docs list them.</summary>
    public static IReadOnlyList<ServedResource> Resources { get; } =
    [
        new(DesktopResources.MonitorsUri, "get_monitors"),
        new(DesktopResources.WindowsUri, "find_windows"),
        new(DesktopResources.LayoutUriTemplate, "get_layout"),
    ];

    /// <summary>
    /// The prompts, in the order the docs list them, and the tools each is built on. automate_app and fill_form act through
    /// element refs, so read-only mode and a server without the input toolset do not serve them; diagnose_click only reads.
    /// </summary>
    public static IReadOnlyList<ServedPrompt> Prompts { get; } =
    [
        new(WorkflowPrompts.AutomateApp, ["get_desktop_state", "find_ui_elements", "element_action"]),
        new(WorkflowPrompts.ArrangeWorkspace, ["find_windows", "set_window_bounds"]),
        new(WorkflowPrompts.FillForm, ["find_ui_elements", "element_action"]),
        new(WorkflowPrompts.DiagnoseClick, ["find_ui_elements", "get_element_at_point"]),
    ];

    /// <summary>
    /// Registers the resources, the prompts, the completion of layout names in wam://layouts/{name}, and the filter that logs
    /// each resource read and prompt request. <see cref="Toolsets.WithToolsets"/> later removes those whose tools are not served.
    /// </summary>
    public static IMcpServerBuilder AddWindowsAppManagerResourcesAndPrompts(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithRequestFilters(filters =>
            {
                filters.AddReadResourceFilter(next => async (request, cancellationToken) =>
                    await Reported(request, "Resource read", request.Params?.Uri, () => next(request, cancellationToken)));
                filters.AddGetPromptFilter(next => async (request, cancellationToken) =>
                    await Reported(request, "Prompt", request.Params?.Name, () => next(request, cancellationToken)));
            })
            .WithServerResources<DesktopResources>()
            .WithServerPrompts<WorkflowPrompts>()
            .WithCompleteHandler(CompleteAsync);
    }

    /// <summary>
    /// Removes the resources whose mirrored tool, and the prompts with a needed tool, that <paramref name="options"/> does not
    /// serve. Runs after the toolsets removed the tools the user's selection leaves out. A resource or prompt the catalog does
    /// not name is kept (contract tests check that it names every one).
    /// </summary>
    internal static void RemoveUnserved(McpServerOptions options)
    {
        var tools = options.ToolCollection?.Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal) ?? [];

        if (options.ResourceCollection is { } resources)
        {
            foreach (var resource in resources.ToArray())
            {
                var entry = Resources.FirstOrDefault(r => r.UriTemplate == resource.ProtocolResourceTemplate.UriTemplate);
                if (entry != null && !tools.Contains(entry.Mirrors))
                {
                    resources.Remove(resource);
                }
            }
        }

        if (options.PromptCollection is { } prompts)
        {
            foreach (var prompt in prompts.ToArray())
            {
                var entry = Prompts.FirstOrDefault(p => p.Name == prompt.ProtocolPrompt.Name);
                if (entry != null && !entry.Needs.All(tools.Contains))
                {
                    prompts.Remove(prompt);
                }
            }
        }
    }

    /// <summary>
    /// completion/complete: the layout names that start with what the user typed, for the name of wam://layouts/{name} while
    /// that resource is served; nothing for anything else (the prompts' arguments are free text).
    /// </summary>
    private static ValueTask<CompleteResult> CompleteAsync(RequestContext<CompleteRequestParams> request, CancellationToken cancellationToken)
    {
        const int maxValues = 100;
        var empty = new CompleteResult { Completion = new Completion { Values = [] } };
        if (request.Params is not { Ref: ResourceTemplateReference { Uri: DesktopResources.LayoutUriTemplate }, Argument.Name: "name" } complete ||
            request.Server.ServerOptions.ResourceCollection?.Any(r => r.ProtocolResourceTemplate.UriTemplate == DesktopResources.LayoutUriTemplate) != true ||
            request.Services?.GetService<ILayoutService>() is not { } layouts ||
            layouts.GetStorageError() != null)
        {
            return ValueTask.FromResult(empty);
        }

        var typed = complete.Argument.Value ?? string.Empty;
        var names = layouts.GetAllPresets()
            .Select(preset => preset.Name)
            .Where(name => name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ValueTask.FromResult(new CompleteResult
        {
            Completion = new Completion { Values = names.Take(maxValues).ToList(), Total = names.Count, HasMore = names.Count > maxValues }
        });
    }

    /// <summary>
    /// Runs one resource read or prompt request with one line in the call log (<see cref="ToolErrorReporting.CallLoggerCategory"/>):
    /// what was asked for (the resource's URI or the prompt's name, never a prompt's arguments), the duration and the outcome.
    /// The URI or name is the client's text, so its line breaks become spaces: it cannot start a log line of its own. A
    /// cancellation, the request's or another one (a wait given up), is logged as cancelled and rethrown, never as a failure
    /// of the server. Any other exception that is not an <see cref="McpException"/> is logged with its stack trace and sent
    /// as an error that names its type and message, instead of the SDK's generic one.
    /// </summary>
    private static async ValueTask<TResult> Reported<TParams, TResult>(
        RequestContext<TParams> request, string kind, string? target, Func<ValueTask<TResult>> next)
    {
        var logger = request.Services?.GetService<ILoggerFactory>()?.CreateLogger(ToolErrorReporting.CallLoggerCategory) ?? NullLogger.Instance;
        var requestId = request.JsonRpcRequest?.Id.ToString() ?? string.Empty;
        target = (target ?? "(none)").ReplaceLineEndings(" ");
        var stopwatch = Stopwatch.StartNew();
        void Finished(string outcome) => logger.LogInformation(
            "{Kind} '{Target}' (request {RequestId}) finished in {DurationMs} ms: {Outcome}",
            kind, target, requestId, (long)stopwatch.Elapsed.TotalMilliseconds, outcome);

        try
        {
            var result = await next().ConfigureAwait(false);
            Finished("ok");
            return result;
        }
        catch (OperationCanceledException)
        {
            Finished("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            var exception = ToolErrorReporting.Unwrap(ex);
            if (exception is OperationCanceledException)
            {
                Finished("cancelled");
                ExceptionDispatchInfo.Throw(exception);
            }

            if (exception is McpProtocolException protocol)
            {
                Finished($"protocolError {(int)protocol.ErrorCode}");
                throw exception == ex ? protocol : new McpProtocolException(protocol.Message, protocol, protocol.ErrorCode);
            }

            if (exception is McpException deliberate)
            {
                Finished("error");
                throw exception == ex ? deliberate : new McpException(deliberate.Message, deliberate);
            }

            // A prompt argument (or a URI template's part) missing or of the wrong type: the client's error, as MCP says
            if (ToolErrorReporting.IsArgumentBindingFailure(exception))
            {
                Finished("invalidParams");
                throw new McpProtocolException($"{kind} '{target}': invalid arguments. {exception.Message}", exception, McpErrorCode.InvalidParams);
            }

            logger.LogError(exception, "{Kind} '{Target}' failed with {ExceptionType}: {ExceptionMessage}",
                kind, target, exception.GetType().FullName, exception.Message);
            Finished($"unhandled {exception.GetType().FullName}");
            throw new McpException($"{kind} '{target}' failed: {exception.GetType().Name}: {exception.Message}", exception);
        }
    }
}
