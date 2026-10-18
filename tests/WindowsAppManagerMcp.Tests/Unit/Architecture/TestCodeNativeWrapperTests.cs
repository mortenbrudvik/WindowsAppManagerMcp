using System.Diagnostics;
using System.Reflection;

namespace WindowsAppManagerMcp.Tests.Unit.Architecture;

/// <summary>
/// The other half of <see cref="NativeCallGuardTests"/>: that scan keeps every input, window change, process start and stop,
/// hook and clipboard call of the server inside the native action wrappers, on the premise that the default test run always
/// replaces those wrappers with mocks. These tests check the premise. They scan the IL of the test assemblies (this one and the
/// shared WindowsAppManagerMcp.Tests.Infrastructure), as <see cref="RealUiAutomationCollectionTests"/> does for UI Automation,
/// outside the desktop-gated tests ([DesktopFact] and its kin, which run only when the user opts in):
/// <list type="bullet">
/// <item>Test code constructs a real action wrapper (<see cref="ActionWrappers"/>) only where <see cref="SanctionedConstructions"/>
/// says why that use only reads: the read-only window wrapper and the process-tree reads.</item>
/// <item>Test code calls a real window or process wrapper only through the members on <see cref="ReadOnlyMembers"/>, and never
/// closes or kills a process through a process handle.</item>
/// <item>Server code that a test hands a real wrapper to (a constructor called in the same method) calls only those read members
/// on it.</item>
/// </list>
/// A Moq setup names a member in an expression tree (ldtoken), not as a call, so mocked members are never counted.
/// </summary>
/// <remarks>
/// The scan follows a real wrapper only within the method that creates it. A server constructor that creates a real wrapper
/// itself when a test leaves the parameter out (ProcessService's processWrapper) is outside what the IL shows; the tests that
/// build a ProcessService pass a mock.
/// </remarks>
public class TestCodeNativeWrapperTests
{
    private static readonly Assembly[] TestAssemblies = [typeof(TestCodeNativeWrapperTests).Assembly, typeof(InProcessMcpServer).Assembly];

    private static readonly Assembly ServerAssembly = typeof(NativeWindowWrapper).Assembly;

    /// <summary>
    /// The wrappers that act on the desktop: input, the clipboard, low-level and WinEvent hooks, the pause hotkey and the named
    /// events every server shares, attaching thread input, packaged app activation, UI Automation's window close, and the window
    /// and process wrappers, which also read.
    /// </summary>
    private static readonly Type[] ActionWrappers =
    [
        typeof(NativeInputWrapper), typeof(NativeClipboardWrapper), typeof(NativeLowLevelInputWrapper), typeof(NativeHotkeyWrapper),
        typeof(NativeThreadInputWrapper), typeof(NativeWinEventWrapper), typeof(NativeAppsWrapper), typeof(UIAutomationWrapper),
        typeof(NativeWindowWrapper), typeof(NativeProcessWrapper)
    ];

    /// <summary>The test types (by full name) that construct a real action wrapper, and why that use only reads.</summary>
    private static readonly Dictionary<(string TestType, Type Wrapper), string> SanctionedConstructions = new()
    {
        [("WindowsAppManagerMcp.Tests.Fixtures.ReadOnlyNativeWindowWrapper", typeof(NativeWindowWrapper))] =
            "the read-only window wrapper: it forwards the queries and throws on every window change (ReadOnlyNativeWindowWrapperTests)",
        [("WindowsAppManagerMcp.Tests.Unit.Native.ProcessAncestryTests", typeof(NativeProcessWrapper))] =
            "reads the test host's process tree and its own process handle's name",
        [("WindowsAppManagerMcp.Tests.Unit.Native.ProcessCommandLineTests", typeof(NativeProcessWrapper))] =
            "reads the test host's command line",
        [("WindowsAppManagerMcp.Tests.Unit.Native.ProcessListingTests", typeof(NativeProcessWrapper))] =
            "lists the running processes and reads the test host's details",
        [("WindowsAppManagerMcp.Tests.Integration.SecurityProbeIntegrationTests", typeof(NativeProcessWrapper))] =
            "reads the test host's ancestors and start time, and records the agent's host with HostGuard, which only reads processes",
    };

