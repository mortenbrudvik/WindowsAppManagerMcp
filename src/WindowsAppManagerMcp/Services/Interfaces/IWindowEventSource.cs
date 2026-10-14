namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// The window events behind wait_for_window's wake-ups (roadmap M3.F22): a top-level window was created, destroyed, shown,
/// hidden, renamed, minimized or restored, cloaked or uncloaked, or came to the front. A wait subscribes while it sleeps
/// between two polls, and an event ends the sleep early (<see cref="WaitWakeUp"/>); it never decides a wait.
/// </summary>
/// <remarks>
/// The server's source (<c>WindowEventSource</c>) listens with out-of-context WinEvent hooks on a thread of its own, through
/// <see cref="INativeWinEventWrapper"/>, which the tests replace with a fake; no test installs a real hook.
/// </remarks>
public interface IWindowEventSource
{
    /// <summary>
    /// Calls <paramref name="changed"/> on every window event until the subscription is disposed. It is called on the event
    /// thread, so it must only signal (<see cref="WaitWakeUp.Signal"/>). Never blocks: the hooks are installed and removed on
    /// the event thread. A source that cannot listen (its hooks fail) still subscribes; it calls nothing.
    /// </summary>
    IDisposable Subscribe(Action changed);
}
