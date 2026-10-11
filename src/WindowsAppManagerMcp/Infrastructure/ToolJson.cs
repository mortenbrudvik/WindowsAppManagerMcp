using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// How the server's tools bind their arguments and describe them in tools/list (roadmap M2.2). The SDK's defaults,
/// plus the <see cref="ToolEnumAttribute"/> enums: <see cref="ToolEnumConverterFactory"/> binds them strictly, and
/// the input schema lists their values (<c>"enum": [...]</c>), so an agent sees the valid values instead of guessing.
/// </summary>
/// <remarks>
/// The SDK's own string-enum converter would list the values too, but it matches custom names case-sensitively, takes
/// no aliases and accepts any number, so the server binds these enums itself. A custom converter hides a type's values
/// from the schema exporter, so <see cref="SchemaOptions"/> writes them back. <see cref="WithServerTools{T}"/>
/// registers a tool class with both, as WithTools&lt;T&gt;() would with the defaults.
/// </remarks>
public static class ToolJson
{
    /// <summary>The SDK's serializer options with <see cref="ToolEnumConverterFactory"/> first.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>The SDK's schema options, with the values of the <see cref="ToolEnumAttribute"/> enums added.</summary>
    public static AIJsonSchemaCreateOptions SchemaOptions { get; } = new() { TransformSchemaNode = ListToolEnumValues };

    /// <summary>
    /// Registers every [McpServerTool] method of <typeparamref name="T"/>, as WithTools&lt;T&gt;() does (public and
    /// non-public, static and instance methods; a new instance of <typeparamref name="T"/> from the request's services
    /// for every call of an instance method), but with <see cref="SerializerOptions"/> and <see cref="SchemaOptions"/>.
    /// </summary>
    public static IMcpServerBuilder WithServerTools<T>(this IMcpServerBuilder builder) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var method in ToolMethods(typeof(T)))
        {
            builder.Services.AddSingleton(services => CreateTool(method, typeof(T), services));
        }

        return builder;
    }

    /// <summary>
    /// Registers every [McpServerTool] method of <paramref name="target"/>, with <see cref="SerializerOptions"/> and
    /// <see cref="SchemaOptions"/>, as WithTools(target) would with the defaults. Tests use it to serve one tool class with
    /// its own services, bound as the server binds it.
    /// </summary>
    public static IMcpServerBuilder WithServerTools<T>(this IMcpServerBuilder builder, T target) where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);

        foreach (var method in ToolMethods(typeof(T)))
        {
            builder.Services.AddSingleton(services => method.IsStatic
                ? McpServerTool.Create(method, target: null, CreateOptions(services))
                : McpServerTool.Create(method, target, CreateOptions(services)));
        }

        return builder;
    }

    /// <summary>The tool of one [McpServerTool] method, with the server's options.</summary>
    public static McpServerTool CreateTool(MethodInfo method, Type toolType, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(toolType);

        return method.IsStatic
            ? McpServerTool.Create(method, target: null, CreateOptions(services))
            : McpServerTool.Create(method, request => CreateTarget(request.Services, toolType), CreateOptions(services));
    }

    private static McpServerToolCreateOptions CreateOptions(IServiceProvider? services) => new()
    {
        Services = services,
        SerializerOptions = SerializerOptions,
        SchemaCreateOptions = SchemaOptions
    };

    private static IEnumerable<MethodInfo> ToolMethods(Type toolType) =>
        toolType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.IsDefined(typeof(McpServerToolAttribute), inherit: false));

    private static object CreateTarget(IServiceProvider? services, Type toolType) =>
        services != null ? ActivatorUtilities.CreateInstance(services, toolType) : Activator.CreateInstance(toolType)!;

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);

        // Before the SDK's string-enum converter, which would otherwise bind these enums too
        options.Converters.Insert(0, new ToolEnumConverterFactory());
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Lists the allowed values of a <see cref="ToolEnumAttribute"/> enum (also as Nullable), or of a string marked
    /// with <see cref="ToolValuesAttribute"/>, as <c>"type": "string", "enum": [...]</c>, with null added for a nullable
    /// one. The description and default the schema already has are kept.
    /// </summary>
    private static JsonNode ListToolEnumValues(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        var type = context.TypeInfo.Type;
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var enumType = ToolEnumInfo.IsToolEnum(underlying)
            ? underlying
            : type == typeof(string) ? context.GetCustomAttribute<ToolValuesAttribute>()?.EnumType : null;
        if (enumType == null)
        {
            return schema;
        }

        var info = ToolEnumInfo.Get(enumType);
        var node = schema as JsonObject ?? [];
        var nullable = underlying != type || AllowsNull(node["type"]);

        node["type"] = nullable ? new JsonArray("string", "null") : "string";
        var values = new JsonArray([.. info.SchemaValues.Select(value => (JsonNode?)JsonValue.Create(value))]);
        if (nullable)
        {
            values.Add(null);
        }

        node["enum"] = values;
        return node;
    }

    private static bool AllowsNull(JsonNode? type) =>
        type is JsonArray types && types.Any(t => t?.GetValue<string>() == "null");
}
