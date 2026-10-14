using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Tests.Unit.Infrastructure;

/// <summary>
/// The cap on the window titles and AutomationIds the tools return (roadmap M3.F14): an application chooses them, and could
/// make them as long as it likes.
/// </summary>
public class AppTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Untitled - Notepad")]
    public void AShortText_IsKeptAsItIs(string? text)
    {
        AppText.Cut(text).Should().Be(text);
    }

    [Fact]
    public void AText_OfExactlyTheCap_IsKeptAsItIs()
    {
        var text = new string('a', AppText.MaxLength);

        AppText.Cut(text).Should().BeSameAs(text);
    }

    [Fact]
    public void ALongerText_IsCutToTheCap_WithAnEllipsis_AndKeepsItsStart()
    {
        var text = "Inbox - " + new string('a', 5000);

        var cut = AppText.Cut(text)!;

        cut.Should().HaveLength(AppText.MaxLength, "the ellipsis is inside the cap, so a second cut changes nothing");
        cut.Should().EndWith("…");
        text.Should().StartWith(cut[..^1]);
        AppText.Cut(cut).Should().Be(cut);
    }

    [Fact]
    public void ACut_NeverSplitsASurrogatePair()
    {
        // The pair's high surrogate is the last character the cut would keep
        var text = new string('a', AppText.MaxLength - 2) + "\U0001F600" + new string('b', 10);

        var cut = AppText.Cut(text)!;

        cut.Should().Be(new string('a', AppText.MaxLength - 2) + "…");
    }

    [Fact]
    public void AnotherCap_CanBeGiven()
    {
        AppText.Cut("Save the document", 9).Should().Be("Save the…");
    }
}
