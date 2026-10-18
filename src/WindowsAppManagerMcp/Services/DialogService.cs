using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>respond_to_dialog (roadmap M3.5); see <see cref="IDialogService"/> for the rules.</summary>
public sealed class DialogService : IDialogService
{
    /// <summary>The default bound of each read and of the button press.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The most buttons read: a dialog's own buttons fit many times over.</summary>
    public const int MaxButtons = 20;

    /// <summary>The most text elements read.</summary>
    public const int MaxTextElements = 20;

    /// <summary>The longest text element kept.</summary>
    public const int MaxTextLength = 500;

    /// <summary>The most text kept in all.</summary>
    public const int MaxTotalText = 2000;

    /// <summary>The longest title kept: the server's cap on a window title (roadmap M3.F14).</summary>
    public const int MaxTitleLength = AppText.MaxLength;

    /// <summary>The longest button name kept (in the result and in messages); a cut one ends in an ellipsis, as a title does.</summary>
    public const int MaxButtonNameLength = 100;

    /// <summary>
    /// How many more elements the button search may return than <see cref="MaxButtons"/>: the title bar and its caption
    /// buttons (minimize, maximize, close, help), which are left out of the dialog's buttons.
    /// </summary>
    private const int TitleBarAllowance = 6;

    /// <summary>How many modal dialogs of a dialog are followed to the front one.</summary>
    private const int MaxDialogDepth = 4;

    private const string TitleBarType = "TitleBar";
    private static readonly string[] ButtonTypes = ["Button", "SplitButton", TitleBarType];
    private static readonly string[] TextTypes = ["Text"];

    private readonly INativeWindowWrapper _windows;
    private readonly IPopupService _popups;
    private readonly IElementSearchService _search;
    private readonly IElementActionService _actions;
    private readonly IInputTargetGuard _guard;
    private readonly IInputSession? _session;
    private readonly IWindowService? _windowService;

    /// <param name="windows">The Win32 window reads that locate the dialog and see it close.</param>
    /// <param name="popups">A window's modal dialog and open dialogs, and the dialog a button press opened.</param>
    /// <param name="search">Reads the dialog's buttons and text, as find_ui_elements.</param>
    /// <param name="actions">Presses the button, with every guard of element_action.</param>
    /// <param name="guard">The target checks of the dialog, before its text is read.</param>
    /// <param name="session">The user's pause; checked before anything is read.</param>
    /// <param name="windowService">Notes the dialog for the audit log line; optional.</param>
    public DialogService(
        INativeWindowWrapper windows,
        IPopupService popups,
        IElementSearchService search,
        IElementActionService actions,
        IInputTargetGuard guard,
        IInputSession? session = null,
        IWindowService? windowService = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _popups = popups ?? throw new ArgumentNullException(nameof(popups));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _session = session;
        _windowService = windowService;
    }

    /// <summary>How long the dialog may take to close after the press before it counts as still open.</summary>
    public TimeSpan CloseWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often the dialog is checked while <see cref="CloseWait"/> runs.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <inheritdoc />
    public DialogLocation Locate(nint window)
    {
        var start = window;
        if (start == nint.Zero)
        {
            start = _windows.GetForegroundWindow();
            if (start == nint.Zero)
            {
                return DialogLocation.Failed(nameof(UIElementErrorCode.DialogNotFound),
                    "No window is in the foreground (the desktop or the lock screen has focus), so there is no dialog to answer. " +
                    "Name the dialog or its window.");
            }
        }
        else if (!_windows.IsWindow(start))
        {
            return DialogLocation.Failed(nameof(UIElementErrorCode.InvalidHandle),
                $"Invalid window handle {(long)start}: it is no window (it closed). Find the dialog again with find_windows or get_active_popups.");
        }

        // A window's own modal dialog takes its input: answer the front one of the chain
        var current = start;
        for (var depth = 0; depth < MaxDialogDepth; depth++)
        {
            var blocking = _popups.GetPopups(current).FirstOrDefault(p => p.IsBlocking);
            if (blocking == null)
            {
                break;
            }

            current = blocking.Handle;
        }

        if (IsDialog(current))
        {
            return new DialogLocation(current);
        }

        var open = _popups.GetPopups(current).FirstOrDefault(p => p.Kind == PopupKind.Dialog);
        if (open != null)
        {
            return new DialogLocation(open.Handle);
        }

        var title = _windows.GetWindowText(current);
        return DialogLocation.Failed(nameof(UIElementErrorCode.DialogNotFound),
            $"Window {(long)current}{TitleOf(title)} is no " +
            $"dialog box and has no open dialog{(window == nint.Zero ? " (it is the window in front)" : string.Empty)}. A dialog is " +
            "a #32770 dialog box or a captioned window owned by another; any other window without an owner is no dialog. " +
            "Nothing was done. Wait for the dialog (wait_for_window), list the app's popups with get_active_popups, or press a " +
            "button of another window with element_action.");
    }

