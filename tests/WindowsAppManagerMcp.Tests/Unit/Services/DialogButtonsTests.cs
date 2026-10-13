namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// How respond_to_dialog picks its button (roadmap M3.5): by the standard ids first, so it works in every language, then by
/// AutomationId, then by the visible text.
/// </summary>
public class DialogButtonsTests
{
    private static DialogButton Button(string? name, string? automationId, bool enabled = true) =>
        new($"w1e{automationId ?? name}", name, automationId, DialogButtons.StandardOf(automationId), enabled);

    // A German Windows message box: Ja, Nein, Abbrechen
    private static readonly DialogButton[] German = [Button("Ja", "6"), Button("Nein", "7"), Button("Abbrechen", "2")];

    [Theory]
    [InlineData("yes", "6")]
    [InlineData("YES", "6")]
    [InlineData("no", "7")]
    [InlineData("cancel", "2")]
    [InlineData(" Cancel ", "2")]
    [InlineData("7", "7")]
    public void AStandardNameOrId_FindsTheButtonByItsControlId_InAnyLanguage(string asked, string expectedId)
    {
        var match = DialogButtons.Match(German, asked);

        match.Single!.AutomationId.Should().Be(expectedId);
        match.By.Should().Be("id");
    }

    [Fact]
    public void ATaskDialogsCommonButton_IsFoundByItsCommandButtonId()
    {
        // The replace prompt of a Save As dialog is a task dialog
        var buttons = new[] { Button("Ja", "CommandButton_6"), Button("Nein", "CommandButton_7") };

        DialogButtons.Match(buttons, "no").Single!.AutomationId.Should().Be("CommandButton_7");
        DialogButtons.StandardOf("CommandButton_6").Should().Be("yes");
    }

    [Theory]
    [InlineData("1", "ok")]
    [InlineData("2", "cancel")]
    [InlineData("10", "tryagain")]
    [InlineData("11", "continue")]
    [InlineData("1148", null)]
    [InlineData("okButton", null)]
    [InlineData(null, null)]
    public void StandardOf_NamesTheStandardButtonOfAnId(string? automationId, string? expected)
    {
        DialogButtons.StandardOf(automationId).Should().Be(expected);
    }

    [Theory]
    [InlineData("Try again")]
    [InlineData("try_again")]
    [InlineData("TryAgain")]
    public void StandardId_AcceptsSpellingsOfTryAgain(string name)
    {
        DialogButtons.StandardId(name).Should().Be(10);
    }

    [Fact]
    public void AnAutomationId_IsMatchedExactly()
    {
        var buttons = new[] { Button("OK", "modalOkButton"), Button("Cancel", "modalCancelButton") };

        var match = DialogButtons.Match(buttons, "modalCancelButton");

        match.Single!.Name.Should().Be("Cancel");
        match.By.Should().Be("automationId");
    }

    [Theory]
    [InlineData("Save")]
    [InlineData("save")]
    [InlineData("&Save")]
    [InlineData("Save...")]
    public void TheVisibleText_MatchesIgnoringCaseTheAccessKeyAndAnEllipsis(string asked)
    {
        var buttons = new[] { Button("&Save…", "saveBtn"), Button("Don't Save", "dontSave") };

        var match = DialogButtons.Match(buttons, asked);

        match.Single!.AutomationId.Should().Be("saveBtn");
        match.By.Should().Be("name");
    }

    [Fact]
    public void AStandardNameWithoutThatId_FallsBackToTheText()
    {
        // A WPF dialog: its buttons' AutomationIds are the developer's
        var buttons = new[] { Button("OK", "modalOkButton"), Button("Cancel", "modalCancelButton") };

        DialogButtons.Match(buttons, "cancel").Single!.AutomationId.Should().Be("modalCancelButton");
    }

    [Fact]
    public void OK_NamesTheOnlyButton_WhenNoButtonCarriesItsId()
    {
        // A localized MB_OK box whose button carries IDCANCEL, so that Esc closes it
        var buttons = new[] { Button("Schließen", "2") };

        var match = DialogButtons.Match(buttons, "ok");

        match.Single.Should().NotBeNull();
        match.By.Should().Be("onlyButton");
    }

    [Theory]
    [InlineData("Schließen", "8")]
    [InlineData("Close", "CommandButton_8")]
    public void OK_NamesTheOnlyButton_WhenItCarriesTheIdOfClose(string name, string automationId)
    {
        DialogButtons.Match([Button(name, automationId)], "ok").By.Should().Be("onlyButton");
    }

    [Theory]
    [InlineData("Restart now", "btnRestart")]
    [InlineData("Delete", "deleteButton")]
    [InlineData("Ja", "6")]
    [InlineData("Wiederholen", "4")]
    public void OK_NeverNamesAnOnlyButtonThatIsACommand(string name, string automationId)
    {
        // "ok" means acknowledge: a WPF or WinForms dialog's lone command, or a lone Yes, is not pressed for it
        var match = DialogButtons.Match([Button(name, automationId)], "ok");

        match.Buttons.Should().BeEmpty();
    }

    [Fact]
    public void TwoButtonsWithTheText_AreAmbiguous()
    {
        var buttons = new[] { Button("Open", "a"), Button("Open", "b") };

        var match = DialogButtons.Match(buttons, "Open");

        match.Buttons.Should().HaveCount(2);
        match.Single.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Retry")]
    [InlineData("help")]
    public void ANameNoButtonHas_MatchesNothing(string asked)
    {
        DialogButtons.Match(German, asked).Buttons.Should().BeEmpty();
    }
}