    /// <summary>
    /// The members of the window and process wrappers that only read: the only ones test code may call on a real instance. The
    /// window wrapper's are every interface member that ReadOnlyNativeWindowWrapperTests does not list as a window change.
    /// </summary>
    private static readonly Dictionary<Type, HashSet<string>> ReadOnlyMembers = new()
    {
        [typeof(NativeProcessWrapper)] = new(StringComparer.Ordinal)
        {
            ".ctor", nameof(NativeProcessWrapper.OpenProcess), nameof(NativeProcessWrapper.IsAncestorOfCurrentProcess),
            nameof(NativeProcessWrapper.GetAncestorProcessIds), nameof(NativeProcessWrapper.GetAncestorProcesses),
            nameof(NativeProcessWrapper.GetAncestorsOf), nameof(NativeProcessWrapper.GetDescendantsOf),
            nameof(NativeProcessWrapper.GetProcessStartTime), nameof(NativeProcessWrapper.GetConsoleWindow),
            nameof(NativeProcessWrapper.GetProcessList), nameof(NativeProcessWrapper.GetProcessImagePath),
            nameof(NativeProcessWrapper.GetProcessCommandLine), nameof(NativeProcessWrapper.ListRunningProcesses),
            nameof(NativeProcessWrapper.ReadProcessDetails), nameof(NativeProcessWrapper.WaitForInputIdle),
            nameof(NativeProcessWrapper.FindAncestors), nameof(NativeProcessWrapper.FindParent), nameof(NativeProcessWrapper.FindDescendants),
            nameof(NativeProcessWrapper.ReadProcessList)
        },
        [typeof(NativeWindowWrapper)] = typeof(INativeWindowWrapper).GetMethods().Select(m => m.Name)
            .Except(Tests.Unit.Infrastructure.ReadOnlyNativeWindowWrapperTests.WindowChanges().Select(row => (string)row[0]))
            .Append(".ctor")
            .ToHashSet(StringComparer.Ordinal)
    };

    /// <summary>What a process handle does that changes the process: test code never calls these outside desktop-gated tests.</summary>
    private static readonly HashSet<string> ProcessHandleActions = new(StringComparer.Ordinal)
    {
        nameof(IProcessHandle.Kill), nameof(IProcessHandle.CloseMainWindow)
    };

    [Fact]
    public void TestCode_ConstructsARealActionWrapper_OnlyWhereThatUseOnlyReads()
    {
        // Act
        var constructions = Calls(TestCode()).Where(c => IsActionWrapperConstruction(c.Callee)).ToList();

        // Assert
        constructions.Should().Contain(c => c.Owner.Name == nameof(ReadOnlyNativeWindowWrapper) && c.Callee.DeclaringType == typeof(NativeWindowWrapper),
            "the scan must see the read-only window wrapper's own construction of the real one");
        Calls(SampleCode()).Where(c => IsActionWrapperConstruction(c.Callee)).Should()
            .Contain(c => c.Callee.DeclaringType == typeof(NativeInputWrapper), "the scan must see a sample's construction of the input wrapper");
        constructions
            .Where(c => !SanctionedConstructions.ContainsKey((c.Owner.FullName!, c.Callee.DeclaringType!)))
            .Select(c => c.Description)
            .Should().BeEmpty(
                "a test that constructs a real input, clipboard, hook, hotkey, window or process wrapper can act on the user's desktop; " +
                "mock the wrapper's interface, gate the test as a desktop test, or add the use to SanctionedConstructions with why it only reads");
    }

