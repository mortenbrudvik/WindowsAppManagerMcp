using System.Text;
using System.Xml.Linq;

namespace WindowsAppManagerMcp.Tests.Unit;

/// <summary>
/// The server's DPI awareness comes from its application manifest: per-monitor v2, falling back to per-monitor on
/// Windows before 10 1703. Every coordinate the server reads or sends is a physical pixel only while it is per-monitor
/// aware; a silent fall back to DPI virtualization would scale them all on displays above 100%. An API call cannot do
/// this reliably (it fails with ACCESS_DENIED once anything else set the awareness), so there is none.
/// These tests read files in the repository and the built exe's bytes; nothing is run.
/// </summary>
public class AppManifestTests
{
    private static readonly XNamespace WindowsSettings2005 = "http://schemas.microsoft.com/SMI/2005/WindowsSettings";
    private static readonly XNamespace WindowsSettings2016 = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";

    private static string ProjectDirectory => RepositoryFiles.PathOf("src", "WindowsAppManagerMcp");

    [Fact]
    public void TheProject_EmbedsItsAppManifest()
    {
        var project = XDocument.Load(Path.Combine(ProjectDirectory, "WindowsAppManagerMcp.csproj"));

        project.Descendants("ApplicationManifest").Select(e => e.Value.Trim()).Should().Equal(["app.manifest"]);
        File.Exists(Path.Combine(ProjectDirectory, "app.manifest")).Should().BeTrue();
    }

    [Fact]
    public void TheManifest_DeclaresPerMonitorV2_WithPerMonitorFallbacks()
    {
        var manifest = XDocument.Load(Path.Combine(ProjectDirectory, "app.manifest"));

        // Windows 10 1607+ reads dpiAwareness (the first value it knows wins); older versions read dpiAware
        manifest.Descendants(WindowsSettings2016 + "dpiAwareness").Select(e => e.Value.Trim())
            .Should().Equal(["PerMonitorV2, PerMonitor"]);
        manifest.Descendants(WindowsSettings2005 + "dpiAware").Select(e => e.Value.Trim())
            .Should().Equal(["true/pm"]);
    }

    [Fact]
    public void TheManifest_KeepsTheServerAsInvoker()
    {
        // The default manifest's execution level: the server runs with the integrity of whoever starts it
        var manifest = XDocument.Load(Path.Combine(ProjectDirectory, "app.manifest"));

        var level = manifest.Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel");
        level.Attribute("level")?.Value.Should().Be("asInvoker");
        level.Attribute("uiAccess")?.Value.Should().Be("false");
    }

    [Fact]
    public void TheBuiltExe_CarriesTheManifest()
    {
        // The SDK copies the managed assembly's Win32 resources, the manifest among them, into the apphost exe; the
        // single-file bundle is that apphost. The test output has a copy of it; its bytes are read, it is not started.
        var exe = Path.Combine(AppContext.BaseDirectory, "WindowsAppManagerMcp.exe");
        File.Exists(exe).Should().BeTrue("the test project references the server, so its apphost is in the test output");

        var text = Encoding.UTF8.GetString(File.ReadAllBytes(exe));

        text.Should().Contain("<dpiAwareness").And.Contain("PerMonitorV2, PerMonitor");
    }

    [Fact]
    public void TheServer_DoesNotSetItsDpiAwarenessThroughTheApi()
    {
        // With the manifest in force the call always fails with ACCESS_DENIED; the server reports the effective
        // awareness instead (get_server_info, and the log at start)
        var program = File.ReadAllText(Path.Combine(ProjectDirectory, "Program.cs"));

        program.Should().NotContain("SetProcessDpiAwareness").And.NotContain("SetThreadDpiAwarenessContext");
    }
}
