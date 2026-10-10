using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

namespace WindowsAppManagerMcp.Tests.Unit.Architecture;

/// <summary>
/// Guards that keep the default test run desktop-safe.
/// Service tests mock INativeInputWrapper and INativeWindowWrapper. If a service called
/// SendInput, SetWindowPos or a similar API directly, its tests would act on the real desktop
/// again. These tests scan the server assembly's IL and fail when anything other than the
/// wrapper calls those APIs.
/// </summary>
public class NativeCallGuardTests
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly ServerAssembly = typeof(WindowService).Assembly;

    private static readonly Type NativeMethodsType =
        ServerAssembly.GetType("WindowsAppManagerMcp.Native.NativeMethods", throwOnError: true)!;

    private static readonly HashSet<string> WindowChangingUser32Methods = new(StringComparer.Ordinal)
    {
        "SetWindowPos", "MoveWindow", "ShowWindow", "SetForegroundWindow", "BringWindowToTop",
        "SetWindowLongW", "SetWindowPlacement", "AllowSetForegroundWindow", "PostMessageW"
    };

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => unchecked((ushort)op.Value));

    [Fact]
    public void NativeInputMethods_AreOnlyCalledByNativeInputWrapper()
    {
        // Arrange
        var inputType = NativeMethodsType.GetNestedType("Input", BindingFlags.NonPublic | BindingFlags.Public);
        inputType.Should().NotBeNull();

        // Act
        var callers = FindCallers(callee => callee.DeclaringType == inputType);

        // Assert
        callers.Keys.Should().Contain(typeof(NativeInputWrapper),
            "the scan must see the wrapper's own SendInput and SetCursorPos calls");
        Unexpected(callers, typeof(NativeInputWrapper), NativeMethodsType).Should().BeEmpty(
            "mouse and keyboard input must go through INativeInputWrapper so tests can mock it");
    }

    [Fact]
    public void WindowChangingUser32Methods_AreOnlyCalledByNativeWindowWrapper()
    {
        // Arrange
        var user32Type = NativeMethodsType.GetNestedType("User32", BindingFlags.NonPublic | BindingFlags.Public);
        user32Type.Should().NotBeNull();

        // Act
        var callers = FindCallers(callee =>
            callee.DeclaringType == user32Type && WindowChangingUser32Methods.Contains(callee.Name));

        // Assert
        callers.Keys.Should().Contain(typeof(NativeWindowWrapper),
            "the scan must see the wrapper's own SetWindowPos and ShowWindow calls");
        Unexpected(callers, typeof(NativeWindowWrapper), NativeMethodsType).Should().BeEmpty(
            "moving, resizing, showing and focusing windows must go through INativeWindowWrapper so tests can mock it");
    }

    [Fact]
    public void ProcessStartAndKill_AreOnlyCalledByNativeProcessWrapper()
    {
        // Act
        var callers = FindCallers(callee =>
            callee.DeclaringType == typeof(Process) &&
            callee.Name is nameof(Process.Start) or nameof(Process.Kill) or nameof(Process.CloseMainWindow));

        // Assert - NativeProcessWrapper (with its nested ProcessHandle) is the process seam that
        // ProcessService and its tests mock. Services and tools must not start, close or kill
        // processes themselves; route new callers through INativeProcessWrapper or IProcessService.
        callers.Keys.Should().Contain(typeof(NativeProcessWrapper),
            "the scan must see the wrapper's own Process.Start, Kill and CloseMainWindow calls");
        callers[typeof(NativeProcessWrapper)].Should().Contain(c => c.EndsWith($"Process.{nameof(Process.Start)}"))
            .And.Contain(c => c.EndsWith($"Process.{nameof(Process.Kill)}"))
            .And.Contain(c => c.EndsWith($"Process.{nameof(Process.CloseMainWindow)}"));
        Unexpected(callers, typeof(NativeProcessWrapper)).Should().BeEmpty(
            "starting and stopping processes must go through INativeProcessWrapper so tests can mock it");
    }

    [Fact]
    public void NoPlatformInvoke_StartsOrTerminatesProcesses()
    {
        // Arrange - Win32 entry points that would bypass the Process class (and so the guard above)
        string[] processControlPrefixes =
        [
            "CreateProcess", "ShellExecute", "WinExec", "TerminateProcess", "TerminateJobObject", "NtTerminateProcess"
        ];

        // Act
        var imports = ServerAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(DeclaredMembers))
            .Select(m => (Method: m, Import: m.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>()))
            .Where(m => m.Import != null)
            .Select(m => (Name: $"{m.Method.DeclaringType?.FullName}.{m.Method.Name}", EntryPoint: m.Import!.EntryPoint ?? m.Method.Name))
            .ToList();

        // Assert
        imports.Should().NotBeEmpty("the scan must see the server's own P/Invoke declarations");
        imports
            .Where(i => processControlPrefixes.Any(prefix => i.EntryPoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(i => $"{i.Name} imports {i.EntryPoint}")
            .Should().BeEmpty("processes must be started and stopped through NativeProcessWrapper, not by direct Win32 calls");
    }

    private static List<string> Unexpected(Dictionary<Type, List<string>> callers, params Type[] allowedOwners) =>
        callers.Where(c => !allowedOwners.Contains(c.Key)).SelectMany(c => c.Value).ToList();

    /// <summary>
    /// Returns every top-level type in the server assembly whose code (including lambdas,
    /// iterators and async state machines) calls a method matching <paramref name="isTarget"/>.
    /// </summary>
    private static Dictionary<Type, List<string>> FindCallers(Func<MethodBase, bool> isTarget)
    {
        var callers = new Dictionary<Type, List<string>>();

        foreach (var type in ServerAssembly.GetTypes())
        {
            var owner = type;
            while (owner.DeclaringType != null)
                owner = owner.DeclaringType;

            var members = type.GetMethods(DeclaredMembers).Cast<MethodBase>()
                .Concat(type.GetConstructors(DeclaredMembers));

            foreach (var method in members)
            {
                foreach (var callee in GetCalledMethods(method).Where(isTarget))
                {
                    if (!callers.TryGetValue(owner, out var calls))
                        callers[owner] = calls = new List<string>();
                    calls.Add($"{type.FullName}.{method.Name} calls {callee.DeclaringType?.Name}.{callee.Name}");
                }
            }
        }

        return callers;
    }

    /// <summary>Decodes a method body's IL and returns the methods it calls or references.</summary>
    private static List<MethodBase> GetCalledMethods(MethodBase method)
    {
        var called = new List<MethodBase>();
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            return called;

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var position = 0;
        while (position < il.Length)
        {
            ushort value = il[position++];
            if (value == 0xFE)
                value = (ushort)(0xFE00 | il[position++]);

            var opCode = OpCodesByValue[value];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, position);
                try
                {
                    if (method.Module.ResolveMethod(token, typeArguments, methodArguments) is { } callee)
                        called.Add(callee);
                }
                catch (ArgumentException)
                {
                    // Token that cannot be resolved in this generic context; not a call we track.
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
}
