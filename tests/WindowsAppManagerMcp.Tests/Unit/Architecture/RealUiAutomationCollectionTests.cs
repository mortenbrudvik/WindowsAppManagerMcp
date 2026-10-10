using System.Reflection;
using System.Reflection.Emit;

namespace WindowsAppManagerMcp.Tests.Unit.Architecture;

/// <summary>
/// Keeps the rule of <see cref="RealUiAutomationCollection"/>: every test class that reaches the real
/// UI Automation client runs in that serial collection. These tests scan the test assembly's IL for
/// the entry points to the real client: the production UiaTree and UIAutomationWrapper (which start
/// the client through UiaClientStartup.Shared), UiaClientStartup.Shared itself, and AutomationElement.
/// UiaTree with a test start gate does not count: that gate never calls the client.
/// </summary>
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
        reaching.Should().Contain(typeof(Unit.Services.UIAutomationWrapperTests),
            "the scan must see the wrapper tests' calls to the real client");
        reaching.Should().Contain(typeof(Unit.Native.UiaTreeTests),
            "the scan must see the UiaTree tests' calls to the real client");
        outside.Should().BeEmpty(
            "classes that call the real UI Automation client need [Collection(RealUiAutomationCollection.Name)]: " +
            "its first call fails with E_FAIL when two threads make it at once");
    }

    /// <summary>The test classes whose methods, lambdas or state machines call into the real client.</summary>
    private static List<Type> TestClassesReachingTheRealClient() =>
        TestAssembly.GetTypes()
            .Where(type => type.GetMethods(Members).Cast<MethodBase>().Concat(type.GetConstructors(Members))
                .Any(method => CalledMethods(method).Any(StartsTheRealClient)))
            .Select(OutermostType)
            .Where(IsTestClass)
            .Distinct()
            .ToList();

    private const BindingFlags Members =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static bool StartsTheRealClient(MethodBase callee) => callee.DeclaringType is { } type && (
        (type == typeof(UiaTree) && callee is ConstructorInfo { IsStatic: false } ctor && ctor.GetParameters().Length == 0) ||
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
