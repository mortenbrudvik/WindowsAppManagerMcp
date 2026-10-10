using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeSecurityWrapper"/>. Every call only reads: it
/// opens processes for PROCESS_QUERY_LIMITED_INFORMATION, tokens for TOKEN_QUERY and the input
/// desktop for DESKTOP_READOBJECTS, and closes every handle it opens. NativeCallGuardTests holds it
/// to the read-only API allow-list.
/// </summary>
public sealed class NativeSecurityWrapper : INativeSecurityWrapper
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    /// <summary>
    /// WTSINFOEXW is a DWORD Level followed by a union that contains LARGE_INTEGERs, so the union is
    /// 8-byte aligned: the WTSINFOEX_LEVEL1_W fields start at offset 8.
    /// </summary>
    private const int Level1Offset = 8;

    private const int Level1SessionIdOffset = Level1Offset;
    private const int Level1SessionStateOffset = Level1Offset + 4;
    private const int Level1SessionFlagsOffset = Level1Offset + 8;

    public DateTime? GetProcessCreationTime(int processId)
    {
        var process = OpenForQuery(processId);
        if (process == nint.Zero)
        {
            return null;
        }

        try
        {
            return ReadCreationTime(process);
        }
        finally
        {
            NativeMethods.Kernel32.CloseHandle(process);
        }
    }

    public ProcessTokenQuery QueryProcessToken(int processId)
    {
        if (processId <= 0)
        {
            // Not a process ID; no call was made, so there is no last error to read
            return new ProcessTokenQuery(TokenQueryStatus.ProcessNotFound, Win32Error: ErrorInvalidParameter);
        }

        var process = OpenForQuery(processId);
        if (process == nint.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            var status = error switch
            {
                ErrorInvalidParameter => TokenQueryStatus.ProcessNotFound,
                ErrorAccessDenied => TokenQueryStatus.ProcessAccessDenied,
                _ => TokenQueryStatus.Failed
            };
            return new ProcessTokenQuery(status, Win32Error: error);
        }

        try
        {
            var creationTime = ReadCreationTime(process);

            if (!NativeMethods.Advapi32.OpenProcessToken(process, NativeMethods.Advapi32.TOKEN_QUERY, out var token))
            {
                var error = Marshal.GetLastPInvokeError();
                var status = error == ErrorAccessDenied ? TokenQueryStatus.TokenAccessDenied : TokenQueryStatus.Failed;
                return new ProcessTokenQuery(status, creationTime, Win32Error: error);
            }

            try
            {
                var rid = ReadIntegrityRid(token, out var integrityError);
                var elevated = ReadElevation(token);
                return rid.HasValue
                    ? new ProcessTokenQuery(TokenQueryStatus.Succeeded, creationTime, rid, elevated)
                    : new ProcessTokenQuery(TokenQueryStatus.Failed, creationTime, null, elevated, integrityError);
            }
            finally
            {
                NativeMethods.Kernel32.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.Kernel32.CloseHandle(process);
        }
    }

    public InputDesktopQuery QueryInputDesktop()
    {
        var desktop = NativeMethods.User32.OpenInputDesktop(0, false, NativeMethods.User32.DESKTOP_READOBJECTS);
        if (desktop == nint.Zero)
        {
            return new InputDesktopQuery(Opened: false, Name: null, Win32Error: Marshal.GetLastPInvokeError());
        }

        try
        {
            return new InputDesktopQuery(Opened: true, Name: ReadObjectName(desktop), Win32Error: 0);
        }
        finally
        {
            NativeMethods.User32.CloseDesktop(desktop);
        }
    }

    public string? GetThreadDesktopName()
    {
        // A handle from GetThreadDesktop must not be closed.
        var desktop = NativeMethods.User32.GetThreadDesktop(NativeMethods.Kernel32.GetCurrentThreadId());
        return desktop == nint.Zero ? null : ReadObjectName(desktop);
    }

    public SessionStateQuery? QuerySessionState()
    {
        int sessionId;
        try
        {
            using var current = Process.GetCurrentProcess();
            sessionId = current.SessionId;
        }
        catch (Exception)
        {
            return null;
        }

        if (!NativeMethods.Wtsapi32.WTSQuerySessionInformationW(
                NativeMethods.Wtsapi32.WTS_CURRENT_SERVER_HANDLE,
                (uint)sessionId,
                NativeMethods.Wtsapi32.WTSSessionInfoEx,
                out var buffer,
                out var bytesReturned))
        {
            return null;
        }

        try
        {
            // Level must be 1, and the buffer must reach SessionFlags.
            if (buffer == nint.Zero ||
                bytesReturned < Level1SessionFlagsOffset + sizeof(int) ||
                Marshal.ReadInt32(buffer) != 1)
            {
                return null;
            }

            return new SessionStateQuery(
                SessionId: Marshal.ReadInt32(buffer, Level1SessionIdOffset),
                ConnectState: Marshal.ReadInt32(buffer, Level1SessionStateOffset),
                SessionFlags: Marshal.ReadInt32(buffer, Level1SessionFlagsOffset));
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                NativeMethods.Wtsapi32.WTSFreeMemory(buffer);
            }
        }
    }

    private static nint OpenForQuery(int processId) =>
        processId <= 0
            ? nint.Zero
            : NativeMethods.Kernel32.OpenProcess(NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);

    private static DateTime? ReadCreationTime(nint process) =>
        NativeMethods.Kernel32.GetProcessTimes(process, out var creation, out _, out _, out _) && creation > 0
            ? DateTime.FromFileTimeUtc(creation)
            : null;

    /// <summary>
    /// Reads TokenIntegrityLevel: a TOKEN_MANDATORY_LABEL whose first field is the label's SID
    /// pointer (into the same buffer). The integrity RID is the SID's last sub-authority.
    /// </summary>
    private static int? ReadIntegrityRid(nint token, out int win32Error)
    {
        win32Error = 0;
        NativeMethods.Advapi32.GetTokenInformation(token, NativeMethods.Advapi32.TokenIntegrityLevel, nint.Zero, 0, out var needed);
        if (needed == 0)
        {
            win32Error = Marshal.GetLastPInvokeError();
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.Advapi32.GetTokenInformation(token, NativeMethods.Advapi32.TokenIntegrityLevel, buffer, needed, out _))
            {
                win32Error = Marshal.GetLastPInvokeError();
                return null;
            }

            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == nint.Zero)
            {
                return null;
            }

            var countPointer = NativeMethods.Advapi32.GetSidSubAuthorityCount(sid);
            if (countPointer == nint.Zero)
            {
                return null;
            }

            var count = Marshal.ReadByte(countPointer);
            if (count == 0)
            {
                return null;
            }

            var ridPointer = NativeMethods.Advapi32.GetSidSubAuthority(sid, (uint)(count - 1));
            return ridPointer == nint.Zero ? null : Marshal.ReadInt32(ridPointer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads TokenElevation: a TOKEN_ELEVATION, one DWORD that is non-zero when elevated.</summary>
    private static unsafe bool? ReadElevation(nint token)
    {
        int elevated;
        return NativeMethods.Advapi32.GetTokenInformation(
            token, NativeMethods.Advapi32.TokenElevation, (nint)(&elevated), sizeof(int), out _)
            ? elevated != 0
            : null;
    }

    private static string? ReadObjectName(nint handle)
    {
        var buffer = new char[256];
        if (!NativeMethods.User32.GetUserObjectInformationW(
                handle, NativeMethods.User32.UOI_NAME, buffer, (uint)(buffer.Length * sizeof(char)), out _))
        {
            return null;
        }

        var length = Array.IndexOf(buffer, '\0');
        var name = new string(buffer, 0, length < 0 ? buffer.Length : length);
        return name.Length == 0 ? null : name;
    }
}
