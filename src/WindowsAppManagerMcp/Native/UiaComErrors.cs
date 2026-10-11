using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Turns the HRESULTs of the UI Automation COM client into the exceptions of the <see cref="IUiaTree"/> seam, so the
/// services handle both engines the same way.
/// </summary>
/// <remarks>
/// The runtime already maps two UI Automation errors to .NET exceptions: UIA_E_TIMEOUT (0x80131505, COR_E_TIMEOUT) is a
/// <see cref="TimeoutException"/>, which is what the seam reports for a provider that does not answer within the
/// transaction timeout, and UIA_E_INVALIDOPERATION (0x80131509) an <see cref="InvalidOperationException"/>.
/// </remarks>
public static class UiaComErrors
{
    /// <summary>UIA_E_ELEMENTNOTENABLED: the element is not enabled.</summary>
    public const int ElementNotEnabled = unchecked((int)0x80040200);

    /// <summary>UIA_E_ELEMENTNOTAVAILABLE: the element is gone.</summary>
    public const int ElementNotAvailable = unchecked((int)0x80040201);

    /// <summary>UIA_E_NOCLICKABLEPOINT.</summary>
    public const int NoClickablePoint = unchecked((int)0x80040202);

    /// <summary>UIA_E_PROXYASSEMBLYNOTLOADED.</summary>
    public const int ProxyAssemblyNotLoaded = unchecked((int)0x80040203);

    /// <summary>UIA_E_NOTSUPPORTED: the provider does not support the call.</summary>
    public const int NotSupported = unchecked((int)0x80040204);

    /// <summary>RPC_E_DISCONNECTED: the provider's process went away during the call.</summary>
    public const int Disconnected = unchecked((int)0x80010108);

    /// <summary>RPC_S_SERVER_UNAVAILABLE: the provider's process is gone.</summary>
    public const int ServerUnavailable = unchecked((int)0x800706BA);

    /// <summary>CO_E_OBJNOTCONNECTED: the provider object is no longer connected.</summary>
    public const int ObjectNotConnected = unchecked((int)0x800401FD);

    /// <summary>ERROR_INVALID_WINDOW_HANDLE as an HRESULT: the element's window was destroyed.</summary>
    public const int InvalidWindowHandle = unchecked((int)0x80070578);

    /// <summary>
    /// The seam's exception for a COM error: <see cref="UiaElementUnavailableException"/> for an element (or its
    /// process or window) that is gone, <see cref="InvalidOperationException"/> for an element that is not enabled,
    /// <see cref="NotSupportedException"/> for a call the provider does not support; null to rethrow it unchanged.
    /// </summary>
    public static Exception? Translate(COMException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.HResult switch
        {
            ElementNotAvailable or Disconnected or ServerUnavailable or ObjectNotConnected or InvalidWindowHandle =>
                new UiaElementUnavailableException($"The element is no longer available ({Describe(exception)}).", exception),
            ElementNotEnabled =>
                new InvalidOperationException($"The element is not enabled ({Describe(exception)}).", exception),
            NotSupported or NoClickablePoint =>
                new NotSupportedException($"The element's provider does not support this ({Describe(exception)}).", exception),
            _ => null
        };
    }

    /// <summary>Runs a call of the COM client and translates its COM errors (<see cref="Translate"/>).</summary>
    public static T Run<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (COMException ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>Runs a call of the COM client that returns nothing and translates its COM errors.</summary>
    public static void Run(Action call)
    {
        try
        {
            call();
        }
        catch (COMException ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    private static string Describe(COMException exception) => $"HRESULT 0x{exception.HResult:X8}";
}
