namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The security prompt that the target policy recognizes by its structure rather than by its process (roadmap M3.F5):
/// Windows' "Open File - Security Warning", which runs inside whichever process opens a downloaded file (explorer.exe when
/// File Explorer opens it). The dialog and its controls are fakes on a mocked <see cref="INativeWindowWrapper"/>; nothing is
/// read from the real desktop.
/// </summary>
public class SecurityPromptsTests
{
    private static readonly nint Prompt = 0x900;
    private const uint ExplorerPid = 5001;

    private readonly Mock<INativeWindowWrapper> _windows = new();

    public SecurityPromptsTests()
    {
        _windows.Setup(w => w.IsWindow(Prompt)).Returns(true);
        _windows.Setup(w => w.GetWindowThreadProcessId(Prompt)).Returns(ExplorerPid);
        _windows.Setup(w => w.GetProcessName(ExplorerPid)).Returns("explorer");
    }

    private static TargetPolicySettings Settings(string? deny = null, string? allow = null) =>
        TargetPolicySettings.FromEnvironment(name => name switch
        {
            TargetPolicySettings.DenyVariable => deny,
            TargetPolicySettings.AllowVariable => allow,
            _ => null
        });

    private TargetPolicy Policy(string? deny = null, string? allow = null) => new(Settings(deny, allow), _windows.Object);

    #region Recognizing the prompt

