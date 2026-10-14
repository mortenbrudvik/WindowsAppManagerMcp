# Windows App Manager MCP Server - Backlog

## Status Legend
- ✅ **Complete** - Task is done
- 🔄 **In Progress** - Currently being worked on
- ⏳ **Pending** - Not yet started
- ❌ **Blocked** - Waiting on dependency

---

## Phase 1: Foundation (Core Infrastructure)

| # | Task | Status | Notes |
|---|------|--------|-------|
| 1.1 | Create solution and project structure | ✅ Complete | `WindowsAppManagerMcp.sln` created |
| 1.2 | Add NuGet packages (MCP SDK, Hosting, DI) | ✅ Complete | ModelContextProtocol 0.5.0-preview.1; 2.2.0 since M2.13 |
| 1.3 | Create folder structure | ✅ Complete | Models, Native, Services, Tools |
| 1.4 | Create base data models | ✅ Complete | WindowInfo, MonitorInfo, LayoutPreset, etc. |
| 1.5 | Implement P/Invoke native methods | ✅ Complete | user32.dll, kernel32.dll |
| 1.6 | Set up MCP server with STDIO transport | ✅ Complete | Program.cs entry point |

---

## Phase 2: Window Discovery

| # | Task | Status | Notes |
|---|------|--------|-------|
| 2.1 | Create `IWindowService` interface | ✅ Complete | |
| 2.2 | Implement `WindowService` | ✅ Complete | EnumWindows, GetWindowText |
| 2.3 | Create `IMonitorService` interface | ✅ Complete | |
| 2.4 | Implement `MonitorService` | ✅ Complete | EnumDisplayMonitors |
| 2.5 | Create `find_windows` tool | ✅ Complete | Search by title, process, PID |
| 2.6 | Create `get_all_windows` tool | ✅ Complete | List all visible windows |
| 2.7 | Create `get_foreground_window` tool | ✅ Complete | Get focused window |
| 2.8 | Create `get_monitors` tool | ✅ Complete | List all displays |
| 2.9 | Create `get_primary_monitor` tool | ✅ Complete | Get main display |

---

## Phase 3: Window Control

| # | Task | Status | Notes |
|---|------|--------|-------|
| 3.1 | Add window manipulation to `WindowService` | ✅ Complete | SetWindowPos, ShowWindow |
| 3.2 | Create `move_window` tool | ✅ Complete | Move to x,y position |
| 3.3 | Create `resize_window` tool | ✅ Complete | Change dimensions |
| 3.4 | Create `set_window_bounds` tool | ✅ Complete | Move + resize combined |
| 3.5 | Create `set_window_state` tool | ✅ Complete | Min/max/restore |
| 3.6 | Create `focus_window` tool | ✅ Complete | Bring to foreground |
| 3.7 | Create `snap_window` tool | ✅ Complete | Snap to halves/quarters/thirds |
| 3.8 | Create `move_window_to_monitor` tool | ✅ Complete | Multi-monitor support |

---

## Phase 4: Application Launch

| # | Task | Status | Notes |
|---|------|--------|-------|
| 4.1 | Create `IProcessService` interface | ✅ Complete | |
| 4.2 | Implement `ProcessService` | ✅ Complete | Process.Start, enumeration |
| 4.3 | Create `launch_application` tool | ✅ Complete | Launch by path/name |
| 4.4 | Create `list_processes` tool | ✅ Complete | List running processes |

---

## Phase 5: Layout Presets

| # | Task | Status | Notes |
|---|------|--------|-------|
| 5.1 | Create `ILayoutService` interface | ✅ Complete | |
| 5.2 | Implement `LayoutService` | ✅ Complete | JSON persistence |
| 5.3 | Create `save_layout` tool | ✅ Complete | Capture current arrangement |
| 5.4 | Create `apply_layout` tool | ✅ Complete | Restore saved preset |
| 5.5 | Create `list_layouts` tool | ✅ Complete | List all presets |
| 5.6 | Create `get_layout` tool | ✅ Complete | Get preset details |
| 5.7 | Create `delete_layout` tool | ✅ Complete | Remove a preset |

---

## Phase 6: Polish & Testing

| # | Task | Status | Notes |
|---|------|--------|-------|
| 6.1 | Add comprehensive error handling | ✅ Complete | Validation in tools |
| 6.2 | Implement DPI awareness | ✅ Complete | Per-monitor DPI via shcore.dll |
| 6.3 | Add logging | ✅ Complete | Logging to stderr |
| 6.4 | Create appsettings.json | ⏳ Pending | Optional configuration |
| 6.5 | Test with Claude Desktop | ✅ Complete | Self-contained exe via publish directory |
| 6.6 | Create example layout presets | ✅ Complete | work-setup, dual-monitor, focus-mode |

---

## Files Created

### ✅ Complete - Models
- `src/WindowsAppManagerMcp/Models/WindowRect.cs`
- `src/WindowsAppManagerMcp/Models/WindowState.cs`
- `src/WindowsAppManagerMcp/Models/WindowInfo.cs`
- `src/WindowsAppManagerMcp/Models/MonitorRect.cs`
- `src/WindowsAppManagerMcp/Models/MonitorInfo.cs`
- `src/WindowsAppManagerMcp/Models/SnapPosition.cs`
- `src/WindowsAppManagerMcp/Models/WindowMatcher.cs`
- `src/WindowsAppManagerMcp/Models/RelativePosition.cs`
- `src/WindowsAppManagerMcp/Models/WindowPlacement.cs`
- `src/WindowsAppManagerMcp/Models/LayoutPreset.cs`
- `src/WindowsAppManagerMcp/Models/ErrorCodes.cs`
- `src/WindowsAppManagerMcp/Models/ScreenshotModels.cs`
- `src/WindowsAppManagerMcp/Models/InputModels.cs`
- `src/WindowsAppManagerMcp/Models/Results/LaunchResult.cs`
- `src/WindowsAppManagerMcp/Models/Results/LayoutApplyResult.cs`
- `src/WindowsAppManagerMcp/Models/Results/KillResult.cs`

### ✅ Complete - Native Interop
- `src/WindowsAppManagerMcp/Native/NativeMethods.User32.cs`
- `src/WindowsAppManagerMcp/Native/NativeMethods.Kernel32.cs`
- `src/WindowsAppManagerMcp/Native/NativeMethods.Shcore.cs`
- `src/WindowsAppManagerMcp/Native/NativeMethods.Gdi32.cs`
- `src/WindowsAppManagerMcp/Native/NativeMethods.Input.cs`
- `src/WindowsAppManagerMcp/Native/NativeStructs.cs`
- `src/WindowsAppManagerMcp/Native/NativeEnums.cs`
- `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs`

### ✅ Complete - Services
- `src/WindowsAppManagerMcp/Services/Interfaces/IWindowService.cs`
- `src/WindowsAppManagerMcp/Services/Interfaces/IMonitorService.cs`
- `src/WindowsAppManagerMcp/Services/Interfaces/IProcessService.cs`
- `src/WindowsAppManagerMcp/Services/Interfaces/ILayoutService.cs`
- `src/WindowsAppManagerMcp/Services/Interfaces/IInputValidationService.cs`
- `src/WindowsAppManagerMcp/Services/Interfaces/IScreenCaptureWrapper.cs`
- `src/WindowsAppManagerMcp/Services/Interfaces/IScreenshotService.cs`
- `src/WindowsAppManagerMcp/Services/Interfaces/IInputSimulationService.cs`
- `src/WindowsAppManagerMcp/Services/WindowService.cs`
- `src/WindowsAppManagerMcp/Services/MonitorService.cs`
- `src/WindowsAppManagerMcp/Services/ProcessService.cs`
- `src/WindowsAppManagerMcp/Services/LayoutService.cs`
- `src/WindowsAppManagerMcp/Services/InputValidationService.cs`
- `src/WindowsAppManagerMcp/Services/ScreenshotService.cs`
- `src/WindowsAppManagerMcp/Services/InputSimulationService.cs`
- `src/WindowsAppManagerMcp/Services/Interfaces/INativeWindowWrapper.cs`
- `src/WindowsAppManagerMcp/Native/NativeWindowWrapper.cs`

### ✅ Complete - MCP Tools
- `src/WindowsAppManagerMcp/Tools/WindowFinderTools.cs`
- `src/WindowsAppManagerMcp/Tools/WindowManagerTools.cs`
- `src/WindowsAppManagerMcp/Tools/MonitorInfoTools.cs`
- `src/WindowsAppManagerMcp/Tools/AppLauncherTools.cs`
- `src/WindowsAppManagerMcp/Tools/LayoutPresetTools.cs`
- `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs`
- `src/WindowsAppManagerMcp/Tools/InputSimulationTools.cs`

### ✅ Complete - Configuration & Docs
- `WindowsAppManagerMcp.sln`
- `src/WindowsAppManagerMcp/WindowsAppManagerMcp.csproj`
- `src/WindowsAppManagerMcp/Program.cs`
- `PLANNING.md`
- `BACKLOG.md`
- `config/layouts/work-setup.json`
- `config/layouts/dual-monitor.json`
- `config/layouts/focus-mode.json`

### ✅ Complete - Claude Code Skills
- `.claude/commands/commit.md` - Versioned release workflow with auto-build

---

## Progress Summary

| Phase | Progress |
|-------|----------|
| Phase 1: Foundation | ██████████ 100% |
| Phase 2: Window Discovery | ██████████ 100% |
| Phase 3: Window Control | ██████████ 100% |
| Phase 4: Application Launch | ██████████ 100% |
| Phase 5: Layout Presets | ██████████ 100% |
| Phase 6: Polish & Testing | █████████░ 90% |

**Overall Progress: ~97%**

---

## MCP Tools Summary (64 Tools)

| Tool Name | Category | Description |
|-----------|----------|-------------|
| `find_windows` | Window Discovery | Find windows by title, process, PID, handle |
| `get_all_windows` | Window Discovery | List all visible windows |
| `get_foreground_window` | Window Discovery | Get currently focused window |
| `get_owned_windows` | Window Discovery | Dialogs and popups a window owns, and which one blocks it |
| `get_window_info` | Window Discovery | One window by handle or selector, at compact, standard or full detail |
| `wait_for_window` | Window Discovery | Wait on the server until a window exists, is gone, is in front, is on screen, has a new title or is ready for input |
| `move_window` | Window Control | Move window to x,y position |
| `resize_window` | Window Control | Resize window dimensions |
| `set_window_bounds` | Window Control | Move and resize in one operation |
| `set_window_state` | Window Control | Minimize, maximize, restore |
| `focus_window` | Window Control | Bring window to foreground, verified, and report what is in front |
| `close_window` | Window Control | Close window gracefully (WM_CLOSE) and verify it closed; refuses shell and agent-host windows |
| `snap_window` | Window Control | Snap to halves/quarters/thirds |
| `move_window_to_monitor` | Window Control | Move to specific monitor |
| `set_windows_bounds_batch` | Batch Operations | Move/resize multiple windows |
| `snap_windows_batch` | Batch Operations | Snap multiple windows |
| `set_windows_state_batch` | Batch Operations | Set state of multiple windows |
| `close_windows_batch` | Batch Operations | Close multiple windows |
| `launch_applications_batch` | Batch Operations | Launch multiple applications, each optionally placed |
| `get_monitors` | Monitor Info | List all displays with bounds |
| `get_primary_monitor` | Monitor Info | Get primary display details |
| `list_screens` | Screenshot | List monitors for capture |
| `take_screenshot` | Screenshot | Capture a monitor (PNG/JPEG) |
| `capture_region` | Screenshot | Capture screen region |
| `capture_window` | Screenshot | Capture a window, or crop it to one element by ref |
| `capture_with_elements` | Screenshot | Capture a window with Set-of-Marks: element refs drawn on the image, and a legend |
| `get_ui_elements` | UI Automation | Get the UI element tree with bounding boxes and refs |
| `get_focused_element` | UI Automation | Get the element with keyboard focus, its window and process |
| `get_element_at_point` | UI Automation | Get the element at a screen point, its ancestors and window |
| `find_ui_elements` | UI Automation | Find elements by name, automation id, control type or class (also in popups and virtualized lists), with refs and click points |
| `get_active_popups` | UI Automation | List an app's open menus, dropdowns, flyouts and dialogs, with refs |
| `read_element_text` | UI Automation | Read an element's text a page at a time (never a password field's) |
| `read_table` | UI Automation | Read a grid's or list's headers and cells a page of rows at a time, without scrolling |
| `wait_for_element` | UI Automation | Wait on the server until an element exists, is gone, is enabled, visible or focused, or has an expected name, value or toggle state |
| `get_desktop_state` | UI Automation | One-call observation: the window in front, window rows, the focus, popups and element lines with refs, an optional capture |
| `element_action` | UI Actions | Act on an element by ref or criteria through UI Automation patterns (invoke, toggle, set value, select, expand, scroll, realize, menu paths), with its state before and after |
| `respond_to_dialog` | UI Actions | Read a dialog's text and buttons, and press one by its standard name in any language |
| `file_dialog` | UI Actions | Complete an Open or Save As dialog with a full path |
| `get_browser_url` | Browser Reading | Get the URL of a browser window (UI Automation or CDP) |
| `get_browser_content` | Browser Reading | Get the text (or HTML via CDP) of a browser page |
| `get_active_browser_url` | Browser Reading | Get the URL of the foreground browser window |
| `launch_browser_with_debug` | Browser Reading | Launch Chrome/Edge/Brave with a CDP debug port and a temporary profile |
| `click` | Input Simulation | Left click at screen coordinates (or an image point with its captureId), guarded, on the window named by windowHandle |
| `right_click` | Input Simulation | Right click at screen coordinates |
| `double_click` | Input Simulation | Double left click at screen coordinates |
| `mouse_move` | Input Simulation | Move cursor without clicking |
| `scroll` | Input Simulation | Turn the mouse wheel over the window named by windowHandle |
| `type_text` | Input Simulation | Type text using Unicode characters into the window named by windowHandle |
| `send_keys` | Input Simulation | Send key combinations (Ctrl+C, Enter, etc.) to the window named by windowHandle |
| `get_control_status` | Input Control | Whether the user paused input, the pause hotkey's state and held keys |
| `resume_input` | Input Control | Resume input after a pause, only when the user confirms |
| `release_all_input` | Input Control | Release stuck keys and mouse buttons |
| `launch_application` | App Launch | Launch app by path/name, optionally placed (placement) or reusing an open window (reuseExisting) |
| `list_processes` | App Launch | List running processes |
| `kill_process` | App Launch | Terminate process by PID (with safeguards) |
| `close_application` | App Launch | Close an application's Alt+Tab windows gracefully and report prompts and what remains |
| `get_clipboard` | Clipboard | Read what the agent copied (text, HTML, files); the user's clipboard only with their consent |
| `set_clipboard` | Clipboard | Put text on the clipboard, kept out of clipboard history |
| `list_layouts` | Layout Presets | List saved presets |
| `get_layout` | Layout Presets | Get preset details |
| `save_layout` | Layout Presets | Save current arrangement |
| `apply_layout` | Layout Presets | Apply saved preset |
| `delete_layout` | Layout Presets | Delete a preset |
| `get_server_info` | Server Info | Build, protocol, integrity, DPI awareness, desktop state, host, pause, safety settings, paths and toolsets, with warnings |

