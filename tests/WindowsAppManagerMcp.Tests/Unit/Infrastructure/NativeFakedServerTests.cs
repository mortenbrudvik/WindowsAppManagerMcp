using System.Reflection;
using WindowsAppManagerMcp.E2ETests.Harness;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// <see cref="NativeFakedServer.IsAction"/>, with which the denied-target contract counts the fakes' actions and
/// SelfGatedToolsContractTests finds the actions in a route's IL, held to the E2E guard's explicit classification of every
/// native wrapper member (<see cref="WrapperCatalog"/>, linked from WindowsAppManagerMcp.E2ETests): a member the catalog does
/// not call a read is an action, so the two lists cannot drift apart. Reflection only; nothing is called.
/// </summary>
public class NativeFakedServerTests
{
    /// <summary>
    /// The members the catalog refuses for another reason than a change, each with why <see cref="NativeFakedServer.IsAction"/>
    /// counts it a read.
    /// </summary>
    private static readonly Dictionary<(Type Seam, string Member), string> RefusedButNoChange = new()
    {
        [(typeof(INativeClipboardWrapper), nameof(INativeClipboardWrapper.Read))] =
            "reads the user's clipboard: the catalog refuses it so that what the user copied stays out of the test output, but it " +
            "changes nothing on the desktop, so it races no input check",
    };

    /// <summary>The seams the catalog does not classify (they are no native wrapper), each with why every member acts.</summary>
    private static readonly Dictionary<Type, string> OutsideTheCatalog = new()
    {
        [typeof(ICdpTransport)] = "a browser's DevTools connection: anything sent over it is a command to a page",
    };

    [Fact]
    public void IsAction_CountsEveryMemberTheE2ECatalogDoesNotCallARead()
    {
        // Arrange
        var members = NativeFakedServer.Seams.Where(WrapperCatalog.Interfaces.Contains)
            .SelectMany(seam => AllMethods(seam).Select(method => (Seam: seam, Method: method, Member: WrapperCatalog.Find(seam, method))))
            .Where(m => m.Member != null)
            .ToList();

        // Act
        var disagreements = members
            .Where(m => !RefusedButNoChange.ContainsKey((m.Seam, m.Method.Name)))
            .Where(m => (m.Member!.Kind != WrapperCallKind.Read) != NativeFakedServer.IsAction(m.Seam, m.Method))
            .Select(m => $"{m.Seam.Name}.{m.Method.Name}: the catalog says {m.Member!.Kind}, IsAction says " +
                         (NativeFakedServer.IsAction(m.Seam, m.Method) ? "an action" : "a read"))
            .ToList();

        // Assert
        members.Select(m => m.Seam).Should().Contain(
            [typeof(INativeWindowWrapper), typeof(IUiaGridPattern), typeof(INativeHotkeyWrapper), typeof(IDesktopPauseFlag)],
            "the check must see the window wrapper, the grid pattern, the hotkey wrapper and the shared pause");
        disagreements.Should().BeEmpty(
            "NativeFakedServer.IsAction must count as an action every seam member the E2E WrapperCatalog does not call a read " +
            "(a member that scrolls, registers a hotkey or sets the shared pause changes the desktop as a window change does): " +
            "classify the member in IsAction as the catalog does, or add it to RefusedButNoChange with why it changes nothing");
        foreach (var ((seam, member), reason) in RefusedButNoChange)
        {
            var method = AllMethods(seam).Single(m => m.Name == member);
            WrapperCatalog.Find(seam, method)!.Kind.Should().NotBe(WrapperCallKind.Read, "{0}.{1} is on the list because the catalog refuses it", seam.Name, member);
            NativeFakedServer.IsAction(seam, method).Should().BeFalse("{0}.{1} {2}", seam.Name, member, reason);
        }
    }

    [Fact]
    public void EverySeamOutsideTheE2ECatalog_ActsInEveryMember()
    {
        // Arrange - a seam the catalog does not classify has nothing to agree with: IsAction then counts every member
        var outside = NativeFakedServer.Seams.Except(WrapperCatalog.Interfaces).Where(seam => AllMethods(seam).Any()).ToList();

        // Assert
        outside.Should().BeEquivalentTo(OutsideTheCatalog.Keys,
            "a seam with members is classified in the E2E WrapperCatalog, or listed in OutsideTheCatalog with why every member acts");
        foreach (var (seam, reason) in OutsideTheCatalog)
        {
            AllMethods(seam).Where(m => !NativeFakedServer.IsAction(seam, m)).Select(m => m.Name).Should().BeEmpty(
                "every member of {0} acts: {1}", seam.Name, reason);
        }
    }

    private static IReadOnlyList<MethodInfo> AllMethods(Type type) =>
        type.GetMethods().Concat(type.GetInterfaces().SelectMany(i => i.GetMethods())).ToList();
}
