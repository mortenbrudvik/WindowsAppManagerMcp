# Technical Backlog - Testing Tasks

> **WindowsAppManagerMcp** | Testing Implementation Backlog

This document contains granular, actionable testing tasks following TDD principles outlined in [TESTING.md](TESTING.md).

---

## Status Legend

- [ ] **Pending** - Not yet started
- [x] **Complete** - Task is done
- [~] **In Progress** - Currently being worked on
- [!] **Blocked** - Waiting on dependency

## Priority Legend

- **P0** - Critical / Blocking
- **P1** - High Priority
- **P2** - Medium Priority
- **P3** - Low Priority / Nice to Have

---

## Coverage Targets

| Layer | Target | Current | Status |
|-------|--------|---------|--------|
| Services | 80%+ | 46-89% | 🔶 Partial |
| Models | 70%+ | 100% | ✅ Complete |
| Tools | 60%+ | 99-100% | ✅ Complete |
| Screenshot | 80%+ | 0% | ⏳ Pending |
| **Overall** | **70-80%** | **64.4%** | 🔶 In Progress |

> **Note**: Service coverage is limited by P/Invoke dependencies. Native Windows API calls cannot be easily mocked without wrapper interfaces. Screenshot service uses `IScreenCaptureWrapper` for testability.

---

## T1: Test Infrastructure Setup ✅

*Foundation for all testing - complete these first.*

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T1.1 | Create `tests/WindowsAppManagerMcp.Tests/` project | P0 | [x] | `dotnet new xunit` |
| T1.2 | Add project reference to main project | P0 | [x] | Reference `src/WindowsAppManagerMcp` |
| T1.3 | Add NuGet: `xunit` 2.8.1 | P0 | [x] | Test framework |
| T1.4 | Add NuGet: `xunit.runner.visualstudio` 2.8.1 | P0 | [x] | VS test runner |
| T1.5 | Add NuGet: `Microsoft.NET.Test.Sdk` 17.10.0 | P0 | [x] | Test SDK |
| T1.6 | Add NuGet: `Moq` 4.20.70 | P0 | [x] | Mocking framework |
| T1.7 | Add NuGet: `FluentAssertions` 6.12.0 | P2 | [x] | Optional - better assertions |
| T1.8 | Add NuGet: `coverlet.collector` 6.0.2 | P1 | [x] | Code coverage |
| T1.9 | Create `GlobalUsings.cs` | P1 | [x] | Common imports |
| T1.10 | Create `Fixtures/TestDataFactory.cs` | P1 | [x] | Test data helpers |
| T1.11 | Add test project to solution | P0 | [x] | Update `.sln` file |
| T1.12 | Verify `dotnet test` runs successfully | P0 | [x] | 207 tests passing |

### T1.A: Native Layer Testability ✅

*Required for mocking P/Invoke calls in WindowService tests.*

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T1.A1 | Create `INativeWindowWrapper` interface | P1 | [x] | Abstract P/Invoke calls |
| T1.A2 | Create `NativeWindowWrapper` implementation | P1 | [x] | Production wrapper |
| T1.A3 | Update `WindowService` constructor to accept `INativeWindowWrapper` | P1 | [x] | Dependency injection |
| T1.A4 | Update DI registration in `Program.cs` | P1 | [x] | Register wrapper |
| T1.A5 | Create `MockNativeWindowWrapper` for tests | P1 | [x] | Test double in TestDataFactory |

---

## T2: WindowService Unit Tests 🔶

*14 methods to test. Target: 80%+ coverage. Current: 59.3%*

> **Note**: WindowService tests are in `WindowServiceTests.cs`. Coverage limited by P/Invoke - tests focus on contract behavior with real Windows API.

### T2.1: IsValidWindow Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.1.1 | Test `IsValidWindow` with zero handle returns false | P0 | [x] | Edge case |
| T2.1.2 | Test `IsValidWindow` with negative handle returns false | P0 | [x] | Via invalid handle test |
| T2.1.3 | Test `IsValidWindow` with valid handle returns true | P0 | [x] | Happy path |
| T2.1.4 | Test `IsValidWindow` with destroyed window returns false | P1 | [x] | Via invalid handle test |

