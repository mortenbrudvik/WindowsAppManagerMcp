using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for simulating mouse and keyboard input using Windows SendInput API. Every call runs the input
/// guard (<see cref="IInputTargetGuard"/>) inside the input gate before each SendInput batch, and reports
/// where the input went.
/// </summary>
public class InputSimulationService : IInputSimulationService
{
    /// <summary>
    /// type_text sends at most this many characters in one SendInput batch, and the guard checks the
    /// foreground window again before each batch, so typing stops within this many characters of a focus
    /// change. With a delay between keystrokes every character is its own batch.
    /// </summary>
    public const int TypeTextBatchSize = 32;

    /// <summary>
    /// Serializes input across the whole process. The MCP SDK runs tool calls concurrently, so
    /// without this gate two type_text calls would interleave their characters, and a mouse move
    /// could land between a click's cursor move and its button press.
    /// </summary>
    private static readonly object InputGate = new();

    private readonly IMonitorService _monitorService;
    private readonly INativeInputWrapper _inputWrapper;
    private readonly IInputTargetGuard _guard;

    public InputSimulationService(IMonitorService monitorService, INativeInputWrapper inputWrapper, IInputTargetGuard guard)
    {
        _monitorService = monitorService;
        _inputWrapper = inputWrapper;
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public ClickResult Click(int x, int y, InputTarget target = default) =>
        ClickAt(x, y, target, "left", leftButton: true, clicks: 1, delayMs: 0);

    public ClickResult RightClick(int x, int y, InputTarget target = default) =>
        ClickAt(x, y, target, "right", leftButton: false, clicks: 1, delayMs: 0);

    public ClickResult DoubleClick(int x, int y, int delayMs = 50, InputTarget target = default) =>
        ClickAt(x, y, target, "double", leftButton: true, clicks: 2, delayMs: delayMs);

    public MouseMoveResult MoveMouse(int x, int y, InputTarget target = default)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new MouseMoveResult(
                Success: false,
                X: x,
                Y: y,
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        using var gate = EnterInputGate();

        var approved = _guard.CheckMouse(x, y, target);
        if (!approved.Allowed)
        {
            return Finish(new MouseMoveResult(false, x, y, approved.Error, approved.ErrorCode), approved);
        }

        if (!_inputWrapper.SetCursorPos(x, y))
        {
            return Finish(new MouseMoveResult(false, x, y, "Failed to set cursor position", InputErrorCode.SendInputFailed), approved);
        }

        return Finish(new MouseMoveResult(Success: true, X: x, Y: y), approved);
    }

    public (int X, int Y)? GetCursorPosition()
    {
        return _inputWrapper.GetCursorPos();
    }

    public bool IsValidScreenCoordinate(int x, int y)
    {
        var monitors = _monitorService.GetAllMonitors();
        return monitors.Any(m =>
            x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width &&
            y >= m.Bounds.Y && y < m.Bounds.Y + m.Bounds.Height);
    }

    public TypeTextResult TypeText(string text, int delayBetweenKeysMs = 0, InputTarget target = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TypeTextResult(
                Success: false,
                Text: text ?? "",
                CharactersTyped: 0,
                Error: "Text cannot be empty",
                ErrorCode: InputErrorCode.EmptyText);
        }

        delayBetweenKeysMs = Math.Clamp(delayBetweenKeysMs, 0, 100);
        var charactersTyped = 0;

        using var gate = EnterInputGate();

        var approved = _guard.CheckKeyboard(target, KeyboardInputKind.Text, keys: null);
        if (!approved.Allowed)
        {
            return Finish(new TypeTextResult(false, text, 0, approved.Error, approved.ErrorCode), approved);
        }

        foreach (var batch in Batches(text, delayBetweenKeysMs == 0 ? TypeTextBatchSize : 1))
        {
            if (charactersTyped > 0)
            {
                if (delayBetweenKeysMs > 0)
                {
                    Thread.Sleep(delayBetweenKeysMs);
                }

                // Before every later batch: still the same window, or a dialog of the target?
                var recheck = _guard.RecheckKeyboard(approved);
                if (!recheck.Allowed)
                {
                    return Finish(new TypeTextResult(false, text, charactersTyped,
                        $"Typing stopped after {charactersTyped} of {text.Length} characters, which went to " +
                        $"{Describe(approved.ReceivingWindow)}. {recheck.Error}",
                        recheck.ErrorCode), recheck);
                }

                approved = recheck;
            }

            var inputs = new List<InputEvent>(batch.Length * 2);
            foreach (var character in batch)
            {
                inputs.Add(InputEvent.Unicode(character, NativeEnums.KEYEVENTF_KEYDOWN));
                inputs.Add(InputEvent.Unicode(character, NativeEnums.KEYEVENTF_KEYUP));
            }

            var sent = _inputWrapper.SendInput(inputs);
            if (sent != inputs.Count)
            {
                // A character counts once its key-down and key-up were both inserted
                charactersTyped += (int)Math.Min(sent, (uint)inputs.Count) / 2;
                return Finish(new TypeTextResult(false, text, charactersTyped,
                    $"SendInput failed at character {charactersTyped}", InputErrorCode.SendInputFailed), approved);
            }

            charactersTyped += batch.Length;
        }

        return Finish(new TypeTextResult(Success: true, Text: text, CharactersTyped: charactersTyped), approved);
    }

