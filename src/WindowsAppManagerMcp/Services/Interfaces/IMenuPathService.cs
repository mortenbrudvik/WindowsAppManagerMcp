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
/// under their item), then in the window and all its open popups.</para>
/// <para>Every item but the last is expanded (ExpandCollapse; Invoke when it has no such pattern); the last is invoked (or
/// clicked through its pattern, a checkable item's Toggle, when it has no Invoke). At most
/// <see cref="Services.MenuPaths.MaxItems"/> items; each search and action is bounded by the request's timeout. A failure
/// stops the path; menus the earlier items opened stay open (the result says so), since closing them would be one more
/// action after a refusal.</para>
/// </remarks>
public interface IMenuPathService
{
    /// <summary>Opens the path; never throws for a refusal (the result's errorCode says why, with the steps done).</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ElementActionResult> RunAsync(MenuPathRequest request, CancellationToken cancellationToken = default);
}
