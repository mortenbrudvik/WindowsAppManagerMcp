using System.Collections;
using System.Reflection;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Builds a fully populated sample instance of an arbitrary DTO type by reflection.
/// Every nullable member gets a non-null value, because a null member serializes fine
/// even when its type is not serializable (e.g. a null <c>nint?</c> becomes <c>null</c>,
/// a non-null one throws). Collections get one element so their element type is exercised.
/// </summary>
public static class SampleValueFactory
{
    private const int MaxDepth = 8;

    public static object? Create(Type type) => Create(type, new HashSet<Type>(), 0);

    private static object? Create(Type type, HashSet<Type> path, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            // A boxed T is a valid value for T?, and it forces the non-null serialization path.
            return Create(underlying, path, depth);
        }

        if (TryCreateScalar(type, out var scalar))
        {
            return scalar;
        }

        // Stop recursive types (e.g. a tree node with a Children list) from looping forever.
        if (depth > MaxDepth || path.Contains(type))
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        path.Add(type);
        try
        {
            if (TryCreateCollection(type, path, depth, out var collection))
            {
                return collection;
            }

            return CreateObject(type, path, depth);
        }
        finally
        {
            path.Remove(type);
        }
    }

    private static bool TryCreateScalar(Type type, out object? value)
    {
        value = null;

        if (type == typeof(string)) value = "sample";
        else if (type == typeof(bool)) value = true;
        else if (type == typeof(char)) value = 'x';
        else if (type == typeof(nint)) value = new nint(1234);
        else if (type == typeof(nuint)) value = new nuint(1234);
        else if (type == typeof(DateTime)) value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        else if (type == typeof(DateTimeOffset)) value = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        else if (type == typeof(TimeSpan)) value = TimeSpan.FromSeconds(5);
        else if (type == typeof(Guid)) value = Guid.Parse("11111111-2222-3333-4444-555555555555");
        else if (type == typeof(Uri)) value = new Uri("https://example.com/");
        else if (type == typeof(object)) value = "sample";
        // A monitor as get_layout shows it (roadmap M6.8): an id, written as a string
        else if (type == typeof(MonitorSelector)) value = MonitorSelector.Of("m-0a1b2c3d");
        // A JSON value a result passes through as it is (browser_evaluate's value, roadmap M5.8); default(JsonElement) cannot be written
        else if (type == typeof(System.Text.Json.JsonElement)) value = System.Text.Json.JsonSerializer.SerializeToElement(new { sample = 7 });
        // A step's own result, passed on as its JSON (run_steps, roadmap M6.2)
        else if (typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type)) value = System.Text.Json.Nodes.JsonNode.Parse("""{ "sample": 7 }""");
        else if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            value = values.Length > 0 ? values.GetValue(values.Length - 1) : Activator.CreateInstance(type);
        }
        else if (type.IsPrimitive || type == typeof(decimal))
        {
            value = Convert.ChangeType(7, type);
        }
        else
        {
            return false;
        }

        return true;
    }

    private static bool TryCreateCollection(Type type, HashSet<Type> path, int depth, out object? value)
    {
        value = null;

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var array = Array.CreateInstance(elementType, 1);
            array.SetValue(Create(elementType, path, depth + 1), 0);
            value = array;
            return true;
        }

        if (type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type))
        {
            return false;
        }

        var dictionaryInterface = FindGenericInterface(type, typeof(IDictionary<,>))
            ?? FindGenericInterface(type, typeof(IReadOnlyDictionary<,>));
        if (dictionaryInterface != null)
        {
            var args = dictionaryInterface.GetGenericArguments();
            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(args);
            var dictionary = (IDictionary)CreateAssignable(type, dictionaryType);
            dictionary.Add(Create(args[0], path, depth + 1)!, Create(args[1], path, depth + 1));
            value = dictionary;
            return true;
        }

        var enumerableInterface = FindGenericInterface(type, typeof(IEnumerable<>));
        if (enumerableInterface != null)
        {
            var elementType = enumerableInterface.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = CreateAssignable(type, listType);
            var add = list.GetType().GetMethod("Add", new[] { elementType })
                ?? throw new NotSupportedException($"Cannot add elements to sample collection type {type}.");
            add.Invoke(list, new[] { Create(elementType, path, depth + 1) });
            value = list;
            return true;
        }

        throw new NotSupportedException($"No sample strategy for non-generic collection type {type}.");
    }

    private static object CreateObject(Type type, HashSet<Type> path, int depth)
    {
        if (type.IsInterface || type.IsAbstract)
        {
            throw new NotSupportedException(
                $"No sample strategy for abstract type {type}. Extend {nameof(SampleValueFactory)}.");
        }

        var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        object instance;
        if (constructor == null)
        {
            // Structs without an explicit constructor.
            instance = Activator.CreateInstance(type)!;
        }
        else
        {
            var arguments = constructor.GetParameters()
                .Select(p => Create(p.ParameterType, path, depth + 1))
                .ToArray();
            instance = constructor.Invoke(arguments);
        }

        // Fill settable members the constructor did not cover (init-only setters included).
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (property.GetValue(instance) == null)
            {
                property.SetValue(instance, Create(property.PropertyType, path, depth + 1));
            }
        }

        return instance;
    }

    private static object CreateAssignable(Type declaredType, Type concreteType)
    {
        if (declaredType.IsAssignableFrom(concreteType))
        {
            return Activator.CreateInstance(concreteType)!;
        }

        if (!declaredType.IsAbstract && !declaredType.IsInterface && declaredType.GetConstructor(Type.EmptyTypes) != null)
        {
            return Activator.CreateInstance(declaredType)!;
        }

        throw new NotSupportedException($"No sample strategy for collection type {declaredType}.");
    }

    private static Type? FindGenericInterface(Type type, Type genericDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition)
        {
            return type;
        }

        return type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericDefinition);
    }
}
