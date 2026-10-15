using System.Diagnostics;
using WindowsAppManagerMcp.Infrastructure;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>browser_handle_dialog (roadmap M5.8, dialogs); see <see cref="IBrowserDialogService"/> for the rules.</summary>
/// <remarks>
/// The dialog is looked for in the window's UI Automation tree on the UI Automation worker, after the target policy
/// (TargetDeniedByPolicy) and the hung-window check (NotResponding). A page can give its own elements any class name
/// (Chromium exposes an HTML element's class attribute as its ClassName), so a view of the dialog's class inside a Document
/// is the page's, never the dialog: only one outside every Document counts. Its buttons and field are registered as refs,
/// which element_action's service then presses and fills.
/// </remarks>
public sealed class BrowserDialogService : IBrowserDialogService
{
    /// <summary>The class of the view Chromium (Chrome, Edge) draws a JavaScript dialog's message and prompt field in.</summary>
    public const string DialogViewClass = "JavaScriptTabModalDialogViewViews";

    /// <summary>The class of the view around it that holds the dialog's buttons (its parent).</summary>
    public const string ClientViewClass = "DialogClientView";

    /// <summary>The bound of each read and of each press.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many windows' shown dialogs are remembered.</summary>
    public const int MaxNoted = 64;

    /// <summary>How many ancestors of the dialog's view are read to tell it from a page's element.</summary>
    public const int MaxAncestors = 40;

    /// <summary>The most text elements of the message read.</summary>
    private const int MaxTextElements = 20;

    private readonly IUiaTree _tree;
    private readonly IUiaWorker _worker;
    private readonly IElementRegistry _registry;
    private readonly IElementActionService _actions;
    private readonly INativeWindowWrapper _windows;
    private readonly ITargetPolicy? _policy;
    private readonly object _gate = new();
    private readonly Dictionary<nint, string> _shown = [];

    /// <param name="tree">The UI Automation tree.</param>
    /// <param name="worker">Runs the read on the UI Automation worker, bounded.</param>
    /// <param name="registry">Gives the dialog's buttons and field refs, for element_action's service.</param>
    /// <param name="actions">Sets the prompt's text and presses the button, with every guard of element_action.</param>
    /// <param name="windows">The window reads: its top-level window, its process, whether it answers.</param>
    /// <param name="policy">The user's target policy; the server always passes it.</param>
    public BrowserDialogService(
        IUiaTree tree, IUiaWorker worker, IElementRegistry registry, IElementActionService actions, INativeWindowWrapper windows,
        ITargetPolicy? policy = null)
    {
        _tree = tree;
        _worker = worker;
        _registry = registry;
        _actions = actions;
        _windows = windows;
        _policy = policy;
    }

