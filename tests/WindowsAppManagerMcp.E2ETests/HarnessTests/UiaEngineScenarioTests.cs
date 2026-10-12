using System.Reflection;
using WindowsAppManagerMcp.E2ETests.Scenarios;

namespace WindowsAppManagerMcp.E2ETests.HarnessTests;

/// <summary>
/// The UI Automation engine scenarios (roadmap M3.2) are the release gate for the default engine (TESTING.md, "Release
/// gate: the UI Automation engines"): each must read the fixture with both engines in one gated run, whatever
/// WAM_UIA_ENGINE says in the shell that runs it. These tests read the assembly's metadata only.
/// </summary>
public class UiaEngineScenarioTests
{
    /// <summary>A scenario named so starts a server per engine itself and compares what they read.</summary>
    private const string BothEnginesPrefix = "BothEngines_";

    private static readonly Type[] EngineScenarioClasses = [typeof(FixtureUiaEngineTests), typeof(FixtureUiElementsTests)];

    [Fact]
    public void EveryEngineScenario_RunsWithEachEngine_OrComparesBoth()
    {
        // Arrange
        var engines = Enum.GetValues<UiaEngine>();
        var scenarios = EngineScenarioClasses
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any())
            .ToList();

        // Act - a theory with an InlineData per engine, or a fact that compares both engines
        var withOneEngine = scenarios
            .Where(m => !m.Name.StartsWith(BothEnginesPrefix, StringComparison.Ordinal))
            .Where(m => !EnginesOf(m).ToHashSet().SetEquals(engines))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .ToList();

        // Assert
        scenarios.Should().NotBeEmpty();
        withOneEngine.Should().BeEmpty(
            "an engine scenario left to the server's default engine runs with whatever WAM_UIA_ENGINE says, so the gate " +
            "would read the fixture with one engine only; make it a theory with [InlineData(UiaEngine.Uia3)] and " +
            "[InlineData(UiaEngine.Uia2)], or a " + BothEnginesPrefix + "* fact that starts a server per engine");
    }

    [Fact]
    public void TheBothEnginesScenarios_AreFacts()
    {
        // A theory over the engine would read with one engine per case, which is not a comparison
        var comparisons = EngineScenarioClasses
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.StartsWith(BothEnginesPrefix, StringComparison.Ordinal))
            .ToList();

        comparisons.Should().NotBeEmpty();
        comparisons.Where(m => m.GetCustomAttribute<DesktopNoInputFactAttribute>() == null || m.GetParameters().Length > 0)
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Should().BeEmpty();
    }

    /// <summary>The engine argument of each InlineData of a theory whose first parameter is the engine.</summary>
    private static IEnumerable<UiaEngine> EnginesOf(MethodInfo method) =>
        method.GetParameters() is [{ ParameterType: var type }, ..] && type == typeof(UiaEngine)
            ? method.GetCustomAttributes<InlineDataAttribute>()
                .SelectMany(d => d.GetData(method))
                .Select(row => (UiaEngine)row[0])
            : [];
}