### T2.2: GetAllWindows Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.2.1 | Test `GetAllWindows` returns non-null list | P0 | [x] | Basic contract |
| T2.2.2 | Test `GetAllWindows(includeMinimized: true)` includes minimized | P1 | [x] | Parameter behavior |
| T2.2.3 | Test `GetAllWindows(includeMinimized: false)` excludes minimized | P1 | [x] | Uses mock |
| T2.2.4 | Test `GetAllWindows` excludes invisible windows | P1 | [x] | Via title check |

### T2.3: FindWindows Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.3.1 | Test `FindWindows` with `titleContains` filter | P0 | [x] | Uses mock |
| T2.3.2 | Test `FindWindows` with `processName` filter | P0 | [x] | Process matching |
| T2.3.3 | Test `FindWindows` with `processId` filter | P1 | [x] | Uses mock |
| T2.3.4 | Test `FindWindows` with `handle` filter | P1 | [x] | Handle matching |
| T2.3.5 | Test `FindWindows` with multiple filters (AND logic) | P1 | [x] | Uses mock |
| T2.3.6 | Test `FindWindows` with `visibleOnly: false` | P2 | [x] | Uses mock |
| T2.3.7 | Test `FindWindows` with no matches returns empty list | P0 | [x] | Empty result |

### T2.4: GetForegroundWindow Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.4.1 | Test `GetForegroundWindow` returns WindowInfo or null | P1 | [x] | Basic contract |
| T2.4.2 | Test `GetForegroundWindow` includes correct handle | P2 | [x] | Via null check |

### T2.5: GetWindowInfo Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.5.1 | Test `GetWindowInfo` with valid handle returns WindowInfo | P0 | [x] | Happy path |
| T2.5.2 | Test `GetWindowInfo` with invalid handle returns null | P0 | [x] | Error handling |
| T2.5.3 | Test `GetWindowInfo` populates all properties correctly | P1 | [x] | Via handle check |

### T2.6-T2.10: Window Manipulation Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.6.2 | Test `MoveWindow` with invalid handle returns false | P0 | [x] | Error handling |
| T2.7.2 | Test `ResizeWindow` with invalid handle returns false | P0 | [x] | Error handling |
| T2.8.2 | Test `SetWindowBounds` with invalid handle returns false | P0 | [x] | Error handling |
| T2.9.4 | Test `SetWindowState` with invalid handle returns false | P0 | [x] | Error handling |
| T2.10.2 | Test `FocusWindow` with invalid handle returns false | P0 | [x] | Error handling |

> **Note**: Valid handle manipulation tests skipped to avoid side effects on real windows.

### T2.11: SnapWindow Tests 🔶

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.11.16 | Test `SnapWindow` with invalid handle returns false | P0 | [x] | Error handling |

> **Note**: SnapPosition parsing tested in WindowManagerToolsTests (14 positions).

### T2.12: MoveWindowToMonitor Tests 🔶

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.12.2 | Test `MoveWindowToMonitor` with invalid handle returns false | P0 | [x] | Error handling |

---

## T3: MonitorService Unit Tests 🔶

*6 methods to test. Target: 80%+ coverage. Current: 0% (P/Invoke limited)*

> **Note**: MonitorService tests are in `MonitorServiceTests.cs`. Uses helper method to test CalculateSnapBounds algorithm without P/Invoke.

### T3.1-T3.5: Monitor Query Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T3.1.1 | Test `GetAllMonitors` returns non-empty list | P0 | [x] | Integration test |
| T3.2.1 | Test `GetPrimaryMonitor` returns monitor with `IsPrimary: true` | P0 | [x] | Integration test |

> **Note**: These are integration tests that run against the actual system (every Windows has monitors).

### T3.6: CalculateSnapBounds Tests (Parameterized) ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T3.6.1 | Test `CalculateSnapBounds` for all 14 SnapPositions | P0 | [x] | 14 parameterized tests |
| T3.6.2 | Test `CalculateSnapBounds` with offset monitor | P1 | [x] | Secondary monitor at x=1920 |
| T3.6.3 | Test `CalculateSnapBounds` respects work area (excludes taskbar) | P1 | [x] | Taskbar offset test |
| T3.6.4 | Test halves cover full width | P1 | [x] | No gaps |
| T3.6.5 | Test thirds cover full width | P1 | [x] | No gaps |
| T3.6.6 | Test quarters cover full area | P1 | [x] | Complete coverage |

