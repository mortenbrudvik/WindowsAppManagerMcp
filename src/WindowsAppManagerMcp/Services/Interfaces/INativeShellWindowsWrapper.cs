namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The folders the File Explorer windows show (roadmap M7.5), read through the shell's window list (ShellWindows), behind a
/// seam the tests replace with a fake. Only reads.
/// </summary>
public interface INativeShellWindowsWrapper
{
    /// <summary>
    /// Each File Explorer window and the folder it shows: a drive or network path, or null for a place that is not a folder
    /// on disk (This PC, the Recycle Bin, a search). Null when the shell cannot be asked or does not answer within a
    /// deadline, so the caller can tell "no window" from "unknown". Never throws.
    /// </summary>
    IReadOnlyList<ExplorerFolder>? GetExplorerFolders();
}

/// <summary>A File Explorer window and its folder (null when it is not a folder on disk).</summary>
public sealed record ExplorerFolder(nint Window, string? Path);
