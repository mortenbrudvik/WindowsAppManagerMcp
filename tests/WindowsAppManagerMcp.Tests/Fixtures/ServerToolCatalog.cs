using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Tools;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// The [McpServerTool] methods of the server assembly, found by reflection the way
/// WithTools&lt;T&gt;() finds them. Nothing is invoked.
/// </summary>
public static class ServerToolCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, MethodInfo>> LazyTools = new(Discover);

    /// <summary>Tool methods by tool name.</summary>
    public static IReadOnlyDictionary<string, MethodInfo> Tools => LazyTools.Value;

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
}