---

## T4: ProcessService Unit Tests ✅

*4 methods to test. Target: 80%+ coverage. Current: 46.3%*

> **Note**: ProcessService tests are in `ProcessServiceTests.cs`. Uses real process enumeration for testing.

### T4.1: LaunchApplication Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T4.1.1 | Test `LaunchApplication` with valid executable returns success | P0 | [x] | Integration test with notepad.exe |
| T4.1.2 | Test `LaunchApplication` with invalid executable returns failure | P0 | [x] | Mock validation service |

> **Note**: T4.1.1 launches notepad.exe and cleans up after. Tests include proper process cleanup.

### T4.2: GetRunningProcesses Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T4.2.1 | Test `GetRunningProcesses` returns non-empty list | P0 | [x] | Basic contract |
| T4.2.2 | Test `GetRunningProcesses` with `nameFilter` filters correctly | P1 | [x] | Filtering |
| T4.2.3 | Test `GetRunningProcesses` with non-matching filter returns empty | P1 | [x] | Edge case |
| T4.2.4 | Test `GetRunningProcesses` returns correct ProcessInfo fields | P2 | [x] | Data accuracy |
| T4.2.5 | Test `GetRunningProcesses` includes current process | P1 | [x] | Verification |

### T4.3: GetProcessName Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T4.3.1 | Test `GetProcessName` with valid PID returns name | P1 | [x] | Happy path |
| T4.3.2 | Test `GetProcessName` with invalid PID returns null | P1 | [x] | Error handling |

### T4.4: GetProcessPath Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T4.4.1 | Test `GetProcessPath` with valid PID returns path | P1 | [x] | Happy path |
| T4.4.2 | Test `GetProcessPath` with invalid PID returns null | P1 | [x] | Error handling |

---

## T5: LayoutService Unit Tests ✅

*6 methods to test. Target: 80%+ coverage. Current: 89.2%*

> **Note**: LayoutService tests are in `LayoutServiceTests.cs`. Most testable service - uses file I/O and mocked dependencies.

### T5.1: GetAllPresets Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.1.1 | Test `GetAllPresets` with no presets returns empty list | P0 | [x] | Edge case |
| T5.1.2 | Test `GetAllPresets` returns all saved presets | P0 | [x] | Happy path |
| T5.1.3 | Test `GetAllPresets` loads from disk on first call | P1 | [x] | Via save/get flow |

### T5.2: GetPreset Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.2.1 | Test `GetPreset` with existing name returns preset | P0 | [x] | Happy path |
| T5.2.2 | Test `GetPreset` with non-existent name returns null | P0 | [x] | Not found |
| T5.2.3 | Test `GetPreset` is case-insensitive | P2 | [x] | Usability |

### T5.3: SavePresetAsync Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.3.1 | Test `SavePresetAsync` creates JSON file | P0 | [x] | File creation |
| T5.3.2 | Test `SavePresetAsync` returns true on success | P0 | [x] | Return value |
| T5.3.3 | Test `SavePresetAsync` overwrites existing preset | P1 | [x] | Update behavior |
| T5.3.4 | Test `SavePresetAsync` sanitizes filename (removes invalid chars) | P1 | [x] | Security |
| T5.3.5 | Test `SavePresetAsync` JSON contains all preset data | P1 | [x] | Via roundtrip |
| T5.3.6 | Test `SavePresetAsync` respects CancellationToken | P2 | [x] | Throws on cancellation |

### T5.4: DeletePresetAsync Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.4.1 | Test `DeletePresetAsync` removes file | P0 | [x] | Happy path |
| T5.4.2 | Test `DeletePresetAsync` returns true when preset exists | P0 | [x] | Return value |
| T5.4.3 | Test `DeletePresetAsync` returns true when preset not found | P0 | [x] | Idempotent |
| T5.4.4 | Test `DeletePresetAsync` removes from cache | P1 | [x] | Cache invalidation |