    /// <summary>
    /// The dialog to press a button of: as <see cref="Locate"/>, except for a dialog named (by handle or selector) that a
    /// modal dialog of its own now disables. The agent read the dialog it named, not that one, so the chain is not followed:
    /// BlockedByModal, naming the modal dialog, and nothing is pressed. A named window that is no dialog (the application's
    /// window) still stands for its modal dialog, down to the front one of the chain.
    /// </summary>
    private DialogLocation LocateToPress(nint window)
    {
        if (window != nint.Zero && _windows.IsWindow(window) && IsDialog(window) &&
            _popups.GetPopups(window).FirstOrDefault(p => p.IsBlocking) is { } blocking)
        {
            return DialogLocation.Failed(nameof(InputErrorCode.BlockedByModal),
                $"Dialog {(long)window}{TitleOf(_windows.GetWindowText(window))} is disabled now by a modal dialog of its own: " +
                $"{(long)blocking.Handle}{TitleOf(blocking.Title)}. Nothing was pressed: the button was asked of the dialog named, " +
                $"and the one in front of it was not read. Read that one (respond_to_dialog with handle {(long)blocking.Handle} " +
                "and no button), then answer it by its handle, or ask the user.");
        }

        return Locate(window);
    }

    /// <summary>" ('title')" for a message, cut; empty for no title.</summary>
    private static string TitleOf(string? title) => string.IsNullOrEmpty(title) ? string.Empty : $" ('{AppText.Cut(title, MaxTitleLength)}')";

    /// <summary>A dialog box (#32770), or an owned window with a title bar that is no tool window (a WPF or WinForms dialog).</summary>
    private bool IsDialog(nint window)
    {
        var className = _windows.GetClassName(window);
        if (WindowClassification.IsDialogClass(className))
        {
            return true;
        }

        var style = (long)_windows.GetWindowLongPtr(window, NativeEnums.GWL_STYLE);
        var exStyle = (long)_windows.GetWindowLongPtr(window, NativeEnums.GWL_EXSTYLE);
        var owned = _windows.GetWindow(window, NativeEnums.GW_OWNER) != nint.Zero;
        return PopupService.Classify(className, style, exStyle, owned) == PopupKind.Dialog;
    }

    /// <inheritdoc />
    public async Task<DialogRead> ReadAsync(nint dialog, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var buttons = await FindAsync(dialog, ButtonTypes, MaxButtons + TitleBarAllowance, timeout, cancellationToken).ConfigureAwait(false);
        if (!buttons.Success)
        {
            return DialogRead.Failed(buttons.ErrorCode.ToString(), $"The dialog could not be read: {buttons.Error}");
        }

        var warnings = new List<string>();
        var texts = await FindAsync(dialog, TextTypes, MaxTextElements, timeout, cancellationToken).ConfigureAwait(false);
        if (!texts.Success)
        {
            warnings.Add($"The dialog's text could not be read ({texts.ErrorCode}): {texts.Error}");
        }

        var dialogButtons = WithoutTitleBar(buttons.Elements ?? []);
        // A web page's HTML id is an AutomationId too (an Electron dialog): it is cut as a name is
        var buttonList = dialogButtons.Take(MaxButtons).Select(e => new DialogButton(
            e.Ref,
            EmptyToNull(AppText.Cut(e.Name ?? string.Empty, MaxButtonNameLength)),
            EmptyToNull(AppText.Cut(e.AutomationId ?? string.Empty, MaxButtonNameLength)),
            DialogButtons.StandardOf(e.AutomationId),
            e.IsEnabled)).ToList();
        var (text, textCut) = Text(texts.Success ? texts.Elements ?? [] : [], buttonList);
        var title = EmptyToNull(_windows.GetWindowText(dialog));
        var content = new DialogContent(
            (long)dialog,
            title == null ? null : AppText.Cut(title, MaxTitleLength),
            EmptyToNull(_windows.GetClassName(dialog)),
            text,
            buttonList,
            TextTruncated: textCut || (texts.Success && texts.Truncated) ? true : null,
            ButtonsTruncated: buttons.Truncated || dialogButtons.Count > MaxButtons ? true : null);
        return new DialogRead(content, warnings.Count == 0 ? null : warnings);
    }

