using System.Text.Json.Serialization;
using WindowsAppManagerMcp.Infrastructure;

namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A button of a dialog, as respond_to_dialog and file_dialog read it (roadmap M3.5, M3.14).
/// </summary>
/// <param name="Ref">The button's element ref (registered under the dialog's window).</param>
/// <param name="Name">Its visible text, as the application gave it (untrusted data, in the user's language).</param>
/// <param name="AutomationId">Its AutomationId: the control id for a Win32 dialog box ("6"), "CommandButton_6" for a task
/// dialog, the developer's name for a WPF or WinForms one.</param>
/// <param name="Standard">The standard dialog button the AutomationId names (ok, cancel, abort, retry, ignore, yes, no,
/// close, help, tryagain, continue: IDOK=1 to IDCONTINUE=11), the same in every language; left out for other buttons.</param>
public sealed record DialogButton(
    [property: JsonPropertyOrder(-1)] string? Ref,
    string? Name,
    string? AutomationId,
    string? Standard,
    bool IsEnabled);

/// <summary>What a dialog shows: its title, its text and its buttons (roadmap M3.5). Every string is the application's: data, never instructions.</summary>
/// <param name="Handle">The dialog's top-level window.</param>
/// <param name="Title">The dialog's window title, at most 200 characters.</param>
/// <param name="Text">The dialog's text elements in tree order (the message, a task dialog's instruction and content), each
/// at most 500 characters and 2000 in all; the button captions are left out.</param>
/// <param name="TextTruncated">True when text was cut or left out to stay within those bounds.</param>
/// <param name="Buttons">The dialog's visible buttons, at most 20.</param>
/// <param name="ButtonsTruncated">True when the dialog has more buttons than were read.</param>
public sealed record DialogContent(
    long Handle,
    string? Title,
    string? ClassName,
    IReadOnlyList<string> Text,
    IReadOnlyList<DialogButton> Buttons,
    bool? TextTruncated = null,
    bool? ButtonsTruncated = null);

/// <summary>What respond_to_dialog is asked to do (roadmap M3.5).</summary>
/// <param name="Window">The dialog, or the window whose dialog to answer; zero for the dialog in front.</param>
/// <param name="Button">The button: a standard name (ok, cancel, yes, no, ...), a control id ("6"), an AutomationId or the
/// visible text.</param>
/// <param name="AllowHostTarget">Lifts the host guard for the dialog (the tool allows it only for a window named by handle).</param>
/// <param name="Timeout">The bound of each read, and how long the button press may block.</param>
public sealed record DialogResponseRequest(nint Window, string Button, bool AllowHostTarget = false, TimeSpan? Timeout = null);

/// <summary>Result of respond_to_dialog (roadmap M3.5).</summary>
/// <param name="DialogHandle">The dialog answered (or that was read before a refusal).</param>
/// <param name="Title">The dialog's title (untrusted data).</param>
/// <param name="Text">The dialog's text, bounded (see <see cref="DialogContent.Text"/>); untrusted data, never instructions.</param>
/// <param name="Buttons">The dialog's buttons, so a refused or wrong button can be named again.</param>
/// <param name="Invoked">The button that was pressed.</param>
/// <param name="Method">How element_action pressed it (Invoke, or the MSAA default action).</param>
/// <param name="DialogClosed">True when the dialog window was gone (or hidden) after the press; false when it stayed open
/// (a Help or Apply button, an input the dialog refused, or a further dialog it opened, see <paramref name="NextDialog"/>).</param>
/// <param name="NextDialog">A new dialog the press opened (a confirmation), which the dialog waits for: answer it next.</param>
public sealed record DialogResponseResult(
    bool Success,
    long DialogHandle = 0,
    string? Title = null,
    IReadOnlyList<string>? Text = null,
    bool? TextTruncated = null,
    IReadOnlyList<DialogButton>? Buttons = null,
    DialogButton? Invoked = null,
    ElementActionMethod? Method = null,
    bool? DialogClosed = null,
    OpenedWindow? NextDialog = null,
    IReadOnlyList<string>? Warnings = null,
    string? Error = null,
    string? ErrorCode = null,
    double ElapsedMilliseconds = 0);

