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
| 1.2 | Add NuGet packages (MCP SDK, Hosting, DI) | ✅ Complete | ModelContextProtocol 0.5.0-preview.1 |
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

## MCP Tools Summary (38 Tools)

| Tool Name | Category | Description |
|-----------|----------|-------------|
| `find_windows` | Window Discovery | Find windows by title, process, PID, handle |
| `get_all_windows` | Window Discovery | List all visible windows |
| `get_foreground_window` | Window Discovery | Get currently focused window |
| `move_window` | Window Control | Move window to x,y position |
| `resize_window` | Window Control | Resize window dimensions |
| `set_window_bounds` | Window Control | Move and resize in one operation |
| `set_window_state` | Window Control | Minimize, maximize, restore |
| `focus_window` | Window Control | Bring window to foreground |
| `close_window` | Window Control | Close window gracefully (WM_CLOSE) |
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
| `click` | Input Simulation | Left click at screen coordinates |
| `right_click` | Input Simulation | Right click at screen coordinates |
| `double_click` | Input Simulation | Double left click at screen coordinates |
| `mouse_move` | Input Simulation | Move cursor without clicking |
| `type_text` | Input Simulation | Type text using Unicode characters |
| `send_keys` | Input Simulation | Send key combinations (Ctrl+C, Alt+F4, etc.) |
| `launch_application` | App Launch | Launch app by path/name |
| `list_processes` | App Launch | List running processes |
| `kill_process` | App Launch | Terminate process by PID (with safeguards) |
| `list_layouts` | Layout Presets | List saved presets |
| `get_layout` | Layout Presets | Get preset details |
| `save_layout` | Layout Presets | Save current arrangement |
| `apply_layout` | Layout Presets | Apply saved preset |
| `delete_layout` | Layout Presets | Delete a preset |

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
   Build command: `dotnet publish src/WindowsAppManagerMcp -c Release -r win-x64 --self-contained -o ./publish`

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
| 8.3 | Build exe to `bin/` directory | ✅ Complete | `dotnet publish -o ./bin` |
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
│   └── WindowsAppManagerMcp.exe # Pre-built self-contained executable
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
| 9.2 | Auto-build dist binaries on `/commit` | ✅ Complete | Ensures `dist/` binary always matches source |

### /commit Workflow

The `/commit` skill automates the release process:

1. **Gather Information** - Version number and description
2. **Update Version Numbers** - plugin.json, marketplace.json
3. **Build Release Binary** - `dotnet publish` to dist/
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
