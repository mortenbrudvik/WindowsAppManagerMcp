# Long Lists

Read this when the item you need is far down a long list (Explorer, Outlook, Settings, long WPF and WinUI lists) and missing from the tree.

To read a list's or table's rows, use `read_table` (see [elements.md](elements.md#read-text-and-tables)).

## In the Loop

- **Long lists (virtualized):** Explorer, Outlook, Settings and long WPF/WinUI lists only create the rows in view, so a row scrolled out of view is missing from the tree. Pass `searchVirtualized: true` to `find_ui_elements` with the row's exact `name` (as the list shows it; the app usually compares case-sensitively, and only lower, upper and capitalized forms are tried after the exact text) or `automationId`: it also asks the lists for rows they have not created, and returns them with `virtualized: true` and a ref but no `clickPoint`. It only reads. Then `element_action` `realize` with that ref creates the row and scrolls it into view; act on it after that. Or in one call: `element_action` `realize` with the `window` and the row's `name`. For a list that cannot be asked (a web list, or a partial name), add `maxScrolls` (for example 30, with a larger `timeoutMs`): it scrolls the window's one list page by page until the row appears.

## Reach an Item Far Down a List

1. **Find:** `find_ui_elements` with the item's exact `name` (or `automationId`) and `searchVirtualized: true`: an item the list has not created comes back with `virtualized: true` and a ref
2. **Act:** `element_action` `realize` with that ref creates it and scrolls it into view; then act on it (`select`, `invoke`)
3. A web or custom list: `element_action` `realize` with the `window`, the item's `name` and `maxScrolls` (and a larger `timeoutMs`); or `scroll` with `windowHandle`, `deltaY` and `x`, `y` inside the list, reading the list again after each turn (when nothing changed, it is at its end)
