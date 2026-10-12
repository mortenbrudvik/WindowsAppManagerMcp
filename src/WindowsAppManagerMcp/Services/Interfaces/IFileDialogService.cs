using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// file_dialog (roadmap M3.14): completes an Open or Save As dialog with a full path, by the dialog's control ids, never its
/// captions, so it works in every language: the file name box (the Edit inside the ComboBox 1148 or FileNameControlHost,
/// an Edit 1148, or an old-style dialog's Edit 1152 next to its ComboBox 1136 or 1137: ids only file dialogs have, so the
/// path is never typed into another dialog) gets the path through element_action set_value (the Value pattern), the
/// default button (AutomationId 1, IDOK: Open or Save) is pressed through element_action invoke, a replace prompt (a
/// dialog of the file dialog with the standard Yes and No buttons, while the file exists) is answered through
/// respond_to_dialog's service by <see cref="FileDialogRequest.Overwrite"/>, and the call waits for the file dialog to
/// close.
/// </summary>
/// <remarks>
/// Every action goes through <see cref="IElementActionService"/>, so its guards apply (pause, input gate, target policy and
/// deny list, host guard, a modal in front, audit). The path is checked first (<see cref="Services.FileDialogPaths"/>); for
/// save, an existing file without overwrite is refused before anything is typed. A prompt the press opens is checked as a
/// target before it is read. Another prompt (an invalid name, a missing file, a create question, a Yes/No question while
/// no file is at the path) is not answered: the result names it, to answer with respond_to_dialog. The file dialog must be
/// named (it, or the window whose modal file dialog it is): the file dialog in front may be another application's, so an
/// unnamed one is refused (InvalidArguments). It is found as respond_to_dialog finds a dialog
/// (<see cref="IDialogService.Locate"/>), and must have a file name box.
/// </remarks>
public interface IFileDialogService
{
    /// <summary>Completes the dialog; never throws for a refusal (the result's errorCode says why).</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<FileDialogResult> CompleteAsync(FileDialogRequest request, CancellationToken cancellationToken = default);
}