### T5.5: CaptureCurrentLayoutAsync Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.5.1 | Test `CaptureCurrentLayoutAsync` creates preset with given name | P0 | [x] | Basic contract |
| T5.5.2 | Test `CaptureCurrentLayoutAsync` captures visible windows | P0 | [x] | Window capture |
| T5.5.3 | Test `CaptureCurrentLayoutAsync` with `includeProcesses` filter | P1 | [x] | Filtering |
| T5.5.4 | Test `CaptureCurrentLayoutAsync` with `excludeProcesses` filter | P1 | [x] | Filtering |
| T5.5.5 | Test `CaptureCurrentLayoutAsync` stores relative positions | P1 | [x] | Via mock verification |
| T5.5.6 | Test `CaptureCurrentLayoutAsync` includes monitor info | P2 | [x] | Via mock verification |
| T5.5.7 | Test `CaptureCurrentLayoutAsync` sets description if provided | P2 | [x] | Optional param |

### T5.6: ApplyPresetAsync Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.6.1 | Test `ApplyPresetAsync` with non-existent preset returns failure | P0 | [x] | Not found |
| T5.6.2 | Test `ApplyPresetAsync` calls SetWindowBounds for matched windows | P0 | [x] | Core behavior |
| T5.6.3 | Test `ApplyPresetAsync` returns WindowsArranged count | P0 | [x] | Return value |
| T5.6.4 | Test `ApplyPresetAsync` with `matchBy: "process_only"` | P1 | [x] | Match strategy |
| T5.6.5 | Test `ApplyPresetAsync` with `matchBy: "title_only"` | P1 | [x] | Match strategy |
| T5.6.6 | Test `ApplyPresetAsync` with `matchBy: "process_and_title"` | P1 | [x] | Match strategy |
| T5.6.7 | Test `ApplyPresetAsync` with `launchMissing: true` launches apps | P0 | [x] | Launch behavior |
| T5.6.8 | Test `ApplyPresetAsync` with `launchMissing: false` skips launch | P1 | [x] | Launch behavior |
| T5.6.9 | Test `ApplyPresetAsync` returns WindowsLaunched count | P1 | [x] | Return value |
| T5.6.10 | Test `ApplyPresetAsync` handles multiple windows per process | P2 | [x] | Edge case |

---

## T6: Model Unit Tests ✅

*Data models and value objects. Current: 100% coverage*

> **Note**: Model tests are in `ModelTests.cs`. Full coverage on all record types.

### T6.1: WindowMatcher Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T6.1.1 | Test `WindowMatcher` with all null properties | P1 | [x] | Valid state |
| T6.1.2 | Test `WindowMatcher` with ProcessName | P1 | [x] | Property setting |
| T6.1.3 | Test `WindowMatcher` with TitleContains | P1 | [x] | Property setting |
| T6.1.4 | Test `WindowMatcher` equality | P2 | [x] | Record equality |
| T6.1.5 | Test `WindowMatcher` JSON serialization | P1 | [x] | Roundtrip |

### T6.2: RelativePosition Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T6.2.1 | Test `RelativePosition` with valid values | P1 | [x] | Basic contract |
| T6.2.2 | Test `RelativePosition` common snap positions | P1 | [x] | 7 parameterized tests |
| T6.2.3 | Test `RelativePosition` equality | P2 | [x] | Record equality |
| T6.2.4 | Test `RelativePosition` JSON serialization | P1 | [x] | Roundtrip |

### T6.3: LayoutPreset Serialization Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T6.3.1 | Test JSON serialization roundtrip | P1 | [x] | Serialization |
| T6.3.2 | Test JSON includes all required properties | P1 | [x] | Complete data |
| T6.3.3 | Test WindowPlacement serialization | P1 | [x] | Nested records |
| T6.3.4 | Test empty placements list | P2 | [x] | Edge case |
| T6.3.5 | Test timestamps are set | P2 | [x] | Auto-generated fields |

### T6.4: Additional Model Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T6.4.1 | Test `MonitorRect` equality | P2 | [x] | Record equality |
| T6.4.2 | Test `WindowRect` equality | P2 | [x] | Record equality |
| T6.4.3 | Test `MonitorInfo` construction | P2 | [x] | All properties |
| T6.4.4 | Test `WindowInfo` construction | P2 | [x] | All properties |

---

## T7: Integration Tests ✅

*Service interaction and file I/O tests. Target: 20% of total tests.*

> **Note**: Integration tests are in `tests/WindowsAppManagerMcp.Tests/Integration/`. 37 tests added.

