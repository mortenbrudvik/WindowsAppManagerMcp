namespace WindowsAppManagerMcp.Tests.Contract;

/// <summary>
/// Contract: what tools/list tells every MCP client, including clients that never load the plugin skill,
/// about the user's pause (roadmap M1.10): PausedByUser means stop, the agent never resumes by itself, and
/// stuck modifiers are released only on request. Reflection only.
/// </summary>
public class InputSessionDescriptionTests
{
    [Theory]
    [InlineData("click")]
    [InlineData("right_click")]
    [InlineData("double_click")]
    [InlineData("mouse_move")]
    [InlineData("type_text")]
    [InlineData("send_keys")]
    public void InputToolDescription_SaysPausedByUserMeansStop(string toolName)
    {
        var description = ServerToolCatalog.Description(toolName);

        description.Should().Contain("PausedByUser").And.Contain("stop and ask the user").And.Contain("never try to resume");
        description.Should().Contain("ModifiersHeld").And.Contain("InputBusy");
        ServerToolCatalog.Tools[toolName].GetParameters().Single(p => p.Name == "releaseHeldModifiers")
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description
            .Should().Contain("nobody is pressing them");
    }

    [Fact]
    public void SendKeysDescription_SaysThePauseChordIsRefused()
    {
        ServerToolCatalog.Description("send_keys").Should().Contain("PauseChordNotAllowed");
    }

    [Fact]
    public void ResumeInputDescription_SaysOnlyTheUsersConfirmationResumes()
    {
        var description = ServerToolCatalog.Description("resume_input");

        description.Should().Contain("only if the user confirms").And.Contain("only when the user has asked you to continue");
        description.Should().Contain("ResumeNeedsUser").And.Contain("ResumeDeclined").And.Contain("Never try to resume any other way");
    }

    [Fact]
    public void GetControlStatusDescription_SaysNeverResumeYourself()
    {
        ServerToolCatalog.Description("get_control_status").Should().Contain("Never try to resume input yourself");
    }

    [Fact]
    public void ReleaseAllInputDescription_LimitsReleasingTheUsersKeys()
    {
        ServerToolCatalog.Description("release_all_input").Should().Contain("nobody is pressing the key");
    }
}