    [Fact]
    public void TestCode_CallsARealWindowOrProcessWrapper_OnlyThroughItsReadMembers()
    {
        // Act
        var calls = Calls(TestCode()).Where(c => ReadOnlyMembers.ContainsKey(c.Callee.DeclaringType!)).ToList();

        // Assert
        calls.Should().Contain(c => c.Owner.Name == "ProcessAncestryTests" && c.Callee.Name == nameof(NativeProcessWrapper.GetDescendantsOf),
            "the scan must see the process tests' calls on the real process wrapper");
        Calls(SampleCode()).Should().Contain(c => c.Callee.Name == nameof(NativeProcessWrapper.StartProcess) && !IsReadOnly(c.Callee),
            "the scan must see a sample's process start as an action");
        calls.Where(c => !IsReadOnly(c.Callee)).Select(c => c.Description).Should().BeEmpty(
            "test code may only read through a real window or process wrapper; a member that changes a window or starts a process " +
            "belongs to a desktop-gated test, or to ReadOnlyMembers after review");
    }

    [Fact]
    public void TestCode_NeverClosesOrKillsAProcessThroughAProcessHandle()
    {
        // Act - through the interface, as test code reaches the handle a real wrapper's OpenProcess returns
        var calls = Calls(TestCode()).Where(c => IsProcessHandleAction(c.Callee)).ToList();

        // Assert
        Calls(SampleCode()).Should().Contain(c => IsProcessHandleAction(c.Callee), "the scan must see a sample's kill");
        calls.Select(c => c.Description).Should().BeEmpty(
            "a real process handle would close or kill the process; tests set these members up on mocks, which is no call");
    }

