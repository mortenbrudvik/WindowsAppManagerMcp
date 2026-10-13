using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// read_element_text and read_table (roadmap M3.9): an element's text a page at a time, and a table's headers and cells, read
/// through UI Automation patterns instead of a tree dump or a screenshot. Both only read.
/// </summary>
/// <remarks>
/// Each read runs in one call on a UI Automation worker thread (<see cref="IUiaWorker"/>) under the request's timeout: the
/// hung-window check of the element's window (NotResponding), the warm-up of a window with web content (roadmap M3.3), the
/// ref's resolution (<see cref="IElementRegistry.Resolve"/>: ElementStale, AmbiguousElement), then the read. A password
/// field's text is never read.
/// </remarks>
public interface IElementReadService
{
    /// <summary>
    /// A page of an element's text: the Text pattern's document (paged by the provider), else the Value pattern's value, else
    /// the name. With <see cref="ElementTextRequest.Query"/>, find_ui_elements' search runs first and must find exactly one
    /// element (ElementNotFound; AmbiguousElement with candidates).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ElementTextResult> ReadTextAsync(ElementTextRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// A page of a table's rows: through the Grid pattern (headers through the Table pattern or the header row), else through
    /// the element's row children (PatternNotSupported when it has neither). Rows read before the timeout are returned.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ElementTableResult> ReadTableAsync(ElementTableRequest request, CancellationToken cancellationToken = default);
}
