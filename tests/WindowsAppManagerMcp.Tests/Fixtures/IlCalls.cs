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
        foreach (var (_, opCode, operand, _) in Instructions(method))
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

    /// <summary>
    /// The instructions of <paramref name="method"/>'s body in order, with the method each one names (call, callvirt, newobj,
    /// ldftn) and the string it loads (ldstr) resolved, and whether control can reach each one other than from the instruction
    /// before it: for tests that follow where a value comes from, such as the name a wrapper is built on. What an instruction
    /// takes from the stack is what the instruction before it pushed only when it is no branch target: at a target, such as the
    /// end of a ?? or a ?:, it may come from another path.
    /// </summary>
    public static IReadOnlyList<IlInstruction> InstructionsOf(MethodBase method)
    {
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var raw = Instructions(method).ToList();

        // Where control can jump to: a branch's, a leave's or a switch's target, and where an exception handler or filter starts
        var clauses = method.GetMethodBody()?.ExceptionHandlingClauses ?? [];
        var targets = raw.SelectMany(i => i.Targets)
            .Concat(clauses.Select(c => c.HandlerOffset))
            .Concat(clauses.Where(c => c.Flags == ExceptionHandlingClauseOptions.Filter).Select(c => c.FilterOffset))
            .ToHashSet();
        var instructions = new List<IlInstruction>();
        foreach (var (offset, opCode, operand, _) in raw)
        {
            MethodBase? callee = null;
            string? text = null;
            try
            {
                callee = opCode.OperandType == OperandType.InlineMethod ? method.Module.ResolveMethod(operand, typeArguments, methodArguments) : null;
                text = opCode == OpCodes.Ldstr ? method.Module.ResolveString(operand) : null;
            }
            catch (ArgumentException)
            {
                // A token that cannot be resolved in this generic context: the instruction is kept without it
            }

            instructions.Add(new IlInstruction(opCode, operand, callee, text, offset, targets.Contains(offset)));
        }

        return instructions;
    }

    /// <summary>
    /// The instructions of a method body: each one's offset; its opcode with its operand when it is a 4-byte token or a 1- or
    /// 2-byte local or argument index or constant, or 0 when it has none (or one of 8 bytes, or a switch table); and the offsets
    /// a branch, a leave or a switch jumps to.
    /// </summary>
    private static IEnumerable<(int Offset, OpCode OpCode, int Operand, int[] Targets)> Instructions(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            yield break;
        }

        var position = 0;
        while (position < il.Length)
        {
            var offset = position;
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
            var operand = opCode.OperandType switch
            {
                OperandType.InlineSwitch => 0,
                _ when size == 4 => BitConverter.ToInt32(il, position),
                OperandType.ShortInlineVar => il[position],
                OperandType.ShortInlineI or OperandType.ShortInlineBrTarget => (sbyte)il[position],
                OperandType.InlineVar => BitConverter.ToUInt16(il, position),
                _ => 0
            };
            // A branch's offset counts from the next instruction, a switch's from the end of its table
            var next = position + size;
            int[] targets = opCode.OperandType switch
            {
                OperandType.ShortInlineBrTarget or OperandType.InlineBrTarget => [next + operand],
                OperandType.InlineSwitch => Enumerable.Range(0, BitConverter.ToInt32(il, position))
                    .Select(k => next + BitConverter.ToInt32(il, position + 4 + 4 * k))
                    .ToArray(),
                _ => []
            };
            yield return (offset, opCode, operand, targets);
            position = next;
        }
    }

    /// <summary>Every method that the code of <paramref name="type"/> (its nested types included) calls.</summary>
    public static IEnumerable<MethodBase> CallsMadeBy(Type type) => MethodsOf(type).SelectMany(CalledMethods);
}

/// <summary>One IL instruction of <see cref="IlCalls.InstructionsOf"/>.</summary>
/// <param name="OpCode">The instruction.</param>
/// <param name="Operand">Its token, its local's or argument's index, or its constant (a branch's relative offset); 0 when it has none.</param>
/// <param name="Method">The method it names (call, callvirt, newobj, ldftn), or null.</param>
/// <param name="Text">The string it loads (ldstr), or null.</param>
/// <param name="Offset">Its offset in the method body's IL.</param>
/// <param name="IsBranchTarget">
/// True when control can reach it other than from the instruction before it: a branch, a leave or a switch jumps to it, or an
/// exception handler or filter starts there. What it takes from the stack may then come from another path.
/// </param>
public sealed record IlInstruction(OpCode OpCode, int Operand, MethodBase? Method, string? Text, int Offset, bool IsBranchTarget);
