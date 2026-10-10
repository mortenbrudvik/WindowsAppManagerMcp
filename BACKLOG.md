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

---

## Phase 13: Automation Roadmap

*From the automated feature review on 2026-10-10. Two independent multi-agent review runs, with adversarial verification and a critic pass in each, were consolidated into one roadmap. The review found 120 distinct verified defects (2 critical, 19 high, 62 medium, 37 low).*

Both reviews reach the same diagnosis of WindowsAppManagerMcp. The architecture is sound: services behind interfaces, DI, mockable native wrappers, per-monitor DPI, structured Success/ErrorCode results, and UIA and CDP read paths. It is not yet safe or reliable for an agent to drive a Windows task from start to finish. Five problems stand out. (1) The model cannot see screenshots, because they arrive as base64 inside a JSON text block. (2) Keyboard and mouse input has no target and no verified activation, so it can land in the agent's own terminal; the test suite already did this with Alt+F4. (3) Many tools report success after doing the wrong thing or nothing at all: maximize on the wrong monitor, unknown states treated as restore, save_layout always true, CDP reads from the wrong tab, and a controlTypes filter that drops nested matches. (4) Headline features are broken: a stale plugin binary, an unserializable debug-launch result, and no published release. (5) Everything is addressed by raw HWND plus hand-computed physical pixels, with no waits and no element actions, so a simple form fill costs 4-10 round trips that cannot be verified. The plan works in layers. M0, now complete, stops the bleeding. M1 makes seeing and typing safe by default. It starts with a default host-window input guard, ships image content together with the true capture origin, and adds verified activation, guarded input with receiving-window reporting, and a desktop-safe fixture e2e suite to validate the Windows-specific behaviour. M2 makes results honest and settles the MCP surface (contract tests, annotations, stable SDK, curated toolsets) before M3-M5 add about 30 tools. It also pulls scroll and clipboard forward. M3 is the biggest lever. It moves from blind coordinates to selector-addressed, pattern-first UIA actions with stable refs, popup-aware search, Chromium accessibility activation, text and table reading, virtualized items and server-side waits, each returning verified post-state. That cuts typical tasks to 1-2 calls that can each be checked. M4-M7 complete the input vocabulary, build a thin browser bridge with a Playwright handoff, add breadth, and hold the deferred items. Design assumptions were checked against the repo. ModelContextProtocol 0.5.0-preview.1 already exposes McpServerOptions.Filters.CallToolFilters and McpServer.ElicitAsync, so the M0 exception filter and M2 elicitation can be built on the current SDK. ProcessStartInfo.ArgumentList is honored with UseShellExecute=true on .NET 10; re-check this on the net8 target. The main residual risks are Windows-behaviour details the critics flagged: the foreground-lock unlock, popup hit-testing, InvokePattern blocking while its modal is open, Edge title formats, and stale element refs. That is why the fixture e2e suite sits in M1, ahead of the large feature work.

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

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M1.1 | Default host-window input guard | ⏳ Pending | P0 | S | Input that names no target still goes to the focused window, which is usually the agent's own terminal. A cheap HostGuard on the default input path makes that impossible unless the agent explicitly targets the host. |
| M1.2 | Captures as MCP image content with the true origin and an image-to-screen transform | ⏳ Pending | P0 | M | Return every capture as an image content block the model can see, with vision-sized defaults. Ship the corrected window-capture origin and transform in the same change: downscaled images on top of a (0,0) origin would… |
| M1.3 | Verified window activation (EnsureForeground) | ⏳ Pending | P0 | M | One routine that gets a window in front despite the foreground lock and reports what is actually in front afterwards. focus_window, the input guard, element-action fallbacks and layout focus all use it. |
| M1.4 | Integrity, elevation and desktop-state probes | ⏳ Pending | P0 | S | A small mockable service that answers three questions: is the target more privileged than the server, is the input desktop available, and is the session locked. Without it, UIPI and secure-desktop failures are invisible… |
| M1.5 | Target-guarded input with receiving-window reporting | ⏳ Pending | P0 | M | Every mouse and keyboard tool can name its intended window. The server refuses to send input that would land elsewhere, including clicks into a modal-disabled window. Every result reports where the input actually went. |
| M1.6 | Unfiltered window reads and user-facing listings | ⏳ Pending | P0 | M | Post-action reads, get_foreground_window and the guard must be able to see untitled popups, menus and splash screens. Listings must stop showing phantom cloaked or shell windows and must attribute UWP windows to the… |
| M1.7 | Close guards for shell and agent-host windows | ⏳ Pending | P0 | S | close_window today posts WM_CLOSE to Program Manager (which opens the Shut Down dialog) or to the agent's own terminal after nothing more than an IsWindow check. |
| M1.8 | Fix the UIA traversal filters behind a testable tree seam | ⏳ Pending | P0 | M | controlTypes:'Button,Edit' returns nothing with Success=true, size and enabled filters prune whole subtrees, and maxDepth is off by one. None of this can be unit-tested today. |
| M1.9 | get_focused_element and get_element_at_point | ⏳ Pending | P1 | S | The cheapest checks before typing ('is the caret in the Email field?') and the cheapest way to map a screenshot point to a semantic element. Pulled forward because the observe-act-verify guidance relies on them. |
| M1.10 | Input session safety: pause hotkey, cancellation and stuck-input recovery | ⏳ Pending | P1 | M | Give the user a guaranteed way to stop a runaway action. Make long input cancellable, and make sure an interrupted batch can never leave modifiers or buttons held down system-wide. |
| M1.11 | Desktop-safe end-to-end tests against a self-owned fixture app | ⏳ Pending | P1 | M | After M0.1, nothing tests the real foreground, UIPI, SendInput, capture and UIA paths, where Windows behaviour differs from mocks. A fixture app that the suite owns makes these tests safe to run. |
| M1.12 | Server-enforced target allow/deny policy | ⏳ Pending | P2 | S | The agent acts on untrusted screen and web content, and the model sets its own confirm flags. A server-side policy stops a prompt-injected agent from typing into password managers, credential prompts or other sensitive… |

