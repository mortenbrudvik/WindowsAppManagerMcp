using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>Calls the tool of one run_steps step (roadmap M6.2), as <see cref="StepRunner"/> needs it.</summary>
public interface IStepDispatcher
{
    /// <summary>The input schema the server serves for <paramref name="tool"/>; null when it does not serve the tool.</summary>
    JsonElement? InputSchemaOf(string tool);

    /// <summary>
    /// Calls <paramref name="tool"/> with <paramref name="arguments"/> as step <paramref name="stepNumber"/> (1 for the
    /// first) of the run, and returns its result as the agent would get it from a direct call.
    /// </summary>
    Task<CallToolResult> InvokeAsync(string tool, JsonObject arguments, int stepNumber, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a step as a tool call inside the server, from the context of the run_steps call, through the server's own
/// registered call-tool filters, so it gets exactly what a direct call of the tool gets (the S0b spike proved this on
/// ModelContextProtocol 2.2.0): its own audit line (roadmap M2.17) with the request id "&lt;run's id&gt;#&lt;step&gt;", its own
/// call-log lines, the refusal of an argument it does not declare (roadmap M3.F42), the error contract (roadmap M2.1), and
/// the input gate filter, which the run's lease passes (<see cref="Services.InputGate.Lease"/>). A tool the toolsets,
/// WAM_EXCLUDE_TOOLS or read-only mode leave out is not in the server's tool collection, so it is never served here
/// either. A step's own progress is not forwarded: its request carries no progress token, and the run reports its own.
/// </summary>
public sealed class McpStepDispatcher : IStepDispatcher
{
    private readonly RequestContext<CallToolRequestParams> _run;
    private readonly IReadOnlyList<McpRequestFilter<CallToolRequestParams, CallToolResult>> _filters;

    /// <param name="run">The context of the run_steps call, which the SDK hands the tool.</param>
    public McpStepDispatcher(RequestContext<CallToolRequestParams> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _run = run;

        // Outermost first: the order they were registered in (ServerFilterPipelineTests)
        _filters = run.Server.ServerOptions.Filters.Request.CallToolFilters.ToList();
    }

    /// <inheritdoc/>
    public JsonElement? InputSchemaOf(string tool) => Find(tool)?.ProtocolTool.InputSchema;

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The server does not serve <paramref name="tool"/>; the run checks first.</exception>
    public Task<CallToolResult> InvokeAsync(string tool, JsonObject arguments, int stepNumber, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var primitive = Find(tool) ?? throw new InvalidOperationException($"The server does not serve '{tool}'.");

        var parameters = new CallToolRequestParams
        {
            Name = tool,
            Arguments = arguments.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value, McpJsonUtilities.DefaultOptions))
        };
        var request = new JsonRpcRequest
        {
            Id = new RequestId($"{_run.JsonRpcRequest.Id}#{stepNumber}"),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
            Context = _run.JsonRpcRequest.Context
        };
        var context = new RequestContext<CallToolRequestParams>(_run.Server, request, parameters)
        {
            Services = _run.Services,
            MatchedPrimitive = primitive
        };

        McpRequestHandler<CallToolRequestParams, CallToolResult> handler = (call, token) => primitive.InvokeAsync(call, token);
        for (var i = _filters.Count - 1; i >= 0; i--)
        {
            handler = _filters[i](handler);
        }

        return handler(context, cancellationToken).AsTask();
    }

    private McpServerTool? Find(string tool) =>
        _run.Server.ServerOptions.ToolCollection is { } tools && tools.TryGetPrimitive(tool, out var primitive) ? primitive : null;
}