    /// <summary>How long the dialog may take to close after the last press before it counts as still open.</summary>
    public TimeSpan CloseWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long a press is given to close the dialog before the same dialog is pressed again: Chromium ignores input that
    /// comes too soon after the dialog was shown (about a double-click time).
    /// </summary>
    public TimeSpan RepressWait { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>The most presses of a dialog that stays shown.</summary>
    public const int MaxPresses = 3;

    /// <summary>How often a closing dialog is looked for.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public void NoteShown(nint window, string message)
    {
        var top = TopLevel(window);
        lock (_gate)
        {
            if (!_shown.ContainsKey(top) && _shown.Count >= MaxNoted)
                _shown.Remove(_shown.Keys.First());

            _shown[top] = message;
        }
    }

    /// <inheritdoc />
    public async Task<BrowserDialogResult> HandleAsync(nint window, bool? accept, string? promptText, CancellationToken cancellationToken = default)
    {
        var top = TopLevel(window);
        if (window == nint.Zero || !_windows.IsWindow(top))
            return BrowserDialogResult.Failed(nameof(UIElementErrorCode.InvalidHandle), $"Window {(long)window} does not exist. Nothing was done.");

        if (UiaTargets.ReadVerdict(_windows, _policy, top) is { Allowed: false } verdict)
        {
            return BrowserDialogResult.Failed(nameof(UIElementErrorCode.TargetDeniedByPolicy),
                $"{UiaTargets.PolicyRefusalMessage(top, verdict)} Nothing was read or pressed.", top);
        }

        var read = await ReadAsync(top, cancellationToken).ConfigureAwait(false);
        if (read.Failure is { } failure)
            return failure;

        var dialog = read.Dialog!;
        var result = new BrowserDialogResult(true, top, dialog.Type, dialog.Message, dialog.PromptValue);
        if (accept is not { } accepting)
        {
            NoteShown(top, dialog.Message);
            return result;
        }

        if (promptText != null && dialog.FieldRef == null)
        {
            return result with
            {
                Success = false,
                ErrorCode = ToolErrorReporting.InvalidArgumentsErrorCode,
                Error = $"promptText answers a prompt, but the dialog is a{(dialog.Type == "alert" ? "n" : string.Empty)} {dialog.Type} (it has " +
                        "no text field). Nothing was pressed."
            };
        }

        if (accepting && dialog.Type != "alert" && !WasShown(top, dialog.Message))
        {
            // A confirm may confirm a deletion, a beforeunload dialog discard input: never accepted before the agent saw it
            NoteShown(top, dialog.Message);
            return result with
            {
                Success = false,
                ErrorCode = nameof(BrowserErrorCode.DialogNotRead),
                Error = $"The {dialog.Type} was not accepted: its text had not been read (dialogMessage has it now, written by the " +
                        "page). Nothing was pressed. Decide on it, then call browser_handle_dialog again with accept, or ask the user."
            };
        }

        if (accepting && promptText != null)
        {
            var typed = await _actions.ActAsync(new ElementActionRequest(dialog.FieldRef!, ElementActionKind.SetValue, promptText,
                ElementValueMode.Value, Timeout: DefaultTimeout), cancellationToken).ConfigureAwait(false);
            if (!typed.Success)
            {
                return result with
                {
                    Success = false,
                    ErrorCode = typed.ErrorCode ?? nameof(UIElementErrorCode.ActionFailed),
                    Error = $"The prompt's text could not be set, so nothing was pressed: {typed.Error}"
                };
            }
        }

        // Accept is the first button (OK, Leave), dismiss the last (Cancel, Stay); an alert's only button is both. Chromium
        // ignores a press that comes right after the dialog was shown (its protection against clicks meant for the page):
        // while the same dialog is still shown after a press, it is pressed again, at most MaxPresses times in all
        var button = accepting ? dialog.ButtonRefs[0] : dialog.ButtonRefs[^1];
        var state = Shown.Same;
        for (var press = 1; press <= MaxPresses && state == Shown.Same; press++)
        {
            var pressed = await _actions.ActAsync(new ElementActionRequest(button, ElementActionKind.Invoke, Timeout: DefaultTimeout),
                cancellationToken).ConfigureAwait(false);
            if (!pressed.Success)
            {
                return result with
                {
                    Success = false,
                    ErrorCode = pressed.ErrorCode ?? nameof(UIElementErrorCode.ActionFailed),
                    Error = $"The dialog's {(accepting ? "accept" : "dismiss")} button was not pressed: {pressed.Error}"
                };
            }

            Forget(top);
            state = await WaitClosedAsync(top, dialog.ViewId, press < MaxPresses ? RepressWait : CloseWait, cancellationToken).ConfigureAwait(false);
        }

        return result with
        {
            Accepted = accepting,
            DialogClosed = state == Shown.None,
            Warning = state switch
            {
                Shown.None => null,
                Shown.Other => "Another JavaScript dialog is shown after the press: the page opened it. Read it before you answer it.",
                _ => "The dialog is still shown after the press: the browser did not take it. Read it again, or ask the user."
            }
        };
    }

    /// <summary>The dialog the window shows, read on the UI Automation worker under <see cref="DefaultTimeout"/>.</summary>
    private async Task<(ShownDialog? Dialog, BrowserDialogResult? Failure)> ReadAsync(nint window, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DefaultTimeout);
        try
        {
            return await _worker.RunAsync(UiaTargets.TargetProcessId(_windows, window), token => Read(window, token), deadline.Token,
                TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or UiaReadRefusedException ||
                                   (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, BrowserDialogResult.Failed(nameof(UIElementErrorCode.OperationTimedOut),
                $"The browser window was not read within {DefaultTimeout.TotalMilliseconds:0} ms ({ex.Message}). Nothing was pressed; " +
                "the browser may be busy.", window));
        }
    }

    /// <summary>
    /// The dialog: its view (by class, outside every Document), the client view that is its parent, the buttons there (one
    /// or two, in tree order), and the message and prompt field in its view; or why it is not there.
    /// </summary>
    private (ShownDialog? Dialog, BrowserDialogResult? Failure) Read(nint window, CancellationToken cancellationToken)
    {
        (ShownDialog?, BrowserDialogResult?) Fail(string code, string error) => (null, BrowserDialogResult.Failed(code, error, window));

        if (UiaTargets.NotRespondingReason(_windows, window, "the dialog was not read") is { } notResponding)
            return Fail(nameof(UIElementErrorCode.NotResponding), notResponding);

        if (_tree.FromHandle(window) is not { } root)
            return Fail(nameof(UIElementErrorCode.ElementAccessFailed), "UI Automation has no element for the browser window. Nothing was done.");

        var views = DialogViews(root, cancellationToken);
        if (views.Count == 0)
        {
            return Fail(nameof(UIElementErrorCode.DialogNotFound),
                "The window shows no JavaScript dialog of Chrome or Edge (alert, confirm, prompt, beforeunload): none of its tabs has " +
                "one open, or it is in a tab that is not shown (show the tab first). Nothing was done. A native dialog (an Open or " +
                "Save dialog, a message box) is read with respond_to_dialog.");
        }

        if (views.Count > 1)
        {
            return Fail(nameof(UIElementErrorCode.ElementAccessFailed),
                $"The window shows {views.Count} JavaScript dialog views where Chromium shows one. Nothing was pressed. Capture the " +
                "window to see it, or ask the user.");
        }

        var view = views[0];
        var client = _tree.GetParent(view);
        if (client == null || _tree.ReadIdentity(client).ClassName != ClientViewClass)
        {
            return Fail(nameof(UIElementErrorCode.ElementAccessFailed),
                "The dialog's buttons could not be found: the view around its message is not the one Chromium uses. Nothing was pressed.");
        }

        var buttons = _tree.FindAll(client, UiaScope.Descendants, UiaCondition.PropertyEquals(UiaProperty.ControlType, "Button"), cancellationToken)
            .Where(b => !b.Properties.IsOffscreen).ToList();
        if (buttons.Count is < 1 or > 2)
        {
            return Fail(nameof(UIElementErrorCode.ElementAccessFailed),
                $"The dialog has {buttons.Count} buttons where a JavaScript dialog has one or two. Nothing was pressed. Capture the window to see it.");
        }

        var message = string.Join("\n", _tree.FindAll(view, UiaScope.Descendants,
                UiaCondition.PropertyEquals(UiaProperty.ControlType, "Text"), cancellationToken)
            .Take(MaxTextElements).Select(t => t.Properties.Name?.Trim()).Where(t => !string.IsNullOrEmpty(t)));
        var field = _tree.FindAll(view, UiaScope.Descendants, UiaCondition.PropertyEquals(UiaProperty.ControlType, "Edit"), cancellationToken)
            .FirstOrDefault();

        // A prompt's message is its field's accessible name: Chromium shows no Text element for it
        if (message.Length == 0 && field?.Properties.Name is { Length: > 0 } label)
            message = label.Trim();

        string? promptValue = null;
        if (field != null && !_tree.ReadIdentity(field.Node).IsPassword)
            promptValue = _tree.GetPattern<IUiaValuePattern>(field.Node)?.Value;

        var generation = _registry.NextGeneration();
        var buttonRefs = buttons.Select(b => _registry.Register(window, b, new ElementOrigin(generation))).ToList();
        var fieldRef = field == null ? null : _registry.Register(window, field, new ElementOrigin(generation));
        if (buttonRefs.Any(r => r == null) || (field != null && fieldRef == null))
        {
            return Fail(nameof(UIElementErrorCode.ElementAccessFailed),
                "The dialog's buttons have no runtime id, so they cannot be named for the press. Nothing was pressed.");
        }

        var type = field != null ? "prompt" : buttons.Count == 2 ? "confirm" : "alert";
        return (new ShownDialog(type, Cut(message, CdpDialogText.MaxDialogMessageLength),
            promptValue == null ? null : Cut(promptValue, CdpDialogText.MaxDialogMessageLength), fieldRef, buttonRefs!,
            _tree.GetRuntimeId(view)), null);
    }

    /// <summary>The window's views of the dialog's class that are onscreen and inside no Document (a page's element would be).</summary>
    private List<IUiaNode> DialogViews(IUiaNode root, CancellationToken cancellationToken) =>
        _tree.FindAll(root, UiaScope.Descendants, UiaCondition.PropertyEquals(UiaProperty.ClassName, DialogViewClass), cancellationToken)
            .Where(v => !v.Properties.IsOffscreen && !InDocument(v.Node, cancellationToken))
            .Select(v => v.Node)
            .ToList();

    /// <summary>Whether the element lies inside a Document (a page), or its ancestors cannot all be read: then it counts as one.</summary>
    private bool InDocument(IUiaNode node, CancellationToken cancellationToken)
    {
        try
        {
            var ancestor = _tree.GetParent(node);
            for (var depth = 0; ancestor != null; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (depth >= MaxAncestors || _tree.Read(ancestor).ControlType == "Document")
                    return true;

                ancestor = _tree.GetParent(ancestor);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// What the window shows within <paramref name="wait"/> after a press: no dialog, the same dialog (its view's runtime id),
    /// or another one. A read that fails counts as the same dialog's.
    /// </summary>
    private async Task<Shown> WaitClosedAsync(nint window, IReadOnlyList<int>? viewId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(DefaultTimeout);
            Shown state;
            try
            {
                state = await _worker.RunAsync(UiaTargets.TargetProcessId(_windows, window), token =>
                {
                    if (_tree.FromHandle(window) is not { } root || DialogViews(root, token) is not { Count: > 0 } views)
                        return Shown.None;

                    return viewId != null && views.Any(v => _tree.GetRuntimeId(v) is { } id && id.SequenceEqual(viewId)) ? Shown.Same : Shown.Other;
                }, deadline.Token, TimeSpan.Zero).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or UiaReadRefusedException ||
                                       (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return Shown.Same;
            }

            if (state != Shown.Same || stopwatch.Elapsed >= wait)
                return state;

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What a window shows after a press.</summary>
    private enum Shown
    {
        None,
        Same,
        Other
    }

    private bool WasShown(nint window, string message)
    {
        lock (_gate)
        {
            return _shown.TryGetValue(window, out var seen) && SameText(seen, message);
        }
    }

    private void Forget(nint window)
    {
        lock (_gate)
        {
            _shown.Remove(window);
        }
    }

    /// <summary>
    /// Whether two readings of a dialog's message are the same message: equal once runs of white space are one space, or one
    /// the start of the other (an action reports the start of a long message, cut with "...").
    /// </summary>
    public static bool SameText(string first, string second)
    {
        var a = Squeeze(first);
        var b = Squeeze(second);
        if (a.Length == 0 || b.Length == 0)
            return a.Length == b.Length;

        var shortA = a.EndsWith("...", StringComparison.Ordinal) ? a[..^3] : a;
        var shortB = b.EndsWith("...", StringComparison.Ordinal) ? b[..^3] : b;
        return a == b || b.StartsWith(shortA, StringComparison.Ordinal) || a.StartsWith(shortB, StringComparison.Ordinal);
    }

    private static string Squeeze(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private nint TopLevel(nint window)
    {
        var root = _windows.GetAncestor(window, WindowAncestor.Root);
        return root != nint.Zero ? root : window;
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..(max - 3)] + "..." : text;

    /// <summary>The dialog as read: its type, message, prompt value, and the refs of its field and buttons (accept first).</summary>
    private sealed record ShownDialog(
        string Type, string Message, string? PromptValue, string? FieldRef, IReadOnlyList<string> ButtonRefs, IReadOnlyList<int>? ViewId);
}
