using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Verified close (roadmap M2.5): polls IsWindow after a close request, and finds the prompt that keeps a window open.
/// Win32 prompts come from <see cref="IWindowService.FindBlockingDialog"/>; their text and buttons, and modal dialogs
/// drawn inside a window, come from UI Automation, read on the <see cref="IUiaWorker"/> under a deadline so a hung
/// application cannot block the call.
/// </summary>
public sealed class WindowCloseVerifier : IWindowCloseVerifier
{
    /// <summary>How often IsWindow is polled while a close is awaited.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How often the windows still open are checked for a Win32 prompt while the close is awaited.</summary>
    public static readonly TimeSpan DefaultPromptCheckInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>How long one UI Automation read of a prompt may take before it is given up (plus <see cref="UiaGrace"/>).</summary>
    public static readonly TimeSpan UiaReadTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long to keep waiting after <see cref="UiaReadTimeout"/> for a read that stops on its own.</summary>
    private static readonly TimeSpan UiaGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>How often a process is looked up while its exit is awaited.</summary>
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How deep below the window a modal dialog is searched for, and how many elements at most.</summary>
    private const int MaxSearchDepth = 6;

    private const int MaxSearchElements = 300;

    /// <summary>How deep below a prompt its text and buttons are read, and how many elements at most.</summary>
    private const int MaxContentDepth = 6;

    private const int MaxContentElements = 200;

    private readonly IWindowService _windows;
    private readonly IUiaTree? _tree;
    private readonly IUiaWorker? _worker;
    private readonly IUIAutomationWrapper? _automation;
    private readonly IProcessService? _processes;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _promptCheckInterval;
    private readonly ILogger<WindowCloseVerifier>? _logger;

