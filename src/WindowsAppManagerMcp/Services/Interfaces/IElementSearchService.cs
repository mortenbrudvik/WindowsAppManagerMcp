using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// find_ui_elements (roadmap M3.4): searches a window's elements by name, automation id, control type and class, with the
/// provider evaluating the criteria (<see cref="Services.ElementSearch"/>, so it is not limited by a walk's depth), and
/// returns each match with a ref (<see cref="IElementRegistry"/>), a click point and its patterns.
/// </summary>
public interface IElementSearchService
{
    /// <summary>
    /// Searches <see cref="ElementQuery.WindowHandle"/> (below <see cref="ElementQuery.WithinRef"/>'s element when set) on a
    /// UI Automation worker thread, under <see cref="ElementQuery.Timeout"/>: a window that does not respond is not read
    /// (NotResponding), and a search that runs out of time returns what it found (truncated, Timeout).
    /// </summary>
    /// <exception cref="System.Text.RegularExpressions.RegexMatchTimeoutException">The name pattern took longer than 100 ms
    /// on an element's name.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="ArgumentException">The query's arguments cannot be searched for, which the tools check before they call:
    /// a name pattern that is not a valid regular expression, or <see cref="ElementQuery.SearchVirtualized"/> without an
    /// automation id or an exact name, or without a window (ElementSearchService.VirtualizedArgumentError says why).</exception>
    Task<FindElementsResult> FindAsync(ElementQuery query, CancellationToken cancellationToken = default);
}
