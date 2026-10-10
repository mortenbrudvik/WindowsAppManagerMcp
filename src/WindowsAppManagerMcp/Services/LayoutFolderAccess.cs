using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Checks the layouts folder with real file system calls: it lists the folder, and it creates a
/// temporary file that the system deletes when it is closed.
/// </summary>
public class LayoutFolderAccess : ILayoutFolderAccess
{
    public void CheckCanList(string folder)
    {
        using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
        entries.MoveNext();
    }

    public void CheckCanWrite(string folder)
    {
        // Not a *.json file, so even if it outlived a crash it would never be read as a preset.
        var probePath = Path.Combine(folder, $".write-check-{Guid.NewGuid():N}.tmp");
        using var probe = new FileStream(
            probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
    }
}
