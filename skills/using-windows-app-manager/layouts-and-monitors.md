# Layouts, Monitors and Snap Positions

Read this when you snap, tile or pin windows, move them between monitors, or save and apply layouts.

Monitor coordinates, and naming a monitor (`"primary"`, `"left"`, `"right"`, `"current"` or an `id`), are in [screens-and-ocr.md](screens-and-ocr.md#coordinates); launching an app straight into its place is in [windows-and-apps.md](windows-and-apps.md).

## Snap Positions

The `snap_window` tool supports 14 positions:

| Position | Description |
|----------|-------------|
| `left_half` / `right_half` | Left/Right 50% |
| `top_half` / `bottom_half` | Top/Bottom 50% |
| `top_left_quarter` | Top-left 25% |
| `top_right_quarter` | Top-right 25% |
| `bottom_left_quarter` | Bottom-left 25% |
| `bottom_right_quarter` | Bottom-right 25% |
| `left_third` / `center_third` / `right_third` | Thirds |
| `left_two_thirds` / `right_two_thirds` | Two-thirds |
| `fullscreen` | Full screen |

## Arrange Windows for Development

1. **Find:** `find_windows` for VS Code and Terminal
2. **Act:** `snap_window` VS Code to `left_two_thirds`, Terminal to `right_third`
3. **Verify:** check `bounds` in each result: snapped windows meet edge to edge

## Save and Restore a Workspace

1. Arrange the windows, then `save_layout` with a descriptive name (`MonitorNotFound`: no monitor, or a window's monitor, could be read while the displays change; nothing was saved, so try again)
2. Later, `apply_layout` (keep the default `matchBy`: a saved layout has no title text for `title_only`). Each placement gets its own window; `status` is `applied`, `partial` or `failed`, and `placements` says per placement what happened (`applied`, `notFound`, `notApplicable` or `failed`, with `window` and `errorCode`). `success: false` lists every problem in `errors`; a placement that could not be arranged or minimized, that its app kept moving back, or with `focus` that did not come to the front is an `ArrangeFailed` error, and `focus` lists each focus placement's activation with the fields of a `focus_window` result
3. **Verify:** `get_foreground_window` before typing anything

## Multi-Monitor Setup

1. **Find:** `get_monitors` for the displays and `primaryIndex` (left out when no monitor is primary; no monitors means they could not be read: try again); a monitor's `dpi` and `scaleFactor` are left out when Windows cannot report its DPI
2. **Act:** `move_window_to_monitor` or `snap_window` with `monitor` (`"left"`, `"right"`, `"primary"` or an `id`), or `set_window_bounds` with `unit: "percent"` and `monitor` for any share of a monitor (thirds tile exactly)
3. **Verify:** check `monitorIndex` and `bounds`; monitors left of or above the primary one have negative coordinates

## Keep a Window in View

1. **Act:** `set_window_zorder` with `mode: "topmost"` pins a reference window above the others without taking the focus; `mode: "above"` with `aboveHandle` raises it above one window (also a topmost overlay). A failed `above` that says the window was, or may be, left topmost: check `isTopmost` (`get_window_info`), and unpin it with `mode: "notopmost"`
2. **Clean up:** unpin it with `mode: "notopmost"` when done; `get_server_info`'s `pinnedWindows` lists what this server left pinned

## Tips

- `apply_layout` with `launchMissing: true` starts what each placement whose window is missing names: the program or app `save_layout` recorded for the window (`launch`), or a `launchCommand` added by hand; for a layout an agent wrote (`placements`), only after the user confirms. Leave it false unless the user wants those programs started
- `list_layouts` also lists the files that are not valid presets (`file`, `error`, `errorCode` `InvalidPresetFile`): they cannot be applied, and the layout tools never change them, so ask the user to fix or delete the file. Its name can be a valid preset's too: `delete_layout` of that name deletes the valid preset, not the file
