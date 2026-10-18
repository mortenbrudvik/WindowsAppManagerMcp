using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Services;

// A ref the agent passed as a tool argument: checked once, the same way, by every tool that takes one
public static partial class ElementRefs
{
    /// <summary>Where refs come from, as the refusal of an argument that is no ref says unless the tool says otherwise.</summary>
    public const string Sources =
        "and come from find_ui_elements, get_ui_elements, get_focused_element, get_element_at_point or capture_with_elements";

    /// <summary>
    /// <paramref name="value"/>, a tool's ref argument, as a ref (<see cref="Normalize"/>: in lower case); otherwise the tool
    /// refuses the call with InvalidArguments before anything is done: "&lt;argument&gt; '&lt;value&gt;' is not an element ref:
    /// refs look like 'w4120035e17' &lt;whereFrom&gt;. &lt;nothingDone&gt;".
    /// </summary>
    /// <param name="value">The argument as the agent passed it (null reads as empty).</param>
    /// <param name="argument">The argument's name: ref, withinRef, listRef.</param>
    /// <param name="nothingDone">What the tool did not do, as its other refusals say it: "Nothing was read."</param>
    /// <param name="whereFrom">Where refs for this argument come from; default <see cref="Sources"/>.</param>
    /// <exception cref="ToolErrorException">InvalidArguments: the value is not a ref.</exception>
    public static string Require(string? value, string argument, string nothingDone, string whereFrom = Sources) =>
        Normalize(value) ?? throw new ToolErrorException(ToolErrorReporting.InvalidArgumentsErrorCode,
            $"{argument} '{value}' is not an element ref: refs look like '{Example}' {whereFrom}. {nothingDone}");
}
