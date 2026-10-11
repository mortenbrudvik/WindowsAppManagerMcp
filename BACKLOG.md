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

## MCP Tools Summary (54 Tools)

| Tool Name | Category | Description |
|-----------|----------|-------------|
| `find_windows` | Window Discovery | Find windows by title, process, PID, handle |
| `get_all_windows` | Window Discovery | List all visible windows |
| `get_foreground_window` | Window Discovery | Get currently focused window |
| `get_owned_windows` | Window Discovery | Dialogs and popups a window owns, and which one blocks it |
| `get_window_info` | Window Discovery | One window by handle or selector, at compact, standard or full detail |
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
| `launch_applications_batch` | Batch Operations | Launch multiple applications |
| `get_monitors` | Monitor Info | List all displays with bounds |
| `get_primary_monitor` | Monitor Info | Get primary display details |
| `list_screens` | Screenshot | List monitors for capture |
| `take_screenshot` | Screenshot | Capture a monitor (PNG/JPEG) |
| `capture_region` | Screenshot | Capture screen region |
| `capture_window` | Screenshot | Capture a window |
| `capture_with_elements` | Screenshot | Capture window with UI element overlays |
| `get_ui_elements` | UI Automation | Get UI element tree with bounding boxes |
| `get_focused_element` | UI Automation | Get the element with keyboard focus, its window and process |
| `get_element_at_point` | UI Automation | Get the element at a screen point, its ancestors and window |
| `get_browser_url` | Browser Reading | Get the URL of a browser window (UI Automation or CDP) |
| `get_browser_content` | Browser Reading | Get the text (or HTML via CDP) of a browser page |
| `get_active_browser_url` | Browser Reading | Get the URL of the foreground browser window |
| `launch_browser_with_debug` | Browser Reading | Launch Chrome/Edge/Brave with a CDP debug port and a temporary profile |
| `click` | Input Simulation | Left click at screen coordinates, guarded, on the window named by windowHandle |
| `right_click` | Input Simulation | Right click at screen coordinates |
| `double_click` | Input Simulation | Double left click at screen coordinates |
| `mouse_move` | Input Simulation | Move cursor without clicking |
| `scroll` | Input Simulation | Turn the mouse wheel over the window named by windowHandle |
| `type_text` | Input Simulation | Type text using Unicode characters into the window named by windowHandle |
| `send_keys` | Input Simulation | Send key combinations (Ctrl+C, Enter, etc.) to the window named by windowHandle |
| `get_control_status` | Input Control | Whether the user paused input, the pause hotkey's state and held keys |
| `resume_input` | Input Control | Resume input after a pause, only when the user confirms |
| `release_all_input` | Input Control | Release stuck keys and mouse buttons |
| `launch_application` | App Launch | Launch app by path/name |
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
| Phase 12: Advanced Automation | ████░░░░░░ 33% |

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
| 12.3 | Element interaction | ⏳ Pending | P2 | Click/type on specific UI elements |
| 12.4 | Accessibility tree navigation | ⏳ Pending | P3 | Navigate and interact via UI Automation |
| 12.5 | Wait for element | ⏳ Pending | P2 | Wait until UI element appears/changes |
| 12.6 | Scroll support | ⏳ Pending | P3 | Scroll windows/elements programmatically |

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

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.1 | Bounded, cached UIA walks with partial results and compact output | ✅ Complete | P1 | M | Branch `m3-uia-engine`. One cache request per walk (children listed first; up to 200 read with their properties and pattern flags in one call, wider levels and failed calls one element at a time, so maxElements and the timeout stop mid-level), breadth first, maxElements (default 300, truncationReason MaxElements), hung-window precheck (IsHungAppWindow, SendMessageTimeout WM_NULL 500 ms: NotResponding), format=tree/flat/text. Kept a thread per read instead of one queued worker (a hung app must not delay reads of other apps). HasMoreChildren at the depth limit and capture_with_elements limits not done. |
| M3.2 | UIA3 (FlaUI) backend behind a widened seam, before refs, actions and waits | ✅ Complete | P1 | L | Branch `m3-uia-engine`. Interop.UIAutomationClient 10.19041.0 (leaner than FlaUI.UIA3, MIT, bundles into the single-file exe); Uia3Tree with 1 s connection and 5 s transaction timeouts, raw control type ids (AppBar, SemanticZoom), cached reads; IUiaTree widened with cached children, FindAll conditions, subtree cache, runtime ids and 15 pattern objects (FakeUiaTree too); WAM_UIA_ENGINE=uia3 (default)/uia2, reported by get_server_info. Gated E2E compares both engines on the fixture (not yet run). **Release blocker:** run FixtureUiaEngineTests and FixtureUiElementsTests with both engines on a real desktop before releasing with the uia3 default; if they differ, set UiaEngineOptions.Default to uia2 for that release. Events wait for M3.7/M3.8. |
| M3.3 | Chromium, Electron and WebView2 accessibility activation | ✅ Complete | P1 | M | Branch `m3-chromium-warmup`. AccessibilityWarmup: before get_ui_elements, capture_with_elements, find_ui_elements, get_focused_element and get_element_at_point read a window with a visible Chrome_RenderWidgetHostHWND (Chromium, Electron, CEF, WebView2), it asks for the page Document and polls (200-500 ms, at most 1.5 s or a third of the timeout) until it has elements that stopped changing; results carry accessibilityWarmup (Ready/Partial/NotReady with a warning); warmed windows remembered (256, dead ones dropped). frameworkId on elements (where it changes) and ancestors. launch_application forceAccessibility adds --force-renderer-accessibility only for a recognized Chromium/Electron/CEF .exe (forceAccessibilityApplied). SPI_SETSCREENREADER deferred (session-wide, not restorable after a kill). Open: a WebView2 fixture pane and a live Chromium scenario; batch items. |
| M3.4 | Element registry with stable refs and find_ui_elements | ✅ Complete | P1 | M | Branch `m3-element-refs`. IElementRegistry: refs `w<window>e<n>` (window numbers start at random per session), kept by the same element, never reused, LRU 5000, dropped with their window; resolution by live read, runtime id, then the stored selector verified against the fingerprint (ElementStale, AmbiguousElement; never another element), with an internal API for M3.6/M3.8/M3.9/M3.11. find_ui_elements (provider-side criteria through a bounded, split search with deadline and candidate limits; refs, click points, patterns, detail levels, withinRef). get_ui_elements, capture_with_elements, get_focused_element and get_element_at_point emit refs. Gated E2E FixtureElementRefsTests not yet run. |
| M3.5 | Popup-, menu- and dialog-aware element search | ⏳ Pending | P1 | M | Context menus (#32768), ComboLBox dropdowns, WPF Popups, WinUI flyouts and WebView2 popups are separate top-level windows outside the target's UIA subtree. An agent can open a menu today but cannot find 'Delete' in it. |
| M3.6 | element_action: pattern-first actions with verified post-state | ✅ Complete | P1 | L | Branch `m3-element-action`. element_action by ref or window + find_ui_elements criteria (exactly one match): pattern-first invoke, click, toggle (to a state), set_value/append_text (mode value|type|paste), select, expand/collapse, scroll_into_view, scroll (nearest scrollable ancestor), set_range_value, realize, focus, in-app window_*; double_click/right_click and fallback mouse through the guarded input path. Target checks via IInputTargetGuard.CheckTarget inside the input gate; pattern calls on IUiaActionRunner threads (InvokedPendingModal with openedPopup); before/after state and outcomeVerified; password values never read. AutoSetFocus off under UIA3. Gated E2E FixtureElementActionTests not yet run. |
| M3.7 | wait_for_window | ⏳ Pending | P1 | S | Launch-then-act and close-then-verify are the most common races. One server-side wait with a clear timeout replaces guessed sleeps and repeated find_windows calls. |
| M3.8 | wait_for_element | ⏳ Pending | P1 | M | Most flakiness in desktop automation is timing: controls not rendered yet, a dialog still opening, a button that enables only after validation. One blocking call that returns the final state replaces polling full tree… |
| M3.9 | Read element text and tables (TextPattern, ValuePattern, Grid/Table) | ⏳ Pending | P1 | M | Reading the Outlook message list or reading pane, Explorer's details view, Task Manager or a Notepad or Word document currently means dumping trees (Name only) or taking screenshots. |
| M3.10 | Realize virtualized list and tree items | ⏳ Pending | P2 | M | Outlook, Explorer, Settings and long WPF/WinUI lists virtualize, so off-screen items do not exist in the UIA tree and find reports a miss for an item that is really there. |
| M3.11 | Capture registry, image-space clicks and Set-of-Marks tied to element refs | ⏳ Pending | P1 | M | The server, not the model, should convert image coordinates to screen coordinates, and it should reject stale captures. Numbered marks are the same refs element_action accepts, so the agent can pick a target visually… |
| M3.12 | get_desktop_state: one-call observation | ⏳ Pending | P1 | M | Getting oriented takes three or four calls today, two of them heavy. One compact observation with actionable refs becomes the recommended first call. |
| M3.13 | Launch-and-arrange in one call | ⏳ Pending | P1 | M | Starting an app and placing it takes four calls today, and the agent races the app's own restore of its saved bounds. One call should launch, place, focus and confirm. |
| M3.14 | Common file dialog helper (localization-safe) | ⏳ Pending | P2 | M | Open and Save As dialogs appear in almost every end-to-end task, and composing them from element actions costs 4-6 error-prone calls. |

### M4: Input vocabulary

*Keyboard and mouse produce exactly what was asked, on any keyboard layout and button configuration. The tools cover the chords, drags, multi-clicks, modifier-clicks and long text that real tasks need, and nothing can stay held down after a call returns.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M4.1 | Keyboard engine: layout-aware mapping and a full key grammar | ⏳ Pending | P1 | M | Ctrl or Alt plus punctuation never fires, 'Hello' types 'hello', AZERTY digits come out wrong, '100%{ENTER}' sends Alt+Enter, and scan codes are always 0. There are also no escapes, groups, Win modifier or numpad keys. |
| M4.2 | Mouse correctness: atomic absolute clicks, swapped buttons, real double-click | ⏳ Pending | P1 | S | Today a click is SetCursorPos, a 10 ms sleep, then relative button events, so it can land wherever the cursor drifted. Swapped buttons invert click and right_click, and a double-click can exceed the double-click time. |
| M4.3 | mouse_click, drag and press_keys, with no cross-call holds | ⏳ Pending | P1 | M | Adds drag-and-drop, middle and X buttons, triple clicks, modifier-clicks and structured chords such as 'ctrl+shift+t', which are more reliable for LLMs than SendKeys punctuation. Press-and-hold exists only within a… |
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

