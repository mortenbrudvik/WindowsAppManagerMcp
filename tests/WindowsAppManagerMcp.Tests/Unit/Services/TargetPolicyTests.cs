namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for the server-enforced target policy (roadmap M1.12): how WAM_DENY_PROCESSES and
/// WAM_ALLOW_PROCESSES are read, and which process names the policy refuses. Environment variables are
/// passed in as a lookup function; the real environment is never read or changed.
/// </summary>
public class TargetPolicyTests
{
    private static TargetPolicySettings Settings(string? deny = null, string? allow = null) =>
        TargetPolicySettings.FromEnvironment(name => name switch
        {
            TargetPolicySettings.DenyVariable => deny,
            TargetPolicySettings.AllowVariable => allow,
            _ => null
        });

    private static TargetPolicy Policy(string? deny = null, string? allow = null) => new(Settings(deny, allow));

    #region Settings from the environment

    [Fact]
    public void FromEnvironment_NothingSet_DeniesTheDefaultListAndHasNoAllowList()
    {
        // Act
        var settings = Settings();

        // Assert
        settings.DeniedProcesses.Should().Equal(TargetPolicySettings.DefaultDeniedProcesses);
        settings.AllowedProcesses.Should().BeNull();
        settings.DenyListSource.Should().Be("default");
    }

    [Fact]
    public void DefaultDeniedProcesses_CoverPasswordManagersAndCredentialPrompts()
    {
        TargetPolicySettings.DefaultDeniedProcesses.Should().Contain(
        [
            "1Password", "Bitwarden", "KeePass", "KeePassXC", "LastPass", "Dashlane", "Keeper", "Enpass",
            "CredentialUIBroker", "consent"
        ]);
    }

    [Fact]
    public void DefaultDeniedProcesses_CoverMorePasswordManagers()
    {
        TargetPolicySettings.DefaultDeniedProcesses.Should().Contain(["NordPass", "ProtonPass", "Proton Pass", "RoboForm", "pwsafe"]);
    }

    [Fact]
    public void DefaultDeniedProcesses_CoverTheSmartScreenPrompt_TheWindowsSecurityApp_AndTheCredentialDialogHost()
    {
        // CHXSmartScreen draws the "Windows protected your PC" prompt; smartscreen.exe is the reputation broker that starts it
        TargetPolicySettings.DefaultDeniedProcesses.Should().Contain(["CHXSmartScreen", "smartscreen", "SecHealthUI", "CredDialogHost"]);
    }

    [Theory]
    [InlineData("KeePass.exe Bitwarden.exe")]
    [InlineData("KeePass.exe  Bitwarden.EXE")]
    [InlineData("KeePass.exe\tBitwarden.exe")]
    public void FromEnvironment_NamesSeparatedOnlyBySpacesAfterExe_AreSplit(string deny)
    {
        // A space-separated list must not turn into one name that matches nothing (and drops the defaults)
        Settings(deny: deny).DeniedProcesses.Should().Equal("KeePass", "Bitwarden");
    }

    [Fact]
    public void FromEnvironment_NameWithASpace_StaysOneName()
    {
        Settings(deny: "Proton Pass.exe, My Vault").DeniedProcesses.Should().Equal("Proton Pass", "My Vault");
    }

    [Fact]
    public void FromEnvironment_DenyList_ReplacesTheDefaultsAndNormalizesNames()
    {
        // Act - separators, quotes, paths and the .exe extension
        var settings = Settings(deny: " MyBank.exe ; \"C:\\Apps\\Vault.EXE\",payroll ");

        // Assert
        settings.DeniedProcesses.Should().Equal("MyBank", "Vault", "payroll");
        settings.DenyListSource.Should().Be(TargetPolicySettings.DenyVariable);
    }

    [Fact]
    public void FromEnvironment_DefaultToken_KeepsTheDefaultsAndAddsMore()
    {
        // Act
        var settings = Settings(deny: "default, MyBank");

        // Assert
        settings.DeniedProcesses.Should().StartWith(TargetPolicySettings.DefaultDeniedProcesses).And.EndWith("MyBank");
    }

    [Fact]
    public void FromEnvironment_None_TurnsTheDenyListOff()
    {
        Settings(deny: "none").DeniedProcesses.Should().BeEmpty();
    }

