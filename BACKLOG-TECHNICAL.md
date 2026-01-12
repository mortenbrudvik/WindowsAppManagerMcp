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

| Layer | Target | Current |
|-------|--------|---------|
| Services | 80%+ | 0% |
| Models | 70%+ | 0% |
| Tools | 60%+ | 0% |
| **Overall** | **70-80%** | **0%** |

---

## T1: Test Infrastructure Setup

*Foundation for all testing - complete these first.*

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T1.1 | Create `tests/WindowsAppManagerMcp.Tests/` project | P0 | [ ] | `dotnet new xunit` |
| T1.2 | Add project reference to main project | P0 | [ ] | Reference `src/WindowsAppManagerMcp` |
| T1.3 | Add NuGet: `xunit` 2.8.1 | P0 | [ ] | Test framework |
| T1.4 | Add NuGet: `xunit.runner.visualstudio` 2.8.1 | P0 | [ ] | VS test runner |
| T1.5 | Add NuGet: `Microsoft.NET.Test.Sdk` 17.10.0 | P0 | [ ] | Test SDK |
| T1.6 | Add NuGet: `Moq` 4.20.70 | P0 | [ ] | Mocking framework |
| T1.7 | Add NuGet: `FluentAssertions` 6.12.0 | P2 | [ ] | Optional - better assertions |
| T1.8 | Add NuGet: `coverlet.collector` 6.0.2 | P1 | [ ] | Code coverage |
| T1.9 | Create `GlobalUsings.cs` | P1 | [ ] | Common imports |
| T1.10 | Create `Fixtures/TestDataFactory.cs` | P1 | [ ] | Test data helpers |
| T1.11 | Add test project to solution | P0 | [ ] | Update `.sln` file |
| T1.12 | Verify `dotnet test` runs successfully | P0 | [ ] | Sanity check |

### T1.A: Native Layer Testability

*Required for mocking P/Invoke calls in WindowService tests.*

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T1.A1 | Create `INativeWindowWrapper` interface | P1 | [ ] | Abstract P/Invoke calls |
| T1.A2 | Create `NativeWindowWrapper` implementation | P1 | [ ] | Production wrapper |
| T1.A3 | Update `WindowService` constructor to accept `INativeWindowWrapper` | P1 | [ ] | Dependency injection |
| T1.A4 | Update DI registration in `Program.cs` | P1 | [ ] | Register wrapper |
| T1.A5 | Create `MockNativeWindowWrapper` for tests | P1 | [ ] | Test double |

---

## T2: WindowService Unit Tests

*14 methods to test. Target: 80%+ coverage.*

### T2.1: IsValidWindow Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.1.1 | Test `IsValidWindow` with zero handle returns false | P0 | [ ] | Edge case |
| T2.1.2 | Test `IsValidWindow` with negative handle returns false | P0 | [ ] | Edge case |
| T2.1.3 | Test `IsValidWindow` with valid handle returns true | P0 | [ ] | Happy path |
| T2.1.4 | Test `IsValidWindow` with destroyed window returns false | P1 | [ ] | Edge case |

### T2.2: GetAllWindows Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.2.1 | Test `GetAllWindows` returns non-null list | P0 | [ ] | Basic contract |
| T2.2.2 | Test `GetAllWindows(includeMinimized: true)` includes minimized | P1 | [ ] | Parameter behavior |
| T2.2.3 | Test `GetAllWindows(includeMinimized: false)` excludes minimized | P1 | [ ] | Parameter behavior |
| T2.2.4 | Test `GetAllWindows` excludes invisible windows | P1 | [ ] | Default behavior |

### T2.3: FindWindows Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.3.1 | Test `FindWindows` with `titleContains` filter | P0 | [ ] | Case-insensitive match |
| T2.3.2 | Test `FindWindows` with `processName` filter | P0 | [ ] | Process matching |
| T2.3.3 | Test `FindWindows` with `processId` filter | P1 | [ ] | PID matching |
| T2.3.4 | Test `FindWindows` with `handle` filter | P1 | [ ] | Handle matching |
| T2.3.5 | Test `FindWindows` with multiple filters (AND logic) | P1 | [ ] | Combined filters |
| T2.3.6 | Test `FindWindows` with `visibleOnly: false` | P2 | [ ] | Include hidden |
| T2.3.7 | Test `FindWindows` with no matches returns empty list | P0 | [ ] | Empty result |

### T2.4: GetForegroundWindow Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.4.1 | Test `GetForegroundWindow` returns WindowInfo or null | P1 | [ ] | Basic contract |
| T2.4.2 | Test `GetForegroundWindow` includes correct handle | P2 | [ ] | Data accuracy |

