using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// Tests for the deny-by-default gate that keeps desktop tests (real app launches,
/// process kills, real input) from running unless the developer opts in.
/// </summary>
public class DesktopTestGateTests
{
    #region GetSkipReason

    [Fact]
    public void GetSkipReason_WhenVariableIsOne_ReturnsNull()
    {
        // Act
        var reason = DesktopTestGate.GetSkipReason("1");

        // Assert
        reason.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("11")]
    public void GetSkipReason_WhenVariableIsNotExactlyOne_ReturnsReasonNamingTheVariable(string? value)
    {
        // Act
        var reason = DesktopTestGate.GetSkipReason(value);

        // Assert
        reason.Should().NotBeNullOrWhiteSpace();
        reason.Should().Contain("WAM_ALLOW_DESKTOP_TESTS=1");
    }

    [Fact]
    public void EnvironmentVariable_IsWamAllowDesktopTests()
    {
        DesktopTestGate.EnvironmentVariable.Should().Be("WAM_ALLOW_DESKTOP_TESTS");
    }

    #endregion

    #region Attributes follow the gate

    [Fact]
    public void DesktopFact_SkipMatchesGateForCurrentEnvironment()
    {
        // Arrange
        var expected = DesktopTestGate.GetSkipReason(
            Environment.GetEnvironmentVariable(DesktopTestGate.EnvironmentVariable));

        // Act
        var attribute = new DesktopFactAttribute();

        // Assert
        attribute.Skip.Should().Be(expected);
    }

    [Fact]
    public void DesktopTheory_SkipMatchesGateForCurrentEnvironment()
    {
        // Arrange
        var expected = DesktopTestGate.GetSkipReason(
            Environment.GetEnvironmentVariable(DesktopTestGate.EnvironmentVariable));

        // Act
        var attribute = new DesktopTheoryAttribute();

        // Assert
        attribute.Skip.Should().Be(expected);
    }

    [Fact]
    public void DesktopTheory_IsATheory()
    {
        typeof(TheoryAttribute).IsAssignableFrom(typeof(DesktopTheoryAttribute)).Should().BeTrue();
    }

    #endregion

    #region Canaries

    // These canaries have no side effects. If the gate ever stops skipping desktop tests,
    // they run in a default (not opted-in) run and fail, instead of a real desktop test
    // silently launching apps or sending input.

    [DesktopFact]
    public void DesktopFact_Canary_RunsOnlyWhenOptedIn()
    {
        Environment.GetEnvironmentVariable(DesktopTestGate.EnvironmentVariable).Should().Be("1");
    }

    [DesktopTheory]
    [InlineData(1)]
    [InlineData(2)]
    public void DesktopTheory_Canary_RunsOnlyWhenOptedIn(int _)
    {
        Environment.GetEnvironmentVariable(DesktopTestGate.EnvironmentVariable).Should().Be("1");
    }

    #endregion

    #region Category trait

    [Fact]
    public void DesktopTraitDiscoverer_ReportsDesktopCategory()
    {
        // Arrange
        var discoverer = new DesktopTraitDiscoverer();

        // Act
        var traits = discoverer.GetTraits(Mock.Of<IAttributeInfo>()).ToList();

        // Assert
        traits.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("Category", "Desktop"));
    }

    [Theory]
    [InlineData(typeof(DesktopFactAttribute))]
    [InlineData(typeof(DesktopTheoryAttribute))]
    public void DesktopAttribute_IsWiredToDesktopTraitDiscoverer(Type attributeType)
    {
        // Act
        var discoverer = attributeType.GetCustomAttributesData()
            .SingleOrDefault(a => a.AttributeType == typeof(TraitDiscovererAttribute));

        // Assert - xUnit only reports the trait when the attribute is an ITraitAttribute
        // whose [TraitDiscoverer] names a type that it can load from this assembly.
        typeof(ITraitAttribute).IsAssignableFrom(attributeType).Should().BeTrue();
        discoverer.Should().NotBeNull();
        discoverer!.ConstructorArguments.Select(a => a.Value).Should().Equal(
            typeof(DesktopTraitDiscoverer).FullName,
            typeof(DesktopTraitDiscoverer).Assembly.GetName().Name);
    }

    #endregion

    #region Guard: Desktop-tagged tests must be gated

    /// <summary>
    /// A plain [Trait("Category", "Desktop")] only lets CI filter a test out; it does not stop
    /// a local run from executing it. Any test tagged Desktop must use [DesktopFact] or
    /// [DesktopTheory] so it is skipped unless WAM_ALLOW_DESKTOP_TESTS=1.
    /// </summary>
    [Fact]
    public void TestsTaggedDesktop_UseDenyByDefaultAttributes()
    {
        // Arrange
        var testMethods = typeof(DesktopTestGateTests).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any())
                .Select(m => (Type: t, Method: m)));

        // Act
        var ungated = testMethods
            .Where(x => HasDesktopTrait(x.Type) || HasDesktopTrait(x.Method))
            .Where(x => !x.Method.GetCustomAttributes(inherit: true)
                .Any(a => a is DesktopFactAttribute or DesktopTheoryAttribute))
            .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            .ToList();

        // Assert
        ungated.Should().BeEmpty(
            "tests tagged Category=Desktop must use [DesktopFact] or [DesktopTheory] so they are skipped by default");
    }

    private static bool HasDesktopTrait(MemberInfo member) =>
        member.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(TraitAttribute) &&
            a.ConstructorArguments.Count == 2 &&
            Equals(a.ConstructorArguments[0].Value, DesktopTestGate.TraitName) &&
            Equals(a.ConstructorArguments[1].Value, DesktopTestGate.TraitValue));

    #endregion
}
