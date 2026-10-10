using System.Collections;
using System.Reflection;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// Follows the instance fields of server objects and reports every native wrapper it can reach without
/// passing a guard: an object of the server's Native namespace, or one implementing a native wrapper
/// interface, that is not a <see cref="GuardProxy"/>. It catches a service that creates its own wrapper
/// (<c>new NativeProcessWrapper()</c>) instead of taking the guarded one from the container.
/// </summary>
/// <remarks>
/// It reads fields only and calls nothing but collection enumerators. It descends into objects of the
/// server assembly (including compiler-generated closures), delegate targets, materialized collections
/// and generic containers of server types (<c>Lazy&lt;IFoo&gt;</c>, <c>KeyValuePair&lt;int, Foo&gt;</c>),
/// and stops at guards and at other framework objects.
/// </remarks>
internal static class NativeReferenceWalker
{
    private const BindingFlags InstanceFields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Assembly ServerAssembly = typeof(INativeWindowWrapper).Assembly;

    /// <summary>The unguarded native wrappers reachable from <paramref name="root"/>, as field paths.</summary>
    public static IReadOnlyList<string> FindUnguardedWrappers(object? root, string rootName)
    {
        var leaks = new List<string>();
        Walk(root, rootName, leaks, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return leaks;
    }

    /// <summary>As <see cref="FindUnguardedWrappers(object?, string)"/>, sharing <paramref name="visited"/> across roots.</summary>
    public static void Walk(object? value, string path, List<string> leaks, HashSet<object> visited)
    {
        if (value == null || IsLeaf(value.GetType()) || value is MemberInfo)
        {
            return;
        }

        var type = value.GetType();
        if (!type.IsValueType && !visited.Add(value))
        {
            return;
        }
        if (value is GuardProxy)
        {
            // The boundary: everything behind it is asked for permission call by call
            return;
        }
        if (WrapperGuard.IsNativeImplementation(type) || type.GetInterfaces().Any(WrapperGuard.IsNativeWrapper))
        {
            leaks.Add($"{path}: {type.FullName}");
            return;
        }
        if (value is Delegate callback)
        {
            foreach (var single in callback.GetInvocationList())
            {
                Walk(single.Target, $"{path}.Target", leaks, visited);
            }
            return;
        }
        if (value is ICollection collection && type.Assembly != ServerAssembly)
        {
            var index = 0;
            foreach (var item in collection)
            {
                Walk(item, $"{path}[{index++}]", leaks, visited);
            }
            return;
        }
        if (type.Assembly != ServerAssembly && !HasServerTypeArgument(type))
        {
            return;
        }

        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(InstanceFields))
            {
                if (!IsLeaf(field.FieldType))
                {
                    Walk(field.GetValue(value), $"{path}.{field.Name}", leaks, visited);
                }
            }
        }
    }

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(string) || type == typeof(decimal);

    private static bool HasServerTypeArgument(Type type) =>
        type.IsGenericType && type.GetGenericArguments().Any(argument =>
            argument.Assembly == ServerAssembly || HasServerTypeArgument(argument));
}