    [Fact]
    public void FromEnvironment_AllowList_IsRead()
    {
        // Act
        var settings = Settings(allow: "notepad.exe, WamFixtureApp");

        // Assert
        settings.AllowedProcesses.Should().Equal("notepad", "WamFixtureApp");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , ; ")]
    public void FromEnvironment_EmptyValues_KeepTheDefaults(string value)
    {
        // Act
        var settings = Settings(deny: value, allow: value);

        // Assert
        settings.DeniedProcesses.Should().Equal(TargetPolicySettings.DefaultDeniedProcesses);
        settings.AllowedProcesses.Should().BeNull();
    }

    [Fact]
    public void FromEnvironment_DuplicateNames_AreListedOnce()
    {
        Settings(deny: "KeePass, keepass.exe, KEEPASS").DeniedProcesses.Should().Equal("KeePass");
    }

    #endregion

    #region Evaluate

    [Theory]
    [InlineData("KeePass")]
    [InlineData("keepass")]
    [InlineData("KeePass.exe")]
    [InlineData("CredentialUIBroker")]
    [InlineData("consent")]
    [InlineData("1Password")]
    public void Evaluate_DefaultDenyList_RefusesPasswordManagersAndCredentialPrompts(string processName)
    {
        // Act
        var verdict = Policy().Evaluate([processName]);

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.ProcessName.Should().NotBeNullOrEmpty();
        verdict.Reason.Should().Contain("deny list").And.Contain(TargetPolicySettings.DenyVariable);
    }

    [Theory]
    [InlineData("CHXSmartScreen")]
    [InlineData(@"C:\Windows\SystemApps\Microsoft.Windows.AppRep.ChxApp_cw5n1h2txyewy\CHXSmartScreen.exe")]
    [InlineData("smartscreen")]
    [InlineData("SecHealthUI")]
    [InlineData(@"C:\Windows\SystemApps\microsoft.creddialoghost_cw5n1h2txyewy\CredDialogHost.exe")]
    [InlineData(@"C:\Windows\SystemApps\Microsoft.AAD.BrokerPlugin_cw5n1h2txyewy\Microsoft.AAD.BrokerPlugin.exe")] // roadmap M3.F6: Entra ID and Microsoft account sign-in
    [InlineData("SecurityHealthHost")] // roadmap M3.F6: Windows Security's notifications and prompts
    public void Evaluate_DefaultDenyList_RefusesTheWindowsSecurityPrompts_InTheirUwpFrame(string processName)
    {
        // Act - a UWP window goes by its app's process and by its frame host
        var verdict = Policy().Evaluate([processName, "ApplicationFrameHost"]);

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.ProcessName.Should().Be(TargetPolicySettings.NormalizeProcessName(processName));
        verdict.Reason.Should().Contain("default deny list");
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("KeePassHelper")] // a name that merely starts with a denied one
    public void Evaluate_DefaultPolicy_AllowsOtherProcesses(string processName)
    {
        Policy().Evaluate([processName]).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AnyNameOfTheTargetDenied_RefusesIt()
    {
        // A UWP window is checked under its app process and its frame host
        Policy(deny: "ApplicationFrameHost").Evaluate(["CalculatorApp", "ApplicationFrameHost"]).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_AllowList_AllowsOnlyListedProcesses()
    {
        // Arrange
        var policy = Policy(allow: "notepad");

        // Act & Assert
        policy.Evaluate(["notepad"]).Allowed.Should().BeTrue();
        var refused = policy.Evaluate(["mspaint"]);
        refused.Allowed.Should().BeFalse();
        refused.ProcessName.Should().Be("mspaint");
        refused.Reason.Should().Contain("allow list").And.Contain(TargetPolicySettings.AllowVariable);
    }

    [Fact]
    public void Evaluate_DenyWinsOverAllow()
    {
        // Arrange - a process on both lists
        var policy = Policy(deny: "KeePass", allow: "KeePass, notepad");

        // Act
        var verdict = policy.Evaluate(["KeePass"]);

        // Assert
        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("deny list");
    }

    [Fact]
    public void Evaluate_AllowListAndOneOfTheNamesListed_AllowsIt()
    {
        // A UWP app is allowed under its own process name even though its frame host is not listed
        Policy(allow: "CalculatorApp").Evaluate(["CalculatorApp", "ApplicationFrameHost"]).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_NoReadableName_IsAllowedWithoutAnAllowListAndRefusedWithOne()
    {
        // Fail-safe only where a list says what is allowed
        Policy().Evaluate([null, ""]).Allowed.Should().BeTrue();
        Policy(allow: "notepad").Evaluate([null]).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_WindowInfo_ChecksTheProcessAndTheHostProcess()
    {
        // Arrange
        var uwp = TestDataFactory.CreateWindowInfo(processName: "CalculatorApp") with { HostProcessName = "ApplicationFrameHost" };

        // Act & Assert
        Policy(deny: "ApplicationFrameHost").Evaluate(uwp).Allowed.Should().BeFalse();
        Policy(deny: "CalculatorApp").Evaluate(uwp).Allowed.Should().BeFalse();
        Policy(deny: "notepad").Evaluate(uwp).Allowed.Should().BeTrue();
    }

    #endregion
}
