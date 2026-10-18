using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using WindowsAppManagerMcp.Infrastructure;

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
/// closes or kills a process through a process handle. In a type that constructs a real window or process wrapper, calls through
/// their interfaces (INativeWindowWrapper, INativeProcessWrapper) are held to the same members: the interface may hold the real
/// one.</item>
/// <item>Server code that a test hands a real wrapper to (a constructor called in the same method) calls only those read members
/// on it.</item>
/// <item>Test code builds the server's production container (ServiceRegistration.AddWindowsAppManagerServices), whose services
/// hold the real input, clipboard, hook, window and process wrappers, only where <see cref="SanctionedProductionContainers"/>
/// says why that use only reads; and there it calls the server's services and wrappers only through constructors, property
/// getters, static members and the reads on <see cref="SanctionedProductionContainerCalls"/>.</item>
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
        [("WindowsAppManagerMcp.Tests.Unit.Native.NativeHotkeyWrapperSharedPauseTests", typeof(NativeHotkeyWrapper))] =
            @"opens only named events of its own (Local\WamTests.SharedPause.<guid>), never the servers' pause, and registers no hotkey; " +
            "the server's parameterless wrapper is only constructed to read the name it would open",
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
            // A pure helper that turns GetWindowTextW's buffer into the title; it makes no native call
            .Append(nameof(NativeWindowWrapper.TextFromBuffer))
            .ToHashSet(StringComparer.Ordinal)
    };

    /// <summary>What a process handle does that changes the process: test code never calls these outside desktop-gated tests.</summary>
    private static readonly HashSet<string> ProcessHandleActions = new(StringComparer.Ordinal)
    {
        nameof(IProcessHandle.Kill), nameof(IProcessHandle.CloseMainWindow)
    };

    /// <summary>The window and process wrappers' interfaces, each with the real wrapper whose <see cref="ReadOnlyMembers"/> it is held to.</summary>
    private static readonly Dictionary<Type, Type> WrapperInterfaces = new()
    {
        [typeof(INativeProcessWrapper)] = typeof(NativeProcessWrapper),
        [typeof(INativeWindowWrapper)] = typeof(NativeWindowWrapper)
    };

    /// <summary>
    /// The test types (by full name) that build the server's production container, whose services hold the real native wrappers
    /// (input, clipboard, hooks, the pause hotkey, windows, processes), and why that use only reads.
    /// </summary>
    private static readonly Dictionary<string, string> SanctionedProductionContainers = new(StringComparer.Ordinal)
    {
        ["WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests"] =
            "resolves the services to check their types, lifetimes and what they hold; it acts through none of them, and calls only " +
            "the reads on SanctionedProductionContainerCalls",
        ["WindowsAppManagerMcp.Tests.Integration.InputGuardRegistrationTests"] =
            "resolves the input guard, the policy and the safety info with the read-only window wrapper, and reads what they hold",
        ["WindowsAppManagerMcp.Tests.Integration.InputSessionRegistrationTests"] =
            "resolves the session and the pause hotkey with the read-only window wrapper and reads what they hold; the hosted " +
            "service that would register the hotkey and share the pause is never started",
        ["WindowsAppManagerMcp.Tests.Integration.ScrollClipboardRegistrationTests"] =
            "resolves the input and clipboard services with the read-only window wrapper and reads what they hold; the clipboard " +
            "wrapper starts its thread only when it is called",
        ["WindowsAppManagerMcp.Tests.Integration.SecurityServiceRegistrationTests"] =
            "resolves the host guard and the security probes with the read-only window wrapper; they only read the process tree " +
            "and the console and foreground windows",
        ["WindowsAppManagerMcp.Tests.Fixtures.ServerToolCatalog"] =
            "reads the tool collection's names from the options; no service is resolved",
        ["WindowsAppManagerMcp.Tests.Contract.DeniedTargetContractTests"] =
            "reads the served tools' input schemas from the options (no service is resolved); its calls go to NativeFakedServer",
        ["WindowsAppManagerMcp.Tests.Contract.SelfGatedToolsContractTests"] =
            "reads the production registrations' implementation types; no container is built from them",
        ["WindowsAppManagerMcp.Tests.Fixtures.NativeFakedServer"] =
            "replaces every native seam with a fake before the container is built, and refuses to start when a service still " +
            "holds a real wrapper",
    };

    /// <summary>
    /// The calls a type of <see cref="SanctionedProductionContainers"/> makes on the server's services and wrappers beyond
    /// constructors, property getters and static members, keyed by (the test type's full name, "Type.Member"), and why each only
    /// reads.
    /// </summary>
    private static readonly Dictionary<(string TestType, string Member), string> SanctionedProductionContainerCalls = new()
    {
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "ILayoutService.GetAllPresets")] =
            "lists the layout presets of the test's own temporary folder",
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "ICdpService.GetPageTargetsAsync")] =
            "asks a recording transport the test registers in place of the real one; nothing reaches the network",
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "IDebugBrowserReadiness.WaitUntilReadyAsync")] =
            "asks the recording transport, with the port owner faked; nothing reaches the network or the TCP table",
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "ICdpTransport.GetJsonAsync")] =
            "the recording transport's own member, and a disposed transport sent to a port the test listens on: nothing is sent",
        [("WindowsAppManagerMcp.Tests.Integration.InputGuardRegistrationTests", "IInputSafetyInfo.Read")] =
            "reads the host, the policy and the chords the guard was given",
        [("WindowsAppManagerMcp.Tests.Fixtures.NativeFakedServer", "IElementRegistry.NextGeneration")] =
            "MintRef: a read generation of the faked server's registry, whose every native seam is a fake",
        [("WindowsAppManagerMcp.Tests.Fixtures.NativeFakedServer", "IElementRegistry.Register")] =
            "MintRef: gives a fake element of the faked server's tree a ref; Register makes no UI Automation call",
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
        // Act - on the real wrappers, and through their interfaces in the types that construct a real one
        var calls = CallsOnARealWindowOrProcessWrapper(TestCode()).ToList();

        // Assert
        calls.Should().Contain(c => c.Owner.Name == "ProcessAncestryTests" && c.Callee.Name == nameof(NativeProcessWrapper.GetDescendantsOf),
            "the scan must see the process tests' calls on the real process wrapper");
        calls.Should().Contain(c => c.Owner == typeof(ReadOnlyNativeWindowWrapper) && c.Callee.DeclaringType == typeof(INativeWindowWrapper),
            "the scan must see the read-only window wrapper's calls through the interface on the real one");
        var samples = CallsOnARealWindowOrProcessWrapper(SampleCode()).ToList();
        samples.Should().Contain(c => c.Callee.DeclaringType == typeof(NativeProcessWrapper) &&
                                      c.Callee.Name == nameof(NativeProcessWrapper.StartProcess) && !IsReadOnly(c.Callee),
            "the scan must see a sample's process start as an action");
        samples.Should().Contain(c => c.Callee.DeclaringType == typeof(INativeProcessWrapper) &&
                                      c.Callee.Name == nameof(INativeProcessWrapper.StartProcess) && !IsReadOnly(c.Callee),
            "the scan must see a process start through the interface, in a type that constructs the real wrapper, as an action");
        samples.Should().Contain(c => c.Callee.DeclaringType == typeof(INativeWindowWrapper) &&
                                      c.Callee.Name == nameof(INativeWindowWrapper.SetWindowPos) && !IsReadOnly(c.Callee),
            "the scan must see a window change through the interface, in a type that constructs the real wrapper, as an action");
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

    [Fact]
    public void TestCode_BuildsTheProductionContainer_OnlyWhereThatUseOnlyReads()
    {
        // Act
        var containers = TypesThatBuildTheProductionContainer(TestCode()).ToList();

        // Assert
        containers.Should().Contain([typeof(Integration.ScrollClipboardRegistrationTests), typeof(NativeFakedServer)],
            "the scan must see the registration tests and the faked server build the production container");
        TypesThatBuildTheProductionContainer(SampleCode()).Should().Contain(typeof(NativeWrapperScanSamples),
            "the scan must see a sample build the production container");
        containers.Select(t => t.FullName!).Where(name => !SanctionedProductionContainers.ContainsKey(name)).Should().BeEmpty(
            "the production container's services hold the real input, clipboard, hook, window and process wrappers: replace them " +
            "with fakes (NativeFakedServer), or add the type to SanctionedProductionContainers with why its use only reads");
        SanctionedProductionContainers.Keys.Should().BeSubsetOf(containers.Select(t => t.FullName!), "an entry names a type that builds the container");
    }

    [Fact]
    public void TestCode_ThatBuildsTheProductionContainer_CallsItsServicesOnlyThroughReads()
    {
        // Act - in each type that builds the container: the calls on the server's types that may act
        var calls = Calls(TestCode())
            .Where(c => SanctionedProductionContainers.ContainsKey(c.Owner.FullName!) && MayAct(c.Callee))
            .ToList();

        // Assert
        calls.Should().Contain(c => c.Owner == typeof(Integration.InputGuardRegistrationTests) && c.Callee.Name == nameof(IInputSafetyInfo.Read),
            "the scan must see a registration test's call on a resolved service");
        Calls(SampleCode()).Where(c => MayAct(c.Callee)).Should()
            .Contain(c => c.Callee.DeclaringType == typeof(INativeClipboardWrapper) && c.Callee.Name == nameof(INativeClipboardWrapper.WriteText),
                "the scan must see a sample write the clipboard through a resolved wrapper")
            .And.Contain(c => c.Callee.DeclaringType == typeof(IInputSimulationService),
                "the scan must see a sample type through a resolved service");
        calls.Where(c => !SanctionedProductionContainerCalls.ContainsKey((c.Owner.FullName!, Member(c.Callee))))
            .Select(c => c.Description)
            .Should().BeEmpty(
                "a type that builds the production container holds the real native wrappers: it may construct services and read their " +
                "properties, and call a member only after review onto SanctionedProductionContainerCalls with why it only reads");
        SanctionedProductionContainerCalls.Keys.Should().BeSubsetOf(calls.Select(c => (c.Owner.FullName!, Member(c.Callee))),
            "an entry names a call the type makes");
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

    /// <summary>
    /// The calls on a real window or process wrapper, and, in a type that constructs one of them, every call through their
    /// interfaces: an INativeProcessWrapper there may hold the real one.
    /// </summary>
    private static IEnumerable<Call> CallsOnARealWindowOrProcessWrapper(IEnumerable<MethodBase> methods)
    {
        var calls = Calls(methods).ToList();
        var owners = calls.Where(c => c.Callee is ConstructorInfo { IsStatic: false } && ReadOnlyMembers.ContainsKey(c.Callee.DeclaringType!))
            .Select(c => c.Owner)
            .ToHashSet();
        return calls.Where(c => ReadOnlyMembers.ContainsKey(c.Callee.DeclaringType!) ||
                                (owners.Contains(c.Owner) && WrapperInterfaces.ContainsKey(c.Callee.DeclaringType!)));
    }

    /// <summary>The outermost types whose code calls ServiceRegistration.AddWindowsAppManagerServices.</summary>
    private static IEnumerable<Type> TypesThatBuildTheProductionContainer(IEnumerable<MethodBase> methods) =>
        Calls(methods)
            .Where(c => c.Callee.DeclaringType == typeof(ServiceRegistration) && c.Callee.Name == nameof(ServiceRegistration.AddWindowsAppManagerServices))
            .Select(c => c.Owner)
            .Distinct();

    /// <summary>
    /// A call on a server type that may act: an instance member that is no constructor, no property getter and no part of a
    /// record's copy (its clone and init-only setters), or a static member of a Native wrapper.
    /// </summary>
    private static bool MayAct(MethodBase callee) =>
        callee.DeclaringType is { } type && type.Assembly == ServerAssembly &&
        callee is not ConstructorInfo &&
        (callee.IsStatic
            ? type.Namespace == typeof(NativeWindowWrapper).Namespace
            : !(callee.IsSpecialName && callee.Name.StartsWith("get_", StringComparison.Ordinal)) &&
              callee.Name != "<Clone>$" && !IsInitOnlySetter(callee));

    private static bool IsInitOnlySetter(MethodBase callee) =>
        callee is MethodInfo { IsSpecialName: true } setter && setter.Name.StartsWith("set_", StringComparison.Ordinal) &&
        setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    private static string Member(MethodBase callee) => $"{callee.DeclaringType!.Name}.{callee.Name}";

    private static bool IsReadOnly(MethodBase callee) =>
        ReadOnlyMembers.TryGetValue(WrapperInterfaces.GetValueOrDefault(callee.DeclaringType!, callee.DeclaringType!), out var reads) &&
        reads.Contains(callee.Name);

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

    public static object? StartsAProcessThroughItsInterface()
    {
        INativeProcessWrapper processes = new NativeProcessWrapper();
        return processes.StartProcess(new ProcessStartInfo("notepad.exe"));
    }

    public static bool MovesAWindowThroughItsInterface()
    {
        INativeWindowWrapper windows = new NativeWindowWrapper();
        return windows.SetWindowPos(0x10010, 0, 0, 800, 600, 0).Succeeded;
    }

    public static async Task ActsThroughTheProductionContainer()
    {
        using var provider = new ServiceCollection().AddWindowsAppManagerServices().BuildServiceProvider();
        provider.GetRequiredService<INativeClipboardWrapper>().WriteText("sample");
        await provider.GetRequiredService<IInputSimulationService>().TypeTextAsync(
            "sample", 0, new InputTarget(null), false, null, TextInputMethod.Unicode, CancellationToken.None);
    }
}
