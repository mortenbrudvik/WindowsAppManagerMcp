namespace WindowsAppManagerMcp.Models;

/// <summary>
/// How far a long operation has come, as a service reports it: a UI Automation walk, a batch launch or a layout. The
/// tools pass it on to the MCP client as progress notifications (<see cref="Infrastructure.ToolProgress"/>), so the
/// services do not depend on the MCP SDK.
/// </summary>
/// <param name="Completed">The items done so far. It only grows during one operation.</param>
/// <param name="Total">The number of items, when it is known in advance; null for a walk of a tree of unknown size.</param>
/// <param name="Message">What was just done, for a person watching the progress.</param>
public readonly record struct OperationProgress(int Completed, int? Total, string Message);
