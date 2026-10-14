using System.Reflection;
using System.Text.Encodings.Web;
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
/// <para>The SDK's own string-enum converter would list the values too, but it matches custom names case-sensitively, takes
/// no aliases and accepts any number, so the server binds these enums itself. A custom converter hides a type's values
/// from the schema exporter, so <see cref="SchemaOptions"/> writes them back. <see cref="WithServerTools{T}"/>
/// registers a tool class with both, as WithTools&lt;T&gt;() would with the defaults.</para>
/// <para>The results are written with the relaxed encoder (<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>), as
/// the error results, get_desktop_state and the audit log were: the SDK's default encoder writes every non-ASCII character,
/// and quotes, apostrophes, &lt;, &gt;, &amp;, + and `, as a six-byte \uXXXX escape, which the agent reads as such (the
/// tool's JSON is text inside the MCP message), so a page of Cyrillic or CJK text cost about six times its size. The text
/// goes to an MCP client over stdio, never into HTML, and the relaxed encoder still escapes quotes, backslashes, control
/// characters, the line and paragraph separators and every character outside the Basic Multilingual Plane, so the JSON
/// stays valid and on one line.</para>
/// </remarks>
public static class ToolJson
{
    /// <summary>
    /// The encoder of every tool result: <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> (see the remarks).
    /// </summary>
    public static JavaScriptEncoder ResultEncoder => JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>The SDK's serializer options with <see cref="ToolEnumConverterFactory"/> first and <see cref="ResultEncoder"/>.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>
    /// The SDK's own serializer options (<see cref="McpJsonUtilities.DefaultOptions"/>) with <see cref="ResultEncoder"/>: for
    /// the results the server writes itself without its enum converter (the capture metadata, the error results).
    /// </summary>
    public static JsonSerializerOptions SdkResultOptions { get; } = CreateSdkResultOptions();

    /// <summary>
    /// How many bytes <paramref name="text"/> takes in a tool result's JSON: UTF-8, with the escapes of
    /// <see cref="ResultEncoder"/> (two bytes for \", \\, \n, \r, \t, \b and \f, six for any other escaped character, so
    /// twelve for a character outside the Basic Multilingual Plane), without the quotes around it. read_element_text and
    /// read_table bound their results with it, so a page costs about as many tokens in any script.
    /// </summary>
    public static int StringBytes(ReadOnlySpan<char> text)
    {
        var bytes = 0;
        foreach (var c in text)
        {
            bytes += CharBytes(c);
        }

        return bytes;
    }

    /// <summary>The bytes one UTF-16 character takes in a tool result's JSON (<see cref="StringBytes"/>).</summary>
    public static int CharBytes(char c) => c switch
    {
        '"' or '\\' or '\n' or '\r' or '\t' or '\b' or '\f' => 2,

        // Each half of a surrogate pair is escaped on its own, and a lone one is written as an escaped U+FFFD
        _ when char.IsSurrogate(c) => 6,
        _ when ResultEncoder.WillEncode(c) => 6,
        < (char)0x80 => 1,
        < (char)0x800 => 2,
        _ => 3
    };

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
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { Encoder = ResultEncoder };

        // Before the SDK's string-enum converter, which would otherwise bind these enums too
        options.Converters.Insert(0, new ToolEnumConverterFactory());
        options.MakeReadOnly();
        return options;
    }

    private static JsonSerializerOptions CreateSdkResultOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { Encoder = ResultEncoder };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Lists the allowed values of a <see cref="ToolEnumAttribute"/> enum (also as Nullable), or of a string marked
    /// with <see cref="ToolValuesAttribute"/>, as <c>"type": "string", "enum": [...]</c>, with null added for a nullable
    /// one. The description and default the schema already has are kept. An array of such an enum (get_desktop_state's
    /// include) gets the values on its items: the custom converter leaves the exporter nothing to describe them with.
    /// </summary>
    private static JsonNode ListToolEnumValues(AIJsonSchemaCreateContext context, JsonNode schema)
    {
        var type = context.TypeInfo.Type;

        // press_keys' keys (roadmap M4.3): a string or an array of strings, which its converter reads
        if (type == typeof(KeyList))
        {
            var keys = schema as JsonObject ?? [];
            keys.Remove("properties");
            keys.Remove("required");
            keys["type"] = new JsonArray("string", "array");
            keys["items"] = new JsonObject { ["type"] = "string" };
            return keys;
        }
        if (type.IsArray && type.GetElementType() is { } element && ToolEnumInfo.IsToolEnum(element) && schema is JsonObject array)
        {
            array["items"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray([.. ToolEnumInfo.Get(element).SchemaValues.Select(value => (JsonNode?)JsonValue.Create(value))])
            };
            return array;
        }

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
