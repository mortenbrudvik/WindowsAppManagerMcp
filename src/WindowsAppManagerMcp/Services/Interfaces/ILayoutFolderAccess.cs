namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Checks that the layouts folder can actually be used, and lists its preset files. Directory.CreateDirectory succeeds for a
/// folder that already exists even when this user cannot list or write it, so LayoutService checks
/// both before it reports the storage as available. Separate from LayoutService so tests can
/// simulate a denied folder without changing permissions on a real one.
/// </summary>
public interface ILayoutFolderAccess
{
    /// <summary>
    /// The *.json files of the folder, as they are now; empty when the folder does not exist. Throws (UnauthorizedAccessException,
    /// IOException, ...) when it cannot be listed, so the caller can keep what it read before and say it may be stale.
    /// </summary>
    IReadOnlyList<FileInfo> ListPresetFiles(string folder);

    /// <summary>
    /// Throws (UnauthorizedAccessException, IOException, ...) when the contents of the existing
    /// folder cannot be listed.
    /// </summary>
    void CheckCanList(string folder);

    /// <summary>
    /// Throws (UnauthorizedAccessException, IOException, ...) when a new file cannot be created in
    /// the existing folder. Leaves nothing behind.
    /// </summary>
    void CheckCanWrite(string folder);
}
