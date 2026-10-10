using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace WindowsAppManagerMcp.Tests.Infrastructure;

/// <summary>
/// The two desktop test tiers. Each has its own opt-in, and both are denied by default.
/// </summary>
public enum DesktopTier
{
    /// <summary>
    /// <see cref="DesktopNoInputFactAttribute"/>: tests that start the suite's own fixture app and only
    /// read it (find its windows, read its UI Automation tree, capture it). They send no keyboard or
    /// mouse input, act on no window and kill only the fixture process they started.
    /// Runs when <see cref="DesktopTestGate.NoInputEnvironmentVariable"/> or
    /// <see cref="DesktopTestGate.EnvironmentVariable"/> is <c>1</c>.
    /// </summary>
    NoInput,

    /// <summary>
    /// <see cref="DesktopFactAttribute"/>: tests with real desktop side effects, such as launching or
    /// killing processes, sending real input or focusing, moving or closing windows.
    /// Runs only when <see cref="DesktopTestGate.EnvironmentVariable"/> is <c>1</c>.
    /// </summary>
    Input
}

/// <summary>
/// Deny-by-default gate for desktop tests: tests that launch or kill real processes,
/// send real mouse or keyboard input, or move, focus or close real windows.
/// Such tests are skipped unless the environment variable
/// <see cref="EnvironmentVariable"/> is set to exactly <c>1</c>.
/// A narrower tier, <see cref="DesktopTier.NoInput"/>, covers tests that only start the suite's
/// own fixture app and read or capture it; <see cref="NoInputEnvironmentVariable"/> opts in to that
/// tier alone.
/// </summary>
/// <remarks>
/// The gate lives in the attributes, not in a runner filter, so it holds for every way the
/// suite can be started: plain <c>dotnet test</c>, an explicit <c>--filter</c>, IDE test
/// explorers and coverage tools. The <c>Category=Desktop</c> trait is reported as well so CI
/// can leave these tests out of the run entirely. This project is shared by the unit test project
/// and the end-to-end test project, so both use the same gate and the same variables.
/// </remarks>
public static class DesktopTestGate
{
    /// <summary>Set this variable to <c>1</c> to run desktop tests of both tiers.</summary>
    public const string EnvironmentVariable = "WAM_ALLOW_DESKTOP_TESTS";

    /// <summary>
    /// Set this variable to <c>1</c> to run only the no-input tier
    /// (<see cref="DesktopNoInputFactAttribute"/>, <see cref="DesktopNoInputTheoryAttribute"/>).
    /// </summary>
    public const string NoInputEnvironmentVariable = "WAM_ALLOW_DESKTOP_NOINPUT_TESTS";

    /// <summary>Trait name reported for every desktop test.</summary>
    public const string TraitName = "Category";

    /// <summary>Trait value reported for every desktop test, of both tiers.</summary>
    public const string TraitValue = "Desktop";

    /// <summary>Additional trait value reported for no-input desktop tests.</summary>
    public const string NoInputTraitValue = "DesktopNoInput";

    /// <summary>
    /// Returns the skip reason for a desktop test given the value of
    /// <see cref="EnvironmentVariable"/>, or null when desktop tests are allowed.
    /// </summary>
    public static string? GetSkipReason(string? environmentValue) =>
        environmentValue == "1"
            ? null
            : "Desktop test: it launches, kills or drives real applications on this machine. " +
              $"Set {EnvironmentVariable}=1 to run it.";

    /// <summary>
    /// Returns the skip reason for a no-input desktop test given the values of
    /// <see cref="NoInputEnvironmentVariable"/> and <see cref="EnvironmentVariable"/>, or null when it is
    /// allowed. The full opt-in includes the no-input tier.
    /// </summary>
    public static string? GetNoInputSkipReason(string? noInputValue, string? desktopValue) =>
        noInputValue == "1" || desktopValue == "1"
            ? null
            : "Desktop no-input test: it starts the suite's own fixture app and reads or captures its windows, " +
              $"without sending input. Set {NoInputEnvironmentVariable}=1 (or {EnvironmentVariable}=1) to run it.";

    /// <summary>
    /// Returns the skip reason for a test of <paramref name="tier"/>, reading the opt-in variables
    /// through <paramref name="readVariable"/>, or null when the tier is allowed.
    /// </summary>
    public static string? GetSkipReason(DesktopTier tier, Func<string, string?> readVariable)
    {
        ArgumentNullException.ThrowIfNull(readVariable);

        return tier switch
        {
            DesktopTier.Input => GetSkipReason(readVariable(EnvironmentVariable)),
            DesktopTier.NoInput => GetNoInputSkipReason(
                readVariable(NoInputEnvironmentVariable),
                readVariable(EnvironmentVariable)),
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown desktop tier.")
        };
    }

