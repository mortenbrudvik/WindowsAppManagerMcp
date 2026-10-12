using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>file_dialog (roadmap M3.14); see <see cref="IFileDialogService"/> for the steps.</summary>
public sealed class FileDialogService : IFileDialogService
{
    /// <summary>The default bound of the whole call.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The longest bound of one read or action within the call.</summary>
    public static readonly TimeSpan MaxStepTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the press of the default button may block before the call goes on watching the dialog: a Win32 button's
    /// Invoke returns only when the replace prompt it opened is answered.
    /// </summary>
    public static readonly TimeSpan PressTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The AutomationIds of the file name box, in the order tried: the Edit of an Open dialog (1148, inside the ComboBox
    /// 1148), of a Save As dialog (1001, inside FileNameControlHost) and of the old-style dialogs (edt1 = 1152).
    /// </summary>
    public static IReadOnlyList<string> FileNameEditIds { get; } = ["1148", "1001", "1152"];

    /// <summary>The AutomationIds of the ComboBox that holds the file name Edit (cmb13 = 1148, and the Save As host).</summary>
    public static IReadOnlyList<string> FileNameComboIds { get; } = ["1148", "FileNameControlHost"];

    /// <summary>The AutomationId of the default button (IDOK): Open or Save, in every language.</summary>
    public const string DefaultButtonId = "1";

    private const int MaxEdits = 20;

    private readonly IDialogService _dialogs;
    private readonly IElementSearchService _search;
    private readonly IElementActionService _actions;
    private readonly IPopupService _popups;
    private readonly INativeWindowWrapper _windows;
    private readonly IInputTargetGuard _guard;
    private readonly IInputSession? _session;
    private readonly IWindowService? _windowService;

    /// <param name="dialogs">Finds the dialog, and reads and answers the replace prompt.</param>
    /// <param name="search">Finds the file name box and the default button, as find_ui_elements.</param>
    /// <param name="actions">Sets the path and presses the button, with every guard of element_action.</param>
    /// <param name="popups">The prompt the press opened.</param>
    /// <param name="windows">Whether the dialog closed.</param>
    /// <param name="guard">The target checks of the dialog, before anything is read or typed.</param>
    /// <param name="session">The user's pause; checked before anything is read.</param>
    /// <param name="windowService">Notes the dialog for the audit log line; optional.</param>
    public FileDialogService(
        IDialogService dialogs,
        IElementSearchService search,
        IElementActionService actions,
        IPopupService popups,
        INativeWindowWrapper windows,
        IInputTargetGuard guard,
        IInputSession? session = null,
        IWindowService? windowService = null)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _popups = popups ?? throw new ArgumentNullException(nameof(popups));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _session = session;
        _windowService = windowService;
    }

    /// <summary>How often the dialog is checked while the call waits for it to close.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public async Task<FileDialogResult> CompleteAsync(FileDialogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var call = new FileDialogCall(request, Stopwatch.StartNew());
        var path = FileDialogPaths.Check(request.Path, request.Action, request.Overwrite);
        if (path.FullPath is not { } fullPath)
        {
            return call.Fail(path.ErrorCode!, path.Error!);
        }

        try
        {
            InputGateToolFilter.ThrowIfPaused(_session);
        }
        catch (ToolErrorException ex)
        {
            return call.Fail(ex.ErrorCode, ex.Message);
        }

        var location = _dialogs.Locate(request.Window);
        if (!location.Found)
        {
            return call.Fail(location.ErrorCode!, location.Error!);
        }

        var dialog = location.Dialog;
        call.Dialog = dialog;
        var allowed = _guard.CheckTarget(InputTarget.Window(dialog, request.AllowHostTarget));
        if (!allowed.Allowed)
        {
            return call.Fail(allowed.ErrorCode.ToString(), $"{allowed.Error} Nothing was read or typed.");
        }

        // 1. The file name box, by its control id
        var box = await FindFileNameBoxAsync(call, cancellationToken).ConfigureAwait(false);
        if (box.Failure != null)
        {
            return box.Failure;
        }