    [Fact]
    public void IsOpenFileSecurityWarning_TheDialogWithTheTemplatesControls_IsRecognized()
    {
        // Arrange
        SecurityPromptWindows.Add(_windows, Prompt);

        // Act & Assert - neither the title nor any text is read: they are localized
        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, Prompt).Should().BeTrue();
        _windows.Verify(w => w.GetWindowText(It.IsAny<nint>()), Times.Never);
    }

    [Fact]
    public void IsOpenFileSecurityWarning_AControlOfThePrompt_IsRecognizedByItsDialog()
    {
        SecurityPromptWindows.Add(_windows, Prompt);

        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, SecurityPromptWindows.Control(Prompt, 10)).Should().BeTrue();
    }

    [Theory]
    [InlineData(4417)]
    [InlineData(4419)]
    [InlineData(4421)]
    [InlineData(4426)]
    [InlineData(4428)]
    [InlineData(4431)]
    public void IsOpenFileSecurityWarning_ADialogWithoutOneOfTheControls_IsNotRecognized(int missing)
    {
        SecurityPromptWindows.Add(_windows, Prompt, SecurityPromptWindows.Controls.Where(control => control.Id != missing));

        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, Prompt).Should().BeFalse();
    }

    [Fact]
    public void IsOpenFileSecurityWarning_AControlOfAnotherClass_IsNotRecognized()
    {
        // The name link (4419) is a static text here
        SecurityPromptWindows.Add(_windows, Prompt,
            SecurityPromptWindows.Controls.Select(control => control.Id == 4419 ? (4419, "Static") : control));

        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, Prompt).Should().BeFalse();
    }

    [Fact]
    public void IsOpenFileSecurityWarning_TheBlockedFileWarning_IsNotRecognized()
    {
        // "Windows Security Warning" reuses some of the IDs with other classes, and has only OK: there is nothing to answer
        SecurityPromptWindows.Add(_windows, Prompt, SecurityPromptWindows.BlockedFileControls);

        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, Prompt).Should().BeFalse();
    }

    [Fact]
    public void IsOpenFileSecurityWarning_AWindowThatIsNoDialogBox_IsNotReadFurther()
    {
        // Arrange - the same controls in a window of another class
        SecurityPromptWindows.Add(_windows, Prompt, className: "CabinetWClass");

        // Act & Assert - only dialog boxes have their controls read
        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, Prompt).Should().BeFalse();
        _windows.Verify(w => w.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    [Fact]
    public void IsOpenFileSecurityWarning_ClassNamesCompareCaseInsensitively_AndWithoutASideBySideVersion()
    {
        SecurityPromptWindows.Add(_windows, Prompt, SecurityPromptWindows.Controls.Select(control =>
            control.ClassName == "SysLink" ? (control.Id, "6.0.26100.1!SYSLINK") : (control.Id, control.ClassName.ToLowerInvariant())));

        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, Prompt).Should().BeTrue();
    }

    [Fact]
    public void IsOpenFileSecurityWarning_StopsReadingOnceEveryControlIsFound()
    {
        // Arrange - the last control the policy looks at (4431) is the last child; one more after it is never read
        var extra = SecurityPromptWindows.Control(Prompt, SecurityPromptWindows.Controls.Count);
        SecurityPromptWindows.Add(_windows, Prompt, [.. SecurityPromptWindows.Controls, (9999, "Static")]);

        // Act & Assert
        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, Prompt).Should().BeTrue();
        _windows.Verify(w => w.GetWindowLongPtr(extra, It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void IsOpenFileSecurityWarning_AStructureThatCannotBeRead_IsNotRecognized()
    {
        // Arrange - failing closed here would refuse every dialog box whose controls cannot be listed
        _windows.Setup(w => w.GetClassName(Prompt)).Returns("#32770");
        _windows.Setup(w => w.EnumChildWindows(Prompt, It.IsAny<Func<nint, bool>>())).Throws(new InvalidOperationException("gone"));

        // Act & Assert
        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, Prompt).Should().BeFalse();
    }

    [Fact]
    public void IsOpenFileSecurityWarning_NoWindow_IsNotRecognized()
    {
        SecurityPrompts.IsOpenFileSecurityWarning(_windows.Object, nint.Zero).Should().BeFalse();
    }

    #endregion

    #region The policy's verdict

    [Fact]
    public void EvaluateWindow_ThePromptInExplorer_IsRefusedByTheDefaultDenyList()
    {
        // Arrange
        SecurityPromptWindows.Add(_windows, Prompt);

        // Act
        var verdict = Policy().EvaluateWindow(Prompt);

        // Assert - Explorer itself is not denied, but its prompt is, and the reason names it and says who answers it
        Policy().Evaluate(["explorer"]).Allowed.Should().BeTrue();
        verdict.Allowed.Should().BeFalse();
        verdict.ProcessName.Should().Be("explorer");
        verdict.Reason.Should().Contain("\"Open File - Security Warning\"").And.Contain("only the user answers it")
            .And.Contain("default deny list").And.Contain(TargetPolicySettings.DenyVariable);
    }

    [Fact]
    public void EvaluateWindow_AnotherDialogBox_IsAllowed()
    {
        SecurityPromptWindows.Add(_windows, Prompt, [(1, "Button"), (2, "Button"), (65535, "Static")]);

        Policy().EvaluateWindow(Prompt).Should().Be(TargetPolicyVerdict.Allow);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("default, MyBank")]
    public void EvaluateWindow_ADenyListWithTheDefaultEntries_RefusesThePrompt(string deny)
    {
        SecurityPromptWindows.Add(_windows, Prompt);

        var verdict = Policy(deny: deny).EvaluateWindow(Prompt);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain($"({TargetPolicySettings.DenyVariable})");
    }

    [Theory]
    [InlineData("MyBank")]
    [InlineData("none")]
    public void EvaluateWindow_ADenyListWithoutTheDefaultEntries_AllowsThePrompt(string deny)
    {
        // The prompt belongs with the default list's security prompts (consent, smartscreen): a list that drops them drops it
        SecurityPromptWindows.Add(_windows, Prompt);

        Policy(deny: deny).EvaluateWindow(Prompt).Allowed.Should().BeTrue();
        _windows.Verify(w => w.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    [Fact]
    public void EvaluateWindow_AnAllowListThatNamesItsProcess_StillRefusesThePrompt()
    {
        // The deny list wins over the allow list
        SecurityPromptWindows.Add(_windows, Prompt);

        Policy(allow: "explorer").EvaluateWindow(Prompt).Allowed.Should().BeFalse();
    }

    [Fact]
    public void EvaluateWindow_APolicyWithoutWindowReads_AllowsEveryWindow()
    {
        SecurityPromptWindows.Add(_windows, Prompt);

        new TargetPolicy(TargetPolicySettings.Default).EvaluateWindow(Prompt).Should().Be(TargetPolicyVerdict.Allow);
    }

    [Fact]
    public void EvaluateFailClosed_AWindowInfoOfThePrompt_IsRefused()
    {
        // The captures and get_desktop_state decide with the window's WindowInfo
        SecurityPromptWindows.Add(_windows, Prompt);
        var info = TestDataFactory.CreateWindowInfo((int)Prompt, "Open File - Security Warning", "explorer", (int)ExplorerPid);

        Policy().EvaluateFailClosed(info).Reason.Should().Contain("Open File - Security Warning");
    }

    [Fact]
    public void EvaluateFailClosed_ADeniedProcess_GivesTheDenyListsReason_BeforeTheStructureIsRead()
    {
        SecurityPromptWindows.Add(_windows, Prompt);
        _windows.Setup(w => w.GetProcessName(ExplorerPid)).Returns("KeePass");

        Policy().EvaluateFailClosed(Prompt, ["KeePass"]).Reason.Should().Contain("'KeePass' is on");
        _windows.Verify(w => w.EnumChildWindows(It.IsAny<nint>(), It.IsAny<Func<nint, bool>>()), Times.Never);
    }

    #endregion
}