    public SendKeysResult SendKeys(string keys, InputTarget target = default)
    {
        if (string.IsNullOrEmpty(keys))
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys ?? "",
                Error: "Keys cannot be empty",
                ErrorCode: InputErrorCode.EmptyText);
        }

        List<InputEvent> inputs;
        try
        {
            inputs = SendKeysParser.Parse(keys);
        }
        catch (ArgumentException ex)
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys,
                Error: ex.Message,
                ErrorCode: InputErrorCode.UnknownKey);
        }

        if (inputs.Count == 0)
        {
            return new SendKeysResult(
                Success: false,
                Keys: keys,
                Error: "No valid keys parsed from input",
                ErrorCode: InputErrorCode.InvalidKeySpecification);
        }

        using var gate = EnterInputGate();

        var approved = _guard.CheckKeyboard(target, KeyboardInputKind.Keys, inputs);
        if (!approved.Allowed)
        {
            return Finish(new SendKeysResult(false, keys, approved.Error, approved.ErrorCode), approved);
        }

        var result = _inputWrapper.SendInput(inputs);
        if (result != inputs.Count)
        {
            return Finish(new SendKeysResult(false, keys, "SendInput failed to send all keystrokes", InputErrorCode.SendInputFailed),
                approved);
        }

        return Finish(new SendKeysResult(Success: true, Keys: keys), approved);
    }

    /// <summary>
    /// A click or double click at a point: the guard checks the window at the point before the cursor
    /// moves, and again before every button press, since moving the cursor can open a popup there.
    /// </summary>
    private ClickResult ClickAt(int x, int y, InputTarget target, string clickType, bool leftButton, int clicks, int delayMs)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: clickType,
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        using var gate = EnterInputGate();

        var approved = _guard.CheckMouse(x, y, target);
        if (!approved.Allowed)
        {
            return Finish(new ClickResult(false, x, y, clickType, approved.Error, approved.ErrorCode), approved);
        }

        if (!_inputWrapper.SetCursorPos(x, y))
        {
            return Finish(new ClickResult(false, x, y, clickType,
                clicks == 1 ? "SendInput failed" : "Failed to set cursor position", InputErrorCode.SendInputFailed), approved);
        }

        // Small delay to ensure cursor position is updated
        Thread.Sleep(10);

        for (var click = 1; click <= clicks; click++)
        {
            if (click > 1)
            {
                // Delay between clicks
                Thread.Sleep(Math.Clamp(delayMs, 10, 500));
            }

            var recheck = _guard.RecheckMouse(x, y, approved);
            if (!recheck.Allowed)
            {
                var sentSoFar = click == 1 ? "Nothing was clicked: " : "The first click was sent, the second was not: ";
                return Finish(new ClickResult(false, x, y, clickType, sentSoFar + recheck.Error, recheck.ErrorCode), recheck);
            }

            approved = recheck;
            if (!SendClickInputs(leftButton))
            {
                var error = clicks == 1 ? "SendInput failed"
                    : click == 1 ? "First click in double-click failed"
                    : "Second click in double-click failed";
                return Finish(new ClickResult(false, x, y, clickType, error, InputErrorCode.SendInputFailed), approved);
            }
        }

        return Finish(new ClickResult(Success: true, X: x, Y: y, ClickType: clickType), approved);
    }

    private bool SendClickInputs(bool leftButton)
    {
        var downFlag = leftButton ? NativeEnums.MOUSEEVENTF_LEFTDOWN : NativeEnums.MOUSEEVENTF_RIGHTDOWN;
        var upFlag = leftButton ? NativeEnums.MOUSEEVENTF_LEFTUP : NativeEnums.MOUSEEVENTF_RIGHTUP;

        var inputs = new[]
        {
            InputEvent.Mouse(downFlag),
            InputEvent.Mouse(upFlag)
        };

        var result = _inputWrapper.SendInput(inputs);
        return result == inputs.Length;
    }

    /// <summary>Adds where the input went, what is in front now and where the cursor is.</summary>
    private ClickResult Finish(ClickResult result, InputGuardResult guard)
    {
        var cursor = _inputWrapper.GetCursorPos();
        return result with
        {
            ReceivingWindow = guard.ReceivingWindow,
            BlockingWindow = guard.BlockingWindow,
            ForegroundAfter = _guard.DescribeForeground(),
            ActualX = cursor?.X,
            ActualY = cursor?.Y
        };
    }

    private MouseMoveResult Finish(MouseMoveResult result, InputGuardResult guard)
    {
        var cursor = _inputWrapper.GetCursorPos();
        return result with
        {
            ReceivingWindow = guard.ReceivingWindow,
            BlockingWindow = guard.BlockingWindow,
            ForegroundAfter = _guard.DescribeForeground(),
            ActualX = cursor?.X,
            ActualY = cursor?.Y
        };
    }

    private TypeTextResult Finish(TypeTextResult result, InputGuardResult guard) => result with
    {
        ReceivingWindow = guard.ReceivingWindow,
        BlockingWindow = guard.BlockingWindow,
        ForegroundAfter = _guard.DescribeForeground()
    };

    private SendKeysResult Finish(SendKeysResult result, InputGuardResult guard) => result with
    {
        ReceivingWindow = guard.ReceivingWindow,
        BlockingWindow = guard.BlockingWindow,
        ForegroundAfter = _guard.DescribeForeground()
    };

    /// <summary>
    /// Splits <paramref name="text"/> into batches of at most <paramref name="size"/> UTF-16 code units,
    /// never between the two halves of a surrogate pair (an emoji, for example), so a batch that is not
    /// sent never leaves half a character typed.
    /// </summary>
    private static IEnumerable<string> Batches(string text, int size)
    {
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(size, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1]))
            {
                length++;
            }

            yield return text.Substring(start, length);
            start += length;
        }
    }

    private static string Describe(WindowSummary? window) => window switch
    {
        null => "no window",
        { Title.Length: > 0 } => $"'{window.Title}' ({window.ProcessName})",
        _ => $"an untitled window of {window.ProcessName}"
    };

    /// <summary>
    /// Holds the process-wide input gate until the returned scope is disposed, so the input one
    /// call sends is never interleaved with input from a concurrent call.
    /// </summary>
    private static InputGateScope EnterInputGate()
    {
        Monitor.Enter(InputGate);
        return new InputGateScope();
    }

    private readonly struct InputGateScope : IDisposable
    {
        public void Dispose() => Monitor.Exit(InputGate);
    }
}
