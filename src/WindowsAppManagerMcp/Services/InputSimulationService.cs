using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Native;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Service for simulating mouse input using Windows SendInput API.
/// </summary>
public class InputSimulationService : IInputSimulationService
{
    private readonly IMonitorService _monitorService;

    public InputSimulationService(IMonitorService monitorService)
    {
        _monitorService = monitorService;
    }

    public ClickResult Click(int x, int y)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "left",
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        if (!SetCursorAndClick(x, y, leftButton: true))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "left",
                Error: "SendInput failed",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        return new ClickResult(
            Success: true,
            X: x,
            Y: y,
            ClickType: "left");
    }

    public ClickResult RightClick(int x, int y)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "right",
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        if (!SetCursorAndClick(x, y, leftButton: false))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "right",
                Error: "SendInput failed",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        return new ClickResult(
            Success: true,
            X: x,
            Y: y,
            ClickType: "right");
    }

    public ClickResult DoubleClick(int x, int y, int delayMs = 50)
    {
        if (!IsValidScreenCoordinate(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "double",
                Error: "Coordinates are outside screen bounds",
                ErrorCode: InputErrorCode.CoordinatesOutOfBounds);
        }

        // Move cursor to position first
        if (!NativeMethods.Input.SetCursorPos(x, y))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "double",
                Error: "Failed to set cursor position",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        // Small delay to ensure cursor position is updated
        Thread.Sleep(10);

        // First click
        if (!SendClickInputs(leftButton: true))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "double",
                Error: "First click in double-click failed",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        // Delay between clicks
        Thread.Sleep(Math.Clamp(delayMs, 10, 500));

        // Second click
        if (!SendClickInputs(leftButton: true))
        {
            return new ClickResult(
                Success: false,
                X: x,
                Y: y,
                ClickType: "double",
                Error: "Second click in double-click failed",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        return new ClickResult(
            Success: true,
            X: x,
            Y: y,
            ClickType: "double");
    }

    public MouseMoveResult MoveMouse(int x, int y)
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

        if (!NativeMethods.Input.SetCursorPos(x, y))
        {
            return new MouseMoveResult(
                Success: false,
                X: x,
                Y: y,
                Error: "Failed to set cursor position",
                ErrorCode: InputErrorCode.SendInputFailed);
        }

        return new MouseMoveResult(
            Success: true,
            X: x,
            Y: y);
    }

    public (int X, int Y)? GetCursorPosition()
    {
        if (NativeMethods.Input.GetCursorPos(out var point))
        {
            return (point.X, point.Y);
        }
        return null;
    }

    public bool IsValidScreenCoordinate(int x, int y)
    {
        var monitors = _monitorService.GetAllMonitors();
        return monitors.Any(m =>
            x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width &&
            y >= m.Bounds.Y && y < m.Bounds.Y + m.Bounds.Height);
    }

    private bool SetCursorAndClick(int x, int y, bool leftButton)
    {
        // Move cursor to position
        if (!NativeMethods.Input.SetCursorPos(x, y))
        {
            return false;
        }

        // Small delay to ensure cursor position is updated
        Thread.Sleep(10);

        return SendClickInputs(leftButton);
    }

    private static bool SendClickInputs(bool leftButton)
    {
        var downFlag = leftButton ? NativeEnums.MOUSEEVENTF_LEFTDOWN : NativeEnums.MOUSEEVENTF_RIGHTDOWN;
        var upFlag = leftButton ? NativeEnums.MOUSEEVENTF_LEFTUP : NativeEnums.MOUSEEVENTF_RIGHTUP;

        var inputs = new INPUT[]
        {
            INPUT.MouseInput(0, 0, downFlag),
            INPUT.MouseInput(0, 0, upFlag)
        };

        var result = NativeMethods.Input.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return result == inputs.Length;
    }
}
