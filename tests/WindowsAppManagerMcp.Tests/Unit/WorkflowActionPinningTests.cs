using System.Text.RegularExpressions;

namespace WindowsAppManagerMcp.Tests.Unit;

/// <summary>
/// A workflow step that names an action by a tag (<c>owner/action@v2</c>) runs whatever that tag
/// points to when the workflow runs, and the action's owner can move the tag at any time. The
/// release job runs with a token that can write to the repository, and users install the plugin
/// from it, so actions from outside GitHub must be pinned to a full commit SHA (with the version
/// in a comment). GitHub's own actions (<c>actions/*</c>) may stay on tags.
/// These tests only read files in the repository.
/// </summary>
public class WorkflowActionPinningTests
{
    private static readonly Regex UsesLine = new(
        @"^\s*(?:-\s+)?uses:\s*(?<ref>[^\s#]+)\s*(?:#\s*(?<comment>.*))?$",
        RegexOptions.Multiline);

    private static readonly Regex FullSha = new("^[0-9a-f]{40}$");

    [Fact]
    public void ThirdPartyActions_ArePinnedToACommitShaWithTheVersionInAComment()
    {
        // Arrange
        var steps = ReadUsesLines();

        // Assert
        steps.Should().NotBeEmpty("the workflows use actions such as actions/checkout");
        var unpinned = steps
            .Where(step => !IsGitHubOwnedOrLocal(step.Reference))
            .Where(step => !IsPinned(step.Reference) || string.IsNullOrWhiteSpace(step.Comment))
            .Select(step => $"{step.File}: uses: {step.Reference}")
            .ToList();
        unpinned.Should().BeEmpty(
            "actions from outside GitHub must be pinned to a full commit SHA, for example " +
            "'owner/action@<40 hex digits> # v1.2.3', so a moved tag cannot change what runs");
    }

    [Theory]
    [InlineData("softprops/action-gh-release@v2", false)]
    [InlineData("danielpalme/ReportGenerator-GitHub-Action@5", false)]
    [InlineData("danielpalme/ReportGenerator-GitHub-Action@d3ebf1f760f7d8ab92cc44d9bcfee7ad73722a31", true)]
    [InlineData("owner/action/sub@D3EBF1F760F7D8AB92CC44D9BCFEE7AD73722A31", false)]
    [InlineData("owner/action@d3ebf1f", false)]
    public void IsPinned_AcceptsOnlyAFullLowercaseCommitSha(string reference, bool expected)
    {
        IsPinned(reference).Should().Be(expected);
    }

    [Theory]
    [InlineData("actions/checkout@v4", true)]
    [InlineData("./.github/actions/local", true)]
    [InlineData("docker://alpine:3.20", false)]
    [InlineData("actions-hacker/checkout@v4", false)]
    [InlineData("softprops/action-gh-release@v2", false)]
    public void IsGitHubOwnedOrLocal_RecognizesOnlyActionsOwnerAndLocalPaths(string reference, bool expected)
    {
        IsGitHubOwnedOrLocal(reference).Should().Be(expected);
    }

    private static List<(string File, string Reference, string Comment)> ReadUsesLines()
    {
        var directory = RepositoryFiles.PathOf(".github", "workflows");
        return Directory.EnumerateFiles(directory, "*.yml")
            .Concat(Directory.EnumerateFiles(directory, "*.yaml"))
            .SelectMany(path => UsesLine.Matches(File.ReadAllText(path))
                .Select(match => (
                    File: Path.GetFileName(path),
                    Reference: match.Groups["ref"].Value.Trim('\'', '"'),
                    Comment: match.Groups["comment"].Value.Trim())))
            .ToList();
    }

    private static bool IsGitHubOwnedOrLocal(string reference) =>
        reference.StartsWith("./", StringComparison.Ordinal) ||
        reference.StartsWith("actions/", StringComparison.Ordinal);

    private static bool IsPinned(string reference)
    {
        var at = reference.LastIndexOf('@');
        return at > 0 && FullSha.IsMatch(reference[(at + 1)..]);
    }
}
