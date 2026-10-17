namespace WindowsAppManagerMcp.Tests.Unit.Models;

/// <summary>
/// PackagedAppId (roadmap M6.5): which launch targets name a packaged app by its AppUserModelID, bare or as
/// shell:AppsFolder\&lt;AUMID&gt;. Pure parsing; nothing is started.
/// </summary>
public class PackagedAppIdTests
{
    [Theory]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe", "App")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "Microsoft.WindowsCalculator_8wekyb3d8bbwe", "App")]
    [InlineData(@"SHELL:appsfolder\MSTeams_8wekyb3d8bbwe!MSTeams", "MSTeams_8wekyb3d8bbwe", "MSTeams")]
    [InlineData("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
        "windows.immersivecontrolpanel_cw5n1h2txyewy", "microsoft.windows.immersivecontrolpanel")]
    [InlineData("remotedesktop.google.com-C05CD007_h2dphjv1brgng!App", "remotedesktop.google.com-C05CD007_h2dphjv1brgng", "App")]
    [InlineData("  CanonicalGroupLimited.UbuntuonWindows_79rhkp1fndgsc!ubuntuonwindows  ",
        "CanonicalGroupLimited.UbuntuonWindows_79rhkp1fndgsc", "ubuntuonwindows")]
    public void TryParse_APackageFamilyNameAndAnAppId_IsAPackagedApp(string value, string family, string appId)
    {
        PackagedAppId.TryParse(value, out var app).Should().BeTrue();

        app!.PackageFamilyName.Should().Be(family);
        app.ApplicationId.Should().Be(appId);
        app.AppUserModelId.Should().Be($"{family}!{appId}");
        app.AppsFolderTarget.Should().Be($@"shell:AppsFolder\{family}!{appId}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notepad")]
    [InlineData("MSEdge")]
    [InlineData(@"shell:AppsFolder\MSEdge")]
    [InlineData(@"shell:AppsFolder\Microsoft.Office.WINWORD.EXE.15")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe")]
    [InlineData("Microsoft.WindowsCalculator!App")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbw!App")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App!Extra")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!1App")]
    [InlineData(@"C:\Apps\Vendor.App_8wekyb3d8bbwe!App")]
    [InlineData(@"shell:AppsFolder\..\Vendor.App_8wekyb3d8bbwe!App")]
    [InlineData("Vendor App_8wekyb3d8bbwe!App")]
    [InlineData("ms-settings:Vendor.App_8wekyb3d8bbwe!App")]
    public void TryParse_AnythingElse_IsNoPackagedApp(string? value)
    {
        PackagedAppId.TryParse(value, out var app).Should().BeFalse();
        app.Should().BeNull();
    }
}