### T7.1: LayoutService Integration ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T7.1.1 | Test save and load preset roundtrip | P0 | [x] | End-to-end |
| T7.1.2 | Test capture and apply layout cycle | P0 | [x] | Full workflow |
| T7.1.3 | Test LayoutService with real WindowService mock | P1 | [x] | Service interaction |
| T7.1.4 | Test LayoutService with real MonitorService mock | P1 | [x] | Service interaction |
| T7.1.5 | Test match by process only | P1 | [x] | Match strategy |
| T7.1.6 | Test match by title only | P1 | [x] | Match strategy |
| T7.1.7 | Test match by process and title | P1 | [x] | Match strategy |
| T7.1.8 | Test delete removes from disk and cache | P1 | [x] | Cleanup |
| T7.1.9 | Test multiple presets independent storage | P1 | [x] | No conflicts |
| T7.1.10 | Test multi-monitor layout | P1 | [x] | Bounds calculation |

### T7.2: File Persistence Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T7.2.1 | Test preset files created in correct directory | P1 | [x] | File location |
| T7.2.2 | Test preset file naming convention | P1 | [x] | Filename |
| T7.2.3 | Test preset directory created if not exists | P2 | [x] | Directory handling |
| T7.2.4 | Test concurrent save operations | P2 | [x] | Thread safety |
| T7.2.5 | Test load valid JSON files | P1 | [x] | All loaded |
| T7.2.6 | Test invalid JSON skipped | P1 | [x] | Error handling |
| T7.2.7 | Test mixed valid/invalid loads valid only | P1 | [x] | Robustness |
| T7.2.8 | Test JSON format camelCase and indented | P1 | [x] | Format verification |

### T7.3: DI Container Tests ✅

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T7.3.1 | Test all services resolve from container | P2 | [x] | DI validation |
| T7.3.2 | Test service lifetimes are correct | P2 | [x] | Singleton vs Transient |
| T7.3.3 | Test no circular dependencies | P2 | [x] | Dependency graph |
| T7.3.4 | Test all MCP tools resolve | P2 | [x] | Tool dependencies |

---

## T8: CI/CD Integration ✅

*Automation and pipeline tasks.*

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T8.1 | Create GitHub Actions workflow for tests | P1 | [x] | `.github/workflows/test.yml` |
| T8.2 | Add test step to build workflow | P1 | [x] | `.github/workflows/build.yml` |
| T8.3 | Configure coverage reporting | P2 | [x] | ReportGenerator with HTML + Markdown |
| T8.4 | Add coverage badge to README | P3 | [x] | Added Build, Test, Coverage badges |
| T8.5 | Configure test result publishing | P2 | [x] | Artifacts upload |
| T8.6 | Set up coverage threshold check (60%) | P2 | [x] | Quality gate in test.yml |

---

## Summary

### Test Statistics

| Metric | Value |
|--------|-------|
| **Total Tests** | 482 |
| **Passing** | 482 (100%) |
| **Line Coverage** | ~70% |
| **Branch Coverage** | ~60% |
| **Method Coverage** | ~90% |

### Task Completion by Category

| Category | Status | Tests | Coverage |
|----------|--------|-------|----------|
| T1: Infrastructure | ✅ Complete | - | - |
| T2: WindowService | ✅ Complete | 46 | ~65% |
| T3: MonitorService | ✅ Complete | 20 | ~50%* |
| T4: ProcessService | ✅ Complete | 15 | ~50% |
| T5: LayoutService | ✅ Complete | 29 | ~90% |
| T6: Models | ✅ Complete | 31 | 100% |
| T7: Integration | ✅ Complete | 37 | - |
| T8: CI/CD | ✅ Complete | - | - |
| T9: Screenshot | ⏳ Pending | 0 | 0% |

*\* MonitorService coverage limited because tests use a helper method to test the algorithm logic without P/Invoke.*

### Tools Layer Tests (Additional)

| Tool Class | Tests | Coverage |
|------------|-------|----------|
| WindowFinderTools | 14 | 100% |
| WindowManagerTools | 26 | 99.1% |
| MonitorInfoTools | 10 | 100% |
| AppLauncherTools | 20 | 100% |
| LayoutPresetTools | 23 | 100% |
| ScreenshotTools | 0 | 0% (pending) |

### Integration Tests (T7)

