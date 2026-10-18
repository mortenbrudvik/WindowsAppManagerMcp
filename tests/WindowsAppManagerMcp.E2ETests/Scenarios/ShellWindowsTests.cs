using System.Diagnostics;
using WindowsAppManagerMcp.Native;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// File Explorer's folders for workspaces (roadmap M7.5), input tier because it opens a window: the test opens its own temp
/// folder in File Explorer, the real shell window list reports that window with that folder, and the test closes that window
/// only, by the handle the list gave for the test's unique folder. The user's own Explorer windows are only read.
/// </summary>
public class ShellWindowsTests
{
    private const uint WmClose = 0x0010;

    private readonly ITestOutputHelper _output;

    public ShellWindowsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopFact]
    public void TheShellWindowList_ReportsTheFolderAFileExplorerWindowShows()
    {
        // Arrange - a folder no other window can show
        var folder = Path.Combine(Path.GetTempPath(), "WamShellWindows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var wrapper = new NativeShellWindowsWrapper();
        nint opened = 0;
        try
        {
            // Act
            using (Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false }))
            {
            }

            ExplorerFolder? found = null;
            Wait.Until(() => (found = (wrapper.GetExplorerFolders()?.Folders ?? []).FirstOrDefault(f =>
                string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase))) != null, TimeSpan.FromSeconds(15));
            opened = found?.Window ?? 0;
            _output.WriteLine($"Explorer windows: {string.Join("; ", (wrapper.GetExplorerFolders()?.Folders ?? []).Select(f => $"0x{f.Window:X} {(f.Path == null ? "(no folder)" : "a folder")}"))}");

            // Assert
            found.Should().NotBeNull("the shell lists the File Explorer window of the folder the test opened");
            E2ENative.IsWindow(found!.Window).Should().BeTrue();
            E2ENative.GetClassName(found.Window).Should().Be("CabinetWClass");
            (wrapper.GetExplorerFolders()?.Folders ?? []).Where(f => f.Path != null).Should().OnlyContain(f => f.Path!.Length > 2,
                "a folder is a drive or network path");
        }
        finally
        {
            // Only the window of the test's own folder
            if (opened != 0 && E2ENative.IsWindow(opened))
            {
                E2ENative.PostMessageW(opened, WmClose, 0, 0);
                Wait.Until(() => !E2ENative.IsWindow(opened), TimeSpan.FromSeconds(5));
            }

            try
            {
                Directory.Delete(folder);
            }
            catch (IOException)
            {
                // The window still had it open
            }
        }
    }
}
