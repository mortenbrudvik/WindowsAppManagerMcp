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
public sealed class PauseHotkeyListener : IPauseHotkey, IDisposable
{
    /// <summary>The ID of the first registered key; a chord with Ctrl and Pause registers a second one.</summary>
    public const int FirstHotkeyId = 0x5741;

    /// <summary>ERROR_HOTKEY_ALREADY_REGISTERED.</summary>
    private const int HotkeyAlreadyRegistered = 1409;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly INativeHotkeyWrapper _native;
    private readonly PauseHotkeyOptions _options;
    private readonly ILogger<PauseHotkeyListener>? _logger;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private Action? _pressed;
    private volatile PauseHotkeyStatus _status;

    public PauseHotkeyListener(INativeHotkeyWrapper native, PauseHotkeyOptions options, ILogger<PauseHotkeyListener>? logger = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
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

        _native.PostStopMessage(_threadId);
        if (!thread.Join(StopTimeout))
        {
            _logger?.LogWarning("The pause hotkey thread did not stop within {Timeout} s", StopTimeout.TotalSeconds);
        }
    }

    public void Dispose() => Stop();

    /// <summary>The hotkey thread: register, then wait for messages until the stop message.</summary>
    private void Run()
    {
        var chord = _options.Chord!;
        var registered = new List<int>();
        try
        {
            _threadId = _native.GetCurrentThreadId();
            _native.CreateMessageQueue();

            var failures = new List<string>();
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
                    failures.Add($"{KeyNames.Of(chord.VirtualKeys[i])}: {Describe(result.Win32Error)}");
                }
            }

            _status = registered.Count > 0
                ? _status with
                {
                    State = PauseHotkeyState.Registered,
                    Error = Join(_options.Warning, failures.Count == 0 ? null : $"Registered, but not for every key the chord can send ({string.Join("; ", failures)}).")
                }
                : _status with
                {
                    State = PauseHotkeyState.Failed,
                    Error = Join(_options.Warning,
                        $"Windows refused to register {chord.Text}: {string.Join("; ", failures)}. Set {PauseHotkeyOptions.EnvironmentVariable} to another chord and restart the server.")
                };
        }
        catch (Exception ex)
        {
            _status = _status with { State = PauseHotkeyState.Failed, Error = $"The hotkey could not be registered: {ex.GetType().Name}: {ex.Message}" };
        }
        finally
        {
            _ready.Set();
        }

        if (registered.Count == 0)
        {
            return;
        }

        try
        {
            Listen(registered);
        }
        catch (Exception ex)
        {
            _status = _status with { State = PauseHotkeyState.Failed, Error = $"The hotkey thread failed: {ex.GetType().Name}: {ex.Message}" };
            _logger?.LogError(ex, "The pause hotkey thread failed; the hotkey no longer works");
        }
        finally
        {
            foreach (var id in registered)
            {
                _native.UnregisterHotKey(id);
            }
        }
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
