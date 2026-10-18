using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// Which failures of UIAutomationWrapper's close are expected (review finding F9): a window that is gone, is no window, or
/// cannot be closed through UI Automation returns false quietly; anything else (a provider that failed, a bug) reaches
/// WindowCloseVerifier, which logs it as a warning, instead of being swallowed with no trace. Pure: no UI Automation call is
/// made, so these tests need no window and no real client.
/// </summary>
public class UIAutomationWrapperTests
{
    public static TheoryData<string, Exception> ExpectedFailures() => new()
    {
        { "the element is gone", new ElementNotAvailableException() },
        { "the handle is no window", new ArgumentException("hwnd") },
        { "the window cannot be closed now", new InvalidOperationException() },
        { "the window is not enabled", new ElementNotEnabledException() },
        { "the provider's process went away", new COMException("Disconnected.", UiaComErrors.Disconnected) },
        { "the provider's process is gone", new COMException("Server unavailable.", UiaComErrors.ServerUnavailable) },
        { "the element is gone (HRESULT)", new COMException("Not available.", UiaComErrors.ElementNotAvailable) },
        { "the provider does not support it", new COMException("Not supported.", UiaComErrors.NotSupported) },
        { "the provider did not answer in time", new TimeoutException() },
    };

    [Theory]
    [MemberData(nameof(ExpectedFailures))]
    public void AnExpectedFailure_IsHandledByTheWrapper(string why, Exception failure)
    {
        UIAutomationWrapper.IsExpectedCloseFailure(failure).Should().BeTrue(why);
    }

    public static TheoryData<string, Exception> UnexpectedFailures() => new()
    {
        { "the provider failed (RPC_E_SERVERFAULT)", new COMException("The server threw an exception.", unchecked((int)0x80010105)) },
        { "the provider failed (E_FAIL)", new COMException("Unspecified error.", unchecked((int)0x80004005)) },
        { "a bug", new NullReferenceException() },
    };

    [Theory]
    [MemberData(nameof(UnexpectedFailures))]
    public void AnUnexpectedFailure_ReachesTheCaller_ToBeLogged(string why, Exception failure)
    {
        UIAutomationWrapper.IsExpectedCloseFailure(failure).Should().BeFalse(why);
    }
}
