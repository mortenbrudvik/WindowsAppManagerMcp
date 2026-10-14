namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The UI Automation events behind wait_for_element's wake-ups (roadmap M3.F22): a structure change in a window's tree,
/// or a change of a property a wait's condition reads. A wait watches its window while it sleeps between two checks, and
/// an event ends the sleep early (<see cref="WaitWakeUp"/>); it never decides a wait.
/// </summary>
/// <remarks>
/// The server's source (<c>ElementEventSource</c>) adds and removes the handlers through <see cref="IUiaTree"/>, on a thread
/// of its own for each watch, never on the read worker or one of UI Automation's event threads, since a hung provider can
/// block both calls. The tests use <see cref="IUiaTree"/>'s fake; no test registers a real handler.
/// </remarks>
public interface IElementEventSource
{
    /// <summary>
    /// Starts watching <paramref name="window"/> (of process <paramref name="targetProcessId"/>, 0 when unknown) for structure
    /// changes and changes of <paramref name="properties"/> anywhere in its tree, calling <paramref name="changed"/> on each
    /// until the watch is disposed. <paramref name="changed"/> runs on UI Automation's event threads, so it must only signal.
    /// Never blocks, and neither does the watch's Dispose: the handlers are added and removed on the watch's own thread. Null
    /// when the source does not watch that application now (its earlier handler calls have not returned).
    /// </summary>
    IDisposable? Watch(nint window, int targetProcessId, IReadOnlyCollection<UiaWatchedProperty> properties, Action changed);
}
