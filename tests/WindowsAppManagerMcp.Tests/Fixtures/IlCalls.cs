using System.Reflection;
using System.Reflection.Emit;

namespace WindowsAppManagerMcp.Tests.Fixtures;

/// <summary>
/// Reads the calls a method body makes from its IL (call, callvirt, newobj, ldftn: every instruction whose operand is a
/// method), for tests that pin which code may call what. Expression trees (Moq setups) load their methods with ldtoken, not as
/// a call, so a mocked member in a setup is never counted as called.
/// </summary>
public static class IlCalls
{
    /// <summary>Every member a method can declare, public or not, instance or static, declared on the type itself.</summary>
    public const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => unchecked((ushort)op.Value));

    /// <summary>The outermost type of <paramref name="type"/>: lambdas, iterators and async state machines are nested in their owner.</summary>
    public static Type OutermostType(Type type)
    {
        while (type.DeclaringType != null)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    /// <summary>
    /// The methods and constructors of <paramref name="type"/> and of every type nested in it (its lambdas' closures, iterators
    /// and async state machines).
    /// </summary>
    public static IEnumerable<MethodBase> MethodsOf(Type type) =>
        type.GetMethods(DeclaredMembers).Cast<MethodBase>()
            .Concat(type.GetConstructors(DeclaredMembers))
            .Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).SelectMany(MethodsOf));

    /// <summary>The methods <paramref name="method"/>'s body calls or references, as its IL names them.</summary>
    public static IReadOnlyList<MethodBase> CalledMethods(MethodBase method)
    {
        var called = new List<MethodBase>();
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return called;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            ushort value = il[position++];
            if (value == 0xFE)
            {
                value = (ushort)(0xFE00 | il[position++]);
            }

            var opCode = OpCodesByValue[value];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                try
                {
                    if (method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments) is { } callee)
                    {
                        called.Add(callee);
                    }
                }
                catch (ArgumentException)
                {
                    // A token that cannot be resolved in this generic context; not a call these tests track
                }
            }

            position += opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
        }

        return called;
    }

    /// <summary>Every method that the code of <paramref name="type"/> (its nested types included) calls.</summary>
    public static IEnumerable<MethodBase> CallsMadeBy(Type type) => MethodsOf(type).SelectMany(CalledMethods);
}
