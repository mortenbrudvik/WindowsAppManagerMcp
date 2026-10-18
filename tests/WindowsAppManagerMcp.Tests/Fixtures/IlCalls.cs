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
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        foreach (var (opCode, operand) in Instructions(method))
        {
            if (opCode.OperandType != OperandType.InlineMethod)
            {
                continue;
            }

            try
            {
                if (method.Module.ResolveMethod(operand, typeArguments, methodArguments) is { } callee)
                {
                    called.Add(callee);
                }
            }
            catch (ArgumentException)
            {
                // A token that cannot be resolved in this generic context; not a call these tests track
            }
        }

        return called;
    }

    /// <summary>True when <paramref name="method"/>'s body has an instruction with <paramref name="opCode"/> (calli, for example).</summary>
    public static bool Uses(MethodBase method, OpCode opCode) => Instructions(method).Any(i => i.OpCode == opCode);

    /// <summary>The instructions of a method body: each opcode with its 4-byte token operand, or 0 when it has none.</summary>
    private static IEnumerable<(OpCode OpCode, int Operand)> Instructions(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            yield break;
        }

        var position = 0;
        while (position < il.Length)
        {
            ushort value = il[position++];
            if (value == 0xFE)
            {
                value = (ushort)(0xFE00 | il[position++]);
            }

            var opCode = OpCodesByValue[value];
            var size = opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
            yield return (opCode, size == 4 ? BitConverter.ToInt32(il, position) : 0);
            position += size;
        }
    }

    /// <summary>Every method that the code of <paramref name="type"/> (its nested types included) calls.</summary>
    public static IEnumerable<MethodBase> CallsMadeBy(Type type) => MethodsOf(type).SelectMany(CalledMethods);
}
