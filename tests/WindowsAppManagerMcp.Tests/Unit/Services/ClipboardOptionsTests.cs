namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>WAM_ALLOW_USER_CLIPBOARD: reading the user's clipboard without asking is the user's choice, and off by default.</summary>
public class ClipboardOptionsTests
{
    private static ClipboardOptions Read(string? value) =>
        ClipboardOptions.FromEnvironment(name => name == ClipboardOptions.EnvironmentVariable ? value : null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("No")]
    [InlineData("off")]
    public void NotSetOrOff_KeepsItOff_WithoutAWarning(string? value)
    {
        var options = Read(value);

        options.AllowUserClipboard.Should().BeFalse();
        options.Warning.Should().BeNull();
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    [InlineData(" on ")]
    public void On_AllowsTheUsersClipboard(string value)
    {
        Read(value).AllowUserClipboard.Should().BeTrue();
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("allow")]
    public void AnyOtherValue_KeepsItOff_ToBeSafe_AndWarns(string value)
    {
        var options = Read(value);

        options.AllowUserClipboard.Should().BeFalse();
        options.Warning.Should().Contain(value).And.Contain("stays off");
    }

    [Fact]
    public void TheDefault_IsOff()
    {
        ClipboardOptions.Default.AllowUserClipboard.Should().BeFalse();
    }
}