### T2.5: GetWindowInfo Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.5.1 | Test `GetWindowInfo` with valid handle returns WindowInfo | P0 | [ ] | Happy path |
| T2.5.2 | Test `GetWindowInfo` with invalid handle returns null | P0 | [ ] | Error handling |
| T2.5.3 | Test `GetWindowInfo` populates all properties correctly | P1 | [ ] | Data accuracy |

### T2.6: MoveWindow Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.6.1 | Test `MoveWindow` with valid handle returns true | P0 | [ ] | Happy path |
| T2.6.2 | Test `MoveWindow` with invalid handle returns false | P0 | [ ] | Error handling |
| T2.6.3 | Test `MoveWindow` calls SetWindowPos with correct x,y | P1 | [ ] | Verify behavior |
| T2.6.4 | Test `MoveWindow` preserves window size | P1 | [ ] | Non-destructive |

### T2.7: ResizeWindow Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.7.1 | Test `ResizeWindow` with valid handle returns true | P0 | [ ] | Happy path |
| T2.7.2 | Test `ResizeWindow` with invalid handle returns false | P0 | [ ] | Error handling |
| T2.7.3 | Test `ResizeWindow` calls SetWindowPos with correct width,height | P1 | [ ] | Verify behavior |
| T2.7.4 | Test `ResizeWindow` preserves window position | P1 | [ ] | Non-destructive |

### T2.8: SetWindowBounds Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.8.1 | Test `SetWindowBounds` with valid handle returns true | P0 | [ ] | Happy path |
| T2.8.2 | Test `SetWindowBounds` with invalid handle returns false | P0 | [ ] | Error handling |
| T2.8.3 | Test `SetWindowBounds` sets all four parameters | P1 | [ ] | Complete operation |

### T2.9: SetWindowState Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.9.1 | Test `SetWindowState` to `Minimized` | P0 | [ ] | State change |
| T2.9.2 | Test `SetWindowState` to `Maximized` | P0 | [ ] | State change |
| T2.9.3 | Test `SetWindowState` to `Normal` (restore) | P0 | [ ] | State change |
| T2.9.4 | Test `SetWindowState` with invalid handle returns false | P0 | [ ] | Error handling |

### T2.10: FocusWindow Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.10.1 | Test `FocusWindow` with valid handle returns true | P0 | [ ] | Happy path |
| T2.10.2 | Test `FocusWindow` with invalid handle returns false | P0 | [ ] | Error handling |
| T2.10.3 | Test `FocusWindow` calls SetForegroundWindow | P1 | [ ] | Verify behavior |

### T2.11: SnapWindow Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.11.1 | Test `SnapWindow` to `LeftHalf` | P0 | [ ] | Snap position |
| T2.11.2 | Test `SnapWindow` to `RightHalf` | P0 | [ ] | Snap position |
| T2.11.3 | Test `SnapWindow` to `TopHalf` | P1 | [ ] | Snap position |
| T2.11.4 | Test `SnapWindow` to `BottomHalf` | P1 | [ ] | Snap position |
| T2.11.5 | Test `SnapWindow` to `TopLeftQuarter` | P1 | [ ] | Snap position |
| T2.11.6 | Test `SnapWindow` to `TopRightQuarter` | P1 | [ ] | Snap position |
| T2.11.7 | Test `SnapWindow` to `BottomLeftQuarter` | P1 | [ ] | Snap position |
| T2.11.8 | Test `SnapWindow` to `BottomRightQuarter` | P1 | [ ] | Snap position |
| T2.11.9 | Test `SnapWindow` to `LeftThird` | P2 | [ ] | Snap position |
| T2.11.10 | Test `SnapWindow` to `CenterThird` | P2 | [ ] | Snap position |
| T2.11.11 | Test `SnapWindow` to `RightThird` | P2 | [ ] | Snap position |
| T2.11.12 | Test `SnapWindow` to `LeftTwoThirds` | P2 | [ ] | Snap position |
| T2.11.13 | Test `SnapWindow` to `RightTwoThirds` | P2 | [ ] | Snap position |
| T2.11.14 | Test `SnapWindow` to `Fullscreen` | P1 | [ ] | Snap position |
| T2.11.15 | Test `SnapWindow` with specific `monitorIndex` | P1 | [ ] | Multi-monitor |
| T2.11.16 | Test `SnapWindow` with invalid handle returns false | P0 | [ ] | Error handling |

