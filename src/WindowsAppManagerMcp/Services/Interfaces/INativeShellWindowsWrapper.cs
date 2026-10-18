namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The folders the File Explorer windows show (roadmap M7.5), read through the shell's window list (ShellWindows), behind a
/// seam the tests replace with a fake. Only reads.
/// </summary>
public interface INativeShellWindowsWrapper
{
    /// <summary>
    /// Each File Explorer window and the folder it shows: a drive or network path, or null for a place that is not a folder
    /// on disk (This PC, the Recycle Bin, a search), with how many of the shell's windows could not be read (their folders
    /// are unknown). Null when the shell cannot be asked or does not answer within a deadline, so the caller can tell
    /// "no window" from "unknown". Never throws.
    /// </summary>
    ExplorerFolderListing? GetExplorerFolders();
}

/// <summary>A File Explorer window and its folder (null when it is not a folder on disk).</summary>
public sealed record ExplorerFolder(nint Window, string? Path);

/// <summary>
/// The File Explorer windows the shell listed (roadmap M7.5).
/// </summary>
/// <param name="Folders">Each window that was read, with its folder, in the shell's order.</param>
/// <param name="SkippedWindows">
/// How many of the shell's windows could not be read (one that closed while it was read, or that the shell failed): their
/// folders are unknown, so a folder one of them shows looks closed.
/// </param>
public sealed record ExplorerFolderListing(IReadOnlyList<ExplorerFolder> Folders, int SkippedWindows);