---

## Phase 7: Enhancements (from Code Review)

*Items identified from external code review feedback*

| # | Task | Status | Priority | Notes |
|---|------|--------|----------|-------|
| 7.1 | Add input validation for `launch_application` | ✅ Complete | High | InputValidationService: path traversal, shell metacharacters, protocol validation |
| 7.2 | Add `close_window` tool | ✅ Complete | Medium | Send WM_CLOSE via PostMessage for graceful close |
| 7.3 | Add version field to `LayoutPreset` | ✅ Complete | Low | Added `Version` property with default 1 for schema evolution |
| 7.4 | Standardize error codes as enum | ✅ Complete | Medium | WindowErrorCode, LaunchErrorCode, LayoutErrorCode enums; ErrorCode field in all results |
| 7.5 | Write unit tests for services | ✅ Complete | High | 325 tests. See [BACKLOG-TECHNICAL.md](BACKLOG-TECHNICAL.md) |
| 7.6 | Add `kill_process` tool | ✅ Complete | Low | Terminate process by PID with safeguards: confirmation required, protected process list, graceful close |

---

## Test Files Created

### ✅ Complete - Test Infrastructure
- `tests/WindowsAppManagerMcp.Tests/WindowsAppManagerMcp.Tests.csproj`
- `tests/WindowsAppManagerMcp.Tests/GlobalUsings.cs`
- `tests/WindowsAppManagerMcp.Tests/Fixtures/TestDataFactory.cs`

