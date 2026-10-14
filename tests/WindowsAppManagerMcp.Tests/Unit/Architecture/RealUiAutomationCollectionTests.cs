using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace WindowsAppManagerMcp.Tests.Unit.Architecture;

/// <summary>
/// Keeps the rule of <see cref="RealUiAutomationCollection"/>: every test class that reaches the real
/// UI Automation client runs in that serial collection. These tests scan the test assembly's IL for
/// the entry points to the real client: the production UiaTree, Uia3Tree (the COM client) and UIAutomationWrapper (which start
/// the client through UiaClientStartup.Shared), UiaClientStartup.Shared itself, and AutomationElement.
/// UiaTree with a test start gate does not count: that gate never calls the client.
/// </summary>
/// <remarks>
/// A class reaches the client when its methods, lambdas or state machines call an entry point directly,
/// or through any method of the test assembly they call (a fixture factory, a helper of another class),
/// followed transitively, together with the lambdas, async and iterator bodies and static constructors
/// of those methods. Calls into the server's code are not followed (the server reaches the client only
/// through the adapters above), and neither are calls through an interface or a virtual method, whose
/// target the IL does not name.
/// </remarks>
public class RealUiAutomationCollectionTests
{
    private static readonly Assembly TestAssembly = typeof(RealUiAutomationCollectionTests).Assembly;

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => unchecked((ushort)op.Value));

    [Fact]
    public void TestClassesThatReachTheRealClient_AreInTheRealUiAutomationCollection()
    {
        // Act
        var reaching = TestClassesReachingTheRealClient();
        var outside = reaching
            .Where(type => !type.GetCustomAttributesData().Any(a =>
                a.AttributeType == typeof(CollectionAttribute) &&
                a.ConstructorArguments.FirstOrDefault().Value as string == RealUiAutomationCollection.Name))
            .Select(type => type.Name)
            .ToList();

        // Assert
        reaching.Should().Contain(typeof(Unit.Native.UiaTreeTests),
            "the scan must see the UiaTree tests' calls to the real client");
        outside.Should().BeEmpty(
            "classes that call the real UI Automation client need [Collection(RealUiAutomationCollection.Name)]: " +
            "its first call fails with E_FAIL when two threads make it at once");
    }

    [Theory]
    [InlineData(nameof(RealClientScanSamples.ThroughAHelperChain))]
    [InlineData(nameof(RealClientScanSamples.ThroughALambdaOfAHelper))]
    [InlineData(nameof(RealClientScanSamples.ThroughAnAsyncHelper))]
    [InlineData(nameof(RealClientScanSamples.ThroughAGenericHelper))]
    [InlineData(nameof(RealClientScanSamples.ThroughAHelperConstructor))]
    [InlineData(nameof(RealClientScanSamples.ThroughAStaticConstructor))]
    public void TheScan_FollowsCallsIntoOtherTypesOfTheTestAssembly(string sample)
    {
        // Arrange
        var method = typeof(RealClientScanSamples).GetMethod(sample)!;

        // Act
        var reaches = new CallScan().ReachesTheRealClient([method]);

        // Assert
        reaches.Should().BeTrue($"{sample} reaches the real client through a helper outside its own class");
    }

    [Fact]
    public void TheScan_DoesNotCountAHelperThatUsesATestStartGate()
    {
        // Arrange
        var method = typeof(RealClientScanSamples).GetMethod(nameof(RealClientScanSamples.ThroughAHelperWithATestGate))!;

        // Act
        var reaches = new CallScan().ReachesTheRealClient([method]);

        // Assert
        reaches.Should().BeFalse("a UiaTree with a test start gate never calls the client");
    }

    /// <summary>The test classes whose methods, lambdas or state machines reach the real client.</summary>
    private static List<Type> TestClassesReachingTheRealClient()
    {
        var scan = new CallScan();
        return TestAssembly.GetTypes()
            .GroupBy(OutermostType)
            .Where(group => IsTestClass(group.Key))
            .Where(group => scan.ReachesTheRealClient(group.SelectMany(DeclaredMethods)))
            .Select(group => group.Key)
            .ToList();
    }

    private const BindingFlags Members =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
        type.GetMethods(Members).Cast<MethodBase>().Concat(type.GetConstructors(Members));

    private static bool StartsTheRealClient(MethodBase callee) => callee.DeclaringType is { } type && (
        (type == typeof(UiaTree) && callee is ConstructorInfo { IsStatic: false } ctor && ctor.GetParameters().Length == 0) ||
        // The UIA3 engine: its production constructor creates the COM client on first use; and the COM client's classes
        // themselves (mocks of its interfaces do not count)
        (type == typeof(Uia3Tree) && callee is ConstructorInfo { IsStatic: false } uia3 && uia3.GetParameters().Length == 0) ||
        (type.Namespace == "Interop.UIAutomationClient" && type.Name.StartsWith("CUIAutomation", StringComparison.Ordinal)) ||
        (type == typeof(UIAutomationWrapper) && callee is ConstructorInfo { IsStatic: false }) ||
        (type == typeof(UiaClientStartup) && callee.Name == "get_" + nameof(UiaClientStartup.Shared)) ||
        type.FullName == "System.Windows.Automation.AutomationElement");

    private static bool IsTestClass(Type type) =>
        type.GetMethods(Members).Any(m => m.IsDefined(typeof(FactAttribute), inherit: true));

    private static Type OutermostType(Type type)
    {
        while (type.DeclaringType != null)
            type = type.DeclaringType;
        return type;
    }

    /// <summary>
    /// Follows calls from a set of methods through the test assembly. Each method's IL is read once per
    /// scan, however many test classes reach it.
    /// </summary>
    private sealed class CallScan
    {
        private readonly Dictionary<MethodBase, IReadOnlyList<MethodBase>> _callees = [];

        /// <summary>
        /// True when <paramref name="start"/> calls an entry point to the real client, directly or through
        /// methods of the test assembly (with their lambdas, async and iterator bodies and static constructors).
        /// </summary>
        public bool ReachesTheRealClient(IEnumerable<MethodBase> start)
        {
            var visited = new HashSet<MethodBase>();
            var pending = new Queue<MethodBase>(start);

            while (pending.Count > 0)
            {
                var method = pending.Dequeue();
                if (!visited.Add(method))
                    continue;

                foreach (var callee in Callees(method))
                {
                    if (StartsTheRealClient(callee))
                        return true;

                    if (callee.DeclaringType?.Assembly != TestAssembly)
                        continue;

                    var definition = Definition(callee);
                    pending.Enqueue(definition);
                    if (definition.GetCustomAttribute<StateMachineAttribute>() is { } stateMachine &&
                        stateMachine.StateMachineType.GetMethod("MoveNext", Members) is { } moveNext)
                        pending.Enqueue(moveNext);
                    if (definition.DeclaringType?.TypeInitializer is { } staticConstructor)
                        pending.Enqueue(staticConstructor);
                }
            }

            return false;
        }

        private IReadOnlyList<MethodBase> Callees(MethodBase method)
        {
            if (!_callees.TryGetValue(method, out var callees))
            {
                callees = CalledMethods(method).ToList();
                _callees[method] = callees;
            }

            return callees;
        }

        /// <summary>
        /// The definition behind a call to a generic method or to a method of a generic type (its
        /// MethodDef token names it), so every instantiation is read once, from the IL the compiler emitted.
        /// </summary>
        private static MethodBase Definition(MethodBase callee) =>
            callee.IsGenericMethod || callee.DeclaringType is { IsGenericType: true }
                ? callee.Module.ResolveMethod(callee.MetadataToken)!
                : callee;
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            yield break;

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
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
                MethodBase? callee = null;
                try
                {
                    callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, methodArguments);
                }
                catch (ArgumentException)
                {
                    // A token that cannot be resolved in this generic context; not a call we track
                }

                if (callee != null)
                    yield return callee;
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
    }
}

