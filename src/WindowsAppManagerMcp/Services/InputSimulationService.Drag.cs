using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

// drag (roadmap M4.3): the one input call that holds a button down across batches, since a drag needs time between its
// press, its moves and its drop. The hold never outlives the call: the input gate is held throughout, every wait is
// cancelled by the request and by the user's pause, the session releases whatever is still down in the finally block, and a
// drag that must not drop at the to point goes back to the from point first.
public partial class InputSimulationService
{
    private const string NothingDragged = "Nothing was dragged.";

    public async Task<DragResult> DragAsync(DragRequest request, InputTarget target, InputTarget dropTarget, bool releaseHeldModifiers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Invalid() is { } invalid)
        {
            throw new ArgumentException(invalid, nameof(request));
        }

        var modifiers = request.Modifiers.Distinct().ToList();
        var r = request with { Modifiers = modifiers };
        DragResult Result(string? error, InputErrorCode code, bool pressed = false) =>
            new DragResult(code == InputErrorCode.None, r.FromX, r.FromY, r.ToX, r.ToY, error, code)
            {
                Button = ToolEnum<MouseButton>.Name(r.Button),
                Modifiers = ModifierNames(modifiers),
                Pressed = pressed
            };

        if (!IsValidScreenCoordinate(r.FromX, r.FromY) || !IsValidScreenCoordinate(r.ToX, r.ToY))
        {
            return Result($"The {(IsValidScreenCoordinate(r.FromX, r.FromY) ? "to" : "from")} point is outside every monitor. {NothingDragged}",
                InputErrorCode.CoordinatesOutOfBounds);
        }

        using var call = new InputCall(_session, cancellationToken);
        var result = await DragInCallAsync().ConfigureAwait(false);
        return call.TakeoverWarns
            ? result with { Warning = TakeoverMessages.Combine(result.Warning, TakeoverMessages.Warning("the drag ran")) }
            : result;

