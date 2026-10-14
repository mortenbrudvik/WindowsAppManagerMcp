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

    /// <summary>The fixture's client area in the 1.15.0 gate's runs: (494, 211), 932 x 628.</summary>
    private static readonly ScreenRect GateClientArea = new(494, 211, 932, 628);

    [Theory]
    [InlineData("TitleBar", 493, 174, 934, 37)]
    [InlineData("MenuBar", 494, 182, 28, 28)]
    [InlineData("MenuItem", 494, 182, 28, 28)]
    [InlineData("Button", 1248, 174, 60, 37)]
    [InlineData("Button", 1308, 174, 59, 37)]
    [InlineData("Button", 1367, 174, 60, 37)]
    public void TheBothEnginesFacts_CompareTheTitleBarAndItsButtons_ByTypeThenByName(string controlType, int x, int y, int width, int height)
    {
        // The elements the first 1.15.0 gate's second run read with other automation ids under UIA2, where Windows serves them itself
        FixtureUiaEngineTests.IsNonClient(controlType, new BoundsDto(x, y, width, height), GateClientArea).Should().BeTrue();
    }

    [Theory]
    [InlineData("Button", 509, 427, 170, 32)]
    [InlineData("Edit", 534, 251, 400, 22)]
    [InlineData("List", 989, 226, 422, 598)]
    [InlineData("ListItem", 991, 228, 397, 24)]
    [InlineData("Edit", 10, 10, 50, 20)]
    public void TheBothEnginesFacts_CompareTheFixturesOwnControls_InFull(string controlType, int x, int y, int width, int height)
    {
        // The fixture's controls are inside its client area; an element outside it that the title bar never holds (an Edit)
        // is compared in full too
        FixtureUiaEngineTests.IsNonClient(controlType, new BoundsDto(x, y, width, height), GateClientArea).Should().BeFalse();
    }

    [Fact]
    public void TheBothEnginesFacts_CompareAButtonWithoutBounds_InFull()
    {
        FixtureUiaEngineTests.IsNonClient("Button", bounds: null, GateClientArea).Should().BeFalse();
    }

    /// <summary>The title bar, the system menu item and a caption button as the 1.15.0 gate read them, and a fixture control.</summary>
    private static readonly (string ControlType, string? Name, BoundsDto? Bounds)[] GateElements =
    [
        ("TitleBar", "WAM Fixture", new BoundsDto(493, 174, 934, 37)),
        ("MenuItem", "System", new BoundsDto(494, 182, 28, 28)),
        ("Button", "Minimize", new BoundsDto(1248, 174, 60, 37)),
        ("Button", "Save", new BoundsDto(509, 427, 170, 32))
    ];

    [Fact]
    public void TheBothEnginesFacts_FindNoNameDifference_WhenTheNonClientNamesAgree()
    {
        FixtureUiaEngineTests.FirstNonClientNameDifference(GateElements, GateElements.ToList(), GateClientArea).Should().BeNull();
    }

    [Fact]
    public void TheBothEnginesFacts_ReportTheFirstNonClientNameThatDiffers_ByTypeAndBothNames()
    {
        // Arrange - a managed proxy whose resources are English on a Windows that names its caption buttons in Norwegian
        var uia3 = GateElements.Select(e => e.Name == "Minimize" ? e with { Name = "Minimer" } : e).ToList();

        // Act
        var difference = FixtureUiaEngineTests.FirstNonClientNameDifference(uia3, GateElements, GateClientArea);

        // Assert - the name is still compared, and the failure says that localization may be the cause (roadmap M3.F29)
        difference.Should().Be("Button: UIA3 'Minimer', UIA2 'Minimize'");
        FixtureUiaEngineTests.NonClientNameReason().Should().Contain("display language").And.Contain("localization");
    }

    [Fact]
    public void TheBothEnginesFacts_LeaveTheNamesOfTheFixturesOwnControls_ToTheFullComparison()
    {
        // The fixture's controls are compared in full, names among the rest, before the non-client names
        var uia2 = GateElements.Select(e => e.Name == "Save" ? e with { Name = "Lagre" } : e).ToList();

        FixtureUiaEngineTests.FirstNonClientNameDifference(GateElements, uia2, GateClientArea).Should().BeNull();
    }

    /// <summary>The engine argument of each InlineData of a theory whose first parameter is the engine.</summary>
    private static IEnumerable<UiaEngine> EnginesOf(MethodInfo method) =>
        method.GetParameters() is [{ ParameterType: var type }, ..] && type == typeof(UiaEngine)
            ? method.GetCustomAttributes<InlineDataAttribute>()
                .SelectMany(d => d.GetData(method))
                .Select(row => (UiaEngine)row[0])
            : [];
}
