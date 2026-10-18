using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions.Execution;

namespace WindowsAppManagerMcp.Tests.Unit;

/// <summary>
/// The plugin skill (skills/using-windows-app-manager, issue #11) is a small core, SKILL.md, which a client loads whole
/// whenever the skill triggers, and reference files beside it that the core names and an agent reads only when its task
/// needs them. An agent finds the details by following the links, so a broken link or anchor hides them, a reference
/// file that the core does not link is never read, and a core that grows back costs every session its tokens again. The
/// whole skills/ folder ships with the plugin (.claude-plugin/plugin.json), and nothing else of the repository does, so
/// a relative link must stay inside the skill's folder. These tests only read files in the repository.
/// </summary>
public partial class SkillFilesTests
{
    private const string CoreFile = "SKILL.md";

    /// <summary>
    /// The token budget of SKILL.md (bytes / <see cref="ToolSurface.BytesPerToken"/>, the estimate of the tool budgets).
    /// A ratchet, not a margin: it sits just above the measured size, 5,047 tokens (20,188 bytes, LF line ends), October
    /// 2026, after issue #11 split the 101,804-byte file into the core and its reference files; then 5,388 tokens (21,549
    /// bytes) after the review of that split gave the error index a line for the launch, clipboard, AccessDenied and
    /// DialogNotFound codes and scoped the InvalidArguments and TargetIsAgentHost lines; then 5,487 tokens (21,948 bytes)
    /// after the second review gave the last three codes of the reference files a line (CoordinatesOutOfBounds,
    /// InvalidControlType, ArrangeFailed): every error code of the reference files now has a line, or, for the browser and
    /// the capture and OCR codes, a pointer to the file that lists them; then 5,496 tokens (21,983 bytes) after the docs pass
    /// of the 3.0.0 review fixes taught the core their refusals and warnings (the server's own pause, UserInterrupted,
    /// UnsafeChromiumSwitch) and dropped repeats of the reference files to make room, 4 tokens below it. When a change adds
    /// to the core on purpose (a safety rule, a code that means stop), raise it in the same change by what the failure
    /// message measures; detail for one kind of task goes into its reference file instead.
    /// </summary>
    private const int CoreTokenBudget = 5_500;