    /// <summary>
    /// The buttons without the title bar's caption buttons (minimize, maximize, close, the context help '?'): they are the
    /// window's, not answers of the dialog, as <see cref="WindowCloseVerifier"/> reads a prompt. A button lies in the title
    /// bar when its centre is inside a TitleBar element's bounds: a caption button can reach past the TitleBar's edge (a
    /// MessageBox's Close button on a real desktop, 5 pixels above it and 2 to its right; roadmap M3.F3).
    /// </summary>
    internal static IReadOnlyList<FoundElement> WithoutTitleBar(IReadOnlyList<FoundElement> elements)
    {
        var titleBars = elements.Where(e => e.ControlType == TitleBarType).Select(e => e.Bounds).OfType<BoundsDto>().ToList();
        return elements.Where(e => e.ControlType != TitleBarType && !(e.Bounds is { } b && titleBars.Any(t => CentreInside(b, t)))).ToList();
    }

    private static bool CentreInside(BoundsDto inner, BoundsDto outer)
    {
        var x = inner.X + inner.Width / 2.0;
        var y = inner.Y + inner.Height / 2.0;
        return x >= outer.X && x < outer.X + outer.Width && y >= outer.Y && y < outer.Y + outer.Height;
    }

    private Task<FindElementsResult> FindAsync(nint dialog, string[] controlTypes, int maxResults, TimeSpan timeout, CancellationToken cancellationToken) =>
        _search.FindAsync(new ElementQuery(
            WindowHandle: dialog,
            ControlTypes: controlTypes,
            VisibleOnly: true,
            EnabledOnly: false,
            MaxResults: maxResults,
            Detail: ElementDetail.Minimal,
            Timeout: timeout,
            Scope: ElementSearchScope.Window), cancellationToken);

    /// <summary>
    /// The dialog's text: the names of its Text elements in the order found, without blanks, repeats and the button
    /// captions, each at most <see cref="MaxTextLength"/> and <see cref="MaxTotalText"/> in all; and whether any was cut.
    /// </summary>
    private static (IReadOnlyList<string> Text, bool Cut) Text(IReadOnlyList<FoundElement> elements, IReadOnlyList<DialogButton> buttons)
    {
        var captions = buttons.Select(b => b.Name).Where(n => n != null).ToHashSet(StringComparer.Ordinal);
        var kept = new List<string>();
        var total = 0;
        var cut = false;
        foreach (var element in elements)
        {
            var name = element.Name?.Trim();
            if (string.IsNullOrEmpty(name) || captions.Contains(name) || kept.Contains(name))
            {
                continue;
            }

            if (name.Length > MaxTextLength)
            {
                name = Start(name, MaxTextLength);
                cut = true;
            }

            if (total + name.Length > MaxTotalText)
            {
                name = Start(name, MaxTotalText - total);
                cut = true;
                if (name.Length > 0)
                {
                    kept.Add(name);
                }

                break;
            }

            kept.Add(name);
            total += name.Length;
        }

        return (kept, cut);
    }

    /// <summary>
    /// The start of <paramref name="text"/> in at most <paramref name="maxLength"/> characters, never ending on the first half
    /// of a surrogate pair (<see cref="TextPages.FittingLength"/>): rather none than half of one, which no result can carry.
    /// </summary>
    private static string Start(string text, int maxLength)
    {
        var length = TextPages.FittingLength(text, maxLength, int.MaxValue);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            // FittingLength keeps a lone first character so that a page is never empty; a dialog's text can do without it
            length--;
        }