        async Task<DragResult> DragInCallAsync()
        {
            if (call.IsPaused)
            {
                return Result(PausedMessage(NothingDragged), InputErrorCode.PausedByUser);
            }

            IDisposable? gate;
            try
            {
                gate = await InputGate.TryEnterAsync(_gateTimeout, call.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (call.IsPaused)
            {
                return Result(PausedMessage(NothingDragged), InputErrorCode.PausedByUser);
            }

            if (gate == null)
            {
                return Result(BusyMessage("dragged"), InputErrorCode.InputBusy);
            }

            using (gate)
            {
                InputGuardResult? approved = null, drop = null;
                HeldCheck held = default;
                var pressed = false;
                KeyboardLayout? layout = null;
                try
                {
                    held = CheckHeldInput(releaseHeldModifiers, "dragged");
                    if (!held.Ok)
                    {
                        return held.Apply(Result(held.Error, held.Code));
                    }

                    approved = _guard.CheckMouse(r.FromX, r.FromY, target);
                    if (!approved.Allowed)
                    {
                        return held.Apply(Finish(call.IsPaused
                            ? Result(PausedMessage(NothingDragged), InputErrorCode.PausedByUser)
                            : Result($"The from point: {approved.Error}", approved.ErrorCode), approved, null));
                    }

                    // The drop point is checked as a click's point would be, against the drop target (the target, or the window the
                    // call names for the drop): a drop is input to the window there
                    drop = _guard.CheckMouse(r.ToX, r.ToY, dropTarget);
                    if (!drop.Allowed)
                    {
                        return held.Apply(Finish(call.IsPaused
                            ? Result(PausedMessage(NothingDragged), InputErrorCode.PausedByUser)
                            : Result($"The to point: {drop.Error}", drop.ErrorCode), approved, drop) with { BlockingWindow = drop.BlockingWindow });
                    }

                    if (MouseButtonsDownNow("dragged") is { } buttons)
                    {
                        return held.Apply(Finish(Result(buttons, InputErrorCode.MouseInUse), approved, drop));
                    }

                    switch (_session.SetCursorPos(r.FromX, r.FromY))
                    {
                        case null:
                            return held.Apply(Finish(Result(PausedMessage(NothingDragged), InputErrorCode.PausedByUser), approved, drop));
                        case false:
                            return held.Apply(Finish(Result(CursorPositionFailedMessage(NothingDragged), InputErrorCode.CursorPositionFailed),
                                approved, drop));
                    }

                    // As for a click: a popup the pointer opens there shows up in the re-check
                    await Task.Delay(10, call.Token).ConfigureAwait(false);

                    var recheck = _guard.RecheckMouse(r.FromX, r.FromY, approved);
                    if (!recheck.Allowed)
                    {
                        return held.Apply(Finish(Result($"{NothingDragged} {recheck.Error}", recheck.ErrorCode), recheck, drop));
                    }

                    approved = recheck;
                    switch (PointerIsAt(r.FromX, r.FromY, out var actual))
                    {
                        case PointerCheck.Paused:
                            return held.Apply(Finish(Result(PausedMessage(NothingDragged), InputErrorCode.PausedByUser), approved, drop));
                        case PointerCheck.Away:
                            return held.Apply(Finish(Result($"{NothingDragged} {PointerMovedAway(r.FromX, r.FromY, actual)}", InputErrorCode.CursorNotAtPoint),
                                approved, drop));
                    }

                    // Right before the press: a modifier or button the user pressed meanwhile would change the drag or take it over
                    if (ModifiersDownNow("dragged") is { } downNow)
                    {
                        return held.Apply(Finish(Result($"{NothingDragged} {downNow.Error}", InputErrorCode.ModifiersHeld), approved, drop) with
                        {
                            HeldModifiers = downNow.Names
                        });
                    }

                    if (MouseButtonsDownNow("dragged") is { } buttonsNow)
                    {
                        return held.Apply(Finish(Result($"{NothingDragged} {buttonsNow}", InputErrorCode.MouseInUse), approved, drop));
                    }

                    // Takeover detection (roadmap M7.4) watches from the press to the drop
                    call.WatchForTakeover(_takeover);

                    // The modifiers take the scan codes of the target's layout, as send_keys' keys do
                    layout = modifiers.Count > 0 ? LayoutFor(target) : null;
                    var press = NewBatch(layout).ModifiersDown(modifiers).MoveTo(r.FromX, r.FromY).Press(r.Button).Build();

                    // A right or middle press in a terminal pastes
                    if (r.Button is MouseButton.Secondary or MouseButton.Middle && PasteRefusal(press.AsUnswapped, approved) is { } pasteRefused)
                    {
                        return held.Apply(Finish(Result($"{NothingDragged} {pasteRefused}", InputErrorCode.PasteNotAllowed), approved, drop));
                    }

                    var sent = Send(press, approved);
                    if (sent.PausedByUser)
                    {
                        return held.Apply(Finish(Result(PausedMessage(NothingDragged), InputErrorCode.PausedByUser), approved, drop));
                    }

                    if (!sent.Complete)
                    {
                        // The session has released what went down
                        return held.Apply(Finish(Result(
                            $"SendInput inserted {sent.Inserted} of {sent.Requested} events of the press (the modifier keys, the move to the " +
                            "from point and the button), and what went down was released at once, so nothing was dragged. The input " +
                            "desktop may not be reachable. Check the window before you try again.", InputErrorCode.SendInputFailed), approved, drop));
                    }

                    pressed = true;
                    if (r.HoldBeforeMoveMs > 0)
                    {
                        await Task.Delay(r.HoldBeforeMoveMs, call.Token).ConfigureAwait(false);
                    }

                    var points = DragPath.Points(r.FromX, r.FromY, r.ToX, r.ToY, r.Steps, _systemInfo?.GetDragThreshold());
                    for (var i = 0; i < points.Count; i++)
                    {
                        call.Token.ThrowIfCancellationRequested();
                        var move = Send(NewBatch().MoveTo(points[i].X, points[i].Y).Build(), approved);
                        if (move.PausedByUser)
                        {
                            throw new OperationCanceledException(call.Token);
                        }

                        if (!move.Complete)
                        {
                            // The session released the button where the pointer was
                            return held.Apply(Finish(Result(
                                $"SendInput did not insert move {i + 1} of {points.Count}, so the button was released where the pointer " +
                                $"was{PointerNow()}, which may have dropped there. Check the window before you try again.",
                                InputErrorCode.SendInputFailed, pressed: true), approved, drop));
                        }

                        if (r.StepDelayMs > 0)
                        {
                            await Task.Delay(r.StepDelayMs, call.Token).ConfigureAwait(false);
                        }
                    }

                    // The drop must land where the guard looks: a pointer kept elsewhere would drop there
                    if (PointerAwayAfterBatch(r.ToX, r.ToY) is { } at)
                    {
                        return held.Apply(Finish(Result(
                            $"The pointer did not get to the to point ({r.ToX}, {r.ToY}); it is at ({at.X}, {at.Y}) (confined to a window, or " +
                            $"moved by the user or the application). {CancelDrag(r, layout)}", InputErrorCode.CursorClamped, pressed: true), approved, drop));
                    }

                    // Right before the release: the window at the drop point may have changed while the pointer moved
                    var dropNow = _guard.RecheckDrop(r.ToX, r.ToY, drop);
                    if (!dropNow.Allowed)
                    {
                        return held.Apply(Finish(Result($"Right before the drop the to point was checked again, and the drop was not sent: {dropNow.Error} {CancelDrag(r, layout)}", dropNow.ErrorCode,
                            pressed: true), approved, dropNow) with { BlockingWindow = dropNow.BlockingWindow });
                    }

                    drop = dropNow;
                    var release = Send(NewBatch(layout).MoveTo(r.ToX, r.ToY).Release(r.Button).ModifiersUp(modifiers).Build(), drop);
                    if (release.PausedByUser)
                    {
                        throw new OperationCanceledException(call.Token);
                    }

                    if (!release.Complete)
                    {
                        return held.Apply(Finish(Result(
                            $"SendInput inserted {release.Inserted} of {release.Requested} events of the drop (the move to the to point, the " +
                            "button's release and the modifier keys' release); the rest was released at once, where the pointer was" +
                            $"{PointerNow()}. Check the window before you try again.", InputErrorCode.SendInputFailed, pressed: true), approved, drop));
                    }

                    if (PointerAwayAfterBatch(r.ToX, r.ToY) is { } after)
                    {
                        return held.Apply(Finish(Result(PointerClamped("The drop", r.ToX, r.ToY, after), InputErrorCode.CursorClamped, pressed: true),
                            approved, drop));
                    }

                    return held.Apply(Finish(Result(null, InputErrorCode.None, pressed: true), approved, drop));
                }
                catch (OperationCanceledException) when (call.IsPaused)
                {
                    // The pause released what the drag held, where the pointer was: nothing more may be sent
                    var paused = Result(pressed
                        ? PausedMessage($"The drag stopped after the press: the button was released where the pointer was{PointerNow()}, " +
                                        "which may have dropped there.")
                        : PausedMessage(NothingDragged), InputErrorCode.PausedByUser, pressed);
                    return held.Apply(approved == null ? paused : Finish(paused, approved, drop));
                }
                catch (OperationCanceledException) when (call.Interrupted)
                {
                    // The user took the mouse or keyboard. Input is not paused, so the drag ends as a refused drop does: back at
                    // the from point, where a drop usually changes nothing. Releasing where the user moved the pointer would drop
                    // there unchecked (a file into whatever folder is under it); the one move back is the lesser intrusion
                    var stopped = Result(TakeoverMessages.Interrupted(pressed
                        ? $"The drag stopped after the press. {CancelDrag(r, layout)}"
                        : NothingDragged), InputErrorCode.UserInterrupted, pressed);
                    return held.Apply(approved == null ? stopped : Finish(stopped, approved, drop));
                }
                catch (OperationCanceledException) when (pressed)
                {
                    // The request was cancelled mid-drag: no drop at a point on the way
                    CancelDrag(r, layout);
                    throw;
                }
                finally
                {
                    _session.ReleaseHeldInput("the end of the input call");
                }
            }
        }
    }

    /// <summary>
    /// Ends a drag that must not drop at the to point: moves the pointer back to the from point and releases the button and the
    /// modifiers there, in one batch, where a drop usually changes nothing (a dragged item returns to its place, a slider to
    /// its value). Returns what happened, for the message. When the batch does not go in (the user paused input, which
    /// released them at once, or SendInput failed, after which the session releases what it can), they were released where
    /// the pointer was.
    /// </summary>
    private string CancelDrag(DragRequest r, KeyboardLayout? layout)
    {
        var sent = _session.Send(NewBatch(layout).MoveTo(r.FromX, r.FromY).Release(r.Button).ModifiersUp(r.Modifiers).Build().Events);
        return sent.Complete
            ? $"The drag was cancelled: the pointer went back to the from point ({r.FromX}, {r.FromY}) and the button was released there, " +
              "where a drop usually changes nothing. Check the window before you try again."
            : $"The drag was cancelled, and the button was released where the pointer was{PointerNow()}, which may have dropped there. " +
              "Check the window before you try again.";
    }

    /// <summary>", at (x, y)" with the pointer's position now, or nothing when it cannot be read.</summary>
    private string PointerNow() => _inputWrapper.GetCursorPos() is { } at ? $", at ({at.X}, {at.Y})" : string.Empty;

    /// <summary>Adds the windows at both points, what is in front now and where the pointer is.</summary>
    private DragResult Finish(DragResult result, InputGuardResult from, InputGuardResult? to)
    {
        var cursor = _inputWrapper.GetCursorPos();
        return result with
        {
            ReceivingWindow = from.ReceivingWindow,
            DropWindow = to?.ReceivingWindow,
            BlockingWindow = from.BlockingWindow,
            ForegroundAfter = _guard.DescribeForeground(),
            ActualX = cursor?.X,
            ActualY = cursor?.Y
        };
    }
}