### T2.12: MoveWindowToMonitor Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T2.12.1 | Test `MoveWindowToMonitor` with valid index | P0 | [ ] | Happy path |
| T2.12.2 | Test `MoveWindowToMonitor` with invalid index returns false | P0 | [ ] | Error handling |
| T2.12.3 | Test `MoveWindowToMonitor` with `positioning: "center"` | P1 | [ ] | Positioning |
| T2.12.4 | Test `MoveWindowToMonitor` with `positioning: "topleft"` | P1 | [ ] | Positioning |
| T2.12.5 | Test `MoveWindowToMonitor` with `positioning: "maximize"` | P1 | [ ] | Positioning |

---

## T3: MonitorService Unit Tests

*6 methods to test. Target: 80%+ coverage.*

### T3.1: GetAllMonitors Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T3.1.1 | Test `GetAllMonitors` returns non-empty list | P0 | [ ] | Basic contract |
| T3.1.2 | Test `GetAllMonitors` includes primary monitor | P1 | [ ] | Data accuracy |
| T3.1.3 | Test `GetAllMonitors` returns correct monitor count | P2 | [ ] | Multi-monitor |

### T3.2: GetPrimaryMonitor Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T3.2.1 | Test `GetPrimaryMonitor` returns monitor with `IsPrimary: true` | P0 | [ ] | Basic contract |
| T3.2.2 | Test `GetPrimaryMonitor` returns valid bounds | P1 | [ ] | Data accuracy |

### T3.3: GetMonitorAt Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T3.3.1 | Test `GetMonitorAt` with point inside primary monitor | P0 | [ ] | Happy path |
| T3.3.2 | Test `GetMonitorAt` with point outside all monitors returns null | P1 | [ ] | Edge case |
| T3.3.3 | Test `GetMonitorAt` with point on secondary monitor | P2 | [ ] | Multi-monitor |

### T3.4: GetMonitorForWindow Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T3.4.1 | Test `GetMonitorForWindow` with valid handle | P1 | [ ] | Happy path |
| T3.4.2 | Test `GetMonitorForWindow` with invalid handle returns null | P1 | [ ] | Error handling |

### T3.5: GetMonitorIndex Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T3.5.1 | Test `GetMonitorIndex` returns correct index for primary | P0 | [ ] | Basic contract |
| T3.5.2 | Test `GetMonitorIndex` returns correct index for secondary | P2 | [ ] | Multi-monitor |

### T3.6: CalculateSnapBounds Tests (Parameterized)

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T3.6.1 | Test `CalculateSnapBounds` for all 14 SnapPositions | P0 | [ ] | Use `[Theory]` with `[InlineData]` |
| T3.6.2 | Test `CalculateSnapBounds` with invalid monitor index | P1 | [ ] | Error handling |
| T3.6.3 | Test `CalculateSnapBounds` respects work area (excludes taskbar) | P1 | [ ] | Correct bounds |

---

## T4: ProcessService Unit Tests

*4 methods to test. Target: 80%+ coverage.*

### T4.1: LaunchApplication Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T4.1.1 | Test `LaunchApplication` with valid executable returns success | P0 | [ ] | Happy path |
| T4.1.2 | Test `LaunchApplication` with invalid executable returns failure | P0 | [ ] | Error handling |
| T4.1.3 | Test `LaunchApplication` with arguments passes them correctly | P1 | [ ] | Parameter passing |
| T4.1.4 | Test `LaunchApplication` with `workingDirectory` sets it | P2 | [ ] | Parameter passing |
| T4.1.5 | Test `LaunchApplication` with `waitForWindow: true` waits | P1 | [ ] | Wait behavior |
| T4.1.6 | Test `LaunchApplication` returns ProcessId on success | P1 | [ ] | Return value |

### T4.2: GetRunningProcesses Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T4.2.1 | Test `GetRunningProcesses` returns non-empty list | P0 | [ ] | Basic contract |
| T4.2.2 | Test `GetRunningProcesses` with `nameFilter` filters correctly | P1 | [ ] | Filtering |
| T4.2.3 | Test `GetRunningProcesses` with `includeWindowless: false` | P1 | [ ] | Filtering |
| T4.2.4 | Test `GetRunningProcesses` returns correct ProcessInfo fields | P2 | [ ] | Data accuracy |

### T4.3: GetProcessName Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T4.3.1 | Test `GetProcessName` with valid PID returns name | P1 | [ ] | Happy path |
| T4.3.2 | Test `GetProcessName` with invalid PID returns null | P1 | [ ] | Error handling |

### T4.4: GetProcessPath Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T4.4.1 | Test `GetProcessPath` with valid PID returns path | P1 | [ ] | Happy path |
| T4.4.2 | Test `GetProcessPath` with invalid PID returns null | P1 | [ ] | Error handling |

