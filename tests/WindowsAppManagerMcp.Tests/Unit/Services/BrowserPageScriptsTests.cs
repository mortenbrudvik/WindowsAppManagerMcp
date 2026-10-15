using System.Text.Json;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// The scripts the DOM bridge runs in a page (roadmap M5.8): what the agent passes goes in as JSON literals, so a selector
/// or a text that looks like code is only ever data.
/// </summary>
public class BrowserPageScriptsTests
{
    [Fact]
    public void FindExpression_PutsTheSelectorInAsAJsonString_AndNoTextOrIndex()
    {
        var expression = BrowserPageScripts.BuildFindExpression(new BrowserElementLocator("a[href='x'] </script>\"); alert(1); (\"", null, null, null));

        expression.Should().Contain("const selector = " + JsonSerializer.Serialize("a[href='x'] </script>\"); alert(1); (\"") + ";");
        expression.Should().Contain("const text = null;").And.Contain("const index = null;");
    }

    [Fact]
    public void FindExpression_PutsTheTextInAsAJsonString_AndTheIndexAsANumber()
    {
        var expression = BrowserPageScripts.BuildFindExpression(new BrowserElementLocator(null, "It's \"Save\"\n now", null, null, 12));

        expression.Should().Contain("const text = " + JsonSerializer.Serialize("It's \"Save\"\n now") + ";");
        expression.Should().Contain("const selector = null;").And.Contain("const index = 12;");
        expression.Should().Contain(BrowserPageScripts.MaxTextSearchElements.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "the text search is bounded");
    }

    [Theory]
    [InlineData(true, "const replace = true;")]
    [InlineData(false, "const replace = false;")]
    public void FocusedFieldExpression_TakesReplaceAsALiteral(bool replace, string expected)
    {
        BrowserPageScripts.BuildFocusedFieldExpression(replace).Should().Contain(expected);
    }

    [Fact]
    public void TheElementFunctions_AreFunctionsOfTheElement_ThatTakeNothingOfTheAgents()
    {
        BrowserPageScripts.ElementStateFunction.Should().StartWith("function () {");
        BrowserPageScripts.HitTestFunction.Should().StartWith("function (x, y) {").And.Contain("elementFromPoint(px, py)");
        BrowserPageScripts.FocusHolderFunction.Should().StartWith("function () {").And.Contain("activeElement");
    }

    [Fact]
    public void TheElementState_ReadsNothingOfTheViewport_WhichItsFrameWouldGiveForAnElementInAFrame()
    {
        // The function runs in the element's own frame: the page's viewport comes from the main frame (ViewportExpression)
        BrowserPageScripts.ElementStateFunction.Should().NotContain("innerWidth").And.NotContain("devicePixelRatio")
            .And.NotContain("visibilityState");
        BrowserPageScripts.ViewportExpression.Should().Contain("innerWidth").And.Contain("devicePixelRatio").And.Contain("visualViewport")
            .And.Contain("visibilityState").And.Contain("location.href");
    }

    [Fact]
    public void TheElementState_CountsADisabledControlItIsInside_AndAriaDisabledAncestors()
    {
        // A span inside <button disabled>, a button inside <fieldset disabled>, a div inside role=button aria-disabled
        BrowserPageScripts.ElementStateFunction.Should().Contain("e.closest('button, input, select, textarea, optgroup, option')")
            .And.Contain("matches(':disabled')").And.Contain("e.closest('[aria-disabled=\"true\"]')");
    }

    [Fact]
    public void TheHitTest_FollowsTheFramesFromTheMainDocument_AndRefusesAFrameItCannotFollow()
    {
        BrowserPageScripts.HitTestFunction.Should().Contain("w !== w.top").And.Contain("frameElement")
            .And.Contain("{ hit: false, frame: true }").And.NotContain("this.ownerDocument !== document",
                "an element in a frame is checked, not taken on trust");
    }
}