    /// <param name="windows">Polls and reads the windows.</param>
    /// <param name="tree">Reads prompts through UI Automation; without it (or <paramref name="worker"/>) only Win32 prompts are found.</param>
    /// <param name="worker">Runs the UI Automation reads under a deadline.</param>
    /// <param name="automation">The UI Automation close (<see cref="CloseWithUiAutomationAsync"/>).</param>
    /// <param name="processes">Looks up whether a process still runs (<see cref="WaitForProcessesToExitAsync"/>).</param>
    /// <param name="pollInterval">How often IsWindow is polled; default <see cref="DefaultPollInterval"/>. Tests pass a short one.</param>
    /// <param name="promptCheckInterval">How often Win32 prompts are looked for; default <see cref="DefaultPromptCheckInterval"/>.</param>
    /// <param name="logger">Logs a UI Automation read or close that failed unexpectedly (a COMException), with the exception.</param>
    public WindowCloseVerifier(
        IWindowService windows,
        IUiaTree? tree = null,
        IUiaWorker? worker = null,
        IUIAutomationWrapper? automation = null,
        IProcessService? processes = null,
        TimeSpan? pollInterval = null,
        TimeSpan? promptCheckInterval = null,
        ILogger<WindowCloseVerifier>? logger = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _tree = tree;
        _worker = worker;
        _automation = automation;
        _processes = processes;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _promptCheckInterval = promptCheckInterval ?? DefaultPromptCheckInterval;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlySet<nint> ShownWindows(IReadOnlyList<nint> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        return handles.Distinct().Where(h => _windows.GetWindowInfo(h) is { IsVisible: true, IsCloaked: false }).ToHashSet();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloseObservation>> WaitForCloseAsync(
        IReadOnlyList<nint> handles, IReadOnlySet<nint> shownBefore, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handles);
        ArgumentNullException.ThrowIfNull(shownBefore);

        var pending = handles.Distinct().ToList();
        var closed = new HashSet<nint>();
        var lastPrompt = new Dictionary<nint, nint>();
        var settled = new Dictionary<nint, ClosePrompt>();
        var waited = Stopwatch.StartNew();
        var nextPromptCheck = _promptCheckInterval;

        while (true)
        {
            foreach (var handle in pending.Where(h => !_windows.IsValidWindow(h)).ToList())
            {
                closed.Add(handle);
                pending.Remove(handle);
            }

            if (pending.Count == 0 || waited.Elapsed >= wait)
                break;

            if (waited.Elapsed >= nextPromptCheck)
            {
                nextPromptCheck = waited.Elapsed + _promptCheckInterval;
                foreach (var handle in pending.ToList())
                {
                    // A prompt must stay for two checks in a row: a dialog that flashes up while the app closes is no prompt
                    var prompt = _windows.FindBlockingDialog(handle);
                    if (prompt != null && lastPrompt.TryGetValue(handle, out var seen) && seen == prompt.Handle)
                    {
                        settled[handle] = prompt;
                        pending.Remove(handle);
                    }
                    else if (prompt != null)
                    {
                        lastPrompt[handle] = prompt.Handle;
                    }
                    else
                    {
                        lastPrompt.Remove(handle);
                    }
                }

                if (pending.Count == 0)
                    break;
            }

            var left = wait - waited.Elapsed;
            await Task.Delay(left < _pollInterval ? (left > TimeSpan.Zero ? left : TimeSpan.Zero) : _pollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The windows still open are looked at all at the same time: each UI Automation read has its own deadline, so the
        // call ends about one deadline after the wait, however many windows there are
        var observations = await Task.WhenAll(handles.Distinct().Select(handle => closed.Contains(handle)
                ? Task.FromResult(new CloseObservation(handle, CloseStatus.Closed))
                : settled.TryGetValue(handle, out var prompt)
                    ? PromptShownAsync(handle, prompt, cancellationToken)
                    : ObserveAsync(handle, shownBefore.Contains(handle), cancellationToken)))
            .ConfigureAwait(false);
        var byHandle = observations.ToDictionary(o => o.Handle);

        return handles.Select(h => byHandle[h]).ToList();
    }

    private async Task<CloseObservation> PromptShownAsync(nint handle, ClosePrompt prompt, CancellationToken cancellationToken)
    {
        var (complete, warning) = await CompleteAsync(prompt, handle, cancellationToken).ConfigureAwait(false);
        return new CloseObservation(handle, CloseStatus.PromptShown, complete) { Warning = warning };
    }

    /// <summary>
    /// What became of a window that did not close or show a prompt during the wait, read once more now. Hidden needs a
    /// window the user could see before the close: one that was already hidden or cloaked (on another virtual desktop) and
    /// still exists did not react to the close, so it is StillOpen. A StillOpen window whose prompt could not be inspected
    /// says so (<see cref="CloseObservation.Warning"/>): it may show a prompt nobody saw.
    /// </summary>
    private async Task<CloseObservation> ObserveAsync(nint handle, bool wasShown, CancellationToken cancellationToken)
    {
        var (prompt, warning) = await FindPromptOrWhyNotAsync(handle, cancellationToken).ConfigureAwait(false);
        if (prompt != null)
            return new CloseObservation(handle, CloseStatus.PromptShown, prompt) { Warning = warning };

        return _windows.GetWindowInfo(handle) switch
        {
            null => new CloseObservation(handle, CloseStatus.Closed),
            { IsVisible: false } or { IsCloaked: true } when wasShown => new CloseObservation(handle, CloseStatus.Hidden),
            _ => new CloseObservation(handle, CloseStatus.StillOpen) { Warning = warning }
        };
    }

    /// <inheritdoc />
    public async Task<ClosePrompt?> FindPromptAsync(nint window, CancellationToken cancellationToken) =>
        (await FindPromptOrWhyNotAsync(window, cancellationToken).ConfigureAwait(false)).Prompt;

    /// <summary>
    /// <see cref="FindPromptAsync"/>, with a warning when UI Automation could not answer: that the window's prompt could not be
    /// inspected (finding none then proves nothing), or that a Win32 prompt's text and buttons could not be read.
    /// </summary>
    private async Task<(ClosePrompt? Prompt, string? Warning)> FindPromptOrWhyNotAsync(nint window, CancellationToken cancellationToken)
    {
        if (!_windows.IsValidWindow(window))
            return (null, null);

        if (_windows.FindBlockingDialog(window) is { } dialog)
            return await CompleteAsync(dialog, window, cancellationToken).ConfigureAwait(false);

        var (found, failure) = await RunUiaAsync<ClosePrompt?>(window, token => FindInWindowDialog(window, token), null,
            "look for a prompt inside it", cancellationToken).ConfigureAwait(false);
        return (found, failure == null
            ? null
            : $"Its prompt could not be inspected: {failure}, so a prompt the app draws inside the window may be there unseen. " +
              "Look at the window with capture_window or get_ui_elements before you take it as open without a prompt.");
    }

    /// <summary>
    /// A Win32 prompt with its text and buttons: read through UI Automation when its controls have none (a task dialog
    /// draws them in a DirectUIHWND). The read counts against <paramref name="window"/>'s application, whose prompt it is.
    /// When that read fails, the prompt comes back as it was found, with a warning that its text and buttons could not be read.
    /// </summary>
    private async Task<(ClosePrompt Prompt, string? Warning)> CompleteAsync(ClosePrompt prompt, nint window, CancellationToken cancellationToken)
    {
        if (prompt.Text != null && prompt.Buttons.Count > 0)
            return (prompt, null);

        var (content, failure) = await RunUiaAsync<PromptContent?>(
            window, token => _tree!.FromHandle(prompt.Handle) is { } node ? ReadContent(node, token) : null, null,
            "read its prompt's text and buttons", cancellationToken).ConfigureAwait(false);
        if (content == null)
        {
            return (prompt, failure == null
                ? null
                : $"The prompt's text and buttons could not be read: {failure}. Look at it with capture_window before you ask the user.");
        }

        return (prompt with
        {
            Text = prompt.Text ?? content.Text,
            Buttons = prompt.Buttons.Count > 0 ? prompt.Buttons : content.Buttons
        }, null);
    }

    /// <summary>
    /// A modal dialog drawn inside the window (a Window element whose Window pattern is modal, below the window's own
    /// element), with its text and buttons, or null.
    /// </summary>
    private ClosePrompt? FindInWindowDialog(nint window, CancellationToken token)
    {
        if (_tree!.FromHandle(window) is not { } root)
            return null;

        var queue = new Queue<(IUiaNode Node, int Depth)>(Children(root, token).Select(c => (c, 1)));
        var read = 0;
        while (queue.Count > 0 && read++ < MaxSearchElements && !token.IsCancellationRequested)
        {
            var (node, depth) = queue.Dequeue();
            try
            {
                var properties = _tree.Read(node);
                if (properties.ControlType == "Window" && _tree.ReadIsModal(node) == true)
                {
                    var identity = _tree.ReadIdentity(node);
                    var content = ReadContent(node, token);
                    return new ClosePrompt(
                        identity.NativeWindowHandle != nint.Zero ? identity.NativeWindowHandle : window,
                        properties.Name ?? string.Empty,
                        identity.ClassName ?? string.Empty,
                        content.Text,
                        content.Buttons,
                        ClosePromptKind.InWindowDialog);
                }
            }
            catch (UiaElementUnavailableException)
            {
                // The element went away while it was read; look at the others
                continue;
            }

            if (depth < MaxSearchDepth)
            {
                foreach (var child in Children(node, token))
                    queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }

    /// <summary>
    /// The text (Text elements) and button captions (Button elements) of a prompt, in tree order. The title bar is left
    /// out (its Close button is not an answer), and so is the inside of a button (its own Text repeats the caption).
    /// </summary>
    private PromptContent ReadContent(IUiaNode prompt, CancellationToken token)
    {
        var lines = new List<string>();
        var buttons = new List<string>();
        var stack = new Stack<(IUiaNode Node, int Depth)>(Children(prompt, token).Select(c => (c, 1)).Reverse());
        var read = 0;
        while (stack.Count > 0 && read++ < MaxContentElements && !token.IsCancellationRequested)
        {
            var (node, depth) = stack.Pop();
            UiaNodeProperties properties;
            try
            {
                properties = _tree!.Read(node);
            }
            catch (UiaElementUnavailableException)
            {
                continue;
            }

            switch (properties.ControlType)
            {
                case "TitleBar":
                    continue;
                case "Button":
                    if (!string.IsNullOrWhiteSpace(properties.Name) && buttons.Count < WindowService.MaxPromptButtons)
                        buttons.Add(WindowService.WithoutAccelerators(properties.Name).Trim());
                    continue;
                case "Text":
                    if (!string.IsNullOrWhiteSpace(properties.Name))
                        lines.Add(properties.Name.Trim());
                    break;
            }

            if (depth < MaxContentDepth)
            {
                foreach (var child in Children(node, token).Reverse())
                    stack.Push((child, depth + 1));
            }
        }

        return new PromptContent(WindowService.PromptText(lines), buttons);
    }

    private IReadOnlyList<IUiaNode> Children(IUiaNode node, CancellationToken token)
    {
        try
        {
            return _tree!.GetChildren(node, token);
        }
        catch (UiaElementUnavailableException)
        {
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<bool?> CloseWithUiAutomationAsync(nint window, CancellationToken cancellationToken)
    {
        if (_automation == null || _worker == null)
            return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(UiaReadTimeout);
        try
        {
            // The wrapper checks the deadline right before it asks for the close, so a lookup that took too long asks nothing
            return await _worker.RunAsync(_windows.GetWindowInfo(window)?.ProcessId ?? 0,
                token => _automation.CloseWindow(window, token), deadline.Token, UiaGrace).ConfigureAwait(false);
        }
        catch (UiaReadRefusedException)
        {
            // Not started: earlier calls into the app are still stuck
            return false;
        }
        catch (TimeoutException)
        {
            // Given up on, but a close already under way may still happen
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Not asked (no Window pattern, the window is gone) or failed: the close counts as not sent. Logged, since an
            // unexpected failure (a COMException) would otherwise leave no trace
            _logger?.LogWarning(ex, "The UI Automation close of window {Handle} failed", window);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> WaitForProcessesToExitAsync(
        IReadOnlyList<int> processIds, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        if (_processes == null)
            return processIds.Distinct().ToList();

        var waited = Stopwatch.StartNew();
        while (true)
        {
            var running = processIds.Distinct().Where(id => _processes.GetProcessName(id) != null).ToList();
            var left = wait - waited.Elapsed;
            if (running.Count == 0 || left <= TimeSpan.Zero)
                return running;

            await Task.Delay(left < ExitPollInterval ? left : ExitPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a UI Automation read of <paramref name="window"/>'s application on the worker, under <see cref="UiaReadTimeout"/>.
    /// A read that fails, times out or is refused (the app is not responding) returns <paramref name="fallback"/> with why, in
    /// words for a warning: UI Automation only adds detail to a close result and must never fail it, but the caller says what
    /// it could not find out. An unexpected failure (a COMException) is logged as a warning, a slow or hung app at debug level.
    /// Without UI Automation the fallback comes back without a failure: nothing was tried. Cancelling the call still cancels.
    /// </summary>
    /// <param name="what">What the read does, for the log ("look for a prompt inside it").</param>
    private async Task<(T Value, string? Failure)> RunUiaAsync<T>(nint window, Func<CancellationToken, T> read, T fallback, string what,
        CancellationToken cancellationToken)
    {
        if (_worker == null || _tree == null)
            return (fallback, null);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(UiaReadTimeout);
        try
        {
            return (await _worker.RunAsync(_windows.GetWindowInfo(window)?.ProcessId ?? 0, read, deadline.Token, UiaGrace)
                .ConfigureAwait(false), null);
        }
        catch (UiaReadRefusedException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Did not {What} for window {Handle}: earlier UI Automation calls into its app are stuck", what, window);
            return (fallback, "its app does not answer UI Automation (earlier calls into it are still stuck)");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger?.LogDebug(ex, "Could not {What} for window {Handle} in time", what, window);
            return (fallback, $"UI Automation did not answer within {UiaReadTimeout.TotalMilliseconds:0} ms");
        }
        catch (UiaElementUnavailableException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The window (or the prompt) went away while it was read, as a closing window does: no fault of the server
            _logger?.LogDebug(ex, "Could not {What} for window {Handle}: its elements went away", what, window);
            return (fallback, "its UI Automation elements went away while they were read (it may be closing)");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning(ex, "Could not {What} for window {Handle}: UI Automation failed", what, window);
            return (fallback, $"UI Automation failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private sealed record PromptContent(string? Text, IReadOnlyList<string> Buttons);
}