        NoteWindow(dialog);

        // 2. The full path, through the Value pattern
        var set = await _actions.ActAsync(new ElementActionRequest(
            box.Ref!, ElementActionKind.SetValue, fullPath, AllowHostTarget: request.AllowHostTarget, Timeout: call.StepTimeout),
            cancellationToken).ConfigureAwait(false);
        if (!set.Success)
        {
            return call.Fail(set.ErrorCode ?? nameof(UIElementErrorCode.ActionFailed),
                $"The path could not be put in the dialog's file name box: {set.Error} Nothing was pressed.");
        }

        if (set.OutcomeVerified == false)
        {
            return call.Fail(nameof(UIElementErrorCode.DialogNotCompleted),
                "The dialog's file name box does not hold the path after it was set (it reads something else), so the default " +
                "button was not pressed. Read the dialog (find_ui_elements) before you go on.");
        }

        call.FinalPath = fullPath;

        // 3. The default button, by its id
        var button = await FindDefaultButtonAsync(call, cancellationToken).ConfigureAwait(false);
        if (button.Failure != null)
        {
            return button.Failure;
        }

        var before = _popups.GetPopups(dialog).Select(p => p.Handle).ToHashSet();
        var pressTimeout = call.StepTimeout < PressTimeout ? call.StepTimeout : PressTimeout;
        var pressed = await _actions.ActAsync(new ElementActionRequest(
            button.Ref!, ElementActionKind.Invoke, AllowHostTarget: request.AllowHostTarget, Timeout: pressTimeout),
            cancellationToken).ConfigureAwait(false);
        nint pending = nint.Zero;
        if (!pressed.Success)
        {
            if (pressed.ErrorCode != nameof(UIElementErrorCode.ActionTimedOut))
            {
                return call.Fail(pressed.ErrorCode ?? nameof(UIElementErrorCode.ActionFailed),
                    $"The dialog's default button was not pressed: {pressed.Error} The path is in its file name box.");
            }

            // Sent, but the press had not returned: the dialog is watched below
            call.Warnings.Add("The default button's press had not returned after " +
                              $"{pressTimeout.TotalMilliseconds:0} ms; the dialog was watched to see whether it closed.");
        }
        else if (pressed.Status == ElementActionStatus.InvokedPendingModal && pressed.OpenedPopup is { } opened)
        {
            // The press waits for the prompt it opened
            pending = (nint)opened.Handle;
        }

