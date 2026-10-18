using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Server;
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
/// says why that use only reads: the read-only window wrapper, the process-tree reads and the shared pause of a name of the
/// test's own.</item>
/// <item>Test code calls a real window, process or hotkey wrapper only through the members on <see cref="ReadOnlyMembers"/> (every
/// sanctioned construction has an entry there), and never closes or kills a process through a process handle. In a type that
/// constructs one of them, calls through their interfaces (<see cref="WrapperInterfaces"/>) are held to the same members: the
/// interface may hold the real one. The hotkey wrapper's parameterless constructor names the servers' own pause and chord
/// marks, so a method that constructs it calls nothing on a hotkey wrapper; the constructor that takes a name opens the pause
/// of any name, so test code passes it only a name visibly of its own (Local\WamTests., traced through the IL).</item>
/// <item>Server code that a test hands a real wrapper to (a constructor called in the same method) calls only those read members
/// on it.</item>
/// <item>Test code builds the server's production container (ServiceRegistration.AddWindowsAppManagerServices), whose services
/// hold the real input, clipboard, hook, window and process wrappers, only in the methods where
/// <see cref="SanctionedProductionContainers"/> says why that use only reads; and a type with such a method calls the server's
/// services and wrappers only through constructors, property getters, static members and the reads on
/// <see cref="SanctionedProductionContainerCalls"/>. A call that runs the services
/// through the framework counts as a call on them: a hosted service's or a host's start, stop or run, a member of a
/// framework interface a server type implements, a tool's invocation through the MCP SDK, and InProcessMcpServer's start and
/// tool call.</item>
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
            "the server's parameterless wrapper is only constructed to read the name it would open (held: ReadOnlyMembers, " +
            "TestCode_ConstructsTheServersHotkeyWrapper_OnlyToReadIt, and TestCode_NamesAHotkeyWrapper_OnlyWithANameOfItsOwn for the names)",
    };

    /// <summary>
    /// The members of the window, process and hotkey wrappers that only read: the only ones test code may call on a real
    /// instance. The window wrapper's are every interface member that ReadOnlyNativeWindowWrapperTests does not list as a window
    /// change. The hotkey wrapper's open no hotkey, mark no chord and post no message: its thread id, whether a server marked a
    /// chord, and the pause it shares, which only a wrapper of a name of the test's own may open (the parameterless one names
    /// the servers' pause: <see cref="TestCode_ConstructsTheServersHotkeyWrapper_OnlyToReadIt"/>; a named one takes any name:
    /// <see cref="TestCode_NamesAHotkeyWrapper_OnlyWithANameOfItsOwn"/>).
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
            .ToHashSet(StringComparer.Ordinal),
        [typeof(NativeHotkeyWrapper)] = new(StringComparer.Ordinal)
        {
            ".ctor", nameof(NativeHotkeyWrapper.GetCurrentThreadId), nameof(NativeHotkeyWrapper.IsChordHeldByAServer),
            nameof(NativeHotkeyWrapper.OpenDesktopPause)
        }
    };

    /// <summary>What a process handle does that changes the process: test code never calls these outside desktop-gated tests.</summary>
    private static readonly HashSet<string> ProcessHandleActions = new(StringComparer.Ordinal)
    {
        nameof(IProcessHandle.Kill), nameof(IProcessHandle.CloseMainWindow)
    };

    /// <summary>The window, process and hotkey wrappers' interfaces, each with the real wrapper whose <see cref="ReadOnlyMembers"/> it is held to.</summary>
    private static readonly Dictionary<Type, Type> WrapperInterfaces = new()
    {
        [typeof(INativeProcessWrapper)] = typeof(NativeProcessWrapper),
        [typeof(INativeWindowWrapper)] = typeof(NativeWindowWrapper),
        [typeof(INativeHotkeyWrapper)] = typeof(NativeHotkeyWrapper)
    };

    /// <summary>The prefix of the names of the shared pause test code may build a hotkey wrapper on.</summary>
    private const string TestPausePrefix = @"Local\WamTests.";

    /// <summary>
    /// What names the servers' own shared objects: the pause and the chord marks, and the hotkey wrapper's fields that hold
    /// them, which reflection reads by name. A name built from any of them is no name of the test's own.
    /// </summary>
    private static readonly string[] ServersHotkeyNames =
    [
        "WindowsAppManagerMcp.InputPaused", "WindowsAppManagerMcp.PauseHotkeyHolder", "DesktopPauseName", "ChordHolderPrefix", "_desktopPauseName"
    ];

    /// <summary>
    /// The methods (by test type's full name and source method) that build a hotkey wrapper on a name the scan cannot trace to a
    /// name of their own (<see cref="TestCode_NamesAHotkeyWrapper_OnlyWithANameOfItsOwn"/>), and why it is no shared name.
    /// </summary>
    private static readonly Dictionary<(string TestType, string Method), string> SanctionedHotkeyNames = new()
    {
        [("WindowsAppManagerMcp.Tests.Unit.Native.NativeHotkeyWrapperSharedPauseTests", "AWrapperWithoutAName_IsRefused")] =
            "passes only an empty and a blank name (its InlineData), which the constructor refuses before it keeps them; nothing is opened",
    };

    /// <summary>
    /// The methods (by test type's full name and source method, with its lambdas and state machine) that build the server's
    /// production container, whose services hold the real native wrappers (input, clipboard, hooks, the pause hotkey, windows,
    /// processes), and why that use only reads. Keyed by the method, so a type reviewed for one use is not reviewed for another:
    /// a server it starts on the production services elsewhere needs its own entry. Field initializers are the constructor's.
    /// </summary>
    private static readonly Dictionary<(string TestType, string Method), string> SanctionedProductionContainers = new()
    {
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "BuildServerServices")] =
            "the registrations of Program.cs the tests resolve to check their types, lifetimes and what they hold; they act through " +
            "none of them, and call only the reads on SanctionedProductionContainerCalls",
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "AddWindowsAppManagerServices_EveryRegistrationIsASingletonThatResolves")] =
            "lists the registrations and resolves each to check its lifetime; nothing is called on them",
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "TheCaptureRegistry_KeepsSignatures_OnlyWhileWaitForVisualIsServed")] =
            "resolves the capture registry for a toolset selection and reads whether it keeps signatures",
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "AddWindowsAppManagerServices_ByDefault_WritesTheAuditLogWhereFromEnvironmentSays")] =
            "resolves the audit log and reads its folder; creating it touches no file",
        [("WindowsAppManagerMcp.Tests.Integration.DependencyInjectionTests", "AddWindowsAppManagerServices_ByDefault_StoresLayoutsWhereFromEnvironmentSays")] =
            "resolves the layout service and reads its path; creating it touches no file",
        [("WindowsAppManagerMcp.Tests.Integration.InputGuardRegistrationTests", ".ctor")] =
            "resolves the input guard, the policy and the safety info with the read-only window wrapper, and reads what they hold",
        [("WindowsAppManagerMcp.Tests.Integration.InputSessionRegistrationTests", ".ctor")] =
            "resolves the session and the pause hotkey with the read-only window wrapper and reads what they hold; the hosted " +
            "service that would register the hotkey and share the pause is never started",
        [("WindowsAppManagerMcp.Tests.Integration.ScrollClipboardRegistrationTests", ".ctor")] =
            "resolves the input and clipboard services with the read-only window wrapper and reads what they hold; the clipboard " +
            "wrapper starts its thread only when it is called",
        [("WindowsAppManagerMcp.Tests.Integration.SecurityServiceRegistrationTests", ".ctor")] =
            "resolves the host guard and the security probes with the read-only window wrapper; they only read the process tree " +
            "and the console and foreground windows",
        [("WindowsAppManagerMcp.Tests.Fixtures.ServerToolCatalog", "DiscoverServedToolNames")] =
            "reads the tool collection's names from the options; no service is resolved",
        [("WindowsAppManagerMcp.Tests.Contract.DeniedTargetContractTests", "ServedSchemas")] =
            "reads the served tools' input schemas from the options (no service is resolved); its calls go to NativeFakedServer",
        [("WindowsAppManagerMcp.Tests.Contract.SelfGatedToolsContractTests", "ProductionRegistrations")] =
            "reads the production registrations' implementation types; no container is built from them",
        [("WindowsAppManagerMcp.Tests.Fixtures.NativeFakedServer", ".ctor")] =
            "replaces every native seam with a fake before the container is built, and refuses to start when a service still " +
            "holds a real wrapper",
    };

    /// <summary>
    /// The calls a type with a method on <see cref="SanctionedProductionContainers"/> makes on the server's services and wrappers beyond
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
        [("WindowsAppManagerMcp.Tests.Fixtures.NativeFakedServer", "InProcessMcpServer.StartAsync")] =
            "hosts its own services, whose every native seam is a fake (checked before it starts), without their hosted services",
        [("WindowsAppManagerMcp.Tests.Contract.DeniedTargetContractTests", "InProcessMcpServer.CallToolAsync")] =
            "calls tools on NativeFakedServer's host, whose every native seam is a fake; the production container it builds only " +
            "gives the tools' schemas",
        [("WindowsAppManagerMcp.Tests.Contract.SelfGatedToolsContractTests", "InProcessMcpServer.StartAsync")] =
            "hosts the contract run's mocks (ContractServices) and the tools, not the production services: the type's one call of " +
            "AddWindowsAppManagerServices (ProductionRegistrations, reviewed on its own) only reads the registrations",
        [("WindowsAppManagerMcp.Tests.Contract.SelfGatedToolsContractTests", "InProcessMcpServer.CallToolAsync")] =
            "calls tools on that host of the contract run's mocks",
    };

    /// <summary>
    /// The framework's interfaces and base classes the server's types implement (IHostedService, for example), outside the BCL:
    /// a call through one of them runs the server's code as a call on the server's own type would.
    /// </summary>
    private static readonly HashSet<Type> FrameworkContracts = ServerAssembly.GetTypes()
        .SelectMany(t => t.GetInterfaces().Concat(BaseTypes(t)))
        .Select(t => t.IsConstructedGenericType ? t.GetGenericTypeDefinition() : t)
        .Where(t => t.Assembly != ServerAssembly && t.Namespace is { } ns &&
                    (ns.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) || ns.StartsWith("ModelContextProtocol", StringComparison.Ordinal)))
        .ToHashSet();

    /// <summary>The lifecycle members of the hosting framework: a host's or a hosted service's start, stop and run.</summary>
    private static readonly HashSet<string> HostingLifecycle = new(StringComparer.Ordinal)
    {
        "Start", "StartAsync", "Stop", "StopAsync", "Run", "RunAsync", "WaitForShutdown", "WaitForShutdownAsync", "StartingAsync",
        "StartedAsync", "StoppingAsync", "StoppedAsync", "ExecuteAsync", "StopApplication"
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
    public void TestCode_CallsARealWindowProcessOrHotkeyWrapper_OnlyThroughItsReadMembers()
    {
        // Act - on the real wrappers, and through their interfaces in the types that construct a real one
        var calls = CallsOnARealWrapper(TestCode()).ToList();

        // Assert
        calls.Should().Contain(c => c.Owner.Name == "ProcessAncestryTests" && c.Callee.Name == nameof(NativeProcessWrapper.GetDescendantsOf),
            "the scan must see the process tests' calls on the real process wrapper");
        calls.Should().Contain(c => c.Owner == typeof(ReadOnlyNativeWindowWrapper) && c.Callee.DeclaringType == typeof(INativeWindowWrapper),
            "the scan must see the read-only window wrapper's calls through the interface on the real one");
        var samples = CallsOnARealWrapper(SampleCode()).ToList();
        samples.Should().Contain(c => c.Callee.DeclaringType == typeof(NativeProcessWrapper) &&
                                      c.Callee.Name == nameof(NativeProcessWrapper.StartProcess) && !IsReadOnly(c.Callee),
            "the scan must see a sample's process start as an action");
        samples.Should().Contain(c => c.Callee.DeclaringType == typeof(INativeProcessWrapper) &&
                                      c.Callee.Name == nameof(INativeProcessWrapper.StartProcess) && !IsReadOnly(c.Callee),
            "the scan must see a process start through the interface, in a type that constructs the real wrapper, as an action");
        samples.Should().Contain(c => c.Callee.DeclaringType == typeof(INativeWindowWrapper) &&
                                      c.Callee.Name == nameof(INativeWindowWrapper.SetWindowPos) && !IsReadOnly(c.Callee),
            "the scan must see a window change through the interface, in a type that constructs the real wrapper, as an action");
        samples.Should().Contain(c => c.Callee.DeclaringType == typeof(INativeHotkeyWrapper) &&
                                      c.Callee.Name == nameof(INativeHotkeyWrapper.RegisterHotKey) && !IsReadOnly(c.Callee),
            "the scan must see a hotkey registered through the interface, in a type that constructs the real wrapper, as an action");
        samples.Should().Contain(c => c.Callee.DeclaringType == typeof(NativeHotkeyWrapper) &&
                                      c.Callee.Name == nameof(NativeHotkeyWrapper.MarkChordHolder) && !IsReadOnly(c.Callee),
            "the scan must see a chord marked on the real hotkey wrapper as an action");
        calls.Should().Contain(c => c.Owner.Name == nameof(Tests.Unit.Native.NativeHotkeyWrapperSharedPauseTests) &&
                                    c.Callee.Name == nameof(NativeHotkeyWrapper.OpenDesktopPause),
            "the scan must see the shared pause tests' calls on the real hotkey wrapper");
        SanctionedConstructions.Keys.Select(k => k.Wrapper).Should().BeSubsetOf(ReadOnlyMembers.Keys,
            "a real wrapper test code may construct needs the members it may call on it");
        calls.Where(c => !IsReadOnly(c.Callee)).Select(c => c.Description).Should().BeEmpty(
            "test code may only read through a real window, process or hotkey wrapper; a member that changes a window, starts a " +
            "process, registers a hotkey or marks a chord belongs to a desktop-gated test, or to ReadOnlyMembers after review");
    }

    [Fact]
    public void TestCode_ConstructsTheServersHotkeyWrapper_OnlyToReadIt()
    {
        // Arrange - the parameterless constructor opens the servers' own names (Local\WindowsAppManagerMcp.InputPaused and the
        // chord marks): a method that constructs it, with its lambdas and state machine, may call no hotkey wrapper member
        static IEnumerable<string> Uses(IEnumerable<MethodBase> methods) =>
            Calls(methods)
                .GroupBy(c => (c.Owner, Method: SourceMethodName(c.Caller) ?? c.Caller.Name))
                .Where(g => g.Any(c => c.Callee is ConstructorInfo { IsStatic: false } ctor && ctor.DeclaringType == typeof(NativeHotkeyWrapper) &&
                                       ctor.GetParameters().Length == 0))
                .SelectMany(g => g.Where(c => c.Callee is MethodInfo &&
                                              (c.Callee.DeclaringType == typeof(NativeHotkeyWrapper) || c.Callee.DeclaringType == typeof(INativeHotkeyWrapper))))
                .Select(c => c.Description);

        // Act
        var uses = Uses(TestCode()).ToList();

        // Assert
        Calls(TestCode()).Should().Contain(c => c.Owner.Name == nameof(Tests.Unit.Native.NativeHotkeyWrapperSharedPauseTests) &&
                                                c.Callee is ConstructorInfo && c.Callee.DeclaringType == typeof(NativeHotkeyWrapper) &&
                                                c.Callee.GetParameters().Length == 0,
            "the scan must see the shared pause tests construct the server's wrapper to read its name");
        Uses(SampleCode()).Should().Contain(use => use.EndsWith("NativeHotkeyWrapper.OpenDesktopPause", StringComparison.Ordinal),
            "the scan must see a sample open the servers' pause");
        uses.Should().BeEmpty(
            "the server's hotkey wrapper opens the pause and chord marks every server on the desktop shares: construct the wrapper " +
            "with a name of the test's own (Local\\WamTests.<test>.<guid>) to call it");
    }

    [Fact]
    public void TestCode_NamesAHotkeyWrapper_OnlyWithANameOfItsOwn()
    {
        // Arrange - the constructor that takes a name opens the pause of any name it is given, the servers' own too: the name
        // must visibly be the test's own (Local\WamTests.), a literal, a test helper that builds it from one, or a local only
        // those set
        static string Key(HotkeyNaming n) => $"{n.Owner.Name}.{SourceMethodName(n.Caller) ?? n.Caller.Name}";

        // Act
        var namings = HotkeyWrapperNamings(TestCode()).ToList();
        var samples = HotkeyWrapperNamings(SampleCode()).ToList();

        // Assert
        namings.Should().Contain(n => n.Owner == typeof(Tests.Unit.Native.NativeHotkeyWrapperSharedPauseTests) && n.OfItsOwn,
            "the scan must see the shared pause tests name their wrappers with names of their own");
        samples.Where(n => !n.OfItsOwn).Select(Key).Should().Contain(
            [
                $"{nameof(NativeWrapperScanSamples)}.{nameof(NativeWrapperScanSamples.OpensTheServersPauseByItsName)}",
                $"{nameof(NativeWrapperScanSamples)}.{nameof(NativeWrapperScanSamples.OpensTheServersPauseByItsField)}",
                $"{nameof(NativeWrapperScanSamples)}.{nameof(NativeWrapperScanSamples.OpensTheServersPauseThroughAHelper)}"
            ], "the scan must see a sample name a wrapper after the servers' pause: by its literal, its field and a helper");
        samples.Where(n => n.OfItsOwn).Select(Key).Should().Contain(
            [
                $"{nameof(NativeWrapperScanSamples)}.{nameof(NativeWrapperScanSamples.MarksAChord)}",
                $"{nameof(NativeWrapperScanSamples)}.{nameof(NativeWrapperScanSamples.OpensAPauseOfItsOwn)}"
            ], "a literal and a helper's name of the test's own are its own");
        var untraced = namings.Where(n => !n.OfItsOwn).ToList();
        untraced.Where(n => !SanctionedHotkeyNames.ContainsKey((n.Owner.FullName!, SourceMethodName(n.Caller) ?? n.Caller.Name)))
            .Select(n => n.Description)
            .Should().BeEmpty(
                "a hotkey wrapper opens the pause of the name it is built on, and the servers' (Local\\WindowsAppManagerMcp.InputPaused) " +
                "would pause or resume every server on the desktop: pass a literal of Local\\WamTests.<test>, or a test helper " +
                "that builds one (UniqueName), directly or through a local; or add the method to SanctionedHotkeyNames with why its name is its own");
        SanctionedHotkeyNames.Keys.Should().BeSubsetOf(untraced.Select(n => (n.Owner.FullName!, SourceMethodName(n.Caller) ?? n.Caller.Name)),
            "an entry names a method whose name the scan cannot trace");
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
                .Where(c => c.GetParameters().Any(p => WrapperInterfaces.ContainsKey(p.ParameterType)))
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
        // Act - each method that builds it, with its lambdas and state machine (a build in the configureServices of a server
        // the method starts is that method's)
        var builders = ContainerBuilders(TestCode()).ToList();
        var samples = ContainerBuilders(SampleCode()).ToList();

        // Assert
        builders.Select(b => b.Owner).Should().Contain([typeof(Integration.ScrollClipboardRegistrationTests), typeof(NativeFakedServer)],
            "the scan must see the registration tests and the faked server build the production container");
        samples.Should().Contain(b => b.Owner == typeof(NativeWrapperScanSamples) && b.Method == nameof(NativeWrapperScanSamples.ActsThroughTheProductionContainer) &&
                                      b.Caller.Name != b.Method,
            "the scan must see a sample build the production container in the services of a server it starts");
        builders.Where(b => !SanctionedProductionContainers.ContainsKey((b.Owner.FullName!, b.Method))).Select(b => b.Description).Distinct().Should().BeEmpty(
            "the production container's services hold the real input, clipboard, hook, window and process wrappers: replace them " +
            "with fakes (NativeFakedServer), or add the method to SanctionedProductionContainers with why its use only reads");
        SanctionedProductionContainers.Keys.Should().BeSubsetOf(builders.Select(b => (b.Owner.FullName!, b.Method)),
            "an entry names a method that builds the container");
    }

    [Fact]
    public void TestCode_ThatBuildsTheProductionContainer_CallsItsServicesOnlyThroughReads()
    {
        // Act - in each type that builds the container: the calls on the server's types that may act
        var calls = Calls(TestCode())
            .Where(c => ProductionContainerTypes.Contains(c.Owner.FullName!) && MayAct(c.Callee))
            .ToList();

        // Assert
        calls.Should().Contain(c => c.Owner == typeof(Integration.InputGuardRegistrationTests) && c.Callee.Name == nameof(IInputSafetyInfo.Read),
            "the scan must see a registration test's call on a resolved service");
        Calls(SampleCode()).Where(c => MayAct(c.Callee)).Should()
            .Contain(c => c.Callee.DeclaringType == typeof(INativeClipboardWrapper) && c.Callee.Name == nameof(INativeClipboardWrapper.WriteText),
                "the scan must see a sample write the clipboard through a resolved wrapper")
            .And.Contain(c => c.Callee.DeclaringType == typeof(IInputSimulationService),
                "the scan must see a sample type through a resolved service")
            .And.Contain(c => c.Callee.DeclaringType == typeof(IHostedService) && c.Callee.Name == nameof(IHostedService.StartAsync),
                "the scan must see a sample start the hosted services, which register the pause hotkey and open the shared pause")
            .And.Contain(c => c.Callee.DeclaringType == typeof(HostingAbstractionsHostExtensions) && c.Callee.Name == nameof(HostingAbstractionsHostExtensions.Start),
                "the scan must see a sample start a host")
            .And.Contain(c => c.Callee.DeclaringType == typeof(InProcessMcpServer) && c.Callee.Name == nameof(InProcessMcpServer.StartAsync),
                "the scan must see a sample host the production services in the in-process server")
            .And.Contain(c => c.Callee.DeclaringType == typeof(InProcessMcpServer) && c.Callee.Name == nameof(InProcessMcpServer.CallToolAsync),
                "the scan must see a sample call a tool on it")
            .And.Contain(c => c.Callee.DeclaringType == typeof(McpServerTool) && c.Callee.Name == nameof(McpServerTool.InvokeAsync),
                "the scan must see a sample invoke a tool of the options' collection");
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
    /// The calls on a real window, process or hotkey wrapper, and, in a type that constructs one of them, every call through
    /// their interfaces: an INativeProcessWrapper there may hold the real one.
    /// </summary>
    private static IEnumerable<Call> CallsOnARealWrapper(IEnumerable<MethodBase> methods)
    {
        var calls = Calls(methods).ToList();
        var owners = calls.Where(c => c.Callee is ConstructorInfo { IsStatic: false } && ReadOnlyMembers.ContainsKey(c.Callee.DeclaringType!))
            .Select(c => c.Owner)
            .ToHashSet();
        return calls.Where(c => ReadOnlyMembers.ContainsKey(c.Callee.DeclaringType!) ||
                                (owners.Contains(c.Owner) && WrapperInterfaces.ContainsKey(c.Callee.DeclaringType!)));
    }

    /// <summary>A hotkey wrapper built on a name in test code, where the name comes from, and whether it is the test's own.</summary>
    private sealed record HotkeyNaming(Type Owner, MethodBase Caller, string Source, bool OfItsOwn)
    {
        public string Description => $"{Caller.DeclaringType?.FullName}.{Caller.Name} builds a NativeHotkeyWrapper on {Source}";
    }

    /// <summary>Every construction of a hotkey wrapper on a name (NativeHotkeyWrapper(string)) in <paramref name="methods"/>.</summary>
    private static IEnumerable<HotkeyNaming> HotkeyWrapperNamings(IEnumerable<MethodBase> methods)
    {
        foreach (var method in methods)
        {
            var code = IlCalls.InstructionsOf(method);
            for (var i = 0; i < code.Count; i++)
            {
                if (code[i].OpCode == OpCodes.Newobj && code[i].Method is ConstructorInfo ctor && ctor.DeclaringType == typeof(NativeHotkeyWrapper) &&
                    ctor.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(string))
                {
                    var (source, ofItsOwn) = NameSource(code, i - 1);
                    yield return new HotkeyNaming(IlCalls.OutermostType(method.DeclaringType!), method, source, ofItsOwn);
                }
            }
        }
    }

    /// <summary>
    /// What pushed the name the instruction after <paramref name="at"/> takes, and whether it is visibly the test's own: a literal
    /// of <see cref="TestPausePrefix"/>; a call of a test method that returns one built from such a literal and names nothing of the
    /// servers' (<see cref="ServersHotkeyNames"/>); or a local whose address is never taken and that only those set. Anything
    /// else (a field, an argument, a string built in place) is not traced, so the check fails closed.
    /// </summary>
    private static (string Source, bool OfItsOwn) NameSource(IReadOnlyList<IlInstruction> code, int at)
    {
        if (at < 0)
        {
            return ("a value the scan cannot read", false);
        }

        var instruction = code[at];
        if (instruction.Text is { } text)
        {
            return ($"'{text}'", IsANameOfItsOwn(text));
        }

        if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) && instruction.Method is MethodInfo helper)
        {
            return ($"{helper.DeclaringType?.Name}.{helper.Name}()", BuildsANameOfItsOwn(helper));
        }

        if (LoadedLocal(instruction) is { } local)
        {
            // Each store of the local takes a name of the test's own; a local set from another local is not followed
            var stores = Enumerable.Range(0, code.Count).Where(j => Local(code[j], StoreLocal, OpCodes.Stloc_S, OpCodes.Stloc) == local).ToList();
            var addressed = code.Any(c => Local(c, [], OpCodes.Ldloca_S, OpCodes.Ldloca) == local);
            var ofItsOwn = stores.Count > 0 && !addressed &&
                           stores.All(j => j > 0 && LoadedLocal(code[j - 1]) == null && NameSource(code, j - 1).OfItsOwn);
            return ($"the local {local}", ofItsOwn);
        }

        return ($"the result of {instruction.OpCode.Name}", false);
    }

    private static readonly OpCode[] LoadLocal = [OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3];

    private static readonly OpCode[] StoreLocal = [OpCodes.Stloc_0, OpCodes.Stloc_1, OpCodes.Stloc_2, OpCodes.Stloc_3];

    private static int? LoadedLocal(IlInstruction instruction) => Local(instruction, LoadLocal, OpCodes.Ldloc_S, OpCodes.Ldloc);

    /// <summary>The local an instruction names: by its opcode (<paramref name="numbered"/>: locals 0 to 3), or by its operand.</summary>
    private static int? Local(IlInstruction instruction, OpCode[] numbered, params OpCode[] indexed) =>
        Array.IndexOf(numbered, instruction.OpCode) is var number and >= 0 ? number
        : indexed.Contains(instruction.OpCode) ? instruction.Operand
        : null;

    private static bool IsANameOfItsOwn(string name) =>
        name.StartsWith(TestPausePrefix, StringComparison.Ordinal) && !NamesTheServers(name);

    private static bool NamesTheServers(string text) => ServersHotkeyNames.Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A test method that returns a name built from a literal of <see cref="TestPausePrefix"/>, and loads nothing of the servers' names.</summary>
    private static bool BuildsANameOfItsOwn(MethodInfo helper)
    {
        if (!TestAssemblies.Contains(helper.DeclaringType?.Assembly) || helper.ReturnType != typeof(string))
        {
            return false;
        }

        var strings = IlCalls.InstructionsOf(helper).Select(i => i.Text).OfType<string>().ToList();
        return strings.Any(s => s.StartsWith(TestPausePrefix, StringComparison.Ordinal)) && !strings.Any(NamesTheServers);
    }

    /// <summary>A call of ServiceRegistration.AddWindowsAppManagerServices: the outermost type, the source method it is in, and the calling code.</summary>
    private sealed record ContainerBuilder(Type Owner, string Method, MethodBase Caller)
    {
        public string Description => $"{Owner.FullName}.{Method}";
    }

    /// <summary>
    /// The calls of ServiceRegistration.AddWindowsAppManagerServices, each by the source method it is in: a call in a lambda, a
    /// local function or a state machine is the method's that holds it (a configureServices handed to a server it starts).
    /// </summary>
    private static IEnumerable<ContainerBuilder> ContainerBuilders(IEnumerable<MethodBase> methods) =>
        Calls(methods)
            .Where(c => c.Callee.DeclaringType == typeof(ServiceRegistration) && c.Callee.Name == nameof(ServiceRegistration.AddWindowsAppManagerServices))
            .Select(c => new ContainerBuilder(c.Owner, SourceMethodName(c.Caller) ?? c.Caller.Name, c.Caller));

    /// <summary>The test types (by full name) with a method on <see cref="SanctionedProductionContainers"/>: their calls are held to reads.</summary>
    private static readonly HashSet<string> ProductionContainerTypes = SanctionedProductionContainers.Keys.Select(k => k.TestType).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A call on a server type that may act: an instance member that is no constructor, no property getter and no part of a
    /// record's copy (its clone and init-only setters), or a static member of a Native wrapper. Or a call that runs the server's
    /// services through the framework (<see cref="RunsTheServices"/>).
    /// </summary>
    private static bool MayAct(MethodBase callee) =>
        callee.DeclaringType is { } type && callee is not ConstructorInfo &&
        (type.Assembly == ServerAssembly
            ? callee.IsStatic
                ? type.Namespace == typeof(NativeWindowWrapper).Namespace
                : !IsGetter(callee) && callee.Name != "<Clone>$" && !IsInitOnlySetter(callee)
            : RunsTheServices(callee, type));

    /// <summary>
    /// A call outside the server's types that runs its services: a member (no getter, no Dispose) of a framework interface or
    /// base class a server type implements (<see cref="FrameworkContracts"/>: IHostedService's StartAsync, for example); a host's
    /// or a hosted service's start, stop or run (<see cref="HostingLifecycle"/>, any type of Microsoft.Extensions.Hosting); an
    /// MCP session's request or a tool's invocation through the SDK (McpClient, McpServer, McpServerTool, an AIFunction such as
    /// McpClientTool); and InProcessMcpServer's start and tool call.
    /// </summary>
    private static bool RunsTheServices(MethodBase callee, Type type)
    {
        if (IsGetter(callee) || callee.Name is nameof(IDisposable.Dispose) or nameof(IAsyncDisposable.DisposeAsync))
        {
            return false;
        }

        var definition = type.IsConstructedGenericType ? type.GetGenericTypeDefinition() : type;
        return FrameworkContracts.Contains(definition) ||
               (type.Namespace?.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal) == true && HostingLifecycle.Contains(callee.Name)) ||
               typeof(McpSession).IsAssignableFrom(type) || typeof(McpServerTool).IsAssignableFrom(type) || typeof(AIFunction).IsAssignableFrom(type) ||
               (type == typeof(InProcessMcpServer) && callee.Name is nameof(InProcessMcpServer.StartAsync) or nameof(InProcessMcpServer.CallToolAsync));
    }

    private static IEnumerable<Type> BaseTypes(Type type)
    {
        for (var current = type.BaseType; current != null && current != typeof(object); current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool IsGetter(MethodBase callee) => callee.IsSpecialName && callee.Name.StartsWith("get_", StringComparison.Ordinal);

    private static bool IsInitOnlySetter(MethodBase callee) =>
        callee is MethodInfo { IsSpecialName: true } setter && setter.Name.StartsWith("set_", StringComparison.Ordinal) &&
        setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    private static string Member(MethodBase callee) => $"{callee.DeclaringType!.Name}.{callee.Name}";

    private static bool IsReadOnly(MethodBase callee) =>
        ReadOnlyMembers.TryGetValue(WrapperInterfaces.GetValueOrDefault(callee.DeclaringType!, callee.DeclaringType!), out var reads) &&
        reads.Contains(callee.Name);

    private static bool IsProcessHandleAction(MethodBase callee) =>
        callee.DeclaringType is { } type && typeof(IProcessHandle).IsAssignableFrom(type) && ProcessHandleActions.Contains(callee.Name);

    /// <summary>A call on the window, process or hotkey wrapper's interface, or on a process handle, that changes something.</summary>
    private static bool IsWrapperAction(MethodBase callee) =>
        IsProcessHandleAction(callee) ||
        (callee.DeclaringType is { } type && WrapperInterfaces.TryGetValue(type, out var wrapper) && !ReadOnlyMembers[wrapper].Contains(callee.Name));

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

        // Through the framework: the hosted services (the pause hotkey and the shared pause), a host, a tool of the options'
        // collection, and the in-process server over the production services
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        provider.GetRequiredService<IHost>().Start();
        await provider.GetServices<McpServerTool>().First().InvokeAsync(null!, CancellationToken.None);
        await using var server = await InProcessMcpServer.StartAsync([], configureServices: services => services.AddWindowsAppManagerServices());
        await server.CallToolAsync("type_text", new Dictionary<string, object?> { ["text"] = "sample" });
    }

    public static bool RegistersAHotkeyThroughItsInterface()
    {
        INativeHotkeyWrapper hotkeys = new NativeHotkeyWrapper(@"Local\WamTests.ScanSample");
        return hotkeys.RegisterHotKey(1, 0x0002, 0x13).Succeeded;
    }

    public static IDisposable MarksAChord() => new NativeHotkeyWrapper(@"Local\WamTests.ScanSample").MarkChordHolder("sample");

    public static IDesktopPauseFlag OpensTheServersPause()
    {
        var hotkeys = new NativeHotkeyWrapper();
        return hotkeys.OpenDesktopPause();
    }

    // The constructor that takes a name, on the servers' pause: by its literal, by its field, and by a helper that also loads a
    // name of the test's own; and on a name of the test's own through a helper and a local

    public static IDesktopPauseFlag OpensTheServersPauseByItsName() =>
        new NativeHotkeyWrapper(@"Local\WindowsAppManagerMcp.InputPaused").OpenDesktopPause();

    public static IDesktopPauseFlag OpensTheServersPauseByItsField()
    {
        var name = (string)typeof(NativeHotkeyWrapper).GetField("DesktopPauseName", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return new NativeHotkeyWrapper(name).OpenDesktopPause();
    }

    public static IDesktopPauseFlag OpensTheServersPauseThroughAHelper() => new NativeHotkeyWrapper(ServersPauseName()).OpenDesktopPause();

    public static IDesktopPauseFlag OpensAPauseOfItsOwn()
    {
        var name = PauseNameOfItsOwn();
        return new NativeHotkeyWrapper(name).OpenDesktopPause();
    }

    private static string ServersPauseName() =>
        typeof(NativeHotkeyWrapper).GetField("DesktopPauseName", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string ??
        @"Local\WamTests.ScanSample";

    private static string PauseNameOfItsOwn() => $@"Local\WamTests.ScanSample.{Guid.NewGuid():N}";
}