---

## T5: LayoutService Unit Tests

*6 methods to test. Target: 80%+ coverage. Most complex service.*

### T5.1: GetAllPresets Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.1.1 | Test `GetAllPresets` with no presets returns empty list | P0 | [ ] | Edge case |
| T5.1.2 | Test `GetAllPresets` returns all saved presets | P0 | [ ] | Happy path |
| T5.1.3 | Test `GetAllPresets` loads from disk on first call | P1 | [ ] | Lazy loading |

### T5.2: GetPreset Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.2.1 | Test `GetPreset` with existing name returns preset | P0 | [ ] | Happy path |
| T5.2.2 | Test `GetPreset` with non-existent name returns null | P0 | [ ] | Not found |
| T5.2.3 | Test `GetPreset` is case-insensitive | P2 | [ ] | Usability |

### T5.3: SavePresetAsync Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.3.1 | Test `SavePresetAsync` creates JSON file | P0 | [ ] | File creation |
| T5.3.2 | Test `SavePresetAsync` returns true on success | P0 | [ ] | Return value |
| T5.3.3 | Test `SavePresetAsync` overwrites existing preset | P1 | [ ] | Update behavior |
| T5.3.4 | Test `SavePresetAsync` sanitizes filename (removes invalid chars) | P1 | [ ] | Security |
| T5.3.5 | Test `SavePresetAsync` JSON contains all preset data | P1 | [ ] | Serialization |
| T5.3.6 | Test `SavePresetAsync` respects CancellationToken | P2 | [ ] | Async contract |

### T5.4: DeletePresetAsync Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.4.1 | Test `DeletePresetAsync` removes file | P0 | [ ] | Happy path |
| T5.4.2 | Test `DeletePresetAsync` returns true when preset exists | P0 | [ ] | Return value |
| T5.4.3 | Test `DeletePresetAsync` returns false when preset not found | P0 | [ ] | Not found |
| T5.4.4 | Test `DeletePresetAsync` removes from cache | P1 | [ ] | Cache invalidation |

### T5.5: CaptureCurrentLayoutAsync Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.5.1 | Test `CaptureCurrentLayoutAsync` creates preset with given name | P0 | [ ] | Basic contract |
| T5.5.2 | Test `CaptureCurrentLayoutAsync` captures visible windows | P0 | [ ] | Window capture |
| T5.5.3 | Test `CaptureCurrentLayoutAsync` with `includeProcesses` filter | P1 | [ ] | Filtering |
| T5.5.4 | Test `CaptureCurrentLayoutAsync` with `excludeProcesses` filter | P1 | [ ] | Filtering |
| T5.5.5 | Test `CaptureCurrentLayoutAsync` stores relative positions | P1 | [ ] | Position calculation |
| T5.5.6 | Test `CaptureCurrentLayoutAsync` includes monitor info | P2 | [ ] | Multi-monitor |
| T5.5.7 | Test `CaptureCurrentLayoutAsync` sets description if provided | P2 | [ ] | Optional param |

### T5.6: ApplyPresetAsync Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T5.6.1 | Test `ApplyPresetAsync` with non-existent preset returns failure | P0 | [ ] | Not found |
| T5.6.2 | Test `ApplyPresetAsync` calls SetWindowBounds for matched windows | P0 | [ ] | Core behavior |
| T5.6.3 | Test `ApplyPresetAsync` returns WindowsArranged count | P0 | [ ] | Return value |
| T5.6.4 | Test `ApplyPresetAsync` with `matchBy: "process_only"` | P1 | [ ] | Match strategy |
| T5.6.5 | Test `ApplyPresetAsync` with `matchBy: "title_only"` | P1 | [ ] | Match strategy |
| T5.6.6 | Test `ApplyPresetAsync` with `matchBy: "process_and_title"` | P1 | [ ] | Match strategy |
| T5.6.7 | Test `ApplyPresetAsync` with `launchMissing: true` launches apps | P0 | [ ] | Launch behavior |
| T5.6.8 | Test `ApplyPresetAsync` with `launchMissing: false` skips launch | P1 | [ ] | Launch behavior |
| T5.6.9 | Test `ApplyPresetAsync` returns WindowsLaunched count | P1 | [ ] | Return value |
| T5.6.10 | Test `ApplyPresetAsync` handles multiple windows per process | P2 | [ ] | Edge case |

---

## T6: Model Unit Tests

*Data models and value objects.*

