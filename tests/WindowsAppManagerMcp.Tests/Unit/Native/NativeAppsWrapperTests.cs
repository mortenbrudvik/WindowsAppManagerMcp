namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// The activation options <see cref="NativeAppsWrapper"/> passes to IApplicationActivationManager. Starting a packaged app
/// needs a desktop, so the activation itself is checked by PackagedAppLaunchTests on a real desktop.
/// </summary>
public class NativeAppsWrapperTests
{
    [Fact]
    public void Activation_AsksForNoErrorUi_NotDesignMode()
    {
        // ACTIVATEOPTIONS (shobjidl_core.h): AO_DESIGNMODE = 0x1, AO_NOERRORUI = 0x2. With design mode Calculator exited at
        // once and the call returned 0x80040904, so every packaged app launch failed on a real desktop
        NativeAppsWrapper.NoErrorUi.Should().Be(0x2);
    }
}
