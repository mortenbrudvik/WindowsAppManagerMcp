using System.Runtime.InteropServices;
using static WindowsAppManagerMcp.FixtureApp.FixtureContract;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// The task dialog the main window's Task dialog button shows (roadmap M3.F4): TaskDialogIndirect with two command links
/// (<see cref="TaskDialogLinks"/>) and the common Cancel button, so UI Automation reads the buttons with the task dialog's
/// own AutomationIds (CommandButton_2 for Cancel) rather than a Win32 dialog box's control ids. Task dialogs are in
/// version 6 of the common controls, which the fixture's app.manifest asks for.
/// </summary>
internal static class FixtureTaskDialog
{
    private const string Content = "A task dialog with two command links and a Cancel button.";

    /// <summary>
    /// Shows the task dialog modal on <paramref name="owner"/> and returns the id of the button that closed it (a link's id,
    /// or IDCANCEL). <paramref name="created"/> gets the dialog's window as soon as it exists (TDN_CREATED).
    /// </summary>
    /// <exception cref="ExternalException">TaskDialogIndirect failed (its HRESULT).</exception>
    public static int Show(nint owner, string instance, Action<nint> created)
    {
        var strings = new List<nint>();
        nint Text(string value)
        {
            var text = Marshal.StringToHGlobalUni(value);
            strings.Add(text);
            return text;
        }

        (int Id, string Text)[] links =
        [
            (TaskDialogLinks.KeepCommand, TaskDialogLinks.Keep),
            (TaskDialogLinks.DiscardCommand, TaskDialogLinks.Discard)
        ];
        var buttonSize = Marshal.SizeOf<NativeMethods.TASKDIALOG_BUTTON>();
        var buttons = Marshal.AllocHGlobal(buttonSize * links.Length);
        NativeMethods.TaskDialogCallback callback = (hwnd, notification, _, _, _) =>
        {
            if (notification == NativeMethods.TDN_CREATED)
            {
                created(hwnd);
            }

            return 0;
        };

        try
        {
            for (var i = 0; i < links.Length; i++)
            {
                Marshal.StructureToPtr(
                    new NativeMethods.TASKDIALOG_BUTTON { nButtonID = links[i].Id, pszButtonText = Text(links[i].Text) },
                    buttons + i * buttonSize,
                    fDeleteOld: false);
            }

            var config = new NativeMethods.TASKDIALOGCONFIG
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.TASKDIALOGCONFIG>(),
                hwndParent = owner,
                dwFlags = NativeMethods.TDF_ALLOW_DIALOG_CANCELLATION | NativeMethods.TDF_USE_COMMAND_LINKS |
                          NativeMethods.TDF_POSITION_RELATIVE_TO_WINDOW,
                dwCommonButtons = NativeMethods.TDCBF_CANCEL_BUTTON,
                pszWindowTitle = Text(TaskDialogTitle(instance)),
                pszMainInstruction = Text(TaskDialogInstruction),
                pszContent = Text(Content),
                cButtons = (uint)links.Length,
                pButtons = buttons,
                // Enter cancels: a key that reaches the dialog by accident changes nothing
                nDefaultButton = TaskDialogLinks.CancelCommand,
                pfCallback = Marshal.GetFunctionPointerForDelegate(callback)
            };

            Marshal.ThrowExceptionForHR(NativeMethods.TaskDialogIndirect(ref config, out var pressed, nint.Zero, nint.Zero));
            return pressed;
        }
        finally
        {
            GC.KeepAlive(callback);
            Marshal.FreeHGlobal(buttons);
            foreach (var text in strings)
            {
                Marshal.FreeHGlobal(text);
            }
        }
    }
}
