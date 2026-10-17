using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The window event journal (roadmap M7.2): what happened to the desktop's top-level windows since an earlier call, so one
/// cheap read answers "did a dialog pop up, did focus move, did the title change", popups that are already gone included.
/// It records only while it is read: from the first <see cref="Read"/> until no read came for <c>WindowEventJournal.IdleStop</c>.
/// </summary>
public interface IWindowEventJournal
{
    /// <summary>
    /// The events after <paramref name="sinceSeq"/> (exclusive; null: the latest <paramref name="limit"/>), oldest first, that
    /// <paramref name="include"/> accepts (null: all). Starts recording when it is not.
    /// </summary>
    WindowEventRead Read(long? sinceSeq, int limit, Func<WindowEventEntry, bool>? include = null);
}

/// <summary>What <see cref="IWindowEventJournal.Read"/> found.</summary>
/// <param name="Events">The events, oldest first.</param>
/// <param name="NextSeq">The sequence number to read after next time.</param>
/// <param name="RecordingSince">When the recording started.</param>
/// <param name="StartedNow">True when this read started the recording.</param>
/// <param name="Missed">How many events after sinceSeq are no longer held, or came while the journal was not recording.</param>
/// <param name="More">True when the limit cut the events.</param>
public sealed record WindowEventRead(
    IReadOnlyList<WindowEventEntry> Events,
    long NextSeq,
    DateTimeOffset RecordingSince,
    bool StartedNow,
    long Missed,
    bool More);
