using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The security prompt that the target policy recognizes by its structure rather than by its process (roadmap M3.F5;
/// <see cref="ITargetPolicy.EvaluateWindow"/>). Windows shows the Attachment Execution Services prompt "Open File - Security
/// Warning" ("Do you want to open this file?", or "Do you want to run this file?" for a program) inside whichever process
/// opens a downloaded file through the shell: explorer.exe when File Explorer opens it, and a browser, a mail client or a
/// launcher otherwise. A deny list of image names cannot name it without denying that whole process, so the dialog itself
/// is recognized. Its title and texts are localized, but its window class and its controls are not: it is a dialog box
/// (#32770) made from dialog template 4416 of shdocvw.dll, whose control IDs and control classes are the same in every
/// language, since the code that fills the dialog in finds its controls by ID. Internet Explorer's "File Download" dialog
/// (ieframe.dll) is made from the same template and is recognized too; it also asks whether to open a downloaded file.
/// These are Win32 reads that Windows answers without the dialog's thread.
/// </summary>
public static class SecurityPrompts
{
    /// <summary>The prompt's title in English, which the refusals name it by.</summary>
    public const string OpenFileSecurityWarning = "Open File - Security Warning";

    /// <summary>The window class of a dialog box: only such windows have their controls read.</summary>
    public const string DialogClass = "#32770";

    /// <summary>
    /// The controls that make a dialog box the "Open File - Security Warning" prompt, as (control ID, window class), from
    /// template 4416 of shdocvw.dll (the same IDs are in the language-neutral shdocvw.dll, its en-US resources and
    /// ieframe.dll's templates 4416 and 37378): 4417 is the question ("Do you want to run this file?"), 4419 and 4421 the
    /// name and publisher links, 4426 the Open or Run button, 4428 the "Always ask before opening this file" box and 4431
    /// the warning with its "What's the risk?" link. Cancel (2) and the labels are left out: any dialog box can have a
    /// Cancel button, and the six controls here already set the template apart. Windows' "Windows Security Warning"
    /// (template 4437: a file that was blocked, with only OK) reuses IDs 4417, 4419 and 4431 with other classes, and does
    /// not match. The IDs were read from the dialog templates, not from a prompt on screen; get_ui_elements on the prompt
    /// lists each control's ID as its automationId, which confirms them (BACKLOG.md, M3.F5).
    /// </summary>
    public static IReadOnlyList<(int Id, string ClassName)> OpenFileSecurityWarningControls { get; } =
    [
        (4417, "Static"), (4419, "SysLink"), (4421, "SysLink"), (4426, "Button"), (4428, "Button"), (4431, "SysLink")
    ];

    /// <summary>
    /// Whether <paramref name="window"/>, or the top-level window it is a control of, is the "Open File - Security Warning"
    /// prompt: a dialog box (<see cref="DialogClass"/>) with every control of <see cref="OpenFileSecurityWarningControls"/>.
    /// Only a dialog box has its controls read, and the read stops once all of them are found. Neither its title nor any text
    /// is read. False when the window or its controls cannot be read: failing closed would refuse every dialog box whose
    /// controls cannot be listed.
    /// </summary>
    public static bool IsOpenFileSecurityWarning(INativeWindowWrapper windows, nint window)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (window == nint.Zero)
        {
            return false;
        }

        try
        {
            // A control of the prompt counts as the prompt
            var root = windows.GetAncestor(window, WindowAncestor.Root);
            var dialog = root == nint.Zero ? window : root;
            if (!IsClass(windows.GetClassName(dialog), DialogClass))
            {
                return false;
            }

            var missing = OpenFileSecurityWarningControls.ToDictionary(control => control.Id, control => control.ClassName);
            windows.EnumChildWindows(dialog, child =>
            {
                // The ID first: a control's class is read only when its ID is one of the prompt's
                var id = unchecked((int)(long)windows.GetWindowLongPtr(child, NativeEnums.GWLP_ID));
                if (missing.TryGetValue(id, out var className) && IsClass(windows.GetClassName(child), className))
                {
                    missing.Remove(id);
                }

                return missing.Count > 0;
            });
            return missing.Count == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a window class is <paramref name="expected"/>. Class names compare case-insensitively, as Windows compares
    /// them, and without the side-by-side version that a common control's class can carry ("6.0.…!SysLink").
    /// </summary>
    private static bool IsClass(string? className, string expected)
    {
        if (string.IsNullOrEmpty(className))
        {
            return false;
        }

        var name = className[(className.LastIndexOf('!') + 1)..];
        return string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
    }
}
