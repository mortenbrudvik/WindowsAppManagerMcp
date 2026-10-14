namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// WAM_UIA_PROVIDER_CONTAINS (roadmap M3.F12): the provider narrows a contains name search only when it is exactly 1, until
/// a real desktop confirms the substring search.
/// </summary>
public class ProviderContainsOptionsTests
{
    private static ProviderContainsOptions Read(string? value) =>
        ProviderContainsOptions.FromEnvironment(name => name == ProviderContainsOptions.EnvironmentVariable ? value : null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    [InlineData("2")]
    public void AnythingBut1_LeavesItOff(string? value)
    {
        Read(value).Enabled.Should().BeFalse();
        ProviderContainsOptions.Default.Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("1")]
    [InlineData(" 1 ")]
    public void One_TurnsItOn(string value)
    {
        Read(value).Enabled.Should().BeTrue();
    }
}