### M2: Honest results and a stable MCP surface

*Every tool either does what it reports or says precisely why not: typed parameters, real post-state, verified close, handoff-aware launch and faithful layouts. The MCP surface (contract tests, generated docs, annotations, stable SDK, curated toolsets, server instructions) is settled before M3-M5 add about 30 tools. Scroll and clipboard, the two primitives agents miss first, are pulled forward.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M2.1 | Honest error contract: isError, no null results, per-item batch errors | ⏳ Pending | P1 | M | Results with Success=false go out with isError=false, null results arrive as empty content, and one bad batch item aborts the whole batch with an NRE. |
| M2.2 | Typed parameters and strict validation with real outcomes | ⏳ Pending | P1 | S | Unknown state, positioning, matchBy and monitorIndex values silently fall back to a different action that is then reported as success. Native failures carry no reason. |
| M2.3 | Correct, verified and hang-proof geometry operations | ⏳ Pending | P1 | L | Fixes several placement defects: maximize on the wrong monitor, moving or snapping a maximized window, minimized windows shrinking to 160x28, gaps from the invisible DWM frame, double DPI scaling, and calls that hang on… |
| M2.4 | Window selectors, ambiguity errors and get_window_info | ⏳ Pending | P1 | M | Every tool needs a raw HWND, matching is substring-only, and layouts silently pick windows[0]. A shared selector resolves to exactly one window or returns an explicit error. |
| M2.5 | Verified close, owned-dialog discovery and close_application | ⏳ Pending | P1 | M | close_window cannot tell 'closed' from 'still open' from 'blocked by a Save changes? prompt'. There is no graceful app-level close. |
| M2.6 | Handoff-aware launch-to-window resolution | ⏳ Pending | P1 | M | Launches that hand off to another process return no window after the full timeout. This covers browsers, calc to CalculatorApp, code, explorer and Firefox. Single-instance and tabbed apps (Win11 Notepad, Terminal, VS… |
| M2.7 | Launch path hygiene and protocol allowlist | ⏳ Pending | P1 | S | Bare names are resolved against the server's working directory first (binary planting), and file:// skips every check. Settings pages and Store apps cannot be launched at all. The ms-settings: and shell:AppsFolder… |
| M2.8 | Faithful layout apply with one-to-one matching and per-placement results | ⏳ Pending | P1 | M | Today N Chrome windows collapse onto one placement, an empty matcher matches every window, apply reports success when nothing matched, and a freshly launched untitled window causes an NRE. Recording a few identity… |
| M2.9 | Capture health: never drop pixels, typed capture errors and warnings | ⏳ Pending | P1 | M | capture_with_elements drops the image on any UIA failure. Minimized, cloaked, hung, blank or off-screen captures all report Success=true. GDI misuse costs extra copies and can corrupt pixels. |
| M2.10 | Tool annotations, titles and human confirmation through elicitation | ⏳ Pending | P1 | S | No tool sets ReadOnly, Destructive, Idempotent, OpenWorld or Title, so clients must treat find_windows like kill_process. Irreversible actions rely on a confirm flag that the model sets for itself. |
| M2.11 | Server instructions, get_server_info and an observe-act-verify skill | ⏳ Pending | P1 | S | Clients without the plugin get no guidance at all. Nothing reports the build, integrity level, DPI mode, host windows or pause state. SKILL.md needs a rewrite around verified actions. |
| M2.12 | In-process MCP contract tests, shared DI and generated docs | ⏳ Pending | P1 | M | Test the server the way an agent sees it, over MCP. Snapshot the tool surface, enforce payload budgets and catch stale binaries. Generate the tool tables in the docs so they stop drifting. This is the safety net for… |
| M2.13 | Upgrade the MCP SDK to stable; cancellation and progress everywhere | ⏳ Pending | P1 | M | Move off ModelContextProtocol 0.5.0-preview.1 right after the contract tests exist, before M3-M5 add about 30 tools. Porting 42 tools now is cheaper than porting about 75 later. |
| M2.14 | Toolsets with a curated default and a read-only mode | ⏳ Pending | P1 | S | This roadmap grows the surface from 42 to about 70-75 tools, and every schema sits in context on every turn. A curated core of about 20-25 tools by default, with everything else opt-in, keeps tool selection reliable. |
| M2.15 | scroll primitive (pulled forward) | ⏳ Pending | P1 | S | Most list items in Settings, Explorer or Outlook cannot be reached without scrolling. The wheel constants already exist, and scroll needs only the M1 guard, not the M4 keyboard rewrite. |
| M2.16 | Clipboard get/set with a privacy guard (pulled forward) | ⏳ Pending | P1 | S | Copy-then-read is the cheapest way to extract text from apps with poor UIA (terminals, Electron, canvases, RDP). Reading the user's clipboard could leak passwords, so reads are guarded. |
| M2.17 | Audit log of injected input and destructive actions | ⏳ Pending | P2 | S | When an action does damage, neither the user nor the agent can reconstruct what was sent where. A cheap append-only log builds trust in a server that types into the desktop. |

### M3: Semantic find-act-wait

*The biggest lever. Move from blind coordinates to selector-addressed, pattern-first UI Automation, so 'click Save in app X' becomes 1-2 calls that can each be verified. Elements are found by name, automationId and type, including in menus, popups, dialogs, Chromium/Electron apps and virtualized lists. Agents act through UIA patterns with verified post-state, read text and tables directly, and wait server-side instead of sleeping.*

| # | Task | Status | Priority | Effort | Notes |
|---|------|--------|----------|--------|-------|
| M3.1 | Bounded, cached UIA walks with partial results and compact output | ⏳ Pending | P1 | M | Each element currently costs 8-10 uncached cross-process calls, a timeout throws away everything, and payloads are unbounded. Make reads fast, bounded and useful even when incomplete. |
| M3.2 | UIA3 (FlaUI) backend behind a widened seam, before refs, actions and waits | ⏳ Pending | P1 | L | The managed client has no per-call timeouts, cannot read newer control-type ids and has no LegacyIAccessible. Building refs, patterns, events and waits on it and porting them later doubles the work, so swap the engine… |
| M3.3 | Chromium, Electron and WebView2 accessibility activation | ⏳ Pending | P1 | M | VS Code, Slack, Teams, Discord, the new Outlook and WebView2 panes build their web accessibility tree lazily. The first query sees an empty Chrome_RenderWidgetHostHWND, and the agent wrongly concludes the control is… |
| M3.4 | Element registry with stable refs and find_ui_elements | ⏳ Pending | P1 | M | Search on the server by name, automationId, controlType and className, and get back refs that later calls can use. Stale or ambiguous refs return explicit errors, and a ref never silently resolves to a different element. |
| M3.5 | Popup-, menu- and dialog-aware element search | ⏳ Pending | P1 | M | Context menus (#32768), ComboLBox dropdowns, WPF Popups, WinUI flyouts and WebView2 popups are separate top-level windows outside the target's UIA subtree. An agent can open a menu today but cannot find 'Delete' in it. |
| M3.6 | element_action: pattern-first actions with verified post-state | ⏳ Pending | P1 | L | Act on elements through UIA patterns, which work on background and occluded windows without fighting the foreground lock, and fall back to a guarded mouse click only when needed. Each call returns the before and after… |
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

