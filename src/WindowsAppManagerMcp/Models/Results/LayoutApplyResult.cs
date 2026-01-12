namespace WindowsAppManagerMcp.Models.Results;

public record LayoutApplyResult(
    bool Success,
    string PresetName,
    int WindowsArranged = 0,
    int WindowsNotFound = 0,
    int WindowsLaunched = 0,
    List<string>? Errors = null
);