        return text[..length];
    }

    /// <inheritdoc />
    public async Task<DialogResponseResult> RespondAsync(DialogResponseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var timeout = request.Timeout ?? DefaultTimeout;
        var readOnly = request.Button == null;
        DialogResponseResult Fail(string code, string error, nint dialog = 0, DialogContent? content = null, IReadOnlyList<string>? warnings = null) =>
            With(new DialogResponseResult(false, (long)dialog, Error: error, ErrorCode: code, Warnings: warnings), content, stopwatch);

        if (request.Button is { } asked && string.IsNullOrWhiteSpace(asked))
        {
            return Fail(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"button names the button to press: a standard name ({string.Join(", ", DialogButtons.StandardNames)}), a control id " +
                "such as \"6\", an AutomationId, or its visible text; leave it out to only read the dialog. Nothing was done.");
        }

        if (request.Button is { } unnamed && request.Window == nint.Zero && !DialogButtons.IsCancel(unnamed))
        {
            // Read and pressed in one call, the dialog in front may be one that came up after the agent looked (a prompt of
            // another application): only cancel, which answers nothing, may go to it unread
            return Fail(ToolErrorReporting.InvalidArgumentsErrorCode,
                $"button '{AppText.Cut(unnamed.Trim(), 100)}' needs the dialog named by handle or window ({{\"foreground\": true}} alone " +
                "names none): the dialog in front now may not be the one you read. Read the dialog first (respond_to_dialog " +
                "without button, or get_active_popups), then answer it by its dialogHandle as handle. Only cancel may be pressed " +
                "in the dialog in front without naming it. Nothing was read or pressed.");
        }

        if (!readOnly)
        {
            // Reading alone is no action: it is allowed while input is paused, as find_ui_elements is
            try
            {
                InputGateToolFilter.ThrowIfPaused(_session);
            }
            catch (ToolErrorException ex)
            {
                return Fail(ex.ErrorCode, ex.Message);
            }
        }

        var location = readOnly ? Locate(request.Window) : LocateToPress(request.Window);
        if (!location.Found)
        {
            return Fail(location.ErrorCode!, location.Error!);
        }

        var dialog = location.Dialog;
        NoteWindow(dialog);

        // Checked as an action's target before anything is read: the dialog of an app the user's policy denies (a password
        // manager, a credential prompt) or of the agent's own host is neither read nor answered
        var allowed = _guard.CheckTarget(InputTarget.Window(dialog, request.AllowHostTarget));
        if (!allowed.Allowed)
        {
            return Fail(allowed.ErrorCode.ToString(), $"{allowed.Error} Nothing was read or pressed.", dialog);
        }

        var read = await ReadAsync(dialog, timeout, cancellationToken).ConfigureAwait(false);
        if (read.Content is not { } content)
        {
            return Fail(read.ErrorCode!, $"{read.Error} Nothing was pressed.", dialog);
        }

        var warnings = new List<string>(read.Warnings ?? []);
        if (request.Button is not { } buttonAsked)
        {
            // Read only: the agent decides on the text, then answers this dialog by its handle
            return With(new DialogResponseResult(true, (long)dialog, Warnings: warnings.Count == 0 ? null : warnings), content, stopwatch);
        }

        var shown = AppText.Cut(buttonAsked.Trim(), 100);
        var match = DialogButtons.Match(content.Buttons, buttonAsked);
        if (match.Buttons.Count == 0)
        {
            return Fail(nameof(UIElementErrorCode.ElementNotFound),
                $"No button of the dialog is '{shown}' (by standard name or id, AutomationId or text). " +
                $"Nothing was pressed. Its buttons (the application's text): {Describe(content.Buttons)}" +
                (content.ButtonsTruncated == true ? $" (only the first {MaxButtons} were read: press another with element_action)." : "."),
                dialog, content, warnings);
        }

        if (match.Single is not { } button)
        {
            return Fail(nameof(UIElementErrorCode.AmbiguousElement),
                $"{match.Buttons.Count} buttons of the dialog match '{shown}' (by {match.By}): " +
                $"{Describe(match.Buttons)}. Nothing was pressed. Name one by its automationId or ref (element_action).",
                dialog, content, warnings);
        }

        if (button.Ref is not { } buttonRef)
        {
            return Fail(nameof(UIElementErrorCode.ElementAccessFailed),
                "The button has no runtime id, so it cannot be named for the press. Nothing was pressed.", dialog, content, warnings);
        }

        if (match.By == "onlyButton")
        {
            warnings.Add($"No button carries the id of OK; the dialog's only button ('{button.Name}') was pressed.");
        }

        var before = _popups.GetPopups(dialog).Select(p => p.Handle).ToHashSet();
        var pressed = await _actions.ActAsync(new ElementActionRequest(
            buttonRef, ElementActionKind.Invoke, AllowHostTarget: request.AllowHostTarget, Timeout: timeout), cancellationToken).ConfigureAwait(false);
        var sentNotReturned = !pressed.Success && pressed.ErrorCode == nameof(UIElementErrorCode.ActionTimedOut);
        if (!pressed.Success && !sentNotReturned)
        {
            return Fail(pressed.ErrorCode ?? nameof(UIElementErrorCode.ActionFailed),
                $"The button '{button.Name ?? button.AutomationId}' was not pressed: {pressed.Error}", dialog, content,
                [.. warnings, .. pressed.Warnings ?? []]);
        }

        warnings.AddRange(pressed.Warnings ?? []);
        if (sentNotReturned)
        {
            // The press was sent; its handler had not returned (a busy application): whether it took effect is seen below
            warnings.Add($"The press of '{button.Name ?? button.AutomationId}' was sent but had not returned after " +
                         $"{timeout.TotalMilliseconds:0} ms; dialogClosed says whether the dialog closed. Do not press it again " +
                         "without reading the dialog.");
        }

        bool closed;
        var next = pressed.OpenedPopup is { } opened ? opened with { Title = AppText.Cut(opened.Title, MaxTitleLength) } : null;
        if (pressed.Status == ElementActionStatus.InvokedPendingModal)
        {
            // The press waits for the dialog it opened: the first dialog stays until that one is answered
            closed = false;
        }
        else
        {
            closed = await WaitClosedAsync(dialog, timeout < CloseWait ? timeout : CloseWait, cancellationToken).ConfigureAwait(false);
            if (!closed)
            {
                next ??= NewDialogOf(dialog, before);
                warnings.Add(next != null
                    ? $"The dialog is still open: the press opened another dialog ('{next.Title}', handle {next.Handle}). Answer it next."
                    : "The dialog is still open after the press (a button such as Help or Apply, input the dialog refused, or a busy " +
                      "application). Read it again.");
            }
        }

        return With(new DialogResponseResult(
            true,
            (long)dialog,
            Invoked: button,
            Method: pressed.Method,
            DialogClosed: closed,
            NextDialog: closed ? null : next,
            Warnings: warnings.Count == 0 ? null : warnings.Distinct().ToList()), content, stopwatch);
    }

    /// <summary>The result with the dialog's content and the elapsed time.</summary>
    private static DialogResponseResult With(DialogResponseResult result, DialogContent? content, Stopwatch stopwatch) =>
        result with
        {
            Title = content?.Title,
            Text = content?.Text,
            TextTruncated = content?.TextTruncated,
            Buttons = content?.Buttons,
            ButtonsTruncated = content?.ButtonsTruncated,
            ElapsedMilliseconds = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2)
        };

    /// <summary>True once the dialog is gone or hidden; false when it is still shown after <paramref name="wait"/>.</summary>
    private async Task<bool> WaitClosedAsync(nint dialog, TimeSpan wait, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (!_windows.IsWindow(dialog) || !_windows.IsWindowVisible(dialog))
            {
                return true;
            }

            if (clock.Elapsed >= wait)
            {
                return false;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A popup of the dialog that was not there before the press (no tooltip), or null.</summary>
    private OpenedWindow? NewDialogOf(nint dialog, IReadOnlySet<nint> before)
    {
        var popup = _popups.GetPopups(dialog).FirstOrDefault(p => !before.Contains(p.Handle) && p.Kind != PopupKind.Tooltip);
        return popup == null
            ? null
            : new OpenedWindow((long)popup.Handle, AppText.Cut(popup.Title ?? string.Empty, MaxTitleLength), popup.ClassName, popup.ProcessId,
                _windows.GetProcessName((uint)popup.ProcessId));
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

    /// <summary>The buttons for a message: text, AutomationId and standard name.</summary>
    internal static string Describe(IReadOnlyList<DialogButton> buttons) =>
        buttons.Count == 0
            ? "none"
            : string.Join(", ", buttons.Select(b =>
                $"'{b.Name}'{(b.AutomationId == null ? string.Empty : $" (automationId {b.AutomationId}")}" +
                $"{(b.Standard == null ? string.Empty : $", {b.Standard}")}{(b.AutomationId == null ? string.Empty : ")")}" +
                $"{(b.IsEnabled ? string.Empty : " [disabled]")}"));

    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
