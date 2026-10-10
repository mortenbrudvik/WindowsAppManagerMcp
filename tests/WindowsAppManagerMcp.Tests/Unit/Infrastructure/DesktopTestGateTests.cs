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

    #region GetNoInputSkipReason and tiers

    [Fact]
    public void NoInputEnvironmentVariable_IsWamAllowDesktopNoInputTests()
    {
        DesktopTestGate.NoInputEnvironmentVariable.Should().Be("WAM_ALLOW_DESKTOP_NOINPUT_TESTS");
    }

    [Theory]
    [InlineData("1", null)]
    [InlineData("1", "0")]
    [InlineData(null, "1")]
    [InlineData("0", "1")]
    [InlineData("1", "1")]
    public void GetNoInputSkipReason_WhenEitherOptInIsExactlyOne_ReturnsNull(string? noInputValue, string? desktopValue)
    {
        // Act - the full opt-in (WAM_ALLOW_DESKTOP_TESTS=1) includes the narrower no-input tier
        var reason = DesktopTestGate.GetNoInputSkipReason(noInputValue, desktopValue);

        // Assert
        reason.Should().BeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("0", "0")]
    [InlineData("true", null)]
    [InlineData(" 1", null)]
    [InlineData(null, "yes")]
    [InlineData("11", "1 ")]
    public void GetNoInputSkipReason_WhenNeitherOptInIsExactlyOne_ReturnsReasonNamingBothVariables(
        string? noInputValue, string? desktopValue)
    {
        // Act
        var reason = DesktopTestGate.GetNoInputSkipReason(noInputValue, desktopValue);

        // Assert
        reason.Should().NotBeNullOrWhiteSpace();
        reason.Should().Contain("WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1");
        reason.Should().Contain("WAM_ALLOW_DESKTOP_TESTS=1");
    }

    [Fact]
    public void GetSkipReason_ForInputTier_IgnoresTheNoInputOptIn()
    {
        // Arrange - the no-input opt-in must never unlock tests that send input
        var variables = new Dictionary<string, string?> { ["WAM_ALLOW_DESKTOP_NOINPUT_TESTS"] = "1" };

        // Act
        var reason = DesktopTestGate.GetSkipReason(DesktopTier.Input, name => variables.GetValueOrDefault(name));

        // Assert
        reason.Should().NotBeNull();
        reason.Should().Contain("WAM_ALLOW_DESKTOP_TESTS=1");
    }

    [Theory]
    [InlineData("WAM_ALLOW_DESKTOP_NOINPUT_TESTS")]
    [InlineData("WAM_ALLOW_DESKTOP_TESTS")]
    public void GetSkipReason_ForNoInputTier_AcceptsEitherOptIn(string variable)
    {
        // Arrange
        var variables = new Dictionary<string, string?> { [variable] = "1" };

        // Act
        var reason = DesktopTestGate.GetSkipReason(DesktopTier.NoInput, name => variables.GetValueOrDefault(name));

        // Assert
        reason.Should().BeNull();
    }

    [Theory]
    [InlineData(DesktopTier.Input)]
    [InlineData(DesktopTier.NoInput)]
    public void ThrowIfNotAllowed_WhenTheTierIsNotOptedIn_Throws(DesktopTier tier)
    {
        // Act - no variable is set
        var act = () => DesktopTestGate.ThrowIfNotAllowed(tier, _ => null);

        // Assert - harness code calls this before it starts anything, as a second line of defense
        act.Should().Throw<InvalidOperationException>().WithMessage("*WAM_ALLOW_DESKTOP_TESTS=1*");
    }

    [Fact]
    public void ThrowIfNotAllowed_WhenTheTierIsOptedIn_DoesNotThrow()
    {
        // Act
        var act = () => DesktopTestGate.ThrowIfNotAllowed(
            DesktopTier.NoInput, name => name == "WAM_ALLOW_DESKTOP_NOINPUT_TESTS" ? "1" : null);

        // Assert
        act.Should().NotThrow();
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

    [Fact]
    public void DesktopNoInputFact_SkipMatchesNoInputGateForCurrentEnvironment()
    {
        // Arrange
        var expected = DesktopTestGate.GetNoInputSkipReason(
            Environment.GetEnvironmentVariable(DesktopTestGate.NoInputEnvironmentVariable),
            Environment.GetEnvironmentVariable(DesktopTestGate.EnvironmentVariable));

        // Act
        var attribute = new DesktopNoInputFactAttribute();

        // Assert
        attribute.Skip.Should().Be(expected);
    }

    [Fact]
    public void DesktopNoInputTheory_SkipMatchesNoInputGateForCurrentEnvironment()
    {
        // Arrange
        var expected = DesktopTestGate.GetNoInputSkipReason(
            Environment.GetEnvironmentVariable(DesktopTestGate.NoInputEnvironmentVariable),
            Environment.GetEnvironmentVariable(DesktopTestGate.EnvironmentVariable));

        // Act
        var attribute = new DesktopNoInputTheoryAttribute();

        // Assert
        attribute.Skip.Should().Be(expected);
    }

    [Fact]
    public void DesktopNoInputTheory_IsATheory()
    {
        typeof(TheoryAttribute).IsAssignableFrom(typeof(DesktopNoInputTheoryAttribute)).Should().BeTrue();
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

    [DesktopNoInputFact]
    public void DesktopNoInputFact_Canary_RunsOnlyWhenOptedIn()
    {
        new[]
        {
            Environment.GetEnvironmentVariable(DesktopTestGate.NoInputEnvironmentVariable),
            Environment.GetEnvironmentVariable(DesktopTestGate.EnvironmentVariable)
        }.Should().Contain("1");
    }

    [DesktopNoInputTheory]
    [InlineData(1)]
    public void DesktopNoInputTheory_Canary_RunsOnlyWhenOptedIn(int _)
    {
        new[]
        {
            Environment.GetEnvironmentVariable(DesktopTestGate.NoInputEnvironmentVariable),
            Environment.GetEnvironmentVariable(DesktopTestGate.EnvironmentVariable)
        }.Should().Contain("1");
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

    [Fact]
    public void DesktopNoInputTraitDiscoverer_ReportsDesktopAndDesktopNoInputCategories()
    {
        // Arrange
        var discoverer = new DesktopNoInputTraitDiscoverer();

        // Act
        var traits = discoverer.GetTraits(Mock.Of<IAttributeInfo>()).ToList();

        // Assert - Category=Desktop keeps the tier out of "Category!=Desktop" runs and inside the
        // CI check that every desktop test is skipped; Category=DesktopNoInput selects the tier alone.
        traits.Should().BeEquivalentTo(new[]
        {
            new KeyValuePair<string, string>("Category", "Desktop"),
            new KeyValuePair<string, string>("Category", "DesktopNoInput")
        });
    }

    [Theory]
    [InlineData(typeof(DesktopFactAttribute), typeof(DesktopTraitDiscoverer))]
    [InlineData(typeof(DesktopTheoryAttribute), typeof(DesktopTraitDiscoverer))]
    [InlineData(typeof(DesktopNoInputFactAttribute), typeof(DesktopNoInputTraitDiscoverer))]
    [InlineData(typeof(DesktopNoInputTheoryAttribute), typeof(DesktopNoInputTraitDiscoverer))]
    public void DesktopAttribute_IsWiredToItsTraitDiscoverer(Type attributeType, Type discovererType)
    {
        // Act
        var discoverer = attributeType.GetCustomAttributesData()
            .SingleOrDefault(a => a.AttributeType == typeof(TraitDiscovererAttribute));

        // Assert - xUnit only reports the trait when the attribute is an ITraitAttribute
        // whose [TraitDiscoverer] names a type that it can load from the named assembly.
        typeof(ITraitAttribute).IsAssignableFrom(attributeType).Should().BeTrue();
        discoverer.Should().NotBeNull();
        discoverer!.ConstructorArguments.Select(a => a.Value).Should().Equal(
            discovererType.FullName,
            discovererType.Assembly.GetName().Name);
    }

    #endregion

    #region Guard: Desktop-tagged tests must be gated

    /// <summary>
    /// A plain [Trait("Category", "Desktop")] only lets CI filter a test out; it does not stop
    /// a local run from executing it. Any test tagged Desktop (or DesktopNoInput) must use a gate
    /// attribute so it is skipped unless its opt-in variable is set to 1.
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
                .Any(a => a is DesktopFactAttribute or DesktopTheoryAttribute
                    or DesktopNoInputFactAttribute or DesktopNoInputTheoryAttribute))
            .Select(x => $"{x.Type.Name}.{x.Method.Name}")
            .ToList();

        // Assert
        ungated.Should().BeEmpty(
            "tests tagged Category=Desktop or Category=DesktopNoInput must use a gate attribute " +
            "([DesktopFact], [DesktopTheory], [DesktopNoInputFact], [DesktopNoInputTheory]) so they are skipped by default");
    }

    private static bool HasDesktopTrait(MemberInfo member) =>
        member.GetCustomAttributesData().Any(a =>
            a.AttributeType == typeof(TraitAttribute) &&
            a.ConstructorArguments.Count == 2 &&
            Equals(a.ConstructorArguments[0].Value, DesktopTestGate.TraitName) &&
            (Equals(a.ConstructorArguments[1].Value, DesktopTestGate.TraitValue) ||
             Equals(a.ConstructorArguments[1].Value, DesktopTestGate.NoInputTraitValue)));

    #endregion
}