### T6.1: WindowMatcher Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T6.1.1 | Test `WindowMatcher` matches by `TitleContains` | P1 | [ ] | Matching logic |
| T6.1.2 | Test `WindowMatcher` matches by `ProcessName` | P1 | [ ] | Matching logic |
| T6.1.3 | Test `WindowMatcher` with both criteria (AND logic) | P1 | [ ] | Combined |
| T6.1.4 | Test `WindowMatcher` case-insensitive matching | P2 | [ ] | Usability |

### T6.2: RelativePosition Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T6.2.1 | Test `RelativePosition` to absolute conversion | P1 | [ ] | Calculation |
| T6.2.2 | Test `RelativePosition` from absolute conversion | P1 | [ ] | Calculation |
| T6.2.3 | Test `RelativePosition` boundary values (0.0, 1.0) | P2 | [ ] | Edge cases |

### T6.3: LayoutPreset Serialization Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T6.3.1 | Test JSON serialization roundtrip | P1 | [ ] | Serialization |
| T6.3.2 | Test JSON includes all required properties | P1 | [ ] | Complete data |
| T6.3.3 | Test deserialization handles missing optional fields | P2 | [ ] | Backwards compat |

---

## T7: Integration Tests

*Service interaction and file I/O tests. Target: 20% of total tests.*

### T7.1: LayoutService Integration

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T7.1.1 | Test save and load preset roundtrip | P0 | [ ] | End-to-end |
| T7.1.2 | Test capture and apply layout cycle | P0 | [ ] | Full workflow |
| T7.1.3 | Test LayoutService with real WindowService mock | P1 | [ ] | Service interaction |
| T7.1.4 | Test LayoutService with real MonitorService mock | P1 | [ ] | Service interaction |

### T7.2: File Persistence Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T7.2.1 | Test preset files created in correct directory | P1 | [ ] | File location |
| T7.2.2 | Test preset file naming convention | P1 | [ ] | Filename |
| T7.2.3 | Test preset directory created if not exists | P2 | [ ] | Directory handling |
| T7.2.4 | Test concurrent save operations | P2 | [ ] | Thread safety |

### T7.3: DI Container Tests

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T7.3.1 | Test all services resolve from container | P2 | [ ] | DI validation |
| T7.3.2 | Test service lifetimes are correct | P2 | [ ] | Singleton vs Transient |

---

## T8: CI/CD Integration

*Automation and pipeline tasks.*

| ID | Task | Priority | Status | Notes |
|----|------|----------|--------|-------|
| T8.1 | Create GitHub Actions workflow for tests | P1 | [ ] | `.github/workflows/test.yml` |
| T8.2 | Add test step to build workflow | P1 | [ ] | `dotnet test` |
| T8.3 | Configure coverage reporting | P2 | [ ] | Coverlet + upload |
| T8.4 | Add coverage badge to README | P3 | [ ] | Status badge |
| T8.5 | Configure test result publishing | P2 | [ ] | Artifacts |
| T8.6 | Set up coverage threshold check (70%) | P2 | [ ] | Quality gate |

---

## Summary

### Task Counts by Category

| Category | Total | P0 | P1 | P2 | P3 |
|----------|-------|----|----|----|----|
| T1: Infrastructure | 17 | 7 | 7 | 2 | 1 |
| T2: WindowService | 47 | 18 | 21 | 8 | 0 |
| T3: MonitorService | 12 | 4 | 5 | 3 | 0 |
| T4: ProcessService | 12 | 3 | 6 | 3 | 0 |
| T5: LayoutService | 27 | 10 | 12 | 5 | 0 |
| T6: Models | 10 | 0 | 6 | 4 | 0 |
| T7: Integration | 10 | 2 | 4 | 4 | 0 |
| T8: CI/CD | 6 | 0 | 2 | 3 | 1 |
| **Total** | **141** | **44** | **63** | **32** | **2** |

### Recommended Execution Order

1. **T1.1-T1.12**: Test infrastructure (blocking all other tasks)
2. **T1.A1-T1.A5**: Native layer testability
3. **T3.6**: MonitorService CalculateSnapBounds (most testable)
4. **T5.1-T5.4**: LayoutService CRUD operations
5. **T2.1-T2.5**: WindowService query methods
6. **T5.5-T5.6**: LayoutService capture/apply
7. **T2.6-T2.12**: WindowService mutation methods
8. **T4**: ProcessService tests
9. **T6**: Model tests
10. **T7**: Integration tests
11. **T8**: CI/CD automation

---

## Related Documents

- [TESTING.md](TESTING.md) - TDD best practices and patterns
- [BACKLOG.md](BACKLOG.md) - Main project backlog
- [PLANNING.md](PLANNING.md) - Project planning document

---

*Last updated: January 2025*