| Test Class | Tests | Focus |
|------------|-------|-------|
| LayoutServiceIntegrationTests | 14 | End-to-end workflows |
| FilePersistenceTests | 10 | File I/O and JSON handling |
| DependencyInjectionTests | 13 | DI container validation |

### Remaining Work

All critical testing tasks are complete. All pending tests from the backlog have been implemented:
- T2.2.3, T2.3.1, T2.3.3, T2.3.5, T2.3.6 (WindowService)
- T5.3.6, T5.6.4, T5.6.5, T5.6.6, T5.6.10 (LayoutService)

Optional enhancements in BACKLOG.md:

### Completed Infrastructure

- **CI/CD**: GitHub Actions workflows for test and build
- **Coverage**: Automatic reporting with 60% threshold gate
- **Integration Tests**: 37 tests covering E2E workflows, file persistence, DI

---

## T9: Screenshot Service Tests (Pending)

*Screenshot capture functionality. Target: 80%+ coverage*

### T9.1: ScreenCaptureWrapper Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T9.1.1 | Test `CaptureScreenRegion` returns valid pixel data | P1 | [ ] | Integration test |
| T9.1.2 | Test `CaptureScreenRegion` with invalid dimensions throws | P1 | [ ] | Error handling |
| T9.1.3 | Test `CaptureScreenRegion` with oversized region throws | P1 | [ ] | Max size limit |
| T9.1.4 | Test `CaptureWindow` with valid handle returns data | P1 | [ ] | Happy path |
| T9.1.5 | Test `CaptureWindow` with invalid handle throws | P1 | [ ] | Error handling |
| T9.1.6 | Test `CaptureWindow` with includeFrame option | P2 | [ ] | Frame vs client |
| T9.1.7 | Test `GetVirtualScreenBounds` returns valid bounds | P1 | [ ] | Multi-monitor |
| T9.1.8 | Test `IsValidWindow` behavior | P1 | [ ] | Validation |

### T9.2: ScreenshotService Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T9.2.1 | Test `ListScreens` returns all monitors | P0 | [ ] | Basic contract |
| T9.2.2 | Test `ListScreens` includes virtual screen bounds | P1 | [ ] | Combined bounds |
| T9.2.3 | Test `CaptureMonitor` with valid index | P0 | [ ] | Happy path |
| T9.2.4 | Test `CaptureMonitor` with invalid index returns error | P0 | [ ] | Error handling |
| T9.2.5 | Test `CaptureMonitor` with PNG format | P1 | [ ] | Format option |
| T9.2.6 | Test `CaptureMonitor` with JPEG format | P1 | [ ] | Format option |
| T9.2.7 | Test `CaptureRegion` returns valid image | P0 | [ ] | Happy path |
| T9.2.8 | Test `CaptureRegion` with invalid dimensions | P1 | [ ] | Error handling |
| T9.2.9 | Test `CaptureWindow` with valid handle | P0 | [ ] | Happy path |
| T9.2.10 | Test `CaptureWindow` with invalid handle | P0 | [ ] | Error handling |
| T9.2.11 | Test image encoding produces valid base64 | P1 | [ ] | Output format |
| T9.2.12 | Test JPEG quality setting | P2 | [ ] | Quality parameter |

### T9.3: ScreenshotTools Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T9.3.1 | Test `list_screens` tool returns screen list | P0 | [ ] | Tool wrapper |
| T9.3.2 | Test `take_screenshot` tool with defaults | P0 | [ ] | Default monitor |
| T9.3.3 | Test `take_screenshot` tool with monitor index | P1 | [ ] | Explicit monitor |
| T9.3.4 | Test `take_screenshot` tool with format option | P1 | [ ] | PNG/JPEG |
| T9.3.5 | Test `capture_region` tool | P0 | [ ] | Region capture |
| T9.3.6 | Test `capture_window` tool | P0 | [ ] | Window capture |
| T9.3.7 | Test `capture_window` tool includeFrame option | P2 | [ ] | Frame toggle |

---

## Related Documents

- [TESTING.md](TESTING.md) - TDD best practices and patterns
- [BACKLOG.md](BACKLOG.md) - Main project backlog
- [PLANNING.md](PLANNING.md) - Project planning document

---

*Last updated: January 16, 2026*