    /// <summary>The skip reason for the current process environment.</summary>
    public static string? CurrentSkipReason =>
        GetSkipReason(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>The skip reason for a no-input test in the current process environment.</summary>
    public static string? CurrentNoInputSkipReason =>
        GetSkipReason(DesktopTier.NoInput, Environment.GetEnvironmentVariable);

    /// <summary>
    /// Throws unless <paramref name="tier"/> is opted in. Harness code that starts processes or builds a
    /// desktop-facing server calls this first, so a test that forgot its gate attribute still cannot run it.
    /// </summary>
    /// <param name="tier">The tier the caller needs.</param>
    /// <param name="readVariable">Reads an environment variable; defaults to the process environment.</param>
    /// <exception cref="InvalidOperationException">The tier is not opted in.</exception>
    public static void ThrowIfNotAllowed(DesktopTier tier, Func<string, string?>? readVariable = null)
    {
        var reason = GetSkipReason(tier, readVariable ?? Environment.GetEnvironmentVariable);
        if (reason != null)
        {
            throw new InvalidOperationException(
                $"Refusing to touch the desktop from a test that is not opted in to the {tier} tier. {reason}");
        }
    }
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
/// A <see cref="FactAttribute"/> for a no-input desktop test: it starts the suite's own fixture app and
/// only reads or captures it. It is skipped unless <c>WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1</c> or
/// <c>WAM_ALLOW_DESKTOP_TESTS=1</c>, and carries the traits <c>Category=Desktop</c> and
/// <c>Category=DesktopNoInput</c>.
/// </summary>
[TraitDiscoverer(DesktopNoInputTraitDiscoverer.TypeName, DesktopNoInputTraitDiscoverer.AssemblyName)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopNoInputFactAttribute : FactAttribute, ITraitAttribute
{
    public DesktopNoInputFactAttribute()
    {
        Skip = DesktopTestGate.CurrentNoInputSkipReason;
    }
}

/// <summary>
/// A <see cref="TheoryAttribute"/> for a no-input desktop test. It is skipped unless
/// <c>WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1</c> or <c>WAM_ALLOW_DESKTOP_TESTS=1</c>, and carries the traits
/// <c>Category=Desktop</c> and <c>Category=DesktopNoInput</c>.
/// </summary>
[TraitDiscoverer(DesktopNoInputTraitDiscoverer.TypeName, DesktopNoInputTraitDiscoverer.AssemblyName)]
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DesktopNoInputTheoryAttribute : TheoryAttribute, ITraitAttribute
{
    public DesktopNoInputTheoryAttribute()
    {
        Skip = DesktopTestGate.CurrentNoInputSkipReason;
    }
}

/// <summary>
/// Reports <c>Category=Desktop</c> for tests marked with <see cref="DesktopFactAttribute"/>
/// or <see cref="DesktopTheoryAttribute"/>, so <c>--filter "Category!=Desktop"</c> works.
/// </summary>
public sealed class DesktopTraitDiscoverer : ITraitDiscoverer
{
    public const string TypeName = "WindowsAppManagerMcp.Tests.Infrastructure.DesktopTraitDiscoverer";
    public const string AssemblyName = "WindowsAppManagerMcp.Tests.Infrastructure";

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>(DesktopTestGate.TraitName, DesktopTestGate.TraitValue);
    }
}

/// <summary>
/// Reports <c>Category=Desktop</c> and <c>Category=DesktopNoInput</c> for tests marked with
/// <see cref="DesktopNoInputFactAttribute"/> or <see cref="DesktopNoInputTheoryAttribute"/>.
/// A test filter treats a trait with several values as matching any of them, so
/// <c>Category!=Desktop</c> leaves these tests out, <c>Category=Desktop</c> includes them and
/// <c>Category=DesktopNoInput</c> selects only them.
/// </summary>
public sealed class DesktopNoInputTraitDiscoverer : ITraitDiscoverer
{
    public const string TypeName = "WindowsAppManagerMcp.Tests.Infrastructure.DesktopNoInputTraitDiscoverer";
    public const string AssemblyName = DesktopTraitDiscoverer.AssemblyName;

    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>(DesktopTestGate.TraitName, DesktopTestGate.TraitValue);
        yield return new KeyValuePair<string, string>(DesktopTestGate.TraitName, DesktopTestGate.NoInputTraitValue);
    }
}
