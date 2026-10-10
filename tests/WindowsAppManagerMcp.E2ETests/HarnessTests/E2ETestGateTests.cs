using System.Reflection;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The E2E assembly may contain only two kinds of tests: gated scenario tests that start the fixture,
/// and ordinary tests of the harness in this namespace, which never start anything. These tests read
/// the assembly's metadata only.
/// </summary>
public class E2ETestGateTests
{
    private static readonly string HarnessTestsNamespace = typeof(E2ETestGateTests).Namespace!;

    [Fact]
    public void EveryTestOutsideHarnessTests_UsesADesktopGateAttribute()
    {
        // Arrange
        var scenarioTests = TestMethods().Where(m => m.DeclaringType!.Namespace != HarnessTestsNamespace).ToList();

        // Act
        var ungated = scenarioTests.Where(m => GateOf(m) == null).Select(Describe).ToList();

        // Assert
        scenarioTests.Should().NotBeEmpty("the E2E assembly holds the fixture scenario tests");
        ungated.Should().BeEmpty(
            "a test that starts the fixture must be [DesktopNoInputFact]/[DesktopNoInputTheory] or " +
            "[DesktopFact]/[DesktopTheory], so it is skipped unless its tier is opted in; tests of the harness " +
            $"itself belong in {HarnessTestsNamespace}");
    }

    [Fact]
    public void HarnessTests_AreNotGated()
    {
        // A gated test here would only run when opted in, so the harness rules it checks would go
        // unchecked in normal runs and in CI.
        var gated = TestMethods()
            .Where(m => m.DeclaringType!.Namespace == HarnessTestsNamespace && GateOf(m) != null)
            .Select(Describe)
            .ToList();

        gated.Should().BeEmpty();
    }

    [Fact]
    public void ScenarioTests_CoverBothTiers()
    {
        // Act
        var tiers = TestMethods().Select(GateOf).Where(t => t != null).Distinct().ToList();

        // Assert
        tiers.Should().BeEquivalentTo(new DesktopTier?[] { DesktopTier.NoInput, DesktopTier.Input });
    }

    private static IEnumerable<MethodInfo> TestMethods() =>
        typeof(E2ETestGateTests).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any());

    private static DesktopTier? GateOf(MethodInfo method) =>
        method.GetCustomAttributes(inherit: true).Select(a => a switch
        {
            DesktopNoInputFactAttribute or DesktopNoInputTheoryAttribute => DesktopTier.NoInput,
            DesktopFactAttribute or DesktopTheoryAttribute => DesktopTier.Input,
            _ => (DesktopTier?)null
        }).FirstOrDefault(t => t != null);

    private static string Describe(MethodInfo method) => $"{method.DeclaringType!.FullName}.{method.Name}";
}