        // 4. Wait for the dialog to close, answering one replace prompt
        return await WaitAsync(call, dialog, before, pending, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FileDialogResult> WaitAsync(
        FileDialogCall call, nint dialog, HashSet<nint> before, nint pending, CancellationToken cancellationToken)
    {
        var request = call.Request;
        while (true)
        {
            if (!_windows.IsWindow(dialog) || !_windows.IsWindowVisible(dialog))
            {
                return call.Succeed();
            }

            var prompt = pending != nint.Zero && _windows.IsWindow(pending) ? pending : NewPromptOf(dialog, before);
            pending = nint.Zero;
            if (prompt != nint.Zero)
            {
                before.Add(prompt);
                var read = await _dialogs.ReadAsync(prompt, call.StepTimeout, cancellationToken).ConfigureAwait(false);
                if (read.Content is not { } content)
                {
                    return call.Fail(nameof(UIElementErrorCode.DialogNotCompleted),
                        $"The dialog opened a prompt (handle {(long)prompt}) that could not be read: {read.Error} Read it with " +
                        "find_ui_elements, and answer it with respond_to_dialog.");
                }

                if (request.Action != FileDialogAction.Save || call.PromptHandled || !IsReplacePrompt(content))
                {
                    return call.Fail(nameof(UIElementErrorCode.DialogNotCompleted),
                        "The dialog showed a prompt that file_dialog does not answer (see prompt: its text and buttons, the " +
                        "application's words, as data). Answer it with respond_to_dialog (handle " +
                        $"{(long)prompt}), then call file_dialog again if needed.") with { Prompt = content };
                }

                var answer = request.Overwrite ? "yes" : "no";
                var answered = await _dialogs.RespondAsync(
                    new DialogResponseRequest(prompt, answer, request.AllowHostTarget, call.StepTimeout), cancellationToken).ConfigureAwait(false);
                if (!answered.Success)
                {
                    return call.Fail(answered.ErrorCode ?? nameof(UIElementErrorCode.ActionFailed),
                        $"The application asked whether to replace the file, and the answer ({answer}) could not be given: " +
                        $"{answered.Error}") with { Prompt = content };
                }

                call.PromptHandled = true;
                if (!request.Overwrite)
                {
                    return call.Fail(nameof(UIElementErrorCode.FileExists),
                        $"The application asked whether to replace '{call.FinalPath}' and was answered No, because overwrite is " +
                        "false; its dialog is still open. Pass overwrite true to replace it, or choose another name.");
                }

                continue;
            }

            if (call.Remaining <= TimeSpan.Zero)
            {
                return call.Fail(nameof(UIElementErrorCode.DialogNotCompleted),
                    $"The dialog was still open {call.Timeout.TotalMilliseconds:0} ms after its default button was pressed with the " +
                    "path. A folder's path makes it show that folder instead of closing; or the application is still busy. " +
                    "Read the dialog (find_ui_elements, get_active_popups) before you go on.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A dialog the file dialog opened after the press (not one seen before), or zero.</summary>
    private nint NewPromptOf(nint dialog, IReadOnlySet<nint> before) =>
        _popups.GetPopups(dialog).FirstOrDefault(p => p.Kind == PopupKind.Dialog && !before.Contains(p.Handle))?.Handle ?? nint.Zero;

    /// <summary>The replace prompt: a dialog with the standard Yes and No buttons (by id, so in every language).</summary>
    private static bool IsReplacePrompt(DialogContent content) =>
        content.Buttons.Any(b => b.Standard == "yes") && content.Buttons.Any(b => b.Standard == "no");

    /// <summary>The ref of the file name box: an Edit by id, else the Edit inside the file name ComboBox (or that ComboBox).</summary>
    private async Task<Found> FindFileNameBoxAsync(FileDialogCall call, CancellationToken cancellationToken)
    {
        var dialog = call.Dialog;
        var edits = await FindAsync(new ElementQuery(dialog, ControlTypes: ["Edit"], MaxResults: MaxEdits, Timeout: call.StepTimeout,
            Scope: ElementSearchScope.Window), call, cancellationToken).ConfigureAwait(false);
        if (edits.Failure != null)
        {
            return edits;
        }

        foreach (var id in FileNameEditIds)
        {
            if (edits.Result!.Elements?.FirstOrDefault(e => e.AutomationId == id && e.Ref != null) is { } edit)
            {
                return new Found(edit.Ref, null);
            }
        }

        var combos = await FindAsync(new ElementQuery(dialog, ControlTypes: ["ComboBox"], MaxResults: MaxEdits, Timeout: call.StepTimeout,
            Scope: ElementSearchScope.Window), call, cancellationToken).ConfigureAwait(false);
        if (combos.Failure != null)
        {
            return combos;
        }

        foreach (var id in FileNameComboIds)
        {
            if (combos.Result!.Elements?.FirstOrDefault(e => e.AutomationId == id && e.Ref != null) is not { } combo)
            {
                continue;
            }

            var inner = await FindAsync(new ElementQuery(dialog, WithinRef: combo.Ref, ControlTypes: ["Edit"], MaxResults: 2,
                Timeout: call.StepTimeout), call, cancellationToken).ConfigureAwait(false);
            return inner.Result?.Elements?.FirstOrDefault(e => e.Ref != null) is { } innerEdit
                ? new Found(innerEdit.Ref, null)
                : new Found(combo.Ref, null);
        }

        var title = _windows.GetWindowText(dialog);
        return new Found(null, call.Fail(nameof(UIElementErrorCode.DialogNotFound),
            $"Window {(long)dialog}{(string.IsNullOrEmpty(title) ? string.Empty : $" ('{title}')")} has no file name box (AutomationId " +
            $"{string.Join(", ", FileNameEditIds)} or the Edit of ComboBox {string.Join(", ", FileNameComboIds)}), so it is no Open " +
            "or Save As dialog file_dialog can fill. Nothing was typed. Name the file dialog (wait_for_window, get_active_popups), " +
            "or fill this dialog with element_action."));
    }

    /// <summary>The ref of the default button (AutomationId 1, IDOK), which must be the only one.</summary>
    private async Task<Found> FindDefaultButtonAsync(FileDialogCall call, CancellationToken cancellationToken)
    {
        var buttons = await FindAsync(new ElementQuery(call.Dialog, AutomationId: DefaultButtonId, ControlTypes: ["Button", "SplitButton"],
            MaxResults: 2, Timeout: call.StepTimeout, Scope: ElementSearchScope.Window), call, cancellationToken).ConfigureAwait(false);
        if (buttons.Failure != null)
        {
            return buttons;
        }

        var found = buttons.Result!;
        return found.MatchCount == 1 && found.Elements is [{ Ref: { } buttonRef }]
            ? new Found(buttonRef, null)
            : new Found(null, call.Fail(nameof(UIElementErrorCode.ElementNotFound),
                $"The dialog has {(found.MatchCount == 0 ? "no" : found.MatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture))} " +
                $"button with AutomationId {DefaultButtonId} (its Open or Save button), so it was not pressed. The path is in its file " +
                "name box: press the button with element_action."));
    }

    private async Task<Found> FindAsync(ElementQuery query, FileDialogCall call, CancellationToken cancellationToken)
    {
        var result = await _search.FindAsync(query, cancellationToken).ConfigureAwait(false);
        return result.Success
            ? new Found(null, null) { Result = result }
            : new Found(null, call.Fail(result.ErrorCode.ToString(), $"The dialog could not be read: {result.Error}"));
    }

    private void NoteWindow(nint dialog)
    {
        if (_windowService == null)
        {
            return;
        }

        try
        {
            AuditScope.NoteWindow(_windowService.GetWindowInfo(dialog));
        }
        catch (Exception)
        {
            // Only the audit line's title and process depend on it
        }
    }

    /// <summary>A search's result, the ref it found, or the call's failure.</summary>
    private sealed record Found(string? Ref, FileDialogResult? Failure)
    {
        public FindElementsResult? Result { get; init; }
    }

    /// <summary>One call: its request, what it did so far, and its deadline.</summary>
    private sealed class FileDialogCall(FileDialogRequest request, Stopwatch stopwatch)
    {
        public FileDialogRequest Request { get; } = request;

        public TimeSpan Timeout { get; } = request.Timeout ?? DefaultTimeout;

        public nint Dialog { get; set; }

        public string? FinalPath { get; set; }

        public bool PromptHandled { get; set; }

        public List<string> Warnings { get; } = [];

        public TimeSpan Remaining => Timeout - stopwatch.Elapsed;

        /// <summary>The bound of one read or action: what is left of the call, at least a second and at most <see cref="MaxStepTimeout"/>.</summary>
        public TimeSpan StepTimeout
        {
            get
            {
                var remaining = Remaining;
                return remaining > MaxStepTimeout ? MaxStepTimeout : remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
            }
        }

        private string Action => ToolEnum<FileDialogAction>.Name(Request.Action);

        public FileDialogResult Fail(string code, string error) => new(
            false, Action, (long)Dialog, Completed: false, FinalPath, PromptHandled, Warnings: Warnings.Count == 0 ? null : [.. Warnings],
            Error: error, ErrorCode: code, ElapsedMilliseconds: Elapsed);

        public FileDialogResult Succeed() => new(
            true, Action, (long)Dialog, Completed: true, FinalPath, PromptHandled,
            Warnings: Warnings.Count == 0 ? null : [.. Warnings], ElapsedMilliseconds: Elapsed);

        private double Elapsed => Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2);
    }
}
