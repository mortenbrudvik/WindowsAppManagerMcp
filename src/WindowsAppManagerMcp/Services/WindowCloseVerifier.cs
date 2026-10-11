using System.Diagnostics;
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

    /// <param name="windows">Polls and reads the windows.</param>
    /// <param name="tree">Reads prompts through UI Automation; without it (or <paramref name="worker"/>) only Win32 prompts are found.</param>
    /// <param name="worker">Runs the UI Automation reads under a deadline.</param>
    /// <param name="automation">The UI Automation close (<see cref="CloseWithUiAutomationAsync"/>).</param>
    /// <param name="processes">Looks up whether a process still runs (<see cref="WaitForProcessesToExitAsync"/>).</param>
    /// <param name="pollInterval">How often IsWindow is polled; default <see cref="DefaultPollInterval"/>. Tests pass a short one.</param>
    /// <param name="promptCheckInterval">How often Win32 prompts are looked for; default <see cref="DefaultPromptCheckInterval"/>.</param>
    public WindowCloseVerifier(
        IWindowService windows,
        IUiaTree? tree = null,
        IUiaWorker? worker = null,
        IUIAutomationWrapper? automation = null,
        IProcessService? processes = null,
        TimeSpan? pollInterval = null,
        TimeSpan? promptCheckInterval = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _tree = tree;
        _worker = worker;
        _automation = automation;
        _processes = processes;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _promptCheckInterval = promptCheckInterval ?? DefaultPromptCheckInterval;
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

    private async Task<CloseObservation> PromptShownAsync(nint handle, ClosePrompt prompt, CancellationToken cancellationToken) =>
        new(handle, CloseStatus.PromptShown, await CompleteAsync(prompt, handle, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// What became of a window that did not close or show a prompt during the wait, read once more now. Hidden needs a
    /// window the user could see before the close: one that was already hidden or cloaked (on another virtual desktop) and
    /// still exists did not react to the close, so it is StillOpen.
    /// </summary>
    private async Task<CloseObservation> ObserveAsync(nint handle, bool wasShown, CancellationToken cancellationToken)
    {
        if (await FindPromptAsync(handle, cancellationToken).ConfigureAwait(false) is { } prompt)
            return new CloseObservation(handle, CloseStatus.PromptShown, prompt);

        return _windows.GetWindowInfo(handle) switch
        {
            null => new CloseObservation(handle, CloseStatus.Closed),
            { IsVisible: false } or { IsCloaked: true } when wasShown => new CloseObservation(handle, CloseStatus.Hidden),
            _ => new CloseObservation(handle, CloseStatus.StillOpen)
        };
    }

    /// <inheritdoc />
    public async Task<ClosePrompt?> FindPromptAsync(nint window, CancellationToken cancellationToken)
    {
        if (!_windows.IsValidWindow(window))
            return null;

        if (_windows.FindBlockingDialog(window) is { } dialog)
            return await CompleteAsync(dialog, window, cancellationToken).ConfigureAwait(false);

        return await RunUiaAsync<ClosePrompt?>(window, token => FindInWindowDialog(window, token), null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A Win32 prompt with its text and buttons: read through UI Automation when its controls have none (a task dialog
    /// draws them in a DirectUIHWND). The read counts against <paramref name="window"/>'s application, whose prompt it is.
    /// </summary>
    private async Task<ClosePrompt> CompleteAsync(ClosePrompt prompt, nint window, CancellationToken cancellationToken)
    {
        if (prompt.Text != null && prompt.Buttons.Count > 0)
            return prompt;

        var content = await RunUiaAsync<PromptContent?>(
            window, token => _tree!.FromHandle(prompt.Handle) is { } node ? ReadContent(node, token) : null, null, cancellationToken)
            .ConfigureAwait(false);
        return content == null
            ? prompt
            : prompt with
            {
                Text = prompt.Text ?? content.Text,
                Buttons = prompt.Buttons.Count > 0 ? prompt.Buttons : content.Buttons
            };
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
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
    /// A read that fails, times out or is refused (the app is not responding) returns <paramref name="fallback"/>: UI
    /// Automation only adds detail to a close result and must never fail it. Cancelling the call still cancels.
    /// </summary>
    private async Task<T> RunUiaAsync<T>(nint window, Func<CancellationToken, T> read, T fallback, CancellationToken cancellationToken)
    {
        if (_worker == null || _tree == null)
            return fallback;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(UiaReadTimeout);
        try
        {
            return await _worker.RunAsync(_windows.GetWindowInfo(window)?.ProcessId ?? 0, read, deadline.Token, UiaGrace)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return fallback;
        }
    }

    private sealed record PromptContent(string? Text, IReadOnlyList<string> Buttons);
}