### ✅ Complete - Unit Tests
- `tests/WindowsAppManagerMcp.Tests/Unit/Services/LayoutServiceTests.cs` (23 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Services/WindowServiceTests.cs` (39 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Services/MonitorServiceTests.cs` (20 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Services/ProcessServiceTests.cs` (11 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Services/InputValidationServiceTests.cs` (57 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Models/ModelTests.cs` (29 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Tools/WindowFinderToolsTests.cs` (14 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Tools/WindowManagerToolsTests.cs` (26 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Tools/MonitorInfoToolsTests.cs` (10 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Tools/AppLauncherToolsTests.cs` (14 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Tools/LayoutPresetToolsTests.cs` (23 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Services/InputSimulationServiceTests.cs` (17 tests)
- `tests/WindowsAppManagerMcp.Tests/Unit/Tools/InputSimulationToolsTests.cs` (24 tests)

### ✅ Complete - Integration Tests
- `tests/WindowsAppManagerMcp.Tests/Integration/LayoutServiceIntegrationTests.cs` (14 tests)
- `tests/WindowsAppManagerMcp.Tests/Integration/FilePersistenceTests.cs` (10 tests)
- `tests/WindowsAppManagerMcp.Tests/Integration/DependencyInjectionTests.cs` (13 tests)

---

## Progress Summary

| Phase | Progress |
|-------|----------|
| Phase 1: Foundation | ██████████ 100% |
| Phase 2: Window Discovery | ██████████ 100% |
| Phase 3: Window Control | ██████████ 100% |
| Phase 4: Application Launch | ██████████ 100% |
| Phase 5: Layout Presets | ██████████ 100% |
| Phase 6: Polish & Testing | █████████░ 90% |
| Phase 7: Enhancements | ██████████ 100% |
| Phase 8: Claude Code Plugin | ███████░░░ 70% |
| Phase 9: Development Tooling | ██████████ 100% |
| Phase 10: Screenshot Capture | ██████████ 100% |
| Phase 11: Enhanced Visual Tools | ░░░░░░░░░░ 0% |
| Phase 12: Advanced Automation | ██████████ 100% |
| Phase 13: Automation Roadmap | ███████░░░ 65% (M0-M3 done: 51 of 78 items) |

**Core Implementation: ~98%**
**With Screenshot: 100%**
**With Plugin: ~70% (testing pending)**
**Future Enhancements: Planned**

---

## Next Steps

1. ✅ **Claude Desktop Integration Complete** - Configuration in `claude_desktop_config.json`:
   ```json
   {
     "mcpServers": {
       "windows-app-manager": {
         "command": "C:\\code\\projects\\WindowsAppManagerMcp\\publish\\WindowsAppManagerMcp.exe"
       }
     }
   }
   ```
   Build command: `dotnet publish src/WindowsAppManagerMcp -c Release -o ./publish` (see README, "Option 3: Build from Source")

2. **Optional Tasks:**
   - 6.4: Create appsettings.json for configuration
   - T8.4: Add coverage badge to README (when README exists)

3. **Testing:**
   - ✅ CI/CD pipeline with GitHub Actions (complete)
   - ✅ 325 tests (288 unit + 37 integration)
   - ✅ Integration tests complete (T7)
   - Target: Increase coverage to 75%+ (currently ~65%)

---

## Phase 8: Claude Code Plugin

*Convert MCP server to Claude Code plugin for marketplace distribution*

| # | Task | Status | Notes |
|---|------|--------|-------|
| 8.1 | Create `.claude-plugin/plugin.json` | ✅ Complete | Plugin metadata + MCP server config |
| 8.2 | Create `.claude-plugin/marketplace.json` | ✅ Complete | Marketplace distribution manifest |
| 8.3 | Build exe to `bin/` directory | ✅ Complete | `scripts/Publish-Plugin.ps1` (see `/commit`); never publish into `bin/` by hand |
| 8.4 | Create `skills/using-windows-app-manager/SKILL.md` | ✅ Complete | Skill file with trigger patterns |
| 8.5 | Update README.md with plugin installation | ✅ Complete | Add Claude Code install instructions |
| 8.6 | Test local plugin installation | ✅ Complete | `/plugin marketplace add`, `/plugin install` |
| 8.7 | Test GitHub-based installation | ⏳ Pending | `mortenbrudvik/WindowsAppManagerMcp` |

### Plugin Structure

```
WindowsAppManagerMcp/
├── .claude-plugin/
│   ├── plugin.json              # Plugin metadata + MCP server config
│   └── marketplace.json         # For marketplace distribution
├── skills/
│   └── using-windows-app-manager/
│       └── SKILL.md             # How to use the 21 tools effectively
├── bin/
│   └── WindowsAppManagerMcp.exe # Pre-built framework-dependent exe (needs .NET 8 Desktop Runtime)
```

### Compatibility

- **Claude Desktop**: Still works via manual `claude_desktop_config.json` configuration
- **Claude Code**: Installable via `/plugin install` command

---

## Phase 9: Development Tooling

*Workflow improvements for maintaining the plugin*

| # | Task | Status | Notes |
|---|------|--------|-------|
| 9.1 | Add `/commit` skill for versioned releases | ✅ Complete | Creates tagged commits with version updates |
| 9.2 | Auto-build the plugin binary on `/commit` | ✅ Complete | `scripts/Publish-Plugin.ps1` rebuilds `bin/` (the exe the plugin runs); CI checks it on release tags |

### /commit Workflow

The `/commit` skill automates the release process:

1. **Gather Information** - Version number and description
2. **Update Version Numbers** - `scripts/Set-Version.ps1` (Directory.Build.props, plugin.json, marketplace.json)
3. **Build Release Binary** - `scripts/Publish-Plugin.ps1` publishes to bin/
4. **Review Changes** - git status/diff
5. **Commit and Tag** - Conventional commit with annotated tag
6. **Push** - Push commit and tags to origin
7. **Confirm** - Display commit hash, tag, and release URL

---

## Phase 10: Screenshot Capture ✅

*Screenshot tools for AI visual orientation*

| # | Task | Status | Notes |
|---|------|--------|-------|
| 10.1 | Add GDI P/Invoke declarations | ✅ Complete | `NativeMethods.Gdi32.cs` - BitBlt, CreateCompatibleDC, etc. |
| 10.2 | Add User32 screenshot methods | ✅ Complete | GetDC, ReleaseDC, PrintWindow, GetClientRect |
| 10.3 | Add bitmap structs | ✅ Complete | BITMAPINFO, BITMAPINFOHEADER in NativeStructs.cs |
| 10.4 | Create `IScreenCaptureWrapper` interface | ✅ Complete | Native capture abstraction for testability |
| 10.5 | Implement `ScreenCaptureWrapper` | ✅ Complete | GDI-based screen/window capture |
| 10.6 | Create `IScreenshotService` interface | ✅ Complete | Service interface |
| 10.7 | Implement `ScreenshotService` | ✅ Complete | PNG/JPEG encoding with System.Drawing.Common |
| 10.8 | Create `list_screens` tool | ✅ Complete | List monitors with virtual screen bounds |
| 10.9 | Create `take_screenshot` tool | ✅ Complete | Capture specific monitor |
| 10.10 | Create `capture_region` tool | ✅ Complete | Capture arbitrary screen coordinates |
| 10.11 | Create `capture_window` tool | ✅ Complete | Capture window (works when occluded) |
| 10.12 | Update documentation | ✅ Complete | TOOLS.md, README.md, SKILL.md |

### Screenshot Tools Summary

| Tool | Description |
|------|-------------|
| `list_screens` | List available monitors for capture with bounds and scale factors |
| `take_screenshot` | Capture a specific monitor (PNG or JPEG) |
| `capture_region` | Capture arbitrary screen region by coordinates |
| `capture_window` | Capture a specific window (works even when occluded) |

### Use Cases

- **Visual Orientation**: AI can see current screen state before taking actions
- **Layout Verification**: Verify windows are positioned correctly after apply_layout
- **Debugging Assistance**: Capture what the user sees to help troubleshoot
- **Application State Understanding**: See if apps are loading, showing errors, or ready

---

## Phase 11: Enhanced Visual Tools ✅

*Advanced screenshot and UI analysis capabilities*

| # | Task | Status | Priority | Notes |
|---|------|--------|----------|-------|
| 11.1 | Add `capture_with_elements` tool | ✅ Complete | P2 | Screenshot with UI element bounding box overlays |
| 11.2 | Add `get_ui_elements` tool | ✅ Complete | P2 | Get UI elements with coordinates (no image) |
| 11.3 | OCR integration | ⏳ Pending | P3 | Extract text from screenshots using Windows.Media.Ocr |
| 11.4 | Screenshot diff detection | ⏳ Pending | P3 | Compare screenshots to detect changes |
| 11.5 | Windows.Graphics.Capture support | ⏳ Pending | P3 | Modern capture API (requires Win10 1903+) |
| 11.6 | Video/GIF recording | ⏳ Pending | P3 | Capture sequences for animated workflows |

### Implemented Tools

#### capture_with_elements
Capture a screenshot with UI Automation element overlays drawn on top.
- Color-coded rectangles by control type (green=buttons, blue=inputs, etc.)
- Returns image with element coordinates for click targeting
- Supports filtering by control types, interactability, visibility

#### get_ui_elements
Get UI element tree with bounding boxes without image capture.
- Lightweight alternative when only coordinates are needed
- Returns element name, type, bounds, and automation ID
- Supports depth limiting and various filters

---

## Phase 12: Advanced Automation (Future)

*AI-driven automation capabilities*

| # | Task | Status | Priority | Notes |
|---|------|--------|----------|-------|
| 12.1 | Click simulation | ✅ Complete | P2 | `click`, `right_click`, `double_click`, `mouse_move` tools using SendInput API |
| 12.2 | Keyboard input | ✅ Complete | P2 | `type_text`, `send_keys` tools using SendInput API with Unicode and VK support |
| 12.3 | Element interaction | ✅ Complete | P2 | Done as element_action, respond_to_dialog and file_dialog (Phase 13, M3.5, M3.6, M3.14) |
| 12.4 | Accessibility tree navigation | ✅ Complete | P3 | Done as element refs, find_ui_elements and get_desktop_state (Phase 13, M3.4, M3.12) |
| 12.5 | Wait for element | ✅ Complete | P2 | Done as wait_for_element and wait_for_window (Phase 13, M3.7, M3.8) |
| 12.6 | Scroll support | ✅ Complete | P3 | Done as scroll (Phase 13, M2.15) and element_action's scroll (M3.6) |

### Security Considerations

- All automation requires explicit tool invocation (no autonomous action)
- Handle validation before any interaction
- Size limits prevent memory exhaustion
- Protected content (DRM) captures as black automatically

---

## Phase 13: Automation Roadmap

*From the automated feature review on 2026-10-10. Two independent multi-agent review runs, with adversarial verification and a critic pass in each, were consolidated into one roadmap. The review found 120 distinct verified defects (2 critical, 19 high, 62 medium, 37 low).*

Both reviews reach the same diagnosis of WindowsAppManagerMcp. The architecture is sound: services behind interfaces, DI, mockable native wrappers, per-monitor DPI, structured Success/ErrorCode results, and UIA and CDP read paths. It is not yet safe or reliable for an agent to drive a Windows task from start to finish. Five problems stand out. (1) The model cannot see screenshots, because they arrive as base64 inside a JSON text block. (2) Keyboard and mouse input has no target and no verified activation, so it can land in the agent's own terminal; the test suite already did this with Alt+F4. (3) Many tools report success after doing the wrong thing or nothing at all: maximize on the wrong monitor, unknown states treated as restore, save_layout always true, CDP reads from the wrong tab, and a controlTypes filter that drops nested matches. (4) Headline features are broken: a stale plugin binary, an unserializable debug-launch result, and no published release. (5) Everything is addressed by raw HWND plus hand-computed physical pixels, with no waits and no element actions, so a simple form fill costs 4-10 round trips that cannot be verified. The plan works in layers. M0, shipped in 1.12.0, stops the bleeding. M1 makes seeing and typing safe by default. It starts with a default host-window input guard, ships image content together with the true capture origin, and adds verified activation, guarded input with receiving-window reporting, and a desktop-safe fixture e2e suite to validate the Windows-specific behaviour. M2 makes results honest and settles the MCP surface (contract tests, annotations, stable SDK, curated toolsets) before M3-M5 add about 30 tools. It also pulls scroll and clipboard forward. M3 is the biggest lever. It moves from blind coordinates to selector-addressed, pattern-first UIA actions with stable refs, popup-aware search, Chromium accessibility activation, text and table reading, virtualized items and server-side waits, each returning verified post-state. That cuts typical tasks to 1-2 calls that can each be checked. M4-M7 complete the input vocabulary, build a thin browser bridge with a Playwright handoff, add breadth, and hold the deferred items. Design assumptions were checked against the repo. ModelContextProtocol 0.5.0-preview.1 already exposes McpServerOptions.Filters.CallToolFilters and McpServer.ElicitAsync, so the M0 exception filter and M2 elicitation can be built on the current SDK. ProcessStartInfo.ArgumentList is honored with UseShellExecute=true on .NET 10; re-check this on the net8 target. The main residual risks are Windows-behaviour details the critics flagged: the foreground-lock unlock, popup hit-testing, InvokePattern blocking while its modal is open, Edge title formats, and stale element refs. That is why the fixture e2e suite sits in M1, ahead of the large feature work.

Design notes, sources and the full defect register: [docs/AUTOMATION-ROADMAP.md](docs/AUTOMATION-ROADMAP.md).

### M0: Stop the bleeding

*Every later fix reaches users and can be tested without touching the developer's desktop. The most dangerous confirmed defects are gone: unsafe tests, the stale binary, the IntPtr crash and opaque errors, the '(x86)' launch block, silent kill escalation, races and UIA crashes, lost layouts, CDP hangs and wrong-tab reads, and docs that teach Alt+F4.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M0.1 | Deny-by-default desktop test gating | ✅ Complete | P0 | M | Branch `m0-test-isolation`. Builds on the merged input-isolation fix (6c4648d). Any test that touches the real desktop is skipped unless the developer explicitly opts in, so no runner, filter or worktree can… |
| M0.2 | Single-source version and working release pipeline | ✅ Complete | P0 | M | Branch `m0-release-pipeline`. Make the exe the plugin runs the one built from main. Make the version visible over MCP, make tag pushes produce GitHub releases, and remove stale duplicate binaries and wrong… |
| M0.3 | Tool contract: serializable DTOs, serialization test, exception-surfacing filter | ✅ Complete | P0 | S | Branch `m0-tool-contract`. Fix the launch_browser_with_debug crash, which comes from System.Text.Json refusing to serialize IntPtr. Add a test over every tool that would have caught it, and replace the… |
| M0.4 | Launch validation, argument quoting and truthful kill_process | ✅ Complete | P0 | M | Branch `m0-launch-and-kill`. Allow normal paths such as 'Program Files (x86)' while guarding against argument injection through .bat/.cmd files. Quote arguments correctly. kill_process must never silently… |
| M0.5 | Concurrency safety and a UIA traversal that survives bad nodes | ✅ Complete | P0 | M | Branch `m0-concurrency-and-uia-crash`. The SDK runs tool calls concurrently. Make the monitor state an atomic snapshot and serialize all injected input. Make UIA traversal survive null ControlType (WinUI AppBar and… |
| M0.6 | Layout storage outside the versioned plugin folder, with honest results | ✅ Complete | P0 | S | Branch `m0-layout-storage`. Saved layouts live inside the versioned plugin folder today, so every update wipes them, and the sample presets never ship. Move storage, seed the samples, migrate old files,… |
| M0.7 | CDP timeouts, safe tab selection and explicit data source | ✅ Complete | P0 | M | Branch `m0-cdp-timeouts`. No CDP call may hang; today one hangs forever when a JS alert is open. Never return another tab's or another browser's data with Success=true. Every result says whether it came… |
| M0.8 | Docs safety hotfix | ✅ Complete | P0 | S | Branch `m0-docs-safety`. Stop the skill and docs from teaching the failure that already closed the user's terminal, and from pointing agents at paths that are known to be broken. Document the four browser… |

### M1: See and type safely

*The model sees real pixels it can click from accurately. Keyboard and mouse input cannot reach the agent's own host, a shell window, an elevated or deny-listed window, or an unintended target. Every input result reports where it actually went. The user can pause everything with a hotkey. A desktop-safe suite against a self-owned fixture app validates the real foreground, UIPI and capture behaviour.*

M1 shipped in release 1.13.0 (PR #3 merged the integration branch `m1-see-and-type-safely` into main, and the release commit rebuilt the committed plugin exe, `bin/WindowsAppManagerMcp.exe`). What shipped, the deliberate deviations and what moved to later milestones are in the [roadmap](docs/AUTOMATION-ROADMAP.md#m1-see-and-type-safely).

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M1.1 | Default host-window input guard | ✅ Complete | P0 | S | Branches `m1-security-probes` (HostGuard) and `m1-input-guard` (wiring). Input with or without a target never reaches the agent's own terminal, editor or chat window (TargetIsAgentHost) unless the call names it as windowHandle with allowHostTarget; the host record also covers the console server (`m1-fix-guards`). isAgentHost on every window; get_server_info moved to M2.11. |
| M1.2 | Captures as MCP image content with the true origin and an image-to-screen transform | ✅ Complete | P0 | M | Branch `m1-captures` (+ wave 1 follow-ups). Image content plus metadata, vision-sized JPEG/PNG defaults, imageToScreen, real origin (MapWindowPoints for client areas), safe outputPath, opaque pixels, widened overlay pens; minimized windows refused (`m1-fix-guards`). Hidden/cloaked/hung capture health moved to M2.9, image-space clicks to M3.11. |
| M1.3 | Verified window activation (EnsureForeground) | ✅ Complete | P0 | M | Branch `m1-foreground` (+ wave 2/3 follow-ups). Verified activation on one thread with setForeground, attachThreadInput and a zero-distance-move unlock; owner-chain in-front rule, one verified menu Esc (skipped while a modifier is down), WindowHidden and policy refusals (`m1-fix-guards`). apply_layout returns the per-placement focus results as `focus` (fixed on `m1-last-fixes`). |
| M1.4 | Integrity, elevation and desktop-state probes | ✅ Complete | P0 | S | Branch `m1-security-probes`. ProcessSecurityProbe: integrity levels, LikelyElevated vs Restricted, input desktop and session state; consumed by the input guard (TargetElevated, DesktopUnavailable, SessionLocked). |
| M1.5 | Target-guarded input with receiving-window reporting | ✅ Complete | P0 | M | Branch `m1-input-guard`. windowHandle on every input tool, a guard before every SendInput batch, receivingWindow/foregroundAfter/actualX/actualY, BlockedByModal, TargetOccluded, destructive and focus-changing chords, atomic presses (CursorNotAtPoint); MouseInUse and chord spellings (`m1-fix-guards`). Selector targets moved to M2.4. |
| M1.6 | Unfiltered window reads and user-facing listings | ✅ Complete | P0 | M | Branch `m1-window-reads` (+ wave 1/2 follow-ups). Unfiltered reads for any window; listings without cloaked, tool, untitled and shell windows; className, isCloaked, ownerHandle and UWP app attribution. |
| M1.7 | Close guards for shell and agent-host windows | ✅ Complete | P0 | S | Branch `m1-close-guards` (+ wave 2/3 follow-ups). ProtectedWindow for shell and host windows unless force; AccessDenied/CloseFailed from the Win32 error; kill_process refuses host processes and checks a forced kill's tree (ProcessTreeUnreadable, `m1-fix-guards`). Destructive annotations moved to M2.10. |
| M1.8 | Fix the UIA traversal filters behind a testable tree seam | ✅ Complete | P0 | M | Branch `m1-uia-tree` (+ wave 1/2 follow-ups). IUiaTree seam with FakeUiaTree, per-element filters with splicing, exact maxDepth, InvalidControlType, truncation reporting, partial tree on timeout (also for a hung provider, `m1-fix-guards`), per-application stuck-read limits. CacheRequest moved to M3.1. |
| M1.9 | get_focused_element and get_element_at_point | ✅ Complete | P1 | S | Branch `m1-uia-tree`. Both read through IUiaTree under a timeout with MCP cancellation; password values are never read; the top-level window comes from WindowFromPoint plus GA_ROOT. Element refs wait for M3.4. |
| M1.10 | Input session safety: pause hotkey, cancellation and stuck-input recovery | ✅ Complete | P1 | M | Branch `m1-session-safety` (+ wave 3 follow-ups). Pause hotkey shared by every server on the desktop, PausedByUser for input and window-changing tools, resume_input only with the user's confirmation, exact held-input release, release_all_input, get_control_status; bounded gate and window waits, InputBusy and NotResponding (`m1-fix-concurrency`). Takeover detection stays in M7.4. |
| M1.11 | Desktop-safe end-to-end tests against a self-owned fixture app | ✅ Complete | P1 | M | Branch `m1-fixture-e2e`, scenarios added with M1.1-M1.10. WamFixtureApp, gated E2E project with a fail-closed wrapper guard, DesktopNoInput tier, on-demand e2e.yml; scenarios for listings, UI elements, capture origins, EnsureForeground, the input guard, close guard and pause. DPI moves, the in-process MCP client (M2.12), an alpha check and the WebView2 pane (M3.3) remain. |
| M1.12 | Server-enforced target allow/deny policy | ✅ Complete | P2 | S | Branch `m1-input-guard`. WAM_DENY_PROCESSES (password managers and credential prompts by default) and WAM_ALLOW_PROCESSES, enforced for input, close, kill (whole tree), window captures and, since `m1-fix-guards`, every window change and focus. take_screenshot/capture_region do not black out denied windows; get_server_info moved to M2.11. |

### M2: Honest results and a stable MCP surface

*Every tool either does what it reports or says precisely why not: typed parameters, real post-state, verified close, handoff-aware launch and faithful layouts. The MCP surface (contract tests, generated docs, annotations, stable SDK, curated toolsets, server instructions) is settled before M3-M5 add about 30 tools. Scroll and clipboard, the two primitives agents miss first, are pulled forward.*

M2 shipped in release 1.14.0 (PR #4 merged the integration branch `m2-honest-results` into main, and the release commit rebuilt the committed plugin exe, `bin/WindowsAppManagerMcp.exe`): all 17 items, the wave-B follow-ups (`m2-wave-b-followups`), the fixes from a final review of the integrated branch (`m2-fix-integration`, `m2-fix-privacy`), a docs pass (`m2-docs-pass`) and the fixes it found (`m2-last-fixes`). The server serves 54 tools (47 in 1.13.0). What shipped, the final-review fixes, the known limits and what moved to later milestones are in the [roadmap](docs/AUTOMATION-ROADMAP.md#m2-honest-results-and-a-stable-mcp-surface); what clients see change is under "Changes for existing clients" in [docs/TOOLS.md](docs/TOOLS.md#changes-for-existing-clients).

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M2.1 | Honest error contract: isError, no null results, per-item batch errors | ✅ Complete | P1 | M | Branch `m2-tool-surface`. The outermost call filter sends every result whose `success` is false with isError true (ToolOutcome.WithIsError), so no tool can forget it; get_foreground_window returns NoForegroundWindow instead of null; batch items are validated one by one (InvalidArguments, InvalidSnapPosition, InvalidState) and fail alone instead of an NRE. |
| M2.2 | Typed parameters and strict validation with real outcomes | ✅ Complete | P1 | S | Branch `m2-tool-surface`. Strict [ToolEnum] parameters (state, positioning, position, matchBy, format, output, browser) listed in the schema, with the parameter's own errorCode and the allowed values instead of a silent fallback; monitorIndex, sizes, maxLength and ports validated; window changes report AccessDenied, MoveFailed, ResizeFailed or StateChangeFailed with the Win32 reason. |
| M2.3 | Correct, verified and hang-proof geometry operations | ✅ Complete | P1 | L | Branch `m2-geometry`. Placements restore a minimized or maximized window without activation (SW_SHOWNOACTIVATE, at most twice; a hidden one is not shown, and the placement fails, `m2-fix-integration`) before measuring, place and report the visible frame (bounds, outerBounds, state, monitorIndex), move keeps the size and resize the position, a placement that crosses a scale change is placed again, move_window_to_monitor clamps to the work area, moves in two steps across DPI and verifies the monitor (and maximizes only once there); pure SnapMath tiles exactly; settleMs; find_windows reports visibleBounds. WindowCallRunner kept for hang safety. |
| M2.4 | Window selectors, ambiguity errors and get_window_info | ✅ Complete | P1 | M | Branch `m2-selectors`. A `window` selector (title contains/equals/regex with a 100 ms timeout, whole process name with contains opt-in, processId, className, foreground, index, cloaked/untitled) beside `handle` on every window-taking tool and batch item, resolved to exactly one window before the input gate and then guarded like a handle; WindowNotFound, AmbiguousWindow with up to 5 candidates, HandleMismatch for a reused handle; the browser-aware title normalizer shared with CDP matching and layouts; find_windows sort and limit; get_window_info with compact, standard and full detail. The window, close and input tools check a resolved selector again inside the input gate (HandleMismatch; the input tools since `m2-fix-integration`). |
| M2.5 | Verified close, owned-dialog discovery and close_application | ✅ Complete | P1 | M | Branch `m2-close-verify`. close_window and close_windows_batch wait (waitMs) and report closed, hidden, stillOpen or promptShown with the prompt's text and buttons, sending inside the input gate and waiting outside it; get_owned_windows names the blocking dialog; close_application closes an app's Alt+Tab windows gracefully and never forces, and reports its windows on other virtual desktops (otherDesktopWindows) instead of closing them; close_windows_batch reports notSent items (`m2-fix-integration`). |
| M2.6 | Handoff-aware launch-to-window resolution | ✅ Complete | P1 | M | Branch `m2-launch`. WindowWaitService finds the window a launch opened whichever process owns it (handoff map, process tree, image path, expectWindow), including reused windows and new tabs (reusedExistingWindow, openedInTab); possibleHandoff, awaitingUserConsent and ElevationCancelled instead of failures or blocked calls; in a batch, a launcher that hands off to another item's instance gets its window (`m2-fix-integration`). |
| M2.7 | Launch path hygiene and protocol allowlist | ✅ Complete | P1 | S | Branch `m2-launch`. Bare names resolve only through the Windows folder, System32, PATH and App Paths (never the current directory), to .com/.exe/.bat/.cmd only, and ExecutableNotFound instead of an unresolved hand-off; programs start in their own folder; ms-settings: and shell:AppsFolder allowed, WAM_ALLOWED_PROTOCOLS for more, InvalidUri for unsafe URIs. |
| M2.8 | Faithful layout apply with one-to-one matching and per-placement results | ✅ Complete | P1 | M | Branch `m2-layouts`. save_layout records each window's identity (exact process name, class, title, exe path, ordinal, state); apply_layout scores every placement-window pair from one snapshot and gives each window to one placement, reports every placement (applied, notFound, notApplicable, failed) with status applied/partial/failed, and refuses NoMatchCriterion and missing monitors instead of guessing; placements report their launchCommand, and a maximized window is no longer activated by its restore (`m2-fix-integration`). |
| M2.9 | Capture health: never drop pixels, typed capture errors and warnings | ✅ Complete | P1 | M | Branch `m2-capture-health`. Minimized, hidden, cloaked and off-screen targets fail with WindowMinimized, WindowHidden, Cloaked or RegionOffScreen; hung windows are copied from the screen with a warning; capture_with_elements keeps the image when the UIA walk fails; EncodeFailed, warnings and restoreIfMinimized. |
| M2.10 | Tool annotations, titles and human confirmation through elicitation | ✅ Complete | P1 | S | Branch `m2-tool-surface`. Every tool has a title and explicit readOnly, destructive, idempotent and openWorld hints, enforced by a contract test; kill_process, large close batches and close_application ask the user through elicitation (ConfirmationDeclined), with confirm kept for clients without it and HandleMismatch when a confirmed window changed hands. Final review: launch_browser_with_debug is destructive and refuses switch-like URLs and real profiles; `force` on a protected window needs the user's yes; a confirmed kill is held to the process's start time (ProcessTreeChanged for a grown tree) and releases the gate before its wait. |
| M2.11 | Server instructions, get_server_info and an observe-act-verify skill | ✅ Complete | P1 | S | Branch `m2-server-info-toolsets`. 17 lines of server instructions for every client; get_server_info (build, protocol, integrity, effective DPI awareness, desktop state, monitors with DPI, host, pause, policy, paths, toolsets, warnings); PerMonitorV2 from app.manifest instead of the API call, logged at start; SKILL.md rewritten around locate, activate and verify, act, verify. Now 18 lines (selectors, scroll, clipboard); get_server_info also reports the clipboard setting and the allowed URI schemes (`m2-fix-integration`). Per-window DPI stays with M2.3. |
| M2.12 | In-process MCP contract tests, shared DI and generated docs | ✅ Complete | P1 | M | Branch `m2-foundation`. tools/list snapshot, every tool called over MCP (isError/null ratchets for M2.1), payload budgets, generated tool tables, CI guard on the built exes. Follow-up (`m2-server-info-toolsets`): initialize result snapshot per revision, UPDATE_SNAPSHOTS refused in CI, guard on 2025-11-25. |
| M2.13 | Upgrade the MCP SDK to stable; cancellation and progress everywhere | ✅ Complete | P1 | M | Branch `m2-foundation`. ModelContextProtocol 2.2.0; cancellation for UIA walks, launches, layouts, window batches and kill_process; progress for walks, batch launches and apply_layout; contract tests on 2026-07-28 and the initialize revisions (2025-11-25 added in `m2-server-info-toolsets`). |
| M2.14 | Toolsets with a curated default and a read-only mode | ✅ Complete | P1 | S | Branch `m2-server-info-toolsets`. WAM_TOOLSETS, WAM_EXCLUDE_TOOLS and WAM_READ_ONLY (keyed on readOnlyHint), read before the registrations; ten toolsets (eleven with M2.16's clipboard), all on by default today, with opt-in groups for the tools of M3-M6; observer and other env profiles in docs/TOOLS.md. WAM_TOOLSETS and WAM_EXCLUDE_TOOLS fail closed on an unknown entry (only the server toolset; `m2-wave-b-followups`, `m2-fix-privacy`), and WAM_EXCLUDE_TOOLS takes toolset names. run_steps (M6.2) must honour them. |
| M2.15 | scroll primitive (pulled forward) | ✅ Complete | P1 | S | Branch `m2-scroll-clipboard`. scroll(windowHandle?, x?, y?, deltaY, deltaX) on the click path of the input guard, one SendInput batch (move plus one wheel event per notch), client centre by default; WheelFollowsFocus when the wheel would follow the focus to another window; a bare foreground selector without a point is InvalidArguments (`m2-fix-privacy`). |
| M2.16 | Clipboard get/set with a privacy guard (pulled forward) | ✅ Complete | P1 | S | Branch `m2-scroll-clipboard`. get_clipboard (text, html, files) and set_clipboard behind INativeClipboardWrapper (STA thread, raw Win32); reads return only the agent's own content (set_clipboard, or a change within 5 s of its copy keys or click), never content marked private, and the user's clipboard only with WAM_ALLOW_USER_CLIPBOARD or the user's yes. Final review (`m2-fix-privacy`): input that pastes follows the same rule (PasteNotAllowed), copy windows run from the first input and back off after empty runs, one question at a time. |
| M2.17 | Audit log of injected input and destructive actions | ✅ Complete | P2 | S | Branch `m2-audit-log`. One JSONL line per input, close, kill, launch, layout and clipboard-write call in %LOCALAPPDATA%\WindowsAppManagerMcp\audit\<date>.jsonl (WAM_AUDIT_PATH), written by the outermost call filter: target (read before the call), receiving window, outcome, errorCode, duration; string arguments only as lengths; 7-day retention; never breaks a call; get_server_info reports the folder. Final review: get_clipboard audited (format, origin, length), close_application windows and apply_layout placements as items, selector targets named, and a background writer that holds a call up for at most 2 s. |

#### M2 follow-ups

*Done on M3's integration branch; they ship with M3 in 1.15.0.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M2.F1 | Decide whether `force` on a protected window needs `confirm` without elicitation | ✅ Complete | P2 | S | Branch `m3-force-confirm`. Decided by the user on 2026-10-11: yes. Without elicitation, `force` on a protected window (the desktop shell, the agent's host) needs `confirm=true` too in close_window (new `confirm` parameter) and close_windows_batch (whatever the number of windows); ConfirmationRequired otherwise, nothing closed. With elicitation the user is still asked. close_application has no `force`. Breaking for clients without elicitation. |
| M2.F2 | Right-click paste detection for more terminals | ✅ Complete | P2 | S | Branch `m3-m2-followups`. A right click is a paste in WezTerm's default class (`org.wezfurlong.wezterm`) and, by process, anywhere in VS Code, Code - Insiders, VSCodium, Cursor, Windsurf, Hyper, Tabby, Alacritty (winit's shared default class) and wezterm-gui: the class Chrome_WidgetWin_1 alone cannot tell them from browsers, and the server cannot tell an editor's terminal panel from the rest, so it errs on refusing. |
| M2.F3 | Check that a batch launch's launcher really handed off | ✅ Complete | P3 | S | Branch `m3-m2-followups`. A handoff needs exit code 0 (IProcessHandle.ExitCode); a non-zero code never hands off, even for a window that appears after the exit (the backlog's "or a window after the exit" would still have credited a failed launcher's later window); only when the code cannot be read does a window first seen after the exit count. |
| M2.F4 | Tell other-desktop windows from suspended or prelaunched frames | ✅ Complete | P3 | S | Branch `m3-m2-followups`. New INativeVirtualDesktopWrapper (IsWindowOnCurrentVirtualDesktop, GetWindowDesktopId; 1 s deadline, abandoned calls answer Unknown) with a mock in the tests; a shell-cloaked window on no desktop or the current one is left out of otherDesktopWindows, one that cannot be placed is kept. |
| M2.F5 | Audit log wait without blocking a thread-pool thread | ✅ Complete | P3 | S | Branch `m3-m2-followups`. IAuditLog.EndAsync awaits the line (Task.WaitAsync, at most 2 s, no thread held), the writer runs on a thread of its own, and the call filter also bounds a log that breaks its contract. |
| M2.F6 | Re-measure the window listing payload budgets | ✅ Complete | P3 | S | Branch `m3-m2-followups`. TypicalDesktop's windows carry `visibleBounds`: find_windows and get_all_windows 1,213 tokens (budget 1,850); get_foreground_window, get_window_info and the launch results re-measured too, and the stale capture and get_server_info sizes corrected. |

### M3: Semantic find-act-wait

*The biggest lever. Move from blind coordinates to selector-addressed, pattern-first UI Automation, so 'click Save in app X' becomes 1-2 calls that can each be verified. Elements are found by name, automationId and type, including in menus, popups, dialogs, Chromium/Electron apps and virtualized lists. Agents act through UIA patterns with verified post-state, read text and tables directly, and wait server-side instead of sleeping.*

M3 is complete on the integration branch `m3-semantic-find-act-wait` (PR #6) and is to ship in release 1.15.0 (64 tools; 54 in 1.14.0), with the M2 follow-ups above: all 14 items, the fixes from a review of the integrated branch (`m3-fix-*`), a docs pass and the fixes for what the first release gate found (`m3-fix-gate`). The release gate passed on 2026-10-13, so the release blocker is cleared: the repo owner ran the whole gated no-input tier twice on a real desktop, on commit 293cacd, with WAM_UIA_ENGINE unset (uia3) and with WAM_UIA_ENGINE=uia2, and both runs passed 44 of 44 tests with no outcome difference, so uia3 stays the default (M3.F1). The input-tier scenarios have not run yet; they do not block the release (M3.F2). What shipped, the review fixes, the repo owner's decisions and what moved on are in the [roadmap](docs/AUTOMATION-ROADMAP.md#m3-semantic-find-act-wait); what clients see change is under "Changes for existing clients" in [docs/TOOLS.md](docs/TOOLS.md#changes-for-existing-clients).

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.1 | Bounded, cached UIA walks with partial results and compact output | ✅ Complete | P1 | M | Branch `m3-uia-engine`. One cache request per walk (children listed first; up to 200 read with their properties and pattern flags in one call, wider levels and failed calls one element at a time, so maxElements and the timeout stop mid-level), breadth first, maxElements (default 300, truncationReason MaxElements), hung-window precheck (IsHungAppWindow, SendMessageTimeout WM_NULL 500 ms: NotResponding), format=tree/flat/text. Kept a thread per read instead of one queued worker (a hung app must not delay reads of other apps). M3 review: the MaxElements check reads one first child per waiting element (HasChildren), the precheck also probes a UWP app's CoreWindow, and the reads refuse a denied app's window (TargetDeniedByPolicy). HasMoreChildren at the depth limit and collapseStructural not done (M3.F16); capture_with_elements got maxElements with M3.11. |
| M3.2 | UIA3 (FlaUI) backend behind a widened seam, before refs, actions and waits | ✅ Complete | P1 | L | Branch `m3-uia-engine`. Interop.UIAutomationClient 10.19041.0 (leaner than FlaUI.UIA3, MIT, bundles into the single-file exe); Uia3Tree with 1 s connection and 5 s transaction timeouts, raw control type ids (AppBar, SemanticZoom), cached reads; IUiaTree widened with cached children, FindAll conditions, subtree cache, runtime ids and pattern objects (FakeUiaTree too); WAM_UIA_ENGINE=uia3 (default)/uia2, reported by get_server_info. M3 review: both engines raise the same seam exceptions, and the gated engine comparison covers identity, state, patterns and refs (the fixture has a PasswordBox). Release gate passed on 2026-10-13 (M3.F1): the whole no-input tier passed with both engines on a real desktop, so uia3 stays the default. The first gate (on c2db508) found the E2E harness recording the fixture as the agent's host, a test bug and unreadable WPF placeholders (M3.10), fixed on `m3-fix-gate`; in its uia2 run the BothEngines_ facts differed at the window's title bar only (uia2's managed proxy read the Minimize button as Minimize-Restore with no framework), so the repo owner kept uia3, and those facts now compare the title bar and its buttons by control type and name only. Events wait for M7.2 (M3.F22). |
| M3.3 | Chromium, Electron and WebView2 accessibility activation | ✅ Complete | P1 | M | Branch `m3-chromium-warmup`. AccessibilityWarmup: before the UI Automation element tools read a window with a visible Chrome_RenderWidgetHostHWND (Chromium, Electron, CEF, WebView2), it asks for the page Document and polls (200-500 ms, at most 1.5 s or a third of the timeout) until it has elements that stopped changing; results carry accessibilityWarmup (Ready/Partial/NotReady with a warning); warmed windows and their pages remembered (256 windows, dead ones dropped). frameworkId on elements (where it changes) and ancestors. launch_application forceAccessibility adds --force-renderer-accessibility only for a recognized Chromium/Electron/CEF .exe (forceAccessibilityApplied). M3 review: a page shown since its window was warmed up (a tab switched to) is warmed up too, and forceAccessibility warns when the app was already running. SPI_SETSCREENREADER deferred (session-wide, not restorable after a kill). Open: WebView2 in visual hosting, a WebView2 fixture pane and a live Chromium scenario, batch items (M3.F4, M3.F23, M3.F25, M3.F26). |
| M3.4 | Element registry with stable refs and find_ui_elements | ✅ Complete | P1 | M | Branch `m3-element-refs`. IElementRegistry: refs `w<window>e<n>` (window numbers start at random per session), kept by the same element, never reused, LRU 10,000, dropped with their window; resolution by live read, runtime id, then the stored selector verified against the fingerprint, with an internal API for M3.6/M3.8/M3.9/M3.11. find_ui_elements (provider-side criteria through a bounded, split search with deadline and candidate limits; refs, click points, patterns, detail levels, withinRef). get_ui_elements, capture_with_elements, get_focused_element and get_element_at_point emit refs. M3 review: owned dialogs are searched only as windows of their own (found once, not twice), a control in a recycled list row goes stale once the row shows another item, a ref moved to a re-created element is reported (refMoved) and element_action refuses it, and a list item named by its text is proven unique through its list. Gated E2E FixtureElementRefsTests passed in the release gate (M3.F1). |
| M3.5 | Popup-, menu- and dialog-aware element search | ✅ Complete | P1 | M | Branches `m3-popup-search` (part 1) and `m3-dialogs` (part 2); get_desktop_state's scope with `m3-desktop-state` (M3.12). Part 1: find_ui_elements `scope` (window, windowAndPopups, the default while the app is in front, desktop with the target policy and host windows left out) under one deadline, maxResults and candidate limit; popup refs registered under the popup's own window; IPopupService (owned, same-process and WebView2 child-process popups, z-order, kinds; WaitForNewPopupAsync) and read-only get_active_popups. read_element_text, wait_for_element and element_action take the same `scope` with their criteria; element_action's openedPopup kept its own probe (the service attributes a menu item's dialog to the main window, not to the menu: see IAppWindowProbe). Part 2: respond_to_dialog (dialog located through IPopupService, text and buttons read by find_ui_elements' search, buttons by standard id, AutomationId or text, pressed through element_action) and element_action `menu_path` ("File>Save As" through the submenus, each item through element_action). M3 review: popups of a denied app or of the agent's host are left out of the reads and counted; respond_to_dialog presses only in a dialog the agent named (cancel exempt) and refuses a named dialog that its own modal now disables (BlockedByModal); `ok` presses a lone button only when it carries Cancel's or Close's id; menu_path refuses a match from an unfinished search; the audit line names the dialog answered. Gated E2E FixturePopupTests and FixtureDialogTests passed on 2026-10-14 (M3.F2). |
| M3.6 | element_action: pattern-first actions with verified post-state | ✅ Complete | P1 | L | Branch `m3-element-action`. element_action by ref or window + find_ui_elements criteria (exactly one match): pattern-first invoke, click, toggle (to a state), set_value/append_text (mode value, type or paste), select, expand/collapse, scroll_into_view, scroll (nearest scrollable ancestor), set_range_value, realize, focus, in-app window_*; double_click/right_click and fallback mouse through the guarded input path. Target checks via IInputTargetGuard.CheckTarget inside the input gate; pattern calls on IUiaActionRunner threads (InvokedPendingModal with openedPopup); before/after state and outcomeVerified; password values never read. AutoSetFocus off under UIA3. M3 review: the mouse paths hit-test their click point (TargetOccluded when another element covers the element), toggle makes at most two toggles and stops when the element is back where it started, a moved ref is refused, another process's element has its window checked before its state is read, the MSAA path reads only the default action, openedPopup leaves tooltips out, observe follows get_desktop_state being served, undeclared arguments are refused, and `name` is audited as its length. The keyboard path is not atomic (M3.F17). Gated E2E FixtureElementActionTests passed on 2026-10-14 (M3.F2). |
| M3.7 | wait_for_window | ✅ Complete | P1 | S | Branch `m3-wait-for-window`. wait_for_window (windows toolset, read-only, outside the input gate, served in read-only mode and while paused): a window selector (M2.4 rules), condition exists, gone, foreground, visible, title_changed (with previousTitle) or input_idle, newOnly and excludeHandles, timeoutMs 0-60000 (default 10000), stableMs 0-10000 (default 300); polls (100 ms, then 250 ms) with progress; not satisfied is success false (isError) with WaitTimedOut and the window as last seen; several windows give matchCount and candidates; input_idle checks for a hung window first and waits on the app's own process. M3 review: a satisfied wait returns only the windows that held for stableMs, newOnly counts a window shown after a hidden start, newOnly with window.handle, and an excludeHandles that holds window.handle, are refused, and title_changed by handle ignores the title filters. Gated E2E FixtureWindowWaitTests passed in the release gate (M3.F1); event wake-ups wait for M7.2 (M3.F22). |
| M3.8 | wait_for_element | ✅ Complete | P1 | M | Branch `m3-wait-for-element`. wait_for_element (elements toolset, read-only): ref or find_ui_elements' criteria in a window waited for first (wait_for_window's exists); exists, gone, enabled, visible, focused, name_equals, value_equals (never on a password field), toggle_state; any of the first 10 matches; checks back off 100-500 ms, warm-up once; WaitTimedOut (isError) or early WindowNotFound / ElementStale / AmbiguousElement; polls, no UIA event handlers (M7.2). M3 review: gone is satisfied when the window handle had closed or withinRef's element is gone and searches the popups on every check, a ref the session never issued ends the wait, a window counts as closed only when Win32 says so, and a wait can end up to about 3 s after timeoutMs. stableMs left for later (M3.F20). Gated E2E FixtureElementWaitTests passed in the release gate (M3.F1). |
| M3.9 | Read element text and tables (TextPattern, ValuePattern, Grid/Table) | ✅ Complete | P1 | M | Branch `m3-read-text-tables`. read_element_text (ref, or window + find_ui_elements' criteria that must find one element; Text pattern paged by the provider, then Value, then Name; password fields never read) and read_table (Grid/Table patterns, else a DataItem/ListItem/TreeItem row walk; paged by startRow; size and time bounded; new PatternNotSupported). M3 review: read_table never scrolls (GetItem only for Win32, WinForms and Chromium grids; other grids through their realized rows, truncationReason NotRealized), a cell's value comes before its name, header rows are paired with their columns and not counted as data, nested tree items are no cells, and a vanished cell is null; pages are bounded in JSON bytes (60,000 and 40,000), and every tool result is written with the relaxed JSON encoder. Gated no-input E2E FixtureElementReadTests passed in the release gate (M3.F1). |
| M3.10 | Realize virtualized list and tree items | ✅ Complete | P2 | M | Branch `m3-virtualized`. find_ui_elements `searchVirtualized` (default false, read-only: FindItemByProperty through the ItemContainer pattern of the window's lists; items it alone found have `virtualized: true` and a ref; result `virtualized` lists the lists). element_action `realize` is the one realize path: Realize, then ScrollIntoView when still offscreen, verified on screen; by criteria it searches the lists' items too, and `maxScrolls` scrolls the window's one visible list page by page through element_action scroll. M3 review: realize of an item it could not act on is not verified. The fallback needs exactly one visible list (M3.F18). The first release gate found that searchVirtualized could not read the items a WPF list has not realized (their placeholders fail most properties); `m3-fix-gate` reads them property by property (IUiaTree.ReadVirtualizedItem, both engines), and their refs resolve. Gated E2E FixtureVirtualizedListTests: the no-input scenario passed in the release gate (M3.F1); the three realize scenarios passed on 2026-10-14 (M3.F2). |
| M3.11 | Capture registry, image-space clicks and Set-of-Marks tied to element refs | ✅ Complete | P1 | M | Branches `m3-capture-registry` (captureId registry, mouse-tool spaces screen/window/client/image with StaleCapture, take_screenshot monitorIndex -1) and `m3-set-of-marks`: capture_with_elements draws marks after scaling, each a filled tag with the element's ref; `legend` lines with image-space centres; defaults actionableOnly (without controlTypes), maxElements 80, labels; drawOverlays, detail legend or full; capture_window `ref` + `padding` crops to one element (ElementOffscreen). M3 review: a client-area ref crop's captureId checks the client area it was cut from, capture_with_elements gives no captureId when the window moved while its elements were read (WindowMoved), a screen captureId checks each monitor, and a converted point is rechecked before every press. Gated E2E FixtureCaptureIdTests and FixtureSetOfMarksTests passed in the release gate (M3.F1). |
| M3.12 | get_desktop_state: one-call observation | ✅ Complete | P1 | M | Branch `m3-desktop-state`. get_desktop_state (elements toolset, read-only): window in front, cursor, modifiers, Caps Lock, pause; windows as rows, focused element, the target's popups and elements (text lines with refs, popups first when in front), optional capture with captureId; one deadline, per-section errors. element_action `observe` (none, state or screenshot). First call in the server instructions and SKILL.md. M3 review: the focus of the agent's host or of a denied app in front is not read at all, a named denied window is not read, popups of denied apps and of the host are left out, a stuck part hands back what it read before the deadline, and observe follows get_desktop_state being served. Gated E2E FixtureDesktopStateTests passed in the release gate (M3.F1). |
| M3.13 | Launch-and-arrange in one call | ✅ Complete | P1 | M | Branch `m3-launch-arrange`. launch_application and launch_applications_batch items take `placement` {snap, monitorIndex, bounds, state, focus}, checked strictly before anything starts, and `reuseExisting`. The placement runs through the window tools' paths once the launch's window is found, is checked again after 500 ms and applied once more when the app moved its window back, and focuses last (EnsureForeground); a placement failure leaves the launch a success with placement.errorCode; the agent's host and denied windows are never placed. reuseExisting returns `alreadyOpen` with a window of the same app that shows what the launch opens. M3 review: a window whose title shows the name after another path is not reused, one that shows only the name is reused with a warning, and a web address needs expectWindow.titleContains (the page sets its title); the launch wait's openedInTab follows the same rules. Gated E2E FixturePlacementTests passed on 2026-10-14 (M3.F2). |
| M3.14 | Common file dialog helper (localization-safe) | ✅ Complete | P2 | M | Branch `m3-dialogs`. file_dialog(path, action open/save, overwrite, handle or window): path checked as outputPath (drive letter, no network paths, no environment variables; save refuses an existing file without overwrite before typing), file name box by the ids only file dialogs have (the Edit in ComboBox 1148 or FileNameControlHost, an Edit 1148, or an old-style edt1 1152 next to the dialog's file type or drive box) set through element_action set_value, IDOK pressed, the replace prompt answered by overwrite through respond_to_dialog's service, wait for the dialog to close; other prompts handed back. Audited with the path's length only. M3 review: it needs handle or window, answers a replace prompt only when its text names the file, reports a save completed only once a file is seen written, and refuses open in a Save As dialog. No gated E2E (the fixture has no file dialog: M3.F4). |

#### M3 follow-ups

*Open items from M3's waves and from its review, grouped by area. A row that belongs to a later milestone says so.*

**Release gate and real-desktop checks**

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.F1 | Release gate: run the no-input tier with both UI Automation engines | ✅ Complete | P0 | S | Passed on 2026-10-13; the release blocker for 1.15.0 is cleared. The repo owner ran the whole DesktopNoInput tier twice on a real desktop, on commit 293cacd ([TESTING.md, "Release gate: the UI Automation engines"](TESTING.md#release-gate-the-ui-automation-engines)): with WAM_UIA_ENGINE unset (the scenarios that leave the engine to the server wrote `UI Automation engine: Uia3 (default)`) and with WAM_UIA_ENGINE=uia2 (`Uia2 (WAM_UIA_ENGINE)`). Both runs passed 44 of 44 E2E tests, the same tests, with no outcome difference (TESTING.md's point 1): the engines agree, and UiaEngineOptions.Default stays uia3. The scenario classes that ran and passed in both runs: FixtureWindowTests (4 tests), FixtureUiElementsTests (8: four theories, each with both engines), FixtureCaptureTests (2), MirroredWindowCaptureTests (1), FixtureInputGuardTests (its 3 no-input scenarios), FixtureUiaEngineTests (10: two BothEngines_ facts and four theories), FixtureElementRefsTests (2), FixtureWindowWaitTests (3), FixtureElementWaitTests (3), FixtureElementReadTests (2), FixtureVirtualizedListTests (its no-input scenario), FixtureCaptureIdTests (2), FixtureSetOfMarksTests (2) and FixtureDesktopStateTests (1). The first gate, on c2db508, failed in both its runs on three problems, which `m3-fix-gate` fixed: the E2E harness created the server's host guard after the fixture had taken the foreground (its Loaded handler's FocusManager.SetFocusedElement took keyboard focus), so the fixture counted as the agent's host (the harness now records the host before the fixture starts and fails at once when the fixture counts as the host, and the fixture sets its focused element before it is shown); FixtureDesktopStateTests' assertion failed on a null errors list (a test bug); and searchVirtualized could not read a WPF list's unrealized items (new IUiaTree.ReadVirtualizedItem, M3.10). In its uia2 run the BothEngines_ facts also differed, at the title bar only: uia2 read the Minimize button as Minimize-Restore with no framework, from .NET's managed proxy, where uia3 read Minimize and Win32, from Windows' native proxy. The repo owner kept uia3, and those facts now compare the window's non-client elements by control type and name only (M3.F29). |
| M3.F2 | Run the M3 input-tier scenarios on a real desktop | ✅ Complete | P1 | S | Passed on 2026-10-14: the repo owner ran the whole Desktop tier (both tiers, uia3 default) on a real desktop with a 150% primary and a 100% second monitor, on branch `fix-e2e-fixture-dpi`, 69 of 69 E2E tests, with the mouse on the 100% monitor: FixtureElementActionTests, FixturePopupTests, FixtureDialogTests, FixturePlacementTests and the three realize scenarios of FixtureVirtualizedListTests among them. The first run, on c20bde7, failed 9 tests on test-harness problems, not server code: (1) the fixture ran system DPI aware although it asks for per-monitor v2 (its Main named WPF types, so compiling it loaded WPF, which set the awareness before Main's call; that call failed with ERROR_ACCESS_DENIED), so on the 100% monitor its own rectangles were 1.5 times the server's physical pixels (7 tests: captures, captureId points, element bounds, and a menu_path click the server refused because the point was on the terminal); Main now names no WPF type and the harness refuses a fixture with another awareness; (2) the E2E guard's process check failed on a WPF item placeholder (ReadIdentity fails as a whole), refusing realize (2 tests); it now falls back to ReadVirtualizedItem as ElementActionService does. Per-monitor v2 then exposed (3): WPF activates a window it moves across a DPI change inside Show, even with ShowActivated false, so a --no-activate fixture on the 100% monitor took the foreground now and then (2 foreground scenarios, intermittent); the fixture's windows now open centred on the primary monitor. Not recorded in that run (it printed the summary only): whether AutoSetFocus off keeps a background Invoke from taking the foreground, whether WPF's asynchronous Invoke reports openedPopup for the fixture's modal, and whether WPF owners list their owned windows in their tree. The fixture no longer opens on a second monitor with another scale, so no scenario covers one (M3.F29). |
| M3.F3 | Real-desktop checks the fakes cannot make | ⏳ Pending | P2 | M | By the user: a dialog box's and a task dialog's caption buttons lie inside the TitleBar's bounds; the ids the dialog tools rely on (a localized MB_OK button, CommandButton_6 and CommandButton_7, FileNameControlHost with Edit 1001 for Save As, ComboBox 1148 with Edit 1148) and a replace prompt whose text names the file; element_action's click-point hit test on real providers (Win32, WPF, Chromium); read_table's realized-rows path (a WPF DataGrid; Explorer's DirectUI grid takes it too, and whether its GetItem scrolls is unknown) and the WinForms and Chromium GetItem path; Explorer's and Outlook's ItemContainer placeholders (searchVirtualized); whether FromHandle on Chrome_RenderWidgetHostHWND returns the Document or a pane above it; WebView2 and XAML-island elements of a third process passing element_action's element-window check; Caps Lock from GetKeyState on a thread-pool thread (get_desktop_state); a prelaunched or suspended Store frame left out of otherDesktopWindows (M2.F4); how readable the Set-of-Marks tags are, and how crowded 80 marks look. Since M3.F5: Explorer's "Open File - Security Warning" (open a downloaded file from File Explorer) is a #32770 whose controls carry the IDs SecurityPrompts.OpenFileSecurityWarningControls expects (4417, 4419, 4421, 4426, 4428, 4431; get_ui_elements with WAM_DENY_PROCESSES=none shows them as automationIds), and the default policy refuses it with TargetDeniedByPolicy, also on a non-English Windows. Since M3.F23: a WinUI 3 WebView2 (the WinUI 3 Gallery's WebView2 page) read with get_ui_elements by a fresh server reports accessibilityWarmup host WebView2, Ready or Partial, with the page's elements; a second read reports none; a window of the same app without web content gets none and no delay (if the first gets none, find_ui_elements with controlType Document shows how deep the Document is). |
| M3.F4 | Fixture surfaces for the untested M3 paths | ✅ Complete | P2 | M | Branch `m3-f04-fixture-surfaces`. WamFixtureApp gets the five surfaces, each with gated scenarios. A table window (`--open-table`) holds a read-only WPF DataGrid of 1,000 rows with column and row headers; no-input read_table scenarios read its first page with headers and row headers, and check that a page past the realized rows ends NotRealized. A menu window (`--open-menu-window`) has a real Win32 menu bar (SetMenu); an input-tier menu_path runs File>Export>As Text and checks that each menu opened its own #32768 popup and that the fixture ran the one command. The main window's Save As button shows the common IFileSaveDialog; the fixture writes the returned file only directly in its event log's folder (FixtureSaveFolder), and input-tier file_dialog scenarios save a new file and replace an existing one through the replace prompt. The Task dialog button shows TaskDialogIndirect with two command links and Cancel (a new app.manifest asks for comctl32 v6, with no DPI setting); input-tier respond_to_dialog scenarios answer it cancel by CommandButton_2 and press a link by its text. An opt-in WebView2 window (`--open-web-view`, Microsoft.Web.WebView2 1.0.2903.40) shows local HTML with NavigateToString, no network and no debug port; no-input scenarios check the accessibility warm-up. The gate attributes gain `Requires` (IDesktopTestRequirement, checked only after the opt-in), so the WebView2 scenarios skip with a reason without the runtime (WebView2Runtime). Compiling Program.Main still leaves the DPI awareness unset (checked). Running the scenarios is M3.F33. |

**Target policy and safety**

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.F5 | Security prompts that run inside a shared process | ✅ Complete | P2 | M | Branch `m3-f05-security-prompts`. Decided by the repo owner on 2026-10-14: a structural rule. ITargetPolicy.EvaluateWindow (Services/SecurityPrompts.cs) recognizes Windows' "Open File - Security Warning" without its localized title or texts: a dialog box (#32770), or a control of one, whose children carry the control IDs and classes of shdocvw.dll's dialog template 4416 (4417 Static, 4419 and 4421 SysLink, 4426 and 4428 Button, 4431 SysLink; the same in the language-neutral shdocvw.dll, its en-US resources and ieframe.dll's templates 4416 and 37378, so the "run this file" variant and IE's "File Download" are recognized too; the blocked-file "Windows Security Warning" is not). Only a dialog box has its controls read, the read stops once all six are found, and a dialog whose controls cannot be read is not refused. It applies in any process while the deny list includes the default list (WAM_DENY_PROCESSES unset or with `default`). Every check that decides for a window asks it once the process is allowed: EvaluateFailClosed (the input guard and so the input tools, element_action, respond_to_dialog and file_dialog; the captures; get_desktop_state), WindowService.EvaluateTargetPolicy (window changes, closes, focus_window, apply_layout, launch placement) and UiaTargets.ReadVerdict (the UI Automation reads). The refusal is TargetDeniedByPolicy naming the prompt; in the server's own process it now comes before TargetIsAgentHost. No new tool, parameter, error code or wrapper member. Unverified on a real desktop: the IDs were read from the dialog templates, not from a prompt on screen (M3.F3). |
| M3.F6 | Optional deny entries after a real-desktop check | ⏳ Pending | P2 | S | Microsoft.AAD.BrokerPlugin (the Entra ID and Microsoft account sign-in host, which takes passwords) and SecurityHealthHost: confirm their windows (className, hostProcessName) with find_windows on a real desktop, then decide whether they join TargetPolicySettings.DefaultDeniedProcesses. |
| M3.F7 | Match a hosted app behind any frame class | ✅ Complete | P3 | S | Branch `m3-f07-frame-hosts`. New UwpFrames.IsFrame: a window is a frame when its class is ApplicationFrameWindow, as before, or when its process is ApplicationFrameHost (matched by name ignoring case, path and ".exe"), so the rule is keyed on the frame host's process, not on a list of classes. WindowService.ResolveProcess, UwpFrames.AppProcessId and UiaTargets (ReadVerdict and NotRespondingReason) use it: a prompt in a frame host window of another class is reported under the app whose CoreWindow child it hosts, with ApplicationFrameHost as host, so the input guard refuses coordinate input and keys to a denied prompt (CredDialogHost) with TargetDeniedByPolicy, as do the captures, the window tools and the UI Automation reads. The class is checked first, so an ApplicationFrameWindow reads as before; another window adds at most one process-name read, which WindowService (cached per call) and ReadVerdict already make. The title match against top-level CoreWindows stays with ApplicationFrameWindow frames. The policy checks both names, so the change only adds a name to deny by. No new tool, parameter, error code or wrapper member. Not run on a real desktop: which classes the prompts' frames have, and that they hold the prompt's CoreWindow as a child (M3.F3). |
| M3.F8 | A guard on the programs launch_application runs | ✅ Complete | P2 | M | Branch `m3-f08-interpreter-guard`. Decided by the repo owner on 2026-10-14: refuse interpreters with arguments. InterpreterGuard (the single list, replacing D-PAL-07's interpreter warning in InputValidationService) is checked by ProcessService.PlanLaunch after resolution and before anything starts, so it covers launch_application, launch_applications_batch items, apply_layout's launchMissing (`LaunchFailed` with the refusal in `error`) and reuseExisting, on both the requested and the resolved name. A command interpreter, script host or other program that runs its command line (cmd, powershell, pwsh, bash, wsl, conhost, wt, wscript, cscript, mshta, python, node, java, dotnet, rundll32, regsvr32, msiexec, msbuild, schtasks and others; the list is in TOOLS.md) given any arguments is InterpreterWithArguments; a script target (.bat, .cmd, .ps1, .vbs, .js, .hta, .py, .jar, .msi and others), with or without arguments, or explorer given one, is ScriptFile. Nothing is started, the message says the server does not run commands or scripts and to ask the user, and a bare interpreter still starts. Names are matched across case, quotes, file: URIs, a missing .exe, alternate data streams, 8.3 short names (GetLongPathNameW, a new read-only native API) and versions (python3.12). Batch file shims are scripts, so `code` (code.cmd) and `npm` are refused: launch `code.exe` (App Paths). Known gaps, stated in TOOLS.md "Commands and scripts" and the README: renamed copies, .lnk and .url shortcuts, user file associations, programs not on the list, and commands typed into a console with the input tools. |

**Search, refs and reads**

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.F9 | Owned windows in the tree walks | ✅ Complete | P2 | S | Branch `m3-f09-owned-windows`. get_ui_elements, capture_with_elements and get_desktop_state's walk leave a window the walked window owns out of its children, with everything inside it, as the searches do (OwnedWindows.Below), so no ref of a dialog is registered under its owner. A level fetched with its properties is tested on them; a level read one element at a time has its first children read one at a time while they are owned windows (at most ElementSearch.MaxOwnedWindows), and the first child that is none keeps its read. Without the Win32 reads nothing is left out. New result field `ownedWindows` (the handles left out, omitted when there are none) in get_ui_elements and capture_with_elements' metadata, so the agent reads or captures each by its own handle; get_desktop_state walks them only as popups, so windowAndPopups no longer lists a dialog's controls twice. Tool surface and token budgets unchanged. Not run on a real desktop. |
| M3.F10 | Row checks for refs from the focus, point and popup reads | ✅ Complete | P3 | S | Branch `m3-f10-f11-registry`. get_focused_element, get_element_at_point and get_active_popups read the element's list row with ElementRows, as find_ui_elements does (at most 8 ancestors, not past a list, tree, grid, table or window), and register it with the ref, so the registry's row check makes such a ref ElementStale once a recycled row shows another item. An element whose ancestors cannot be read keeps its ref without a row; in get_active_popups a popup whose row read times out is listed without a ref. No new tool, parameter or error code. Unverified on a real desktop: whether WPF's GetParent from a recycled template child can return the old item's peer. |
| M3.F11 | Handle reuse within the same process and class | ✅ Complete | P3 | S | Branch `m3-f10-f11-registry`. New INativeWindowWrapper.GetWindowThreadId (GetWindowThreadProcessId's thread id, 0 for no window), a read in the E2E WrapperCatalog and forwarded by ReadOnlyNativeWindowWrapper. The element registry records the window's thread with its process and class, and drops the window's refs on lookup, resolution, NextGeneration and the next registration when both threads are known and differ; a thread read that fails tells nothing, like a failed process or class read. A reuse by a window of the same thread is still not told apart (no cheap Win32 read of a window's creation). No new tool, parameter or error code. Not run on a real desktop. |
| M3.F12 | Provider-side nameMatch contains | ✅ Complete | P3 | S | Branch `m3-f12-provider-contains`. Opt-in, off by default: with WAM_UIA_PROVIDER_CONTAINS=1 (ProviderContainsOptions) the UIA3 provider also narrows a nameMatch contains search; off, contains is the server's test alone and the provider's condition is as before. New seam condition UiaCondition.PropertyContains: Uia3Tree builds it with PropertyConditionFlags_MatchSubstring and IgnoreCase; a client that refuses it (E_INVALIDARG, Windows before 10 1809) is remembered for the process; UiaTree (uia2) never takes it. The narrowing is the longest run of the text's ASCII characters other than i and I, so every name the server's OrdinalIgnoreCase test accepts contains it; the server's test still runs on every element returned, so a provider that ignores or misapplies the flag adds nothing. A complete search returns the same matches, order, refs, matchCount and truncation; only the 2000-candidate limit is reached later. The search, its uniqueness proof (merged into M3.F13's per-window reads) and the registry's selector re-run pass it when the option is on. Off until a desktop check (TESTING.md, "Desktop check: provider-side contains") shows that Windows takes MatchSubstring|IgnoreCase, that it narrows, and that names with combining marks (Cafe + U+0301) still match. |
| M3.F13 | Stuck-read accounting per window for multi-window scopes | ✅ Complete | P3 | S | Branch `m3-f13-per-window-reads`. Done at the repo owner's request (2026-10-14) rather than waiting for it to block an app. A windowAndPopups or desktop search is no longer one worker read: its start (the named window's hung check, node, warm-up, title and the scope's windows) is counted against the named window's application, and each window's search, and the reads after it that touch that window's elements (the virtualized lookup, the uniqueness proofs under one shared budget, describing its matches), are reads of their own counted against that window's application (UiaTargets.TargetProcessId), one at a time under the one deadline (ElementSearchService.Windows.cs). The search loop and result assembly are the single read's own code, so windows, limits, order, counts, maxResults and truncation are unchanged; the window scope keeps its single read. A window other than the named one whose application has too many stuck reads is skipped (PartsSkipped); a later refused read leaves out only its own part; the named window's application being refused still fails with OperationTimedOut. No tool surface change. Not run on a real desktop: a real provider stuck in another process's popup. |
| M3.F14 | Bound AutomationIds and titles in search and popup results | ✅ Complete | P3 | S | Branch `m3-f14-bounded-strings`. New AppText (Infrastructure) holds the cap in one place: 200 characters, as respond_to_dialog's titles and the audit log; a cut value ends in "…" inside the 200 and is never split inside a surrogate pair. The cut is applied only where a value is returned: window selectors and element criteria still compare the whole value, and the services that run their own searches (respond_to_dialog, menu_path, element_action) read whole AutomationIds. Cut: the window tools' titles (find_windows, get_all_windows, get_foreground_window, get_window_info, the launch tools' window, get_owned_windows), a selector's candidates, find_ui_elements' and wait_for_element's windowTitle and automationIds, get_active_popups' titles (so get_desktop_state's popups too), get_desktop_state's titles and focus.automationId, and element_action's openedPopup; respond_to_dialog's own cuts end in "…" now. TOOLS.md documents the cap under Untrusted content: a cut value does not match titleEquals or automationId, so use titleContains, the handle or the ref. Tool surface unchanged. The walks and the focus and point reads are M3.F31. |
| M3.F15 | read_table and read_element_text gaps | ✅ Complete | P3 | M | Branch `m3-f15-read-gaps`. read_table returns `rowHeaders`, one per row (null for a row without one): the Table pattern's row headers for a grid read through GetItem, unless they are cells of the grid already (an HTML th in each row), and the HeaderItem that starts a row (WPF's DataGridRowHeader) for a grid read through its rows; left out when no row of the page has one with text, and counted in the page's 40,000 bytes. Both row walks take the rows inside Group children (down to three levels) as rows in their place, numbered across the groups, so Explorer's grouped views and a grouped WPF DataGrid page as any list (group names are not returned). A page that times out before its first row has no nextStartRow, and a warning says to read the same startRow again with a larger timeoutMs. read_element_text returns `selectedText` (at most 1,000 characters, `selectionTruncated` when cut) for an element with the Text pattern; a password field is never read. Text attributes are not read: they vary along a page, so reporting them would take a walk of its format runs and a payload as long as the text. Both tool descriptions name the new fields. Not run on a real desktop: Explorer's grouped view, WinForms and WPF row headers, and GetSelection of real edits and documents. |
| M3.F16 | Walk output, and minimized and elevated targets | ✅ Complete | P3 | M | Branch `m3-f16-walk-warnings`. get_ui_elements gains `warnings` (left out when there are none), one "Code: text" sentence each as the captures' warnings. WindowMinimized for a successful walk of a minimized window (visibleOnly left its elements out, or they are offscreen and element_action refuses them; it names the handle to restore with set_window_state or focus_window). TargetElevated when the window's process (for a UWP frame, the app's) is more privileged than the server by the input guard's own test (IProcessSecurityProbe), for a successful or failed walk whatever the element count. Both are read after the walk and the call still succeeds; no new error code. capture_with_elements merges them into its `warnings`, and get_desktop_state gives them in `elements.warnings`. D-UIA-07 is fixed. Left with M6.1: HasMoreChildren at the depth limit (one more call per element at the limit, and a field on many elements of ordinary walks; depthReached already signals it), collapseStructural and leaving out default-valued fields. Left with M6.6: structured elevation fields on the window results. Not run on a real desktop. |

**Actions and waits**

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.F17 | Make element_action's keyboard path atomic | ✅ Complete | P2 | M | Branch `m3-f17-atomic-keys`. New InputTarget.FocusRecheck, beside SelectorRecheck and PointRecheck: the input guard runs it inside the gate once the target is in front (CheckKeyboard) and before every batch (RecheckKeyboard, ahead of its same-window shortcut), so it covers Ctrl+A or Ctrl+End, Delete or Ctrl+V and every 32-character batch of typed text. element_action's keyboard path sets it: keyboard focus read on the UI Automation worker (HasFocus) for at most FocusRecheckTimeout (1 s), since the gate is held meanwhile; focus on another control is FocusNotOnElement, an unanswered read OperationTimedOut (fails closed), and `error` says how much was sent. The separate focus check between Ctrl+A and the text is gone (the recheck covers it, inside the gate). type_text and send_keys set no recheck and are unchanged. No new tool, parameter or error code. Not run on a real desktop: the gated E2E FixtureElementActionTests cover the path, not a focus change between batches. |
| M3.F18 | Choose the list for realize's scroll fallback | ✅ Complete | P2 | S | Branch `m3-f18-realize-list`. Both fixes: of several visible lists, realize with maxScrolls now scrolls the only one with a Scroll pattern of its own (a web page's navigation or footer list scrolls with the page; it reads up to 20 lists, and more is still AmbiguousElement), and element_action takes `listRef` (realize with maxScrolls only; an element of another window is InvalidArguments) for windows with several lists that scroll, such as Explorer's navigation tree and file list; AmbiguousElement says to pass it, from find_ui_elements' virtualized.containers. The tools/list budget rose by the new parameter (51,400 to 51,475 tokens). Not run on a real desktop. |
| M3.F19 | Smaller follow-ups of the M3 tools | ✅ Complete | P3 | M | Branch `m3-f19-small-followups`. Four of the seven are done; the other three are M3.F32. (1) realize with maxScrolls puts the list back where it was when the search ends without the item (ElementNotFound or OperationTimedOut), with one more element_action scroll, with every check, to the list's vertical position before the first page (the Scroll pattern's SetScrollPercent, through the internal ElementActionRequest.ScrollPercent, which no tool argument sets); the error says it was put back, or why not. A scroll through a scrollable ancestor now reports that ancestor's percentages in before and after. (2) When no list returns an item for the exact text, searchVirtualized and realize by criteria ask the lists again all lower case, all upper case, with a capital first letter and with capital words; other mixes of case are still not found. (3) openedPopup (and respond_to_dialog's nextDialog) carries `ref`, the popup window's root element, registered under that window as get_active_popups does, read after the window passes CheckElementWindow and answers messages, within 1 s, and left out when it cannot be read. (4) With scope windowAndPopups, maxScrolls also looks for the list in the window's open popups and searches a popup's list in the popup's own window; listRef is still a list of the window itself. Only the maxScrolls description changed in tools/list. Not run on a real desktop. |
| M3.F20 | stableMs for wait_for_element | ✅ Complete | P3 | S | Branch `m3-f20-f25-params`. wait_for_element takes stableMs (default 0, 0-10000 or InvalidArguments, cut to timeoutMs, as wait_for_window's) for every condition: it must hold at every check for that long, timed from the first check of the run that saw it, and a check that does not see it, or cannot tell, starts the time again, so a list that is re-created no longer satisfies gone in the gap. Stability is on the condition, not one element. A gone that cannot be undone (the window closed, a ref's or withinRef's element is gone) counts at once, and so does a gone wait that finds no window at the start (with its warning). A timeout after it held, but not long enough, says "(for N of M ms)" in lastObservedState. No new result field; stableMs 0 is the old behaviour. Not run on a real desktop. |
| M3.F21 | value_equals on a password field | ✅ Complete | P3 | S | Decided by the repo owner on 2026-10-14: keep the never-read rule. value_equals never reads a password field, not even to compare it, so such matches stay skipped (InvalidArguments when only password fields match); providers mostly refuse or mask the value anyway. No change. |
| M3.F22 | Event wake-ups for the waits | ✅ Complete | P3 | M | Branch `m3-f22-wait-events`. Event wake-ups only speed the waits up: an event ends the sleep between two checks early (WaitWakeUp, at least 25 ms, so a burst is one check), the checks alone decide, stableMs is still measured by them, wait_for_element's back-off counts only the sleeps that ran their length, and a wait without events polls exactly as before. wait_for_window: WindowEventSource (optional dependency of WindowWaitService) installs out-of-context WinEvent hooks (create, destroy, show, hide, name change, minimize and restore, cloak and uncloak, foreground; not location changes) on one STA thread with a message loop, started by the first wait that sleeps, hooked only while a wait subscribes and stopped with the server, through the new INativeWinEventWrapper (pinned by NativeCallGuardTests, reads in the E2E WrapperCatalog). wait_for_element: IUiaTree gains AddStructureChangedHandler and AddPropertyChangedHandler (both engines and FakeUiaTree); ElementEventSource adds and removes them on an MTA thread of its own per watch, never on the read worker or an event thread, and a call past its 2 s bound counts as stuck, so that application's waits poll until it returns. Handlers only signal. No new tool, parameter or result field. M7.2's event journal can reuse INativeWinEventWrapper, the WindowEventSource thread model and ElementEventSource's registration model. Not run on a real desktop: the gated FixtureWindowWaitTests and FixtureElementWaitTests cover the waits, not the events. |

**Web content and launch**

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.F23 | WebView2 in visual hosting | ✅ Complete | P2 | M | Branch `m3-f23-visual-webview`. AccessibilityWarmup: a window with no visible Chrome_RenderWidgetHostHWND whose process started an msedgewebview2.exe (INativeProcessWrapper.GetProcessList, one Toolhelp snapshot, no UI Automation call) has its UI Automation tree searched breadth first for a Document with frameworkId Chrome (through Pane, Group, Custom and Window, at most 6 levels and 16 listings). A Document found is polled as the HWND path polls (same steps, budget, Ready/Partial/NotReady and warnings, host WebView2), found again if it disappears, and the window is remembered by its handle. A window of any other process costs one process list; the HWND path is unchanged. No new tool, parameter or field. No fixture pane: the WPF WebView2CompositionControl draws into a WPF Image through Windows.Graphics.Capture and has no automation peer (checked up to package 1.0.4191.47), so its page is not in UI Automation at all. Unit tests only (12 over FakeUiaTree); the check on a WinUI 3 WebView2 is in M3.F3. |
| M3.F24 | The browser tools' UI Automation path on the seam | ✅ Complete | P2 | M | Branch `m3-f24-browser-seam`. New BrowserUiaReader holds get_browser_url's and get_browser_content's UI Automation reads over IUiaTree, so both engines serve them; the lookup is moved as it was (M5.7 still owns the locale-independent lookup). BrowserReadingService runs the read as one UiaWorker call counted against the browser's process, under its own timeoutMs, after the target policy (TargetDeniedByPolicy) and the hung-window check (NotResponding); a missed deadline or refused read is OperationTimedOut (BrowserErrorCode 14-16, the UI Automation tools' names). get_browser_content warms up the page first (M3.3) and reports accessibilityWarmup; get_browser_url reads only the address bar and does not wait. CDP reads are unchanged and not covered by the policy. IUIAutomationWrapper keeps only CloseWindow (WindowCloseVerifier); its browser members and their tests are gone. Not run on a real desktop: the warm-up against a live Chrome or Edge page and the URL lookups through UIA3. |
| M3.F25 | forceAccessibility on launch_applications_batch items | ✅ Complete | P3 | S | Branch `m3-f20-f25-params`. launch_applications_batch items take forceAccessibility (default false), passed to the launch service as launch_application's parameter is: --force-renderer-accessibility only for a recognized Chromium, Electron or CEF .exe, otherwise the item's warning says why. Each item's result has forceAccessibilityApplied, left out when the item did not ask. Not run on a real desktop. |
| M3.F26 | Opt-in screen reader flag (SPI_SETSCREENREADER) | ✅ Complete | P3 | S | Decided by the repo owner on 2026-10-14: not added. The flag is session-wide, cannot be restored after a crash and would make two servers race; the accessibility warm-up (M3.3) and forceAccessibility already cover the Chromium cases. No change. |
| M3.F27 | Late-moving windows and apply_layout's launches | ✅ Complete | P3 | M | Branch `m3-f27-late-moves`. The single re-check 500 ms after a launch placement is a watch now: LaunchPlacementService (on a TimeProvider) reads the placed windows after 250 ms, then at intervals doubling to 1 s, until each has stayed put for 1 s, for at most 3 s; a window the app moved, resized or restored is placed again, at most 3 times (then it is left where the app put it, with placement.warning, and the placement still succeeds). A window that closes while watched is InvalidHandle; a pause stops the next placement. apply_layout's launchMissing places the windows it launched through LaunchPlacementService: the same checks (the agent's host, the target policy, the pause), the watch and focus last, with the preset's rectangle as the outer window rect (LaunchPlacementPlan.Frame); open windows are arranged as before, and LaunchPlacementOutcome's FailedStep and Activation keep apply_layout's result model. Left with M7.2: an app that moves its window after 3 s, or between two reads, is not followed. Left with M6.7 and M6.8: windows are placed by monitorIndex, not by monitor identity. Not run on a real desktop. |
| M3.F28 | Coordinate spaces for drag and mouse_down | ✅ Complete | P3 | S | Moved to M4.3 on 2026-10-14: it is a requirement of tools that do not exist yet (drag and mouse_down), so it is kept in M4.3's row, where those tools will be built. |

**Tests and CI**

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.F29 | Test and CI follow-ups | ✅ Complete | P3 | S | Branch `m3-f29-test-ci`. AuditLogContractTests' UnnamedStrings helper now checks a tool's content arguments (AuditRecord.ContentArguments: element_action's name), and a new theory sends each one. .github/workflows/e2e.yml has an engine input (uia3 default, uia2) that sets WAM_UIA_ENGINE for the test step and names the results artifact by tier and engine; TESTING.md's release gate says the workflow can run either engine. The BothEngines_ facts compare the non-client area by control type and place first and its names last: a name difference fails on its own, with a message that gives the display language and says it may be the proxies' localization (every name is still compared). Timing: DesktopStateService takes an optional TimeProvider, and its deadline tests run on a new ManualTimerClock the test moves; the in-process MCP server's step timeout (start, connect, stop) is 30 s instead of 10 s, which the contract tests hit at 11-14 s under load; LayoutToolErrorContractTests ignores the slow-call Warning. The suite passed in full under heavy artificial load. The mixed-DPI scenario is M3.F30. |
| M3.F30 | A fixture scenario on a monitor at another scale | ⏳ Pending | P3 | S | From M3.F29 and M3.F2. No scenario covers a fixture window on a monitor whose scale differs from the primary's: since M3.F2 the fixture opens on the primary, because WPF activates a window it moves across a DPI change inside Show. Such a scenario needs its window created on that monitor (no DPI change inside Show) and kept out of the foreground, and a real desktop with two monitors at different scales. |
| M3.F31 | Bound titles and AutomationIds in the walks and the focus and point reads | ✅ Complete | P3 | S | Branch `m3-f31-bounded-walks`. get_ui_elements and capture_with_elements cut the window title and every element's AutomationId (the whole tree, in every format) in UIElementTools.ReadTreeAsync, which both read through; capture_with_elements' own window title is cut where it is read. get_focused_element and get_element_at_point cut the window title and the AutomationIds of the element and its ancestors. Each record's Bounded() returns the record itself when nothing needed a cut, so an ordinary result is passed on as the service gave it. The cut is at the tool layer, as M3.F14's: the services and the element registry keep the whole values. Element names are not cut, and some error messages still quote whole titles. No tool surface change. Not run on a real desktop. |
| M3.F32 | The rest of M3.F19 | ✅ Complete | P3 | M | Branch `m3-f32-marks-observe`. Two of its three parts; file_dialog for UWP and WinUI pickers is M3.F34. (1) get_desktop_state's new `screenshotMarks` (default false; needs 'screenshot' and 'elements' in include) draws Set-of-Marks marks on its screenshot through capture_with_elements' capture, made once the elements walk has returned and marking that walk's own elements, so each tag is the ref that starts its element line; popups get no marks, and a window that moved between the read and the capture gets none (ElementOverlayOptions.MarkMovedWindow) with a WindowMoved warning. (2) click, right_click, double_click, type_text, send_keys and respond_to_dialog take element_action's `observe` (none by default), with one shared description and one implementation (Tools/ActionObservation) that element_action uses too: refused before anything is sent when get_desktop_state (or for a screenshot capture_window) is not served, observed after a failed input too, never naming a window the input was refused for. These tools return a CallToolResult with the same JSON. tools/list rose by about 580 tokens (observe costs about 83 per tool). Not run on a real desktop. |
| M3.F33 | Run M3.F4's scenarios on a real desktop | ⏳ Pending | P2 | S | The user runs them with the opt-ins, never an agent; none has run. No-input: `FixtureElementReadTests.ReadTable_OfTheDataGrid` and `FixtureWebViewTests` (they skip, with the reason, without the WebView2 runtime). Input: `FixtureFileDialogTests`, `FixtureDialogTests.ATaskDialog` and `FixtureDialogTests.AMenuPath_OpensTheWin32`. Then the whole no-input tier, since the main window has two new buttons. Untested guesses to confirm: the TASKDIALOGCONFIG layout (Pack = 1, 160 bytes on x64; E_INVALIDARG is logged in taskDialogClosed), the Win32 menu expand through Windows' menu proxy, the DataGrid row headers read as HeaderItems named after their content, WebView2 staying out of the foreground with --no-activate, and the page button found by name and type. The new code of M3.F7, F9, F10, F11, F15, F16, F17, F18 and F19 has not run on a real desktop either; what each needs is in its row. |
| M3.F34 | file_dialog for UWP and WinUI pickers | ⏳ Pending | P3 | M | From M3.F32 (M3.F19). file_dialog refuses UWP and WinUI pickers (PickerHost) and other pickers without the file name box ids with DialogNotFound. Their structure has to be learned on a real desktop first (get_ui_elements on the picker), then file_dialog can fill them. |

### M4: Input vocabulary

*Keyboard and mouse produce exactly what was asked, on any keyboard layout and button configuration. The tools cover the chords, drags, multi-clicks, modifier-clicks and long text that real tasks need, and nothing can stay held down after a call returns.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M4.1 | Keyboard engine: layout-aware mapping and a full key grammar | ⏳ Pending | P1 | M | Ctrl or Alt plus punctuation never fires, 'Hello' types 'hello', AZERTY digits come out wrong, '100%{ENTER}' sends Alt+Enter, and scan codes are always 0. There are also no escapes, groups, Win modifier or numpad keys. |
| M4.2 | Mouse correctness: atomic absolute clicks, swapped buttons, real double-click | ⏳ Pending | P1 | S | Today a click is SetCursorPos, a 10 ms sleep, then relative button events, so it can land wherever the cursor drifted. Swapped buttons invert click and right_click, and a double-click can exceed the double-click time. |
| M4.3 | mouse_click, drag and press_keys, with no cross-call holds | ⏳ Pending | P1 | M | Adds drag-and-drop, middle and X buttons, triple clicks, modifier-clicks and structured chords such as 'ctrl+shift+t', which are more reliable for LLMs than SendKeys punctuation. Press-and-hold exists only within a… From M3.F28: drag and mouse_down take the same `space` and `captureId` parameters as click, through InputSimulationTools.Point, with the point rechecked before every press. |
| M4.4 | type_text paste mode and bounded long text | ⏳ Pending | P2 | M | Long or multi-line text typed key by key is slow, and autocomplete and auto-indent corrupt it. A runaway type_text cannot be bounded until a paste path exists. |

### M5: Browser bridge

*Reliable CDP for debug-enabled Chromium, bound to the right window and tab, with a few high-value tools: readiness, tabs, navigation, waits and paged content. The UIA fallback is locale-independent, and anything richer is handed to Playwright MCP explicitly. After M2 this milestone can run in parallel with M3/M4.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M5.1 | Debug-browser launch readiness and first-run suppression | ⏳ Pending | P1 | S | launch_browser_with_debug reports success without checking that the endpoint answers. Fresh profiles hit welcome, default-browser and search-engine-choice screens. Per-user installs are missed. |
| M5.2 | CDP transport seam, persistent sessions and explicit errors | ⏳ Pending | P1 | M | CdpService builds HttpClient and ClientWebSocket inline and uses one-shot evaluates with a hard-coded id of 1, so the hang and matching bugs cannot be tested. Make the transport testable and every failure explicit. |
| M5.3 | HWND-to-target binding and automatic port discovery | ⏳ Pending | P1 | M | The agent should not need to know cdpPort, and tab, navigation and wait tools all need a deterministic mapping from a window to its active tab. |
| M5.4 | Debug-browser lifecycle: reuse, named profiles, close and cleanup | ⏳ Pending | P2 | M | Re-running setup fails with PortInUse, profiles are either throwaway or raw paths, and nothing closes the browser or deletes the cookies left in %TEMP%. |
| M5.5 | Tabs, navigation with a load wait, and browser waits | ⏳ Pending | P1 | L | Opening a URL in a specific window and knowing when it has loaded takes five or six fragile calls today, and tabs cannot be listed or switched without blind keystrokes. |
| M5.6 | Paged, scoped, structured browser content | ⏳ Pending | P1 | S | get_browser_content returns multi-MB outerHTML by default, measures ContentLength after truncation and has no paging. Content should be bounded, pageable, structured and marked untrusted. |
| M5.7 | Locale-independent UIA browser fallback | ⏳ Pending | P1 | M | The omnibox lookup relies on English names. A page titled 'Address bar' returns the window title as the URL. Display text is returned unvalidated, and the browser window cannot be found when the agent's terminal is in… |
| M5.8 | Thin DOM bridge and explicit Playwright MCP handoff | ⏳ Pending | P2 | M | Add a few CDP primitives that connect the DOM to the visible desktop window and act without needing the foreground, then point agents to Playwright MCP for full DOM automation. |

### M6: Platform, efficiency and breadth

*Fewer, leaner tools and fewer round trips through consolidation and run_steps. Coverage for surfaces UIA misses (OCR, visual change detection), packaged apps, richer process, monitor and z-order control, layout schema v2 and MCP resources and prompts. Most of these sit behind opt-in toolsets.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M6.1 | Tool consolidation and measured token budgets | ⏳ Pending | P2 | M | Overlapping tools make tool selection less reliable, and default payloads are unbounded. Merge the duplicates using short deprecation windows, and enforce the budgets measured in the contract tests. |
| M6.2 | run_steps: bounded, guarded step sequences | ⏳ Pending | P2 | L | Form filling and menu navigation cost one round trip per step, and focus can drift between any two of them. A bounded batch with one guard and stop-on-error cuts round trips about five-fold. |
| M6.3 | OCR: read_screen_text and find_text_on_screen | ⏳ Pending | P2 | M | When UIA is empty (canvas apps, games, RDP/Citrix, rendered PDFs), OCR provides text-based targeting and cheap text verification with no vision tokens. |
| M6.4 | Visual change detection | ⏳ Pending | P2 | M | Cheap checks that cost no image tokens: did the screen change after an action, and has rendering settled. |
| M6.5 | Packaged app launch by AUMID and installed-app discovery | ⏳ Pending | P2 | L | Calculator, Notepad, Terminal, Teams, the new Outlook and Spotify cannot be launched with a usable PID or window, and friendly names do not resolve to anything launchable. |
| M6.6 | Richer list_processes and elevation fields | ⏳ Pending | P2 | S | WindowCount is 0 whenever includeWindowless is true, and results lack windows, parent, start time and elevation, so 'which window belongs to this app' takes extra calls. |
| M6.7 | Monitor addressing, percentage placement, z-order and topmost | ⏳ Pending | P2 | M | Users say 'the left monitor', and enumeration order shifts when docking. Agents also need to pin a reference window or raise one above a topmost overlay without stealing focus. |
| M6.8 | Layout schema v2 and agent-authored layouts with dry run | ⏳ Pending | P2 | L | Presets cannot record window state or monitor identity, so they break after docking. The agent can only create a layout by arranging real windows and cannot preview what apply would move. |
| M6.9 | MCP resources and prompts | ⏳ Pending | P3 | S | After the SDK upgrade, expose read-mostly state as resources and common recipes as prompts, for clients that use them. |

### M7: Later

*Valuable but deferred: a modern capture backend and visual aids, event-driven window tracking, virtual-desktop detection, opt-in takeover detection, best-effort workspace restore, and a scenario harness that measures round trips.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M7.1 | Windows.Graphics.Capture backend, cursor, grid and zoom | ⏳ Pending | P3 | L | PrintWindow returns black frames for some DirectComposition, WinUI and UWP surfaces, and its fallback captures whatever window is covering the target. The model also cannot see the cursor or a coordinate grid. |
| M7.2 | Window event journal | ⏳ Pending | P3 | L | Answer 'did a dialog pop up, did focus move, did the title change' in one cheap call, catch transient popups that polling misses, and let waits wake on events. |
| M7.3 | Virtual desktop awareness (detection only) | ⏳ Pending | P3 | S | Windows on other virtual desktops show up unmarked, and snapping or capturing them silently does nothing useful. |
| M7.4 | User-takeover detection (opt-in, warning-first) | ⏳ Pending | P3 | S | Long injections keep going after the user grabs the mouse. This complements the M1.10 pause hotkey, which remains the primary control. |
| M7.5 | Workspace snapshot and restore (best effort) | ⏳ Pending | P3 | L | 'Restore my morning workspace' should reopen the right files, folders and URLs, not only the window rectangles. |
| M7.6 | Scenario evaluation harness with a round-trip metric | ⏳ Pending | P3 | M | A repeatable suite of real desktop tasks that measures success rate, tool calls per task and response size, so automation improvements can be quantified. |

### Non-goals

- Do not reimplement Playwright-style DOM automation (CSS/ARIA selectors, form filling, uploads, network interception, multi-engine support) in CdpService. Keep a thin CDP bridge that ties the DOM to the visible desktop window, and hand off to Playwright MCP with --cdp-endpoint.
- Do not bundle a vision grounding model (OmniParser, UI-TARS, GroundingDINO) or a custom OCR engine, and do not run an LLM loop inside the server. The client model plans; the server supplies UIA refs, Windows.Media.Ocr, Set-of-Marks and verified actions.
- Do not add generic shell, PowerShell, registry, filesystem or HTTP tools (Windows-MCP style). They duplicate the host's own tools and greatly widen the prompt-injection surface of a server that already injects input.
- Do not bypass UIPI, the foreground lock or the secure desktop: no uiAccess manifest, no elevated-by-default server, no SYSTEM helper service, no DLL injection, no driver-level input. Detect and report TargetElevated/DesktopUnavailable. The only elevation path is an explicit runAsAdmin flag that a human approves through UAC.
- Do not try to inject Ctrl+Alt+Del or Win+L, or to drive UAC or the secure desktop.
- Do not use undocumented virtual-desktop COM interfaces (IVirtualDesktopManagerInternal) to switch desktops or move other processes' windows between them; MoveWindowToDesktop returns E_ACCESSDENIED for foreign windows anyway. Detection only.
- Do not build a YAML workflow engine or macro recorder, and never record the user's live keyboard and mouse with global hooks to build macros. run_steps is a bounded, guarded batch, and durable replayable workflows belong in the client. The opt-in takeover hook records timestamps only.
- Do not run persistent or triggered background layouts inside the stdio server. Its lifetime is tied to one client session, duplicate servers would fire twice, and a message-only window cannot receive WM_DISPLAYCHANGE. Revisit only if a long-lived host appears.
- Do not add a headless mode to launch_browser_with_debug. A headless browser has no HWND, so this server's window, UIA and capture tools cannot reach it.
- Do not add one tool per UIA pattern (invoke_element, toggle_element, select_item and so on), and do not keep deprecated aliases indefinitely. Use one element_action, one-release deprecation windows and a curated default toolset to keep the per-turn schema budget down.
- Do not promise lossless clipboard save and restore, or a full multi-tab session restore. Delay-rendered clipboard formats and browsers without a debug port make both best effort, so document the limits.
- Do not do a wholesale UIA3/FlaUI rewrite. Swap engines only behind the IUiaTree seam, after the managed traversal fixes and fake-tree tests exist, and keep UIA2 as a fallback.
- Do not build RDP-loopback 'picture-in-picture' agent desktops (UFO2 style). Target guards, the pause hotkey and takeover detection address input contention at proportionate cost.
- Do not make PostMessage/SendMessage control input (WM_CHAR, WM_LBUTTONDOWN, ControlSend-style) the default 'background' input path; Chromium, WPF, UWP and WinUI ignore or mishandle posted input. Use UIA patterns and CDP Input.dispatch* instead.
- Do not add an HTTP or remote transport or remote-control features. A local stdio process is the right trust boundary for a server that types into the user's desktop.
- Do not keep base64-in-JSON as the default image path, and do not reimplement Anthropic's computer-use tool one-for-one. Return MCP image content plus semantic targets, with pixel-coordinate tools as the fallback.
- Do not ship English-only dialog helpers driven by button captions. Address dialogs by AutomationId, ClassName (#32770) and patterns, so automation works on localized Windows.
- Do not pursue MSIX/ODR registration until its containment model has been validated against the broad UI access this server needs.
- Do not expose input holds that span tool calls, such as standalone key_down/mouse_down. Holds live inside one call (press_keys holdMs, drag, run_steps) and are released in a finally block.

