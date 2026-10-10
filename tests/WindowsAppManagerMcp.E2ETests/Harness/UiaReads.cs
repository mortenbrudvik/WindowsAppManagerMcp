using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WindowsAppManagerMcp.E2ETests.Harness;

/// <summary>
/// UI Automation reads the tests use to check state the server does not report yet (keyboard focus,
/// an edit box's value). Reads only: no pattern here acts on an element.
/// </summary>
public static class UiaReads
{
    /// <summary>
    /// The AutomationId of the element with keyboard focus when it belongs to process
    /// <paramref name="processId"/>; otherwise a description of where the focus is, or null when it
    /// cannot be read.
    /// </summary>
    public static string? FocusedAutomationId(int processId)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null)
            {
                return null;
            }
            var current = focused.Current;
            return current.ProcessId == processId
                ? current.AutomationId
                : $"(process {current.ProcessId}) {current.ControlType?.ProgrammaticName} '{current.Name}'";
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>The ValuePattern value of the element with <paramref name="automationId"/> in window <paramref name="window"/>.</summary>
    public static string? ReadValue(nint window, string automationId)
    {
        var element = AutomationElement.FromHandle(window)?.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        if (element == null)
        {
            return null;
        }
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
            ? ((ValuePattern)pattern).Current.Value
            : null;
    }

    /// <summary>Every element of a get_ui_elements tree, depth first.</summary>
    public static IEnumerable<UIElementInfo> Flatten(IEnumerable<UIElementInfo>? elements)
    {
        foreach (var element in elements ?? [])
        {
            yield return element;
            foreach (var child in Flatten(element.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>A window for failure messages: handle, title and process.</summary>
    public static string DescribeWindow(nint window) =>
        window == 0
            ? "(none)"
            : $"0x{window:X} '{E2ENative.GetWindowText(window)}' (process {E2ENative.GetProcessId(window)})";
}
