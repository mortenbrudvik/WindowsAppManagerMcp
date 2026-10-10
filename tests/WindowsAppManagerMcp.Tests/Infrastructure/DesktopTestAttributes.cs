using Xunit.Abstractions;
using Xunit.Sdk;

namespace WindowsAppManagerMcp.Tests.Infrastructure;

/// <summary>
/// Deny-by-default gate for desktop tests: tests that launch or kill real processes,
/// send real mouse or keyboard input, or move, focus or close real windows.
/// Such tests are skipped unless the environment variable
/// <see cref="EnvironmentVariable"/> is set to exactly <c>1</c>.
/// </summary>
/// <remarks>
/// The gate lives in the attribute, not in a runner filter, so it holds for every way the
/// suite can be started: plain <c>dotnet test</c>, an explicit <c>--filter</c>, IDE test
/// explorers and coverage tools. The <c>Category=Desktop</c> trait is reported as well so CI
/// can leave these tests out of the run entirely.
/// </remarks>
public static class DesktopTestGate
{
    /// <summary>Set this variable to <c>1</c> to run desktop tests.</summary>
    public const string EnvironmentVariable = "WAM_ALLOW_DESKTOP_TESTS";

    /// <summary>Trait name reported for every desktop test.</summary>
    public const string TraitName = "Category";

    /// <summary>Trait value reported for every desktop test.</summary>
    public const string TraitValue = "Desktop";

    /// <summary>
    /// Returns the skip reason for a desktop test given the value of
    /// <see cref="EnvironmentVariable"/>, or null when desktop tests are allowed.
    /// </summary>
    public static string? GetSkipReason(string? environmentValue) =>
        environmentValue == "1"
            ? null
            : "Desktop test: it launches, kills or drives real applications on this machine. " +
              $"Set {EnvironmentVariable}=1 to run it.";

    /// <summary>The skip reason for the current process environment.</summary>
    public static string? CurrentSkipReason =>
        GetSkipReason(Environment.GetEnvironmentVariable(EnvironmentVariable));
}

/// <summary>
/// A <see cref="FactAttribute"/> for a test with real desktop side effects. It is skipped
/// unless <c>WAM_ALLOW_DESKTOP_TESTS=1</c> and always carries the trait <c>Category=Desktop</c>.
/// </summary>
[TraitDiscoverer(DesktopTraitDiscoverer.TypeName, DesktopTraitDiscoverer.AssemblyName)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopFactAttribute : FactAttribute, ITraitAttribute
{
    public DesktopFactAttribute()
    {
        Skip = DesktopTestGate.CurrentSkipReason;
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> for a test with real desktop side effects. It is skipped
/// unless <c>WAM_ALLOW_DESKTOP_TESTS=1</c> and always carries the trait <c>Category=Desktop</c>.
/// </summary>
[TraitDiscoverer(DesktopTraitDiscoverer.TypeName, DesktopTraitDiscoverer.AssemblyName)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    public DesktopTheoryAttribute()
    {
        Skip = DesktopTestGate.CurrentSkipReason;
    }
}

/// <summary>
/// Reports <c>Category=Desktop</c> for tests marked with <see cref="DesktopFactAttribute"/>
/// or <see cref="DesktopTheoryAttribute"/>, so <c>--filter "Category!=Desktop"</c> works.
/// </summary>
public sealed class DesktopTraitDiscoverer : ITraitDiscoverer
{
    public const string TypeName = "WindowsAppManagerMcp.Tests.Infrastructure.DesktopTraitDiscoverer";
    public const string AssemblyName = "WindowsAppManagerMcp.Tests";

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>(DesktopTestGate.TraitName, DesktopTestGate.TraitValue);
    }
}
