using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The [McpServerTool] methods of the server assembly, found by reflection the way
/// WithTools&lt;T&gt;() finds them, and the tool names the server's production registration
/// serves. Nothing is invoked.
/// </summary>
public static class ServerToolCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, MethodInfo>> LazyTools = new(Discover);
    private static readonly Lazy<IReadOnlyList<string>> LazyServedToolNames = new(DiscoverServedToolNames);

    /// <summary>Tool methods by tool name, from the [McpServerToolType] classes of the server assembly.</summary>
    public static IReadOnlyDictionary<string, MethodInfo> Tools => LazyTools.Value;

    /// <summary>
    /// The names of the tools the server really serves: the tool collection that
    /// <see cref="ServiceRegistration.AddWindowsAppManagerTools"/> (used by Program.cs) builds, which
    /// is what tools/list returns. <see cref="Tools"/> must list exactly these: a class registered with
    /// WithTools&lt;T&gt;() but missing [McpServerToolType] is served but not in <see cref="Tools"/>,
    /// and an attributed class that is never registered is in <see cref="Tools"/> but not served.
    /// </summary>
    public static IReadOnlyList<string> ServedToolNames => LazyServedToolNames.Value;

    /// <summary>The tool-level [Description] text that tools/list sends to clients.</summary>
    public static string Description(string toolName) =>
        Tools[toolName].GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;

    /// <summary>Parameters the client supplies; the SDK binds CancellationToken itself.</summary>
    public static IEnumerable<ParameterInfo> ClientParameters(MethodInfo method) =>
        method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken));

    /// <summary>Every tool and parameter [Description], labelled "tool" or "tool.parameter".</summary>
    public static IEnumerable<(string Source, string Text)> AllDescriptions()
    {
        foreach (var (name, method) in Tools)
        {
            yield return (name, Description(name));
            foreach (var parameter in method.GetParameters())
            {
                var text = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (text != null)
                {
                    yield return ($"{name}.{parameter.Name}", text);
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, MethodInfo> Discover()
    {
        // Mirrors WithTools<T>(): public and non-public, instance and static methods on [McpServerToolType] types.
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        return typeof(InputSimulationTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(t => t.GetMethods(flags))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attribute != null)
            .ToDictionary(x => x.Attribute!.Name ?? x.Method.Name, x => x.Method);
    }

    private static IReadOnlyList<string> DiscoverServedToolNames()
    {
        // The production registrations, without a transport. Building the tool collection only
        // reflects over the tool methods: no service, tool class or layout storage is created.
        var services = new ServiceCollection();
        services.AddWindowsAppManagerServices(() =>
            throw new InvalidOperationException("The tool catalog must not create the layout storage."));
        services.AddMcpServer().AddWindowsAppManagerTools();

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection
            ?? throw new InvalidOperationException("The production registration has no tool collection.");
        return tools.Select(tool => tool.ProtocolTool.Name).ToList();
    }
}
