using Microsoft.Extensions.Logging;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// The pause hotkey on its own background thread (see <see cref="IPauseHotkey"/>). RegisterHotKey without a
/// window posts WM_HOTKEY to the registering thread, so that thread registers the chord and then waits for
/// messages until it is stopped. Everything native goes through <see cref="INativeHotkeyWrapper"/>, which the
/// tests replace with a fake.
/// </summary>
/// <remarks>
/// Every MCP client session starts its own server, and only one process can register a chord. A server that
/// registered it marks itself as the chord's holder; one that finds the chord taken reports whether a server
/// holds it (<see cref="PauseHotkeyState.OtherServer"/>: its presses pause every server, since they share the
/// pause) or another program does (<see cref="PauseHotkeyState.Failed"/>), and tries again every
/// <see cref="DefaultRetryInterval"/>, so the chord keeps working after the server that held it stops.
/// </remarks>
public sealed class PauseHotkeyListener : IPauseHotkey, IDisposable
{
    /// <summary>The ID of the first registered key; a chord with Ctrl and Pause registers a second one.</summary>
    public const int FirstHotkeyId = 0x5741;

    /// <summary>How often a server whose chord is taken tries to register it again.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED.</summary>
    private const int HotkeyAlreadyRegistered = 1409;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly INativeHotkeyWrapper _native;
    private readonly PauseHotkeyOptions _options;
    private readonly ILogger<PauseHotkeyListener>? _logger;
    private readonly TimeSpan _retryInterval;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly ManualResetEventSlim _stopping = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private Action? _pressed;
    private volatile PauseHotkeyStatus _status;

    /// <param name="native">The native hotkey calls.</param>
    /// <param name="options">The chord.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="retryInterval">How often to try again while the chord is taken; default <see cref="DefaultRetryInterval"/>.</param>
    public PauseHotkeyListener(
        INativeHotkeyWrapper native,
        PauseHotkeyOptions options,
        ILogger<PauseHotkeyListener>? logger = null,
        TimeSpan? retryInterval = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _retryInterval = retryInterval ?? DefaultRetryInterval;
        _status = options.Chord == null
            ? new PauseHotkeyStatus(null, PauseHotkeyState.Disabled, options.Source)
            : new PauseHotkeyStatus(options.Chord.Text, PauseHotkeyState.NotStarted, options.Source, options.Warning);
    }

    /// <inheritdoc />
    public PauseHotkeyStatus Status => _status;

    /// <inheritdoc />
    public void Start(Action pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);
        lock (_lock)
        {
            if (_thread != null || _options.Chord == null)
            {
                return;
            }

            _pressed = pressed;
            _thread = new Thread(Run) { IsBackground = true, Name = "WAM pause hotkey" };
            _thread.Start();
        }

        if (!_ready.Wait(StartTimeout))
        {
            _status = _status with
            {
                State = PauseHotkeyState.Failed,
                Error = $"The hotkey thread did not register the chord within {StartTimeout.TotalSeconds:0} s."
            };
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        Thread? thread;
        lock (_lock)
        {
            thread = _thread;
        }

        // The thread posts nothing before its queue exists, and Start waited for that
        if (thread == null || !thread.IsAlive || !_ready.IsSet)
        {
            return;
        }

        // The flag ends the wait between registration attempts; the message ends the message loop
        _stopping.Set();
        _native.PostStopMessage(_threadId);
        if (!thread.Join(StopTimeout))
        {
            _logger?.LogWarning("The pause hotkey thread did not stop within {Timeout} s", StopTimeout.TotalSeconds);
        }
    }

    public void Dispose() => Stop();

    /// <summary>
    /// The hotkey thread: register, or try again every retry interval while the chord is taken; then wait for
    /// messages until the stop message.
    /// </summary>
    private void Run()
    {
        var chord = _options.Chord!;
        var registered = new List<int>();
        IDisposable? holderMark = null;
        try
        {
            bool retry;
            try
            {
                _threadId = _native.GetCurrentThreadId();
                _native.CreateMessageQueue();
                retry = !TryRegister(chord, registered, ref holderMark);
            }
            catch (Exception ex)
            {
                // The status says Failed, so nothing may listen: free a key that was registered before the failure
                Free(registered, ref holderMark);
                _status = _status with { State = PauseHotkeyState.Failed, Error = $"The hotkey could not be registered: {ex.GetType().Name}: {ex.Message}" };
                retry = false;
            }
            finally
            {
                _ready.Set();
            }

            while (retry && registered.Count == 0)
            {
                if (_stopping.Wait(_retryInterval))
                {
                    return;
                }

                retry = !TryRegister(chord, registered, ref holderMark);
            }

            if (registered.Count == 0)
            {
                return;
            }

            Listen(registered);
        }
        catch (Exception ex)
        {
            _status = _status with { State = PauseHotkeyState.Failed, Error = $"The hotkey thread failed: {ex.GetType().Name}: {ex.Message}" };
            _logger?.LogError(ex, "The pause hotkey thread failed; the hotkey no longer works");
        }
        finally
        {
            Free(registered, ref holderMark);
        }
    }

