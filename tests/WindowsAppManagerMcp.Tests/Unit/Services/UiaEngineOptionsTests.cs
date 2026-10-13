namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>WAM_UIA_ENGINE (roadmap M3.2): the UIA3 engine by default, the managed client as the fallback.</summary>
public class UiaEngineOptionsTests
{
    private static UiaEngineOptions Read(string? value) =>
        UiaEngineOptions.FromEnvironment(name => name == UiaEngineOptions.EnvironmentVariable ? value : null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NotSet_IsTheUia3Engine_FromTheDefault(string? value)
    {
        var options = Read(value);

        options.Engine.Should().Be(UiaEngine.Uia3);
        options.Source.Should().Be("default");
        options.Warning.Should().BeNull();
    }

    [Theory]
    [InlineData("uia3", UiaEngine.Uia3)]
    [InlineData("UIA3", UiaEngine.Uia3)]
    [InlineData(" uia2 ", UiaEngine.Uia2)]
    [InlineData("Uia2", UiaEngine.Uia2)]
    public void AnEngineName_ChoosesThatEngine(string value, UiaEngine expected)
    {
        var options = Read(value);

        options.Engine.Should().Be(expected);
        options.Source.Should().Be(UiaEngineOptions.EnvironmentVariable);
        options.Warning.Should().BeNull();
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("3")]
    [InlineData("flaui")]
    public void AnyOtherValue_KeepsTheDefault_AndWarns(string value)
    {
        var options = Read(value);

        options.Engine.Should().Be(UiaEngine.Uia3);
        options.Source.Should().Be("default");
        options.Warning.Should().Contain(UiaEngineOptions.EnvironmentVariable).And.Contain($"'{value}'").And.Contain("uia2");
    }

    [Fact]
    public void Default_IsUia3()
    {
        UiaEngineOptions.Default.Engine.Should().Be(UiaEngine.Uia3);
    }
}
