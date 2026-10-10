namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The outcome of a native call that can fail: whether it succeeded and, when it did not, the Win32
/// error it set. The wrapper that makes the call reads the error immediately afterwards and returns it
/// here, because any later native call (in the runtime, a logger or a test guard in between) can
/// overwrite the thread's last error. Services and tools never read the last error themselves
/// (NativeCallGuardTests enforces that).
/// </summary>
/// <param name="Succeeded">True when the call succeeded.</param>
/// <param name="Win32Error">The Win32 error the failed call set (GetLastError), or 0.</param>
public readonly record struct Win32CallResult(bool Succeeded, int Win32Error)
{
    /// <summary>A call that succeeded.</summary>
    public static Win32CallResult Ok { get; } = new(true, 0);

    /// <summary>A call that failed with <paramref name="win32Error"/>.</summary>
    public static Win32CallResult Failure(int win32Error) => new(false, win32Error);
}