    /// <summary>Unregisters the keys in <paramref name="registered"/> and removes the holder mark.</summary>
    private void Free(List<int> registered, ref IDisposable? holderMark)
    {
        foreach (var id in registered)
        {
            _native.UnregisterHotKey(id);
        }

        registered.Clear();
        holderMark?.Dispose();
        holderMark = null;
    }

    /// <summary>
    /// Registers the keys of the chord and updates the status. False only when nothing was registered because
    /// someone else has the chord, so trying again later can help.
    /// </summary>
    /// <remarks>
    /// Once a key is registered, this server marks itself as the chord's holder (<paramref name="holderMark"/>)
    /// before the status says Registered, and so before <see cref="Start"/> returns: a server that starts after
    /// that finds the chord taken and marked, and reports <see cref="PauseHotkeyState.OtherServer"/>, not
    /// <see cref="PauseHotkeyState.Failed"/> (another program). Only one that tries in the moment between the
    /// registration and the mark sees Failed, until its next attempt.
    /// </remarks>
    private bool TryRegister(PauseHotkeyChord chord, List<int> registered, ref IDisposable? holderMark)
    {
        var failures = new List<string>();
        var taken = 0;
        for (var i = 0; i < chord.VirtualKeys.Count; i++)
        {
            var id = FirstHotkeyId + i;
            var result = _native.RegisterHotKey(id, chord.RegisterModifiers, chord.VirtualKeys[i]);
            if (result.Succeeded)
            {
                registered.Add(id);
            }
            else
            {
                taken += result.Win32Error == HotkeyAlreadyRegistered ? 1 : 0;
                failures.Add($"{KeyNames.Of(chord.VirtualKeys[i])}: {Describe(result.Win32Error)}");
            }
        }

        var allTaken = taken == chord.VirtualKeys.Count;
        if (registered.Count > 0)
        {
            holderMark = MarkHolder(chord);
            if (_status.State is PauseHotkeyState.OtherServer or PauseHotkeyState.Failed)
            {
                _logger?.LogWarning("The pause hotkey {Chord} is free again and is now registered by this server", chord.Text);
            }

            _status = _status with
            {
                State = PauseHotkeyState.Registered,
                Error = Join(_options.Warning, failures.Count == 0 ? null : $"Registered, but not for every key the chord can send ({string.Join("; ", failures)}).")
            };
            return true;
        }

        if (allTaken && IsHeldByAServer(chord))
        {
            _status = _status with
            {
                State = PauseHotkeyState.OtherServer,
                Error = Join(_options.Warning,
                    $"Another Windows App Manager server on this desktop has registered {chord.Text}. Pressing it pauses and resumes " +
                    "this server too, because the servers share the pause; this server registers the chord itself once that server stops.")
            };
            return false;
        }

        _status = _status with
        {
            State = PauseHotkeyState.Failed,
            Error = Join(_options.Warning,
                $"Windows refused to register {chord.Text}: {string.Join("; ", failures)}. " + (allTaken
                    ? $"The server tries again every {_retryInterval.TotalSeconds:0.#} s. To use another chord, set {PauseHotkeyOptions.EnvironmentVariable} and restart the server."
                    : $"Set {PauseHotkeyOptions.EnvironmentVariable} to another chord and restart the server."))
        };
        return !allTaken;
    }

    private bool IsHeldByAServer(PauseHotkeyChord chord)
    {
        try
        {
            return _native.IsChordHeldByAServer(ChordId(chord));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks this server as the chord's holder, so other servers report that a server has it. A failure, of any
    /// kind, is not fatal: the hotkey works without the mark, and a server that starts later only reports Failed
    /// instead of OtherServer.
    /// </summary>
    private IDisposable? MarkHolder(PauseHotkeyChord chord)
    {
        try
        {
            return _native.MarkChordHolder(ChordId(chord));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The pause hotkey is registered, but other servers cannot see that this server holds it");
            return null;
        }
    }

    /// <summary>The chord as a name part: its RegisterHotKey modifiers and key, such as "4007-13".</summary>
    public static string ChordId(PauseHotkeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        return $"{chord.RegisterModifiers:X4}-{chord.Key:X2}";
    }

    private void Listen(List<int> registered)
    {
        while (true)
        {
            var message = _native.WaitForMessage();
            switch (message.Kind)
            {
                case HotkeyMessageKind.Stop:
                    return;

                case HotkeyMessageKind.Failed:
                    _status = _status with
                    {
                        State = PauseHotkeyState.Failed,
                        Error = $"The hotkey thread's message loop failed ({Describe(message.Win32Error)}), so the hotkey no longer works."
                    };
                    _logger?.LogError("The pause hotkey's message loop failed with Windows error {Error}; the hotkey no longer works",
                        message.Win32Error);
                    return;

                case HotkeyMessageKind.Hotkey when registered.Contains(message.HotkeyId):
                    try
                    {
                        _pressed?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        // The next press must still work
                        _logger?.LogError(ex, "Handling the pause hotkey failed");
                    }

                    break;
            }
        }
    }

    private static string Describe(int win32Error) => win32Error == HotkeyAlreadyRegistered
        ? "another program already uses this chord (ERROR_HOTKEY_ALREADY_REGISTERED)"
        : $"Windows error {win32Error}";

    private static string? Join(string? first, string? second) =>
        first == null ? second : second == null ? first : $"{first} {second}";
}
