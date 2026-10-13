using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// element_action (roadmap M3.6): acts on an element named by its ref through UI Automation patterns, falls back to guarded
/// mouse or keyboard input only where the action or the caller asks for it, and reports the element's state before and
/// after.
/// </summary>
/// <remarks>
/// <para>Safety, in order: a pause refuses before anything is read (PausedByUser); the call waits for the input gate
/// (InputBusy) and holds it while it reads and acts, so no input or window call runs in between; the element's window passes
/// the input guard's target checks (<see cref="IInputTargetGuard.CheckTarget"/>: the target policy's deny list, the agent's
/// host unless allowHostTarget, elevation, a modal dialog, a locked session); then the element is resolved, validated
/// (enabled, on screen, the pattern there, not read-only) and acted on. A pattern action that blocks (a Win32 Invoke waits
/// for the modal dialog it opened) runs on a thread of its own (<see cref="IUiaActionRunner"/>), never on the read worker,
/// so the agent can still read the dialog; the gate is released when the timeout passes. Mouse and keyboard paths go
/// through <see cref="IInputSimulationService"/> after the gate is released (it takes the gate itself), so every input
/// check applies to them as to click and type_text.</para>
/// <para>Never throws for a refusal or an application error: they come back as the result's errorCode. Throws
/// <see cref="OperationCanceledException"/> when the request is cancelled, and ToolErrorException (PausedByUser,
/// InputBusy) from the gate.</para>
/// </remarks>
public interface IElementActionService
{
    Task<ElementActionResult> ActAsync(ElementActionRequest request, CancellationToken cancellationToken = default);
}
