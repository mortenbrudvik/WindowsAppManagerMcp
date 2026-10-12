using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// element_action's menu_path (roadmap M3.5): opens a path of menu items such as "File>Save As" one item at a time, each
/// through element_action (<see cref="IElementActionService"/>), so every guard of an action applies to every item.
/// </summary>
/// <remarks>
/// <para>Each item is a MenuItem found by its name (ignoring case, blanks around it and a trailing "..." or "…", and an
/// accelerator after a tab), and must be the only match where it is searched. The first item is searched below the start
/// element (a menu bar, an open menu) or in the window and, by the scope, its open popups. Each further item is searched in
/// the submenu window the previous item opened (element_action's openedPopup, else
/// <see cref="IPopupService.WaitForNewPopupAsync"/>), then below the previous item (WPF and WinForms show a submenu's items
/// under their item); only when no submenu window was seen, also in the window and all its open popups, so an item missing
/// from the submenu never matches the same name in another menu.</para>
/// <para>Every item but the last is expanded (ExpandCollapse); an item before the last without that pattern has no submenu
/// (it is a command), so the path stops with PatternNotSupported and the item is not invoked. The last is invoked (or
/// clicked through its pattern, a checkable item's Toggle, when it has no Invoke). At most
/// <see cref="Services.MenuPaths.MaxItems"/> items; each search and action is bounded by the request's timeout, and the
/// whole path by that timeout per item (at most <see cref="Services.MenuPathService.MaxTotalTimeout"/>; OperationTimedOut
/// with the steps done). A failure stops the path; menus the earlier items opened stay open (the result says so), since
/// closing them would be one more action after a refusal.</para>
/// </remarks>
public interface IMenuPathService
{
    /// <summary>Opens the path; never throws for a refusal (the result's errorCode says why, with the steps done).</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ElementActionResult> RunAsync(MenuPathRequest request, CancellationToken cancellationToken = default);
}