    [Fact]
    public void ServerCode_ThatATestHandsARealWrapperTo_OnlyReadsIt()
    {
        // Arrange - a method that constructs a real window or process wrapper, and a server constructor it calls: the server
        // type may get the wrapper, so its own calls on the wrapper's interface must be reads too
        var handedTo = TestCode()
            .Where(m => IlCalls.CalledMethods(m).Any(c => c is ConstructorInfo && ReadOnlyMembers.ContainsKey(c.DeclaringType!)))
            .SelectMany(m => IlCalls.CalledMethods(m)
                .Where(c => c is ConstructorInfo && c.DeclaringType!.Assembly == ServerAssembly && !ActionWrappers.Contains(c.DeclaringType))
                .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(INativeProcessWrapper) || p.ParameterType == typeof(INativeWindowWrapper)))
                .Select(c => IlCalls.OutermostType(c.DeclaringType!)))
            .Distinct()
            .ToList();

        // Act
        var actions = handedTo
            .SelectMany(consumer => IlCalls.CallsMadeBy(consumer).Where(IsWrapperAction).Select(c => $"{consumer.Name} calls {c.DeclaringType!.Name}.{c.Name}"))
            .ToList();

        // Assert
        handedTo.Should().Contain(typeof(HostGuard), "the scan must see the agent-host test hand the real process wrapper to HostGuard");
        actions.Should().BeEmpty("a server type a test hands a real wrapper to must only read through it");
    }

    // ---- the scan ----

    /// <summary>A call in test code: the outermost type that owns the calling code, the calling method, and the callee.</summary>
    private sealed record Call(Type Owner, MethodBase Caller, MethodBase Callee)
    {
        public string Description => $"{Caller.DeclaringType?.FullName}.{Caller.Name} calls {Callee.DeclaringType?.Name}.{Callee.Name}";
    }

    /// <summary>Every method and constructor of the test assemblies, without the samples below and without desktop-gated tests.</summary>
    private static IEnumerable<MethodBase> TestCode() =>
        TestAssemblies.SelectMany(a => a.GetTypes())
            .Where(t => IlCalls.OutermostType(t) != typeof(NativeWrapperScanSamples))
            .SelectMany(t => t.GetMethods(IlCalls.DeclaredMembers).Cast<MethodBase>().Concat(t.GetConstructors(IlCalls.DeclaredMembers)))
            .Where(m => !IsInADesktopGatedTest(m));

    /// <summary>The samples' methods, which only the canaries scan.</summary>
    private static IEnumerable<MethodBase> SampleCode() => IlCalls.MethodsOf(typeof(NativeWrapperScanSamples));

    private static IEnumerable<Call> Calls(IEnumerable<MethodBase> methods) =>
        methods.SelectMany(m => IlCalls.CalledMethods(m).Select(callee => new Call(IlCalls.OutermostType(m.DeclaringType!), m, callee)));

    private static bool IsActionWrapperConstruction(MethodBase callee) =>
        callee is ConstructorInfo { IsStatic: false } && ActionWrappers.Contains(callee.DeclaringType);

    private static bool IsReadOnly(MethodBase callee) =>
        ReadOnlyMembers.TryGetValue(callee.DeclaringType!, out var reads) && reads.Contains(callee.Name);

    private static bool IsProcessHandleAction(MethodBase callee) =>
        callee.DeclaringType is { } type && typeof(IProcessHandle).IsAssignableFrom(type) && ProcessHandleActions.Contains(callee.Name);

    /// <summary>A call on the window or process wrapper's interface, or on a process handle, that changes something.</summary>
    private static bool IsWrapperAction(MethodBase callee) =>
        IsProcessHandleAction(callee) ||
        (callee.DeclaringType == typeof(INativeProcessWrapper) && !ReadOnlyMembers[typeof(NativeProcessWrapper)].Contains(callee.Name)) ||
        (callee.DeclaringType == typeof(INativeWindowWrapper) && !ReadOnlyMembers[typeof(NativeWindowWrapper)].Contains(callee.Name));

    private static readonly HashSet<Type> DesktopTestAttributes =
    [
        typeof(DesktopFactAttribute), typeof(DesktopTheoryAttribute), typeof(DesktopNoInputFactAttribute), typeof(DesktopNoInputTheoryAttribute)
    ];

    /// <summary>
    /// True for the body of a desktop-gated test, its lambdas and its async state machine: the compiler names them after the test
    /// method (&lt;Test&gt;b__0, &lt;Test&gt;d__3), which is found on the test class.
    /// </summary>
    private static bool IsInADesktopGatedTest(MethodBase method)
    {
        var owner = IlCalls.OutermostType(method.DeclaringType!);
        var name = SourceMethodName(method);
        return name != null && owner.GetMethods(IlCalls.DeclaredMembers)
            .Where(m => m.Name == name)
            .Any(m => m.GetCustomAttributes().Any(a => DesktopTestAttributes.Contains(a.GetType())));
    }

    private static string? SourceMethodName(MethodBase method)
    {
        static string? Inside(string name) =>
            name.StartsWith('<') && name.IndexOf('>') is var end and > 1 ? name[1..end] : null;

        if (Inside(method.Name) is { } fromMethod)
        {
            return fromMethod;
        }

        for (var type = method.DeclaringType; type != null; type = type.DeclaringType)
        {
            if (Inside(type.Name) is { } fromType)
            {
                return fromType;
            }

            if (!type.Name.StartsWith('<'))
            {
                return type == method.DeclaringType ? method.Name : null;
            }
        }

        return null;
    }
}

/// <summary>
/// Samples for <see cref="TestCodeNativeWrapperTests"/>, which only scans their IL. Nothing calls them, and nothing may: they would
/// start or kill a process or press keys for real.
/// </summary>
internal static class NativeWrapperScanSamples
{
    public static object ConstructsTheInputWrapper() => new NativeInputWrapper();

    public static object? StartsAProcess() => new NativeProcessWrapper().StartProcess(new ProcessStartInfo("notepad.exe"));

    public static void KillsAProcess(IProcessHandle handle) => handle.Kill(entireProcessTree: true);
}