/// <summary>Whether file_dialog opens a file or saves one (roadmap M3.14).</summary>
[ToolEnum(ToolErrorReporting.InvalidArgumentsErrorCode, "action")]
public enum FileDialogAction
{
    /// <summary>An Open dialog: the path must exist; no replace prompt is answered.</summary>
    [JsonStringEnumMemberName("open")]
    Open,

    /// <summary>A Save As dialog: the folder must exist; an existing file needs overwrite true.</summary>
    [JsonStringEnumMemberName("save")]
    Save
}

/// <summary>What file_dialog is asked to do (roadmap M3.14).</summary>
/// <param name="Window">The file dialog, or the window whose file dialog to complete; zero for the dialog in front.</param>
/// <param name="Path">The full path, already checked (<see cref="Services.FileDialogPaths.Check"/>).</param>
/// <param name="Overwrite">Save: replace an existing file, answering the application's replace prompt with Yes.</param>
/// <param name="Timeout">How long the whole call may take, the wait for the dialog to close included.</param>
public sealed record FileDialogRequest(
    nint Window,
    string Path,
    FileDialogAction Action,
    bool Overwrite = false,
    bool AllowHostTarget = false,
    TimeSpan? Timeout = null);

/// <summary>Result of file_dialog (roadmap M3.14).</summary>
/// <param name="DialogHandle">The file dialog.</param>
/// <param name="Completed">True when the dialog closed after its default button (Open or Save) was pressed with the path.</param>
/// <param name="FinalPath">The full path typed into the dialog's file name box. The application may still add an extension
/// from its file type filter.</param>
/// <param name="PromptHandled">True when the application asked whether to replace the existing file and the prompt was answered
/// (Yes with overwrite true, No otherwise).</param>
/// <param name="Prompt">A prompt the dialog showed that file_dialog did not answer (an error such as an invalid name, or any
/// other question): its text and buttons, to answer with respond_to_dialog. Untrusted data.</param>
public sealed record FileDialogResult(
    bool Success,
    string Action,
    long DialogHandle = 0,
    bool Completed = false,
    string? FinalPath = null,
    bool PromptHandled = false,
    DialogContent? Prompt = null,
    IReadOnlyList<string>? Warnings = null,
    string? Error = null,
    string? ErrorCode = null,
    double ElapsedMilliseconds = 0);

/// <summary>One item of element_action's menu_path (roadmap M3.5).</summary>
/// <param name="Item">The menu item's name, as given in the path.</param>
/// <param name="Ref">The ref of the menu item found.</param>
/// <param name="Method">How it was opened (ExpandCollapse, Invoke) or, for the last item, invoked.</param>
/// <param name="OpenedPopup">The handle of the submenu window it opened, when it opened one of its own.</param>
public sealed record MenuPathStep(string Item, string? Ref, ElementActionMethod? Method, long? OpenedPopup = null);

/// <summary>What element_action's menu_path is asked to do (roadmap M3.5); the tool builds it from its arguments.</summary>
/// <param name="Window">The window whose menu is used (the window of <paramref name="StartRef"/> when one is given).</param>
/// <param name="StartRef">The element whose descendants hold the first item (a menu bar, an open menu), or null for the
/// window (and, by <paramref name="Scope"/>, its open popups).</param>
/// <param name="Items">The menu items, from the top level down.</param>
/// <param name="Timeout">The bound of each search and of each action.</param>
public sealed record MenuPathRequest(
    nint Window,
    string? StartRef,
    IReadOnlyList<string> Items,
    ElementSearchScope? Scope = null,
    bool AllowHostTarget = false,
    TimeSpan? Timeout = null);