    [GeneratedRegex(@"\[(?<text>[^\]\n]*)\]\((?<target>[^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^(?<level>#{1,6})\s+(?<heading>.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    private static string SkillDirectory => RepositoryFiles.PathOf("skills", "using-windows-app-manager");

    private static IEnumerable<string> FileNames() =>
        Directory.EnumerateFiles(SkillDirectory, "*.md").Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal);

    private static IEnumerable<string> ReferenceFileNames() => FileNames().Where(name => name != CoreFile);

    private static string Read(string fileName) => Snapshots.ReadNormalized(Path.Combine(SkillDirectory, fileName))!;

    public static TheoryData<string> SkillFiles()
    {
        var data = new TheoryData<string>();
        foreach (var name in FileNames())
        {
            data.Add(name);
        }

        return data;
    }

    public static TheoryData<string> ReferenceFiles()
    {
        var data = new TheoryData<string>();
        foreach (var name in ReferenceFileNames())
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void TheSkill_HasItsCoreAndReferenceFiles()
    {
        FileNames().Should().Contain(CoreFile);
        ReferenceFileNames().Should().NotBeEmpty("the core names reference files for the details of each kind of task");
    }

    [Theory]
    [MemberData(nameof(SkillFiles))]
    public void EveryRelativeLink_ResolvesToAFileInTheSkill_AndToItsAnchor(string fileName)
    {
        // Arrange
        var links = Links(Read(fileName)).Where(link => !IsExternal(link.Target)).ToList();

        // Act
        var problems = new List<string>();
        foreach (var (line, target) in links)
        {
            var hash = target.IndexOf('#', StringComparison.Ordinal);
            var path = hash < 0 ? target : target[..hash];
            var anchor = hash < 0 ? null : target[(hash + 1)..];
            var linkedName = path.Length == 0 ? fileName : path;
            var linkedPath = Path.GetFullPath(Path.Combine(SkillDirectory, linkedName));

            if (!linkedPath.StartsWith(SkillDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"line {line}: '{target}' leaves the skill's folder, which is all of the repository the plugin installs");
            }
            else if (!File.Exists(linkedPath))
            {
                problems.Add($"line {line}: '{target}' names no file");
            }
            else if (anchor != null && !Anchors(Snapshots.ReadNormalized(linkedPath)!).Contains(anchor))
            {
                problems.Add($"line {line}: '{target}' names no heading of {linkedName}");
            }
        }

        // Assert
        problems.Should().BeEmpty("an agent finds the details of {0} by following its links", fileName);
    }

    [Fact]
    public void EveryReferenceFile_IsLinkedFromTheCore()
    {
        var linked = Links(Read(CoreFile))
            .Select(link => link.Target.Split('#')[0])
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        ReferenceFileNames().Where(name => !linked.Contains(name)).Should().BeEmpty(
            "an agent reads a reference file only when SKILL.md names it (see its 'Where the Details Are' table)");
    }

    [Theory]
    [MemberData(nameof(ReferenceFiles))]
    public void EveryReferenceFile_StartsByStatingWhenToReadIt(string fileName)
    {
        // The title, then one line that tells an agent whether its task needs the file before it reads the rest
        var lines = Read(fileName).Split('\n').Where(line => line.Trim().Length > 0).Take(2).ToList();

        lines.Should().HaveCount(2);
        lines[0].Should().StartWith("# ", "{0} starts with its title", fileName);
        lines[1].Should().StartWith("Read this ", "{0} says when to read it right below its title", fileName);
    }

    [Fact]
    public void Core_KeepsTheFrontMatterThatMakesTheSkillTrigger()
    {
        var lines = Read(CoreFile).Split('\n');

        using var scope = new AssertionScope();
        lines[0].Should().Be("---");
        lines[1].Should().Be("name: using-windows-app-manager");
        lines[2].Should().StartWith("description:", "the description is what makes a client load the skill");
        lines.Skip(1).Should().Contain("---", "the front matter is closed");
    }

    [Fact]
    public void Core_StaysWithinItsTokenBudget()
    {
        var bytes = Encoding.UTF8.GetByteCount(Read(CoreFile));
        var tokens = ToolSurface.EstimatedTokens(bytes);

        tokens.Should().BeLessThanOrEqualTo(CoreTokenBudget,
            "a client loads SKILL.md whole whenever the skill triggers ({0} bytes, {1} tokens): move detail for one kind of task " +
            "into its reference file, or raise CoreTokenBudget on purpose", bytes, tokens);
    }

    [Fact]
    public void Anchors_FollowGitHubsHeadingIds()
    {
        // What the link test resolves an anchor against, as GitHub (and the agents' Markdown readers) make them
        var text = "# Safety Rules (Read First)\n## Screenshots & UI Automation\n### Window Discovery (6 tools)\n## Tips\n## Tips\n" +
                   "```\n# not a heading\n```\n";

        Anchors(text).Should().BeEquivalentTo(
            ["safety-rules-read-first", "screenshots--ui-automation", "window-discovery-6-tools", "tips", "tips-1"]);
    }

    [Fact]
    public void Links_AreFoundOutsideCodeBlocks_AndKeepTheirLine()
    {
        var text = "See [errors.md](errors.md#input-refusals) and [docs](https://example.com/x).\n```\n[no](link.md)\n```\n`[ref] Type`\n";

        Links(text).Should().Equal((1, "errors.md#input-refusals"), (1, "https://example.com/x"));
    }

    private static bool IsExternal(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Each Markdown link's 1-based line and target, outside fenced code blocks.</summary>
    private static List<(int Line, string Target)> Links(string text)
    {
        var links = new List<(int, string)>();
        var lines = text.Split('\n');
        var inFence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                links.AddRange(MarkdownLink().Matches(lines[i]).Select(m => (i + 1, m.Groups["target"].Value)));
            }
        }

        return links;
    }

    /// <summary>
    /// The anchors of a Markdown file's headings, as GitHub makes them: lower case, every character but a letter, a digit,
    /// a space, a hyphen or an underscore left out, spaces turned into hyphens, and a repeated id numbered (tips, tips-1).
    /// </summary>
    private static HashSet<string> Anchors(string text)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var inFence = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || Heading().Match(line) is not { Success: true } heading)
            {
                continue;
            }

            var id = new StringBuilder();
            foreach (var ch in heading.Groups["heading"].Value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                {
                    id.Append(ch);
                }
                else if (ch == ' ')
                {
                    id.Append('-');
                }
            }

            var anchor = id.ToString();
            for (var n = 1; !anchors.Add(anchor); n++)
            {
                anchor = $"{id}-{n}";
            }
        }

        return anchors;
    }
}
