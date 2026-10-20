using System.Runtime.InteropServices;
using System.Text;

namespace WindowsAppManagerMcp.FixtureApp;

/// <summary>
/// The few Win32 calls the fixture needs: its DPI awareness, its own geometry for the ready event, the Win32 menu bar
/// of its menu window, the long form of a path for its save folder, its task dialog, the monitors for
/// <see cref="FixturePlacement.CenterOnMonitorAtAnotherScale"/>, the hook of <see cref="NoActivateWhileShown"/>, the
/// styles and owner the overlay reports, and the display affinity it can set on itself (issue #17).
/// </summary>
internal static class NativeMethods
{
    public static readonly nint DpiAwarenessContextUnaware = -1;
    public static readonly nint DpiAwarenessContextSystemAware = -2;
    public static readonly nint DpiAwarenessContextPerMonitorAware = -3;
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = -4;

    public const int WM_COMMAND = 0x0111;
    public const uint MF_STRING = 0x0000;
    public const uint MF_POPUP = 0x0010;

    public const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
    public const uint TDF_USE_COMMAND_LINKS = 0x0010;
    public const uint TDF_POSITION_RELATIVE_TO_WINDOW = 0x1000;
    public const uint TDCBF_CANCEL_BUTTON = 0x0008;
    public const uint TDN_CREATED = 0;

    /// <summary>A task dialog's notification callback (PFTASKDIALOGCALLBACK).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate int TaskDialogCallback(nint hwnd, uint notification, nint wParam, nint lParam, nint referenceData);

    /// <summary>TASKDIALOGCONFIG. commctrl.h packs it to 1 byte on every platform; every string is an HGLOBAL the caller frees.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public nint hwndParent;
        public nint hInstance;
        public uint dwFlags;
        public uint dwCommonButtons;
        public nint pszWindowTitle;
        public nint hMainIcon;
        public nint pszMainInstruction;
        public nint pszContent;
        public uint cButtons;
        public nint pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public nint pRadioButtons;
        public int nDefaultRadioButton;
        public nint pszVerificationText;
        public nint pszExpandedInformation;
        public nint pszExpandedControlText;
        public nint pszCollapsedControlText;
        public nint hFooterIcon;
        public nint pszFooter;
        public nint pfCallback;
        public nint lpCallbackData;
        public uint cxWidth;
    }

    /// <summary>TASKDIALOG_BUTTON, packed as <see cref="TASKDIALOGCONFIG"/>.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TASKDIALOG_BUTTON
    {
        public int nButtonID;
        public nint pszButtonText;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    public static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern int GetAwarenessFromDpiAwarenessContext(nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>Where the cursor was when the last message read from the queue was posted: a button message's point.</summary>
    [DllImport("user32.dll")]
    public static extern uint GetMessagePos();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hwnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(nint hwnd, ref POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreateMenu();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(nint menu, uint flags, nuint idOrSubmenu, string text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenu(nint hwnd, nint menu);

    /// <summary>Shows a task dialog; returns an HRESULT. In comctl32 version 6 only, which app.manifest asks for.</summary>
    [DllImport("comctl32.dll")]
    public static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG config, out int button, nint radioButton, nint verificationChecked);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint length);

    /// <summary>The long form of an existing path (8.3 short names expanded), or the path itself when it cannot be read.</summary>
    public static string LongPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
    }

    public delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    /// <summary>MDT_EFFECTIVE_DPI: the scale the user chose for the monitor.</summary>
    public const int EffectiveDpi = 0;

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    public const int WH_CBT = 5;
    public const int HCBT_CREATEWND = 3;
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000;

    public delegate nint HookProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, HookProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(nint hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    public static extern nint GetWindow(nint hwnd, uint command);

    /// <summary>WDA_EXCLUDEFROMCAPTURE (Windows 10 2004 and later): shown on the monitor, left out of every capture.</summary>
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
}
