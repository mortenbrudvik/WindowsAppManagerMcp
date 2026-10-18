namespace WindowsAppManagerMcp.Models;

/// <summary>
/// A *.json file in the layouts folder that is not a valid layout preset (review finding F1): it is not JSON, lacks a name or
/// placements, has a placement without a matcher or position, a position that is no rectangle, or a value of the wrong
/// kind (a state given as a number). It is never applied, and never left out silently either: list_layouts lists it with
/// why, and get_layout, apply_layout, delete_layout and restore_workspace name it when they do not find the preset asked for.
/// </summary>
/// <param name="FileName">The file's name in the layouts folder, such as "work-setup.json".</param>
/// <param name="Error">Why it is not a valid preset, as a sentence.</param>
/// <param name="LastWriteTimeUtc">When the file was last written.</param>
/// <param name="DeclaredName">The preset name the file gives, when its JSON can be read that far; null otherwise.</param>
public sealed record InvalidLayoutFile(string FileName, string Error, DateTime LastWriteTimeUtc, string? DeclaredName = null)
{
    /// <summary>The preset the file stands for: the name it gives, else its file name without ".json".</summary>
    public string Name => DeclaredName ?? Path.GetFileNameWithoutExtension(FileName);
}