/// <summary>
/// Samples for <see cref="RealUiAutomationCollectionTests"/>, which only scans their IL. Nothing calls
/// them, and nothing may: most of them would start the real UI Automation client. They are not test
/// classes, so the collection rule does not apply to them.
/// </summary>
internal static class RealClientScanSamples
{
    public static object ThroughAHelperChain() => RealClientScanHelpers.CreateTree();

    public static Func<object> ThroughALambdaOfAHelper() => RealClientScanHelpers.TreeFactory();

    public static Task<object> ThroughAnAsyncHelper() => RealClientScanHelpers.CreateTreeAsync();

    public static object ThroughAGenericHelper() => RealClientScanHelpers.CreateTreeFor<int>();

    public static object ThroughAHelperConstructor() => new RealClientScanHelpers.TreeHolder();

    public static object? ThroughAStaticConstructor() => RealClientScanHelpers.SharedTreeHolder.Tree;

    public static object ThroughAHelperWithATestGate() => RealClientScanHelpers.CreateTreeWithATestGate();
}

/// <summary>Helpers that reach the real client the ways a shared test fixture could. Never called.</summary>
internal static class RealClientScanHelpers
{
    public static object CreateTree() => CreateTreeInAPrivateHelper();

    private static object CreateTreeInAPrivateHelper() => new UiaTree();

    public static Func<object> TreeFactory() => () => new UiaTree();

    public static async Task<object> CreateTreeAsync()
    {
        await Task.Yield();
        return new UiaTree();
    }

    public static object CreateTreeFor<T>() => new UiaTree();

    public static object CreateTreeWithATestGate() => new UiaTree(new UiaClientStartup(() => { }));

    public sealed class TreeHolder
    {
        private readonly UiaTree _tree = new();

        public override string ToString() => _tree.ToString() ?? string.Empty;
    }

    public static class SharedTreeHolder
    {
        public static object? Tree => Instance;

        private static readonly UiaTree Instance = new();
    }
}
