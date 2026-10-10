# Testing Guide

> **WindowsAppManagerMcp** | .NET 8.0 | xUnit | Moq

A comprehensive guide for Test-Driven Development (TDD) using the Red-Green-Refactor methodology.

> **Desktop safety:** a plain `dotnet test` is safe to run on your own desktop. A test that
> launches or kills real processes, sends real keyboard or mouse input, or moves, focuses or
> closes real windows must use `[DesktopFact]` or `[DesktopTheory]`. Those tests are skipped
> unless `WAM_ALLOW_DESKTOP_TESTS=1`. A test that only starts the suite's own fixture app and
> reads or captures it uses `[DesktopNoInputFact]` and is skipped unless
> `WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1` (or `WAM_ALLOW_DESKTOP_TESTS=1`). See
> [Desktop Tests](#desktop-tests).

---

## Table of Contents

1. [Introduction & Philosophy](#1-introduction--philosophy)
2. [TDD Red-Green-Refactor Cycle](#2-tdd-red-green-refactor-cycle)
3. [Test Structure (AAA Pattern)](#3-test-structure-aaa-pattern)
4. [Test Organization](#4-test-organization)
5. [Naming Conventions](#5-naming-conventions)
6. [Test Doubles Guide](#6-test-doubles-guide)
7. [xUnit Practices](#7-xunit-practices)
8. [Moq Patterns](#8-moq-patterns)
9. [Mocking Native P/Invoke Layer](#9-mocking-native-pinvoke-layer)
10. [Code Coverage](#10-code-coverage)
11. [Common Anti-Patterns](#11-common-anti-patterns)
12. [Integration Testing](#12-integration-testing)
13. [Practical Examples](#13-practical-examples)
14. [Test Data Factories](#14-test-data-factories)
15. [Quick Reference Checklists](#15-quick-reference-checklists)
16. [Test Project Setup](#16-test-project-setup)
17. [Running Tests](#17-running-tests)

---

## 1. Introduction & Philosophy

### Purpose

This document establishes testing standards and best practices for the WindowsAppManagerMcp project. It serves as:

- A guide for TDD implementation
- A template for consistent test creation
- A quick reference for common testing patterns

### Testing Goals

| Metric | Target |
|--------|--------|
| Code Coverage | 70-80% |
| Test Execution | < 1 second per unit test |
| Build Validation | All tests pass before merge |

### Test Pyramid

```
                    /\
                   /  \
                  / E2E \           ~10% - Full system integration
                 / (10%) \          Slow, expensive, critical paths only
                /----------\
               /            \
              / Integration  \      ~20% - Service interactions
             /    (20%)       \     Moderate speed, real dependencies
            /------------------\
           /                    \
          /    Unit Tests        \  ~70% - Single units in isolation
         /       (70%)            \ Fast, cheap, comprehensive
        /--------------------------\
```

**Key Principle**: More tests at the base (fast, cheap), fewer at the top (slow, expensive).

### Why TDD?

- **Catch bugs early** - Before they reach production
- **Design better APIs** - Writing tests first forces you to think about usage
- **Document behavior** - Tests serve as living documentation
- **Refactor with confidence** - Tests catch regressions immediately
- **Reduce debugging time** - Teams report 30-50% lower mean-time-to-detect for critical failures

---

## 2. TDD Red-Green-Refactor Cycle

### The Three Phases

```
    +-------+     +-------+     +----------+
    |  RED  | --> | GREEN | --> | REFACTOR |
    +-------+     +-------+     +----------+
        ^                            |
        |                            |
        +----------------------------+
```

#### Phase 1: RED - Write a Failing Test

Write a test that describes the expected behavior. Run it - it should fail.

```csharp
[Fact]
public void IsValidWindow_WithZeroHandle_ReturnsFalse()
{
    // Arrange
    var mockMonitorService = new Mock<IMonitorService>();
    var sut = new WindowService(mockMonitorService.Object);

    // Act
    var result = sut.IsValidWindow(nint.Zero);

    // Assert
    Assert.False(result);
}
```

**Why RED matters**: If the test passes without writing code, either:
- The functionality already exists
- The test is wrong

#### Phase 2: GREEN - Make It Pass

Write the **minimum code** needed to make the test pass. Don't over-engineer.

```csharp
public bool IsValidWindow(nint handle)
{
    if (handle == nint.Zero)
        return false;

    return NativeMethods.User32.IsWindow(handle);
}
```

**Why minimum code**: You'll refactor next. Get to green first.

#### Phase 3: REFACTOR - Improve the Code

With tests passing, improve the code quality:

- Extract common patterns
- Remove duplication
- Improve naming
- Optimize if needed

**Critical Rule**: Only refactor when GREEN. Run tests after each change.

### TDD Workflow Checklist

- [ ] Write test describing expected behavior
- [ ] Run test - verify it **fails** (RED)
- [ ] Write simplest code to pass test
- [ ] Run test - verify it **passes** (GREEN)
- [ ] Refactor code while keeping tests green
- [ ] Commit changes
- [ ] Repeat

### Commit Frequency

Commit after each green cycle (every 5-10 minutes). Benefits:

- Easy to revert if you go down the wrong path
- Small, focused commits are easier to review
- Git history shows incremental progress

---

## 3. Test Structure (AAA Pattern)

### The Pattern

Every test should have three clearly separated sections:

```csharp
[Fact]
public void MethodName_Scenario_ExpectedBehavior()
{
    // Arrange - Set up test data and dependencies
    var mockDependency = new Mock<IDependency>();
    mockDependency.Setup(x => x.Method()).Returns(expectedValue);
    var sut = new SystemUnderTest(mockDependency.Object);

    // Act - Execute the method being tested (usually 1 line)
    var result = sut.MethodUnderTest(input);

    // Assert - Verify the expected outcome (usually 1-3 lines)
    Assert.Equal(expected, result);
}
```

### Concrete Example

```csharp
[Fact]
public void SnapWindow_ToLeftHalf_CalculatesCorrectBounds()
{
    // Arrange
    var mockMonitorService = new Mock<IMonitorService>();
    var expectedBounds = new MonitorRect(0, 0, 960, 1080);
    mockMonitorService
        .Setup(m => m.GetMonitorIndex(It.IsAny<nint>()))
        .Returns(0);
    mockMonitorService
        .Setup(m => m.CalculateSnapBounds(0, SnapPosition.LeftHalf))
        .Returns(expectedBounds);

    var windowService = new WindowService(mockMonitorService.Object);
    var windowHandle = new nint(12345);

    // Act
    var result = windowService.SnapWindow(windowHandle, SnapPosition.LeftHalf);

    // Assert
    Assert.True(result);
    mockMonitorService.Verify(
        m => m.CalculateSnapBounds(0, SnapPosition.LeftHalf),
        Times.Once);
}
```

### AAA Anti-Patterns

**BAD: Multiple Acts**
```csharp
[Fact]
public void BadTest_MultipleActs()
{
    var sut = new Service();

    var result1 = sut.Method1();  // First Act
    Assert.True(result1);

    var result2 = sut.Method2();  // Second Act - SPLIT INTO SEPARATE TEST
    Assert.True(result2);
}
```

**BAD: Arrange After Act**
```csharp
[Fact]
public void BadTest_ArrangeAfterAct()
{
    var sut = new Service(mock.Object);
    var result = sut.Method();      // Act
    mock.Setup(x => x.Other());     // Arrange AFTER Act - WRONG
    Assert.True(result);
}
```

**BAD: Mixed Act/Assert**
```csharp
[Fact]
public void BadTest_MixedActAssert()
{
    var sut = new Service();
    Assert.DoesNotThrow(() => sut.Method());  // Assert wrapping Act
}
```

---

## 4. Test Organization

### Project Structure

```
WindowsAppManagerMcp/
├── src/
│   └── WindowsAppManagerMcp/
│       ├── Infrastructure/        # MCP pipeline extensions (ToolErrorReporting)
│       ├── Models/                # DTOs, result records, error codes
│       ├── Native/                # P/Invoke declarations and the native wrappers tests mock
│       ├── Services/
│       │   ├── Interfaces/        # IWindowService, IMonitorService, IProcessService, ...
│       │   └── *.cs               # WindowService, MonitorService, ProcessService, LayoutService, ...
│       └── Tools/                 # [McpServerTool] classes
│
└── tests/
    ├── WindowsAppManagerMcp.Tests/
    │   ├── WindowsAppManagerMcp.Tests.csproj
    │   ├── GlobalUsings.cs
    │   ├── Contract/              # Tool contract: serialization, error reporting, descriptions, docs
    │   ├── Fixtures/              # TestDataFactory, RepositoryFiles, InProcessMcpServer, FakeCdp, ...
    │   ├── Integration/           # DI, file persistence, layout service, screen capture (read-only)
    │   └── Unit/
    │       ├── Architecture/      # NativeCallGuardTests (IL scan for native calls)
    │       ├── Infrastructure/    # DesktopTestGateTests
    │       ├── Models/
    │       ├── Native/
    │       ├── Services/
    │       └── Tools/
    ├── WindowsAppManagerMcp.Tests.Infrastructure/  # Shared desktop test gate ([DesktopFact], [DesktopNoInputFact], ...)
    ├── FixtureApp/                # WamFixtureApp: the WPF app the end-to-end tests drive
    └── WindowsAppManagerMcp.E2ETests/
        ├── Harness/               # Starts the fixture, guarded server services, event log reader
        ├── HarnessTests/          # Ordinary tests of the harness (never start the fixture)
        └── Scenarios/             # Gated end-to-end tests against the fixture
```

### Test Class Organization

**Option 1: Regions by Method**
```csharp
public class WindowServiceTests
{
    #region Constructor Tests
    // Tests for constructor behavior
    #endregion

    #region GetAllWindows Tests
    [Fact]
    public void GetAllWindows_ReturnsEmptyList_WhenNoWindowsExist() { }
    #endregion

    #region MoveWindow Tests
    [Fact]
    public void MoveWindow_ReturnsFalse_WhenHandleIsInvalid() { }
    #endregion
}
```

**Option 2: Nested Classes per Method** (Recommended for larger test classes)
```csharp
public class WindowServiceTests
{
    public class GetAllWindowsMethod
    {
        [Fact]
        public void ReturnsEmptyList_WhenNoWindowsExist() { }

        [Fact]
        public void ExcludesMinimizedWindows_WhenIncludeMinimizedIsFalse() { }
    }

    public class MoveWindowMethod
    {
        [Fact]
        public void ReturnsFalse_WhenHandleIsInvalid() { }

        [Fact]
        public void MovesWindow_WhenHandleIsValid() { }
    }
}
```

---

## 5. Naming Conventions

### Primary Convention: `MethodName_Scenario_ExpectedBehavior`

```csharp
// Format: [MethodUnderTest]_[Scenario]_[ExpectedResult]

// Good examples:
IsValidWindow_WithZeroHandle_ReturnsFalse()
FindWindows_WithMatchingTitle_ReturnsMatchingWindows()
SnapWindow_ToLeftHalf_SetsCorrectBounds()
ApplyPresetAsync_WhenPresetNotFound_ReturnsFailureResult()
SavePresetAsync_WithValidPreset_CreatesJsonFile()
```

### Alternative Convention: `Should_When`

```csharp
// Format: Should[ExpectedBehavior]_When[Scenario]

ShouldReturnFalse_WhenHandleIsZero()
ShouldReturnMatchingWindows_WhenTitleContainsFilter()
ShouldCreateJsonFile_WhenPresetIsValid()
```

### Bad Examples

```csharp
TestIsValidWindow()               // No scenario context
IsValidWindowTest()               // No expected behavior
ShouldReturnFalse()              // No method context
TestMethod1()                     // Meaningless
Test_Window_Works()              // Too vague
```

### Naming Checklist

- [ ] Method being tested is clearly identified
- [ ] Test scenario/condition is described
- [ ] Expected outcome is stated
- [ ] No implementation details in name
- [ ] Uses underscores for readability

---

## 6. Test Doubles Guide

### Quick Reference Table

| Type | Purpose | Verifies Calls? | Returns Values? | Use When |
|------|---------|-----------------|-----------------|----------|
| **Mock** | Verify interactions | YES | YES | Need to verify method was called |
| **Stub** | Provide canned answers | NO | YES | Need controlled return values |
| **Fake** | Working implementation | NO | YES | Need realistic but simple behavior |
| **Spy** | Record calls for inspection | YES | Partial | Need to inspect call details |

### When to Use Each

#### Mocks - Verify Interactions

Use when you need to verify that a dependency was called correctly.

```csharp
[Fact]
public async Task ApplyPresetAsync_CallsSetWindowBounds_ForEachMatchedWindow()
{
    // Arrange
    var mockWindowService = new Mock<IWindowService>();
    mockWindowService
        .Setup(w => w.GetAllWindows(true))
        .Returns(new List<WindowInfo> { testWindow });

    var sut = new LayoutService(mockWindowService.Object, ...);

    // Act
    await sut.ApplyPresetAsync("test-preset");

    // Assert - Verify the mock was called
    mockWindowService.Verify(
        w => w.SetWindowBounds(It.IsAny<nint>(),
            It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>()),
        Times.AtLeastOnce);
}
```

#### Stubs - Provide Data

Use when you just need controlled return values, without verification.

```csharp
[Fact]
public void CaptureCurrentLayout_UsesMonitorWorkArea()
{
    // Arrange - Stub provides data but we don't verify it was called
    var stubMonitorService = new Mock<IMonitorService>();
    stubMonitorService
        .Setup(m => m.GetAllMonitors())
        .Returns(new List<MonitorInfo>
        {
            new MonitorInfo(0, "DISPLAY1", true,
                new MonitorRect(0, 0, 1920, 1080),
                new MonitorRect(0, 0, 1920, 1040),
                1.0)
        });

    var sut = new LayoutService(..., stubMonitorService.Object, ...);

    // Act & Assert - No Verify calls needed
}
```

#### Fakes - Working Implementation

Use for realistic but simplified behavior, especially for I/O operations.

```csharp
public class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> _files = new();

    public Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        _files[path] = content;
        return Task.CompletedTask;
    }

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        return Task.FromResult(_files.TryGetValue(path, out var content)
            ? content
            : throw new FileNotFoundException());
    }

    public bool FileExists(string path) => _files.ContainsKey(path);
}
```

#### Spies - Capture Details

Use when you need to inspect the exact arguments passed to a method.

```csharp
[Fact]
public void MoveWindow_PassesCorrectCoordinates()
{
    // Arrange - Spy captures call arguments
    int capturedX = 0, capturedY = 0;

    var mockNative = new Mock<INativeWrapper>();
    mockNative
        .Setup(n => n.SetWindowPos(It.IsAny<nint>(),
            It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>()))
        .Callback<nint, int, int, int, int>((h, x, y, w, height) =>
        {
            capturedX = x;
            capturedY = y;
        })
        .Returns(true);

    // Act
    sut.MoveWindow(handle, 100, 200);

    // Assert - Inspect captured values
    Assert.Equal(100, capturedX);
    Assert.Equal(200, capturedY);
}
```

### Best Practices

1. **Don't mock what you don't own** - Only mock interfaces in your codebase
2. **Mock at the boundary** - Mock external dependencies, not internal classes
3. **Keep it simple** - Avoid complex mock setups
4. **One mock behavior per test** - Don't test multiple mock interactions in one test

---

## 7. xUnit Practices

### `[Fact]` - Single Test Cases

Use for tests with no parameters.

```csharp
[Fact]
public void GetPrimaryMonitor_ReturnsMonitorWithIsPrimaryTrue()
{
    // Arrange
    var sut = CreateMonitorService();

    // Act
    var result = sut.GetPrimaryMonitor();

    // Assert
    Assert.True(result.IsPrimary);
}
```

### `[Theory]` with `[InlineData]` - Parameterized Tests

Use for testing multiple scenarios with the same logic.

```csharp
[Theory]
[InlineData(SnapPosition.LeftHalf, 0, 0, 960, 1080)]
[InlineData(SnapPosition.RightHalf, 960, 0, 960, 1080)]
[InlineData(SnapPosition.TopHalf, 0, 0, 1920, 540)]
[InlineData(SnapPosition.BottomHalf, 0, 540, 1920, 540)]
[InlineData(SnapPosition.TopLeftQuarter, 0, 0, 960, 540)]
[InlineData(SnapPosition.TopRightQuarter, 960, 0, 960, 540)]
[InlineData(SnapPosition.BottomLeftQuarter, 0, 540, 960, 540)]
[InlineData(SnapPosition.BottomRightQuarter, 960, 540, 960, 540)]
public void CalculateSnapBounds_ReturnsCorrectBounds(
    SnapPosition position,
    int expectedX, int expectedY,
    int expectedWidth, int expectedHeight)
{
    // Arrange
    var sut = CreateMonitorServiceWithSingleMonitor(1920, 1080);

    // Act
    var result = sut.CalculateSnapBounds(0, position);

    // Assert
    Assert.Equal(expectedX, result.X);
    Assert.Equal(expectedY, result.Y);
    Assert.Equal(expectedWidth, result.Width);
    Assert.Equal(expectedHeight, result.Height);
}
```

### `[Theory]` with `[MemberData]` - Complex Test Data

Use when test data is too complex for `[InlineData]`.

```csharp
public class WindowServiceTests
{
    public static IEnumerable<object[]> InvalidHandleTestCases =>
        new List<object[]>
        {
            new object[] { nint.Zero, "Zero handle" },
            new object[] { new nint(-1), "Negative handle" },
        };

    [Theory]
    [MemberData(nameof(InvalidHandleTestCases))]
    public void IsValidWindow_WithInvalidHandle_ReturnsFalse(
        nint handle, string description)
    {
        // Arrange
        var sut = CreateWindowService();

        // Act
        var result = sut.IsValidWindow(handle);

        // Assert
        Assert.False(result, $"Failed for: {description}");
    }
}
```

### `[Theory]` with `[ClassData]` - Reusable Test Data

Use when test data should be shared across test classes.

```csharp
public class WindowMatcherTestData : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return new object[]
        {
            new WindowMatcher(TitleContains: "Notepad"),
            CreateTestWindow("Untitled - Notepad", "notepad"),
            true  // Should match
        };
        yield return new object[]
        {
            new WindowMatcher(ProcessName: "chrome"),
            CreateTestWindow("Google", "chrome"),
            true  // Should match
        };
        yield return new object[]
        {
            new WindowMatcher(ProcessName: "notepad"),
            CreateTestWindow("Chrome", "chrome"),
            false  // Should not match
        };
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

[Theory]
[ClassData(typeof(WindowMatcherTestData))]
public void MatchesWindow_ReturnsExpectedResult(
    WindowMatcher matcher, WindowInfo window, bool shouldMatch)
{
    var result = matcher.Matches(window);
    Assert.Equal(shouldMatch, result);
}
```

### `[DesktopFact]` / `[DesktopTheory]` - Tests With Real Desktop Side Effects

Prefer mocking the native layer. When a test must really launch or kill a process, send input,
or change a real window, use `[DesktopFact]` or `[DesktopTheory]` instead of `[Fact]` or
`[Theory]`. They are skipped unless `WAM_ALLOW_DESKTOP_TESTS=1` and report the trait
`Category=Desktop`. See [Desktop Tests](#desktop-tests).

```csharp
[DesktopFact]
public void SomeOperation_OnTheRealDesktop_Works()
{
    // Has a real side effect (a process, input or a window change):
    // only runs when WAM_ALLOW_DESKTOP_TESTS=1
}
```

A test that only starts the suite's own fixture app and reads it (find its windows, read its UI
Automation tree, capture it) uses `[DesktopNoInputFact]` or `[DesktopNoInputTheory]`. It is skipped
unless `WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1` or `WAM_ALLOW_DESKTOP_TESTS=1`, and reports both
`Category=Desktop` and `Category=DesktopNoInput`.

Besides the gate's own canaries in `DesktopTestGateTests`, the tests marked this way are the
fixture app scenarios in `tests/WindowsAppManagerMcp.E2ETests`. See
[End-to-end tests against the fixture app](#end-to-end-tests-against-the-fixture-app).

### xUnit Assertions Reference

```csharp
// Boolean
Assert.True(result);
Assert.False(result);

// Equality
Assert.Equal(expected, actual);
Assert.NotEqual(unexpected, actual);
Assert.Same(expected, actual);       // Reference equality
Assert.NotSame(unexpected, actual);

// Null
Assert.Null(result);
Assert.NotNull(result);

// Collections
Assert.Empty(collection);
Assert.NotEmpty(collection);
Assert.Single(collection);
Assert.Contains(expected, collection);
Assert.DoesNotContain(unexpected, collection);
Assert.All(collection, item => Assert.True(item.IsValid));

// Types
Assert.IsType<ExpectedType>(result);
Assert.IsAssignableFrom<IInterface>(result);

// Exceptions
Assert.Throws<ArgumentNullException>(() => sut.Method(null));
await Assert.ThrowsAsync<InvalidOperationException>(
    () => sut.MethodAsync());

// Ranges
Assert.InRange(value, low, high);

// Strings
Assert.StartsWith("prefix", result);
Assert.EndsWith("suffix", result);
Assert.Contains("substring", result);
Assert.Matches("regex", result);
```

---

## 8. Moq Patterns

### Basic Setup

```csharp
// Create mock
var mockService = new Mock<IWindowService>();

// Setup return value
mockService
    .Setup(w => w.IsValidWindow(It.IsAny<nint>()))
    .Returns(true);

// Setup with specific argument
mockService
    .Setup(w => w.GetWindowInfo(new nint(12345)))
    .Returns(testWindowInfo);

// Use mock
var sut = new LayoutService(mockService.Object, ...);
```

### Verification

```csharp
// Verify method was called once
mockService.Verify(
    w => w.FocusWindow(It.IsAny<nint>()),
    Times.Once);

// Verify with specific arguments
mockService.Verify(
    w => w.SetWindowBounds(handle, 100, 200, 800, 600),
    Times.Once);

// Verify never called
mockService.Verify(
    w => w.SetWindowState(It.IsAny<nint>(), It.IsAny<WindowState>()),
    Times.Never);

// Verify called at least N times
mockService.Verify(
    w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()),
    Times.AtLeast(2));
```

### Argument Matchers

```csharp
// Any value of type
It.IsAny<nint>()
It.IsAny<string>()

// Specific conditions
It.Is<int>(x => x > 0)
It.Is<string>(s => s.Contains("notepad", StringComparison.OrdinalIgnoreCase))
It.Is<WindowState>(s => s == WindowState.Normal || s == WindowState.Maximized)

// Range
It.IsInRange(0, 100, Range.Inclusive)

// Regex
It.IsRegex(@"\.exe$")

// Null checks
It.IsNotNull<string>()
```

### Sequence Returns

```csharp
// Return different values on successive calls
mockService
    .SetupSequence(w => w.IsValidWindow(It.IsAny<nint>()))
    .Returns(true)   // First call
    .Returns(false)  // Second call
    .Returns(true);  // Third call
```

### Callback for Capturing Arguments

```csharp
var capturedArgs = new List<(int x, int y)>();

mockService
    .Setup(w => w.MoveWindow(It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>()))
    .Callback<nint, int, int>((handle, x, y) =>
    {
        capturedArgs.Add((x, y));
    })
    .Returns(true);
```

### Project-Specific Mock Examples

#### IMonitorService Mock

```csharp
private Mock<IMonitorService> CreateMockMonitorService()
{
    var mock = new Mock<IMonitorService>();

    var monitors = new List<MonitorInfo>
    {
        new MonitorInfo(
            Index: 0,
            DeviceName: @"\\.\DISPLAY1",
            IsPrimary: true,
            Bounds: new MonitorRect(0, 0, 1920, 1080),
            WorkArea: new MonitorRect(0, 0, 1920, 1040),
            ScaleFactor: 1.0),
        new MonitorInfo(
            Index: 1,
            DeviceName: @"\\.\DISPLAY2",
            IsPrimary: false,
            Bounds: new MonitorRect(1920, 0, 2560, 1440),
            WorkArea: new MonitorRect(1920, 0, 2560, 1400),
            ScaleFactor: 1.25)
    };

    mock.Setup(m => m.GetAllMonitors()).Returns(monitors);
    mock.Setup(m => m.GetPrimaryMonitor()).Returns(monitors[0]);
    mock.Setup(m => m.GetMonitorIndex(It.IsAny<nint>())).Returns(0);
    mock.Setup(m => m.GetMonitorAt(It.IsAny<int>(), It.IsAny<int>()))
        .Returns((int x, int y) =>
            monitors.FirstOrDefault(m =>
                x >= m.Bounds.X && x < m.Bounds.X + m.Bounds.Width));

    return mock;
}
```

#### IWindowService Mock

```csharp
private Mock<IWindowService> CreateMockWindowService()
{
    var mock = new Mock<IWindowService>();

    var testWindows = new List<WindowInfo>
    {
        TestDataFactory.CreateWindowInfo(1001, "Notepad", "notepad"),
        TestDataFactory.CreateWindowInfo(1002, "Chrome", "chrome"),
        TestDataFactory.CreateWindowInfo(1003, "VS Code", "Code")
    };

    mock.Setup(w => w.GetAllWindows(It.IsAny<bool>()))
        .Returns(testWindows);

    mock.Setup(w => w.IsValidWindow(It.Is<nint>(h => h != nint.Zero)))
        .Returns(true);
    mock.Setup(w => w.IsValidWindow(nint.Zero))
        .Returns(false);

    mock.Setup(w => w.SetWindowBounds(
            It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>()))
        .Returns(true);

    return mock;
}
```

#### IProcessService Mock

```csharp
private Mock<IProcessService> CreateMockProcessService()
{
    var mock = new Mock<IProcessService>();

    mock.Setup(p => p.LaunchApplication(
            It.IsAny<string>(),
            It.IsAny<string[]?>(),
            It.IsAny<string?>(),
            It.IsAny<bool>(),
            It.IsAny<int>()))
        .Returns((string exe, string[]? args, string? dir, bool wait, int timeout) =>
            new LaunchResult(
                Success: true,
                ProcessId: 9999,
                WindowHandle: wait ? new nint(8888) : null));

    return mock;
}
```

---

## 9. Mocking Native P/Invoke Layer

### The Problem

Native P/Invoke calls are static and cannot be directly mocked:

```csharp
// This cannot be mocked
public bool IsValidWindow(nint handle)
{
    return NativeMethods.User32.IsWindow(handle);  // Static call
}
```

### Solution: Wrapper Interface Pattern

#### Step 1: Create a Wrapper Interface

```csharp
// File: Services/Interfaces/INativeWindowWrapper.cs
namespace WindowsAppManagerMcp.Services.Interfaces;

public interface INativeWindowWrapper
{
    bool IsWindow(nint handle);
    bool IsWindowVisible(nint handle);
    bool IsIconic(nint handle);
    bool IsZoomed(nint handle);
    bool GetWindowRect(nint handle, out RECT rect);
    bool SetWindowPos(nint handle, nint insertAfter,
        int x, int y, int width, int height, uint flags);
    bool ShowWindow(nint handle, int cmdShow);
    bool SetForegroundWindow(nint handle);
    nint GetForegroundWindow();
}
```

#### Step 2: Create Production Implementation

```csharp
// File: Services/NativeWindowWrapper.cs
public class NativeWindowWrapper : INativeWindowWrapper
{
    public bool IsWindow(nint handle) =>
        NativeMethods.User32.IsWindow(handle);

    public bool SetWindowPos(nint handle, nint insertAfter,
        int x, int y, int width, int height, uint flags) =>
        NativeMethods.User32.SetWindowPos(handle, insertAfter,
            x, y, width, height, flags);

    // ... implement all methods
}
```

#### Step 3: Inject and Mock in Tests

```csharp
[Fact]
public void MoveWindow_CallsSetWindowPos_WithCorrectParameters()
{
    // Arrange
    var mockNative = new Mock<INativeWindowWrapper>();
    mockNative.Setup(n => n.IsWindow(It.IsAny<nint>())).Returns(true);
    mockNative.Setup(n => n.SetWindowPos(
            It.IsAny<nint>(), It.IsAny<nint>(),
            It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<uint>()))
        .Returns(true);

    var sut = new WindowService(mockMonitorService.Object, mockNative.Object);

    // Act
    var result = sut.MoveWindow(handle, 100, 200);

    // Assert
    Assert.True(result);
    mockNative.Verify(n => n.SetWindowPos(
        handle, nint.Zero,
        100, 200, It.IsAny<int>(), It.IsAny<int>(),
        It.IsAny<uint>()), Times.Once);
}
```

---

## 10. Code Coverage

### Coverage Targets by Layer

| Layer | Target | Priority | Notes |
|-------|--------|----------|-------|
| Services | 80%+ | HIGH | Business logic |
| Models | 70%+ | MEDIUM | Data structures |
| Tools | 60%+ | LOW | Thin wrappers over services |
| Native | - | N/A | Mocked in unit tests; not excluded from the measured coverage |

### Configuring Coverlet

Add to test project `.csproj`:

```xml
<PropertyGroup>
    <CollectCoverage>true</CollectCoverage>
    <CoverletOutputFormat>cobertura</CoverletOutputFormat>
    <CoverletOutput>./coverage/</CoverletOutput>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.2" />
    <PackageReference Include="coverlet.msbuild" Version="6.0.2" />
</ItemGroup>
```

### Excluding Code from Coverage

```csharp
using System.Diagnostics.CodeAnalysis;

[ExcludeFromCodeCoverage]
public static class NativeMethods
{
    // P/Invoke declarations - can't unit test
}

[ExcludeFromCodeCoverage]
public class Program
{
    // Entry point - tested via integration tests
}
```

### Running Coverage

```bash
# Run tests with coverage (same filter as CI)
dotnet test --filter "Category!=Desktop" --collect:"XPlat Code Coverage" --results-directory ./TestResults

# Generate HTML report
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:./TestResults/**/coverage.cobertura.xml -targetdir:./TestResults/report

# Open report
start ./TestResults/report/index.html
```

CI fails the build when line coverage of the `Category!=Desktop` run drops below the
threshold in `.github/workflows/test.yml` (currently 59%). It measures the server assembly
(package `WindowsAppManagerMcp`), merged over the coverage files of every test project: a line
counts as covered when any project covers it. Lines are matched by full source path, because
coverlet writes file names relative to a source root that depends on what each project
instruments. Measured this way, the suite covers about 76% of the server's lines (October 2026); the
threshold has not been raised to match yet.

---

## 11. Common Anti-Patterns

### 1. Testing Implementation Details

**BAD**: Checking private state
```csharp
[Fact]
public void BadTest_ChecksPrivateState()
{
    var sut = new LayoutService(...);
    await sut.SavePresetAsync(preset);

    // BAD: Accessing private field
    var cache = typeof(LayoutService)
        .GetField("_presetCache", BindingFlags.NonPublic | BindingFlags.Instance)
        .GetValue(sut);
    Assert.Contains(preset.Name, (Dictionary<string, LayoutPreset>)cache);
}
```

**GOOD**: Test observable behavior
```csharp
[Fact]
public async Task GoodTest_VerifiesPublicBehavior()
{
    var sut = new LayoutService(...);
    await sut.SavePresetAsync(preset);

    // GOOD: Test through public interface
    var retrieved = sut.GetPreset(preset.Name);
    Assert.NotNull(retrieved);
    Assert.Equal(preset.Name, retrieved.Name);
}
```

### 2. Over-Mocking (Mocking the SUT)

**BAD**: Testing the mock framework
```csharp
[Fact]
public void BadTest_MocksTheSystemUnderTest()
{
    var mockSut = new Mock<IWindowService>();
    mockSut.Setup(w => w.MoveWindow(It.IsAny<nint>(), 100, 200)).Returns(true);

    var result = mockSut.Object.MoveWindow(handle, 100, 200);

    // This tests NOTHING - just verifies Moq works!
    Assert.True(result);
}
```

**GOOD**: Mock dependencies only
```csharp
[Fact]
public void GoodTest_MocksDependenciesOnly()
{
    var mockMonitor = new Mock<IMonitorService>();
    var sut = new WindowService(mockMonitor.Object);  // Real SUT

    var result = sut.MoveWindow(handle, 100, 200);
    // Now you're testing actual WindowService behavior
}
```

### 3. Brittle Tests (Order-Dependent)

**BAD**: Depends on collection order
```csharp
[Fact]
public void BadTest_DependsOnOrder()
{
    var windows = sut.GetAllWindows();
    Assert.Equal("Notepad", windows[0].ProcessName);  // Fragile!
    Assert.Equal("Chrome", windows[1].ProcessName);   // Fragile!
}
```

**GOOD**: Order-independent assertion
```csharp
[Fact]
public void GoodTest_OrderIndependent()
{
    var windows = sut.GetAllWindows();
    Assert.Contains(windows, w => w.ProcessName == "Notepad");
    Assert.Contains(windows, w => w.ProcessName == "Chrome");
}
```

### 4. Hidden Dependencies

**BAD**: Depends on actual file system
```csharp
[Fact]
public void BadTest_DependsOnFileSystem()
{
    var sut = new LayoutService(..., @"C:\RealPath\layouts");
    var presets = sut.GetAllPresets();  // Depends on actual files!
    Assert.NotEmpty(presets);
}
```

**GOOD**: Isolated test environment
```csharp
[Fact]
public void GoodTest_IsolatedFileSystem()
{
    var testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(testDir);
    try
    {
        File.WriteAllText(Path.Combine(testDir, "test.json"),
            JsonSerializer.Serialize(testPreset));

        var sut = new LayoutService(..., testDir);
        var presets = sut.GetAllPresets();

        Assert.Single(presets);
    }
    finally
    {
        Directory.Delete(testDir, recursive: true);
    }
}
```

### 5. Multiple Acts Per Test

**BAD**: Testing multiple behaviors
```csharp
[Fact]
public void BadTest_MultipleBehaviors()
{
    var sut = new WindowService(...);

    Assert.True(sut.MoveWindow(handle, 100, 200));
    Assert.True(sut.ResizeWindow(handle, 800, 600));
    Assert.True(sut.FocusWindow(handle));
}
```

**GOOD**: One behavior per test
```csharp
[Fact]
public void MoveWindow_WithValidHandle_ReturnsTrue() { ... }

[Fact]
public void ResizeWindow_WithValidHandle_ReturnsTrue() { ... }

[Fact]
public void FocusWindow_WithValidHandle_ReturnsTrue() { ... }
```

### Anti-Pattern Checklist

- [ ] Not testing the mock instead of the SUT
- [ ] Not testing private methods directly
- [ ] Not relying on test execution order
- [ ] Not depending on external state (files, network, time)
- [ ] Not having multiple acts testing different behaviors
- [ ] Not using `Thread.Sleep` in tests
- [ ] Not testing auto-generated code (records, properties)

---

## 12. Integration Testing

### When to Use Integration Tests

| Scenario | Test Type |
|----------|-----------|
| Single method, mocked dependencies | Unit Test |
| Multiple services working together | Integration Test |
| File system read/write cycle | Integration Test |
| Database operations | Integration Test |
| Full request/response flow | E2E Test |

### Service Interaction Tests

```csharp
public class LayoutServiceIntegrationTests : IDisposable
{
    private readonly string _testLayoutPath;
    private readonly Mock<IWindowService> _mockWindowService;
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly Mock<IProcessService> _mockProcessService;
    private readonly LayoutService _sut;

    public LayoutServiceIntegrationTests()
    {
        _testLayoutPath = Path.Combine(
            Path.GetTempPath(),
            $"WindowsAppManagerTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testLayoutPath);

        _mockWindowService = CreateMockWindowService();
        _mockMonitorService = CreateMockMonitorService();
        _mockProcessService = CreateMockProcessService();

        _sut = new LayoutService(
            _mockWindowService.Object,
            _mockMonitorService.Object,
            _mockProcessService.Object,
            _testLayoutPath);
    }

    [Fact]
    public async Task CaptureAndApplyLayout_RoundTrip_Success()
    {
        // Arrange
        var testWindows = new List<WindowInfo>
        {
            TestDataFactory.CreateWindowInfo(1, "Test Window", "testapp")
        };
        _mockWindowService.Setup(w => w.GetAllWindows(false))
            .Returns(testWindows);

        // Act - Capture
        var captured = await _sut.CaptureCurrentLayoutAsync("integration-test");

        // Act - Apply (simulates new session)
        _mockWindowService.Setup(w => w.GetAllWindows(true))
            .Returns(testWindows);
        var result = await _sut.ApplyPresetAsync("integration-test");

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.WindowsArranged);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testLayoutPath))
        {
            Directory.Delete(_testLayoutPath, recursive: true);
        }
    }
}
```

### File Persistence Tests

```csharp
public class LayoutPersistenceTests : IDisposable
{
    private readonly string _testPath;

    public LayoutPersistenceTests()
    {
        _testPath = Path.Combine(Path.GetTempPath(),
            $"LayoutTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testPath);
    }

    [Fact]
    public async Task SaveAndLoadPreset_PreservesAllData()
    {
        // Arrange
        var preset = TestDataFactory.CreateLayoutPreset(
            name: "persistence-test",
            description: "Testing JSON round-trip");

        var sut = CreateLayoutService(_testPath);

        // Act - Save
        await sut.SavePresetAsync(preset);

        // Act - Load (new instance forces disk read)
        var sut2 = CreateLayoutService(_testPath);
        var loaded = sut2.GetPreset("persistence-test");

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(preset.Name, loaded.Name);
        Assert.Equal(preset.Description, loaded.Description);
        Assert.Equal(preset.Placements.Count, loaded.Placements.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testPath))
            Directory.Delete(_testPath, recursive: true);
    }
}
```

---

## 13. Practical Examples

### Complete WindowServiceTests

```csharp
namespace WindowsAppManagerMcp.Tests.Unit.Services;

public class WindowServiceTests
{
    private readonly Mock<IMonitorService> _mockMonitorService;

    public WindowServiceTests()
    {
        _mockMonitorService = new Mock<IMonitorService>();
        SetupDefaultMonitor();
    }

    private void SetupDefaultMonitor()
    {
        var monitor = new MonitorInfo(
            Index: 0,
            DeviceName: @"\\.\DISPLAY1",
            IsPrimary: true,
            Bounds: new MonitorRect(0, 0, 1920, 1080),
            WorkArea: new MonitorRect(0, 0, 1920, 1040),
            ScaleFactor: 1.0);

        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });
        _mockMonitorService.Setup(m => m.GetPrimaryMonitor())
            .Returns(monitor);
        _mockMonitorService.Setup(m => m.GetMonitorIndex(It.IsAny<nint>()))
            .Returns(0);
    }

    private WindowService CreateSut() =>
        new WindowService(_mockMonitorService.Object);

    public class IsValidWindowMethod : WindowServiceTests
    {
        [Fact]
        public void WithZeroHandle_ReturnsFalse()
        {
            var sut = CreateSut();

            var result = sut.IsValidWindow(nint.Zero);

            Assert.False(result);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void WithInvalidHandle_ReturnsFalse(int handleValue)
        {
            var sut = CreateSut();

            var result = sut.IsValidWindow(new nint(handleValue));

            Assert.False(result);
        }
    }

    public class MoveWindowToMonitorMethod : WindowServiceTests
    {
        [Fact]
        public void WithInvalidMonitorIndex_ReturnsFalse()
        {
            var sut = CreateSut();
            var handle = new nint(12345);

            var result = sut.MoveWindowToMonitor(handle, 99);

            Assert.False(result);
        }
    }
}
```

### Complete LayoutServiceTests

```csharp
namespace WindowsAppManagerMcp.Tests.Unit.Services;

public class LayoutServiceTests : IDisposable
{
    private readonly Mock<IWindowService> _mockWindowService;
    private readonly Mock<IMonitorService> _mockMonitorService;
    private readonly Mock<IProcessService> _mockProcessService;
    private readonly string _testLayoutPath;

    public LayoutServiceTests()
    {
        _mockWindowService = new Mock<IWindowService>();
        _mockMonitorService = new Mock<IMonitorService>();
        _mockProcessService = new Mock<IProcessService>();
        _testLayoutPath = Path.Combine(
            Path.GetTempPath(),
            $"LayoutServiceTests_{Guid.NewGuid()}");

        SetupDefaultMocks();
    }

    private void SetupDefaultMocks()
    {
        var monitor = TestDataFactory.CreateMonitorInfo();

        _mockMonitorService.Setup(m => m.GetAllMonitors())
            .Returns(new List<MonitorInfo> { monitor });

        _mockWindowService.Setup(w => w.GetAllWindows(It.IsAny<bool>()))
            .Returns(new List<WindowInfo>());

        _mockWindowService.Setup(w => w.SetWindowBounds(
                It.IsAny<nint>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>()))
            .Returns(true);
    }

    private LayoutService CreateSut() => new LayoutService(
        _mockWindowService.Object,
        _mockMonitorService.Object,
        _mockProcessService.Object,
        _testLayoutPath);

    public class GetPresetMethod : LayoutServiceTests
    {
        [Fact]
        public void WhenNotExists_ReturnsNull()
        {
            var sut = CreateSut();

            var result = sut.GetPreset("nonexistent");

            Assert.Null(result);
        }

        [Fact]
        public async Task AfterSave_ReturnsPreset()
        {
            var sut = CreateSut();
            var preset = TestDataFactory.CreateLayoutPreset("test");
            await sut.SavePresetAsync(preset);

            var result = sut.GetPreset("test");

            Assert.NotNull(result);
            Assert.Equal("test", result.Name);
        }
    }

    public class SavePresetAsyncMethod : LayoutServiceTests
    {
        [Fact]
        public async Task WithValidPreset_CreatesJsonFile()
        {
            var sut = CreateSut();
            var preset = TestDataFactory.CreateLayoutPreset("save-test");

            var result = await sut.SavePresetAsync(preset);

            Assert.True(result);
            Assert.True(File.Exists(Path.Combine(_testLayoutPath, "save-test.json")));
        }
    }

    public class ApplyPresetAsyncMethod : LayoutServiceTests
    {
        [Fact]
        public async Task WhenPresetNotFound_ReturnsFailure()
        {
            var sut = CreateSut();

            var result = await sut.ApplyPresetAsync("nonexistent");

            Assert.False(result.Success);
        }

        [Fact]
        public async Task WithMatchingWindows_ArrangesWindows()
        {
            // Arrange
            var testWindow = TestDataFactory.CreateWindowInfo(
                processName: "testapp");
            _mockWindowService.Setup(w => w.GetAllWindows(true))
                .Returns(new List<WindowInfo> { testWindow });

            var sut = CreateSut();
            var preset = TestDataFactory.CreateLayoutPreset("apply-test");
            await sut.SavePresetAsync(preset);

            // Act
            var result = await sut.ApplyPresetAsync("apply-test");

            // Assert
            Assert.True(result.Success);
            _mockWindowService.Verify(
                w => w.SetWindowBounds(testWindow.Handle,
                    It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<int>(), It.IsAny<int>()),
                Times.Once);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_testLayoutPath))
        {
            Directory.Delete(_testLayoutPath, recursive: true);
        }
    }
}
```

---

## 14. Test Data Factories

### TestDataFactory Class

```csharp
// File: tests/WindowsAppManagerMcp.Tests/Fixtures/TestDataFactory.cs
namespace WindowsAppManagerMcp.Tests.Fixtures;

public static class TestDataFactory
{
    #region WindowInfo Factory

    public static WindowInfo CreateWindowInfo(
        int handleValue = 12345,
        string title = "Test Window",
        string processName = "testapp",
        int processId = 1000,
        int x = 100, int y = 100,
        int width = 800, int height = 600,
        WindowState state = WindowState.Normal,
        bool isVisible = true,
        int monitorIndex = 0)
    {
        return new WindowInfo(
            Handle: new nint(handleValue),
            Title: title,
            ProcessName: processName,
            ProcessId: processId,
            Bounds: new WindowRect(x, y, width, height),
            State: state,
            IsVisible: isVisible,
            MonitorIndex: monitorIndex);
    }

    public static IReadOnlyList<WindowInfo> CreateTypicalWindowSet()
    {
        return new List<WindowInfo>
        {
            CreateWindowInfo(1001, "Document.txt - Notepad", "notepad", 1001),
            CreateWindowInfo(1002, "Google Chrome", "chrome", 1002, 200, 100, 1200, 800),
            CreateWindowInfo(1003, "Visual Studio Code", "Code", 1003, 0, 0, 960, 1040),
            CreateWindowInfo(1004, "Windows Terminal", "WindowsTerminal", 1004, 960, 520, 960, 520)
        };
    }

    #endregion

    #region MonitorInfo Factory

    public static MonitorInfo CreateMonitorInfo(
        int index = 0,
        string deviceName = @"\\.\DISPLAY1",
        bool isPrimary = true,
        int boundsWidth = 1920, int boundsHeight = 1080,
        int workAreaHeight = 1040,
        double scaleFactor = 1.0)
    {
        return new MonitorInfo(
            Index: index,
            DeviceName: deviceName,
            IsPrimary: isPrimary,
            Bounds: new MonitorRect(0, 0, boundsWidth, boundsHeight),
            WorkArea: new MonitorRect(0, 0, boundsWidth, workAreaHeight),
            ScaleFactor: scaleFactor);
    }

    public static IReadOnlyList<MonitorInfo> CreateDualMonitorSetup()
    {
        return new List<MonitorInfo>
        {
            CreateMonitorInfo(0, @"\\.\DISPLAY1", true),
            CreateMonitorInfo(1, @"\\.\DISPLAY2", false, 2560, 1440, 1400, 1.25)
        };
    }

    #endregion

    #region LayoutPreset Factory

    public static LayoutPreset CreateLayoutPreset(
        string name = "test-preset",
        string? description = "Test layout preset",
        List<WindowPlacement>? placements = null)
    {
        return new LayoutPreset(
            Name: name,
            Description: description,
            Placements: placements ?? new List<WindowPlacement>
            {
                CreateWindowPlacement()
            });
    }

    public static WindowPlacement CreateWindowPlacement(
        string? titleContains = null,
        string processName = "testapp",
        int monitorIndex = 0,
        double x = 0, double y = 0,
        double width = 0.5, double height = 1.0)
    {
        return new WindowPlacement(
            Matcher: new WindowMatcher(titleContains, processName),
            MonitorIndex: monitorIndex,
            Position: new RelativePosition(x, y, width, height));
    }

    #endregion
}
```

---

## 15. Quick Reference Checklists

### New Test Checklist

- [ ] Test file in correct location (`tests/Unit/Services/` or `tests/Integration/`)
- [ ] Test class name matches `{ClassName}Tests`
- [ ] Test method follows naming convention
- [ ] Uses AAA pattern with clear sections
- [ ] Only one Act per test
- [ ] Asserts are specific and meaningful
- [ ] No dependencies on external state
- [ ] Cleanup implemented if test creates resources
- [ ] No real desktop side effects (input, process launch or kill, window changes); if
      unavoidable, the test uses `[DesktopFact]` or `[DesktopTheory]`

### Mock Setup Checklist

- [ ] Mock only direct dependencies, not SUT
- [ ] Setup returns sensible defaults
- [ ] Verify important interactions
- [ ] Use `It.IsAny<>` where exact value doesn't matter
- [ ] Consider `MockBehavior.Strict` for critical tests

### Code Review Checklist for Tests

- [ ] Tests cover happy path
- [ ] Tests cover error/edge cases
- [ ] Tests cover boundary conditions
- [ ] No duplicate test coverage
- [ ] Tests are deterministic (no random/time dependencies)
- [ ] Tests run independently
- [ ] Tests run fast (< 1 second each)
- [ ] Test names clearly describe the scenario

---

## 16. Test Project Setup

### Project File Template

```xml
<!-- tests/WindowsAppManagerMcp.Tests/WindowsAppManagerMcp.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.8.1" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.1">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Moq" Version="4.20.70" />
    <PackageReference Include="FluentAssertions" Version="6.12.0" />
    <PackageReference Include="coverlet.collector" Version="6.0.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\WindowsAppManagerMcp\WindowsAppManagerMcp.csproj" />
    <!-- The desktop test gate, shared with WindowsAppManagerMcp.E2ETests -->
    <ProjectReference Include="..\WindowsAppManagerMcp.Tests.Infrastructure\WindowsAppManagerMcp.Tests.Infrastructure.csproj" />
  </ItemGroup>

</Project>
```

### GlobalUsings.cs Template

```csharp
// tests/WindowsAppManagerMcp.Tests/GlobalUsings.cs
global using Xunit;
global using Moq;
global using FluentAssertions;

global using WindowsAppManagerMcp.Models;
global using WindowsAppManagerMcp.Services;
global using WindowsAppManagerMcp.Services.Interfaces;
global using WindowsAppManagerMcp.Tests.Fixtures;
```

### Directory Structure to Create

```
tests/
└── WindowsAppManagerMcp.Tests/
    ├── WindowsAppManagerMcp.Tests.csproj
    ├── GlobalUsings.cs
    ├── Unit/
    │   ├── Services/
    │   │   ├── WindowServiceTests.cs
    │   │   ├── MonitorServiceTests.cs
    │   │   ├── ProcessServiceTests.cs
    │   │   └── LayoutServiceTests.cs
    │   └── Models/
    ├── Integration/
    │   └── LayoutPersistenceTests.cs
    └── Fixtures/
        └── TestDataFactory.cs
```

---

## 17. Running Tests

### Command Line

```bash
# Run all tests (desktop tests are reported as Skipped)
dotnet test

# Run what CI runs (desktop tests left out entirely)
dotnet test --filter "Category!=Desktop"

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"

# Run specific test class
dotnet test --filter "FullyQualifiedName~WindowServiceTests"

# Run specific test method
dotnet test --filter "FullyQualifiedName~IsValidWindow_WithZeroHandle"

# Run tests matching pattern
dotnet test --filter "ClassName~Layout"

# Run with coverage
dotnet test --filter "Category!=Desktop" --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

### Desktop Tests

Most tests mock the native layer: `INativeInputWrapper` for mouse and keyboard input and
`INativeWindowWrapper` for window changes. Some tests use the real stack read-only: they read
real system state (the monitor, window and process lists, the UI Automation root in `UiaTreeTests`
and `UIAutomationWrapperTests`, a small screen region in `ScreenCaptureWrapperIntegrationTests`,
process tokens, the input desktop, the session state and the agent's host chain in
`SecurityProbeIntegrationTests`) but change nothing. Test classes that reach the real UI Automation
client belong to the `RealUiAutomationCollection` (`[Collection(RealUiAutomationCollection.Name)]`),
which runs them one at a time: the client's first call in a process fails with E_FAIL when two
threads make it at once. `RealUiAutomationCollectionTests` scans the test assembly and fails for a
class that calls the real client (the production `UiaTree` or `UIAutomationWrapper`,
`UiaClientStartup.Shared` or `AutomationElement`) outside that collection, directly or through a
helper in the test assembly (a fixture factory, another class's helper, their lambdas, async
bodies and static constructors). Calls through an interface or a virtual method are not followed,
so keep real-client factories non-virtual.

Process launches and kills go through a mocked `INativeProcessWrapper`, so no default test starts
or stops a real process. A test that really needs a side effect like that must be marked with a
desktop gate attribute
(`tests/WindowsAppManagerMcp.Tests.Infrastructure/DesktopTestAttributes.cs`, a small project
that the unit tests and the end-to-end tests share). There are two tiers:

| Attribute | Tier | Runs when | Traits |
|-----------|------|-----------|--------|
| `[DesktopFact]`, `[DesktopTheory]` | Input: may start or kill processes, send real input, focus, move or close windows | `WAM_ALLOW_DESKTOP_TESTS=1` | `Category=Desktop` |
| `[DesktopNoInputFact]`, `[DesktopNoInputTheory]` | No input: starts only the suite's own fixture app and reads or captures it | `WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1` or `WAM_ALLOW_DESKTOP_TESTS=1` | `Category=Desktop`, `Category=DesktopNoInput` |

- **The attributes skip the test unless its variable is exactly `1`**. The gate is in the
  attribute, so it holds for every runner: plain `dotnet test`, any `--filter`, IDE test
  explorers and coverage tools. The no-input opt-in never unlocks an input test.
- **They report the traits above**, so you can select or exclude them with `--filter`. A filter
  treats a trait with two values as matching either, so `Category!=Desktop` leaves both tiers out,
  `Category=Desktop` selects both and `Category=DesktopNoInput` selects the no-input tier alone.

Besides the gate's canaries in `DesktopTestGateTests`, which only check that the gate let them run,
the desktop tests are the fixture app scenarios (see
[End-to-end tests against the fixture app](#end-to-end-tests-against-the-fixture-app)). An input
test brings a window to the front and types into it, so save your work before opting in, and run
them deliberately:

```powershell
# PowerShell: the no-input tier only
$env:WAM_ALLOW_DESKTOP_NOINPUT_TESTS = "1"
dotnet test --filter "Category=DesktopNoInput"
Remove-Item Env:WAM_ALLOW_DESKTOP_NOINPUT_TESTS

# PowerShell: both tiers
$env:WAM_ALLOW_DESKTOP_TESTS = "1"
dotnet test --filter "Category=Desktop"
Remove-Item Env:WAM_ALLOW_DESKTOP_TESTS
```

```bash
# Git Bash
WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1 dotnet test --filter "Category=DesktopNoInput"
WAM_ALLOW_DESKTOP_TESTS=1 dotnet test --filter "Category=Desktop"
```

Rules for new tests:

- Do not send real input, start or kill processes, or move, focus or close real windows
  from a `[Fact]` or `[Theory]`. Mock the wrapper instead (see
  [Mocking Native P/Invoke Layer](#9-mocking-native-pinvoke-layer)).
- Build `ProcessService` with a mocked `INativeProcessWrapper` and `IExecutableResolver`, as
  `CreateSut` in `ProcessServiceTests` and `ProcessKillTests` does. Without them the constructor
  falls back to the real `NativeProcessWrapper` and `ExecutableResolver`. `LaunchPlanningTests`
  only calls `PlanLaunch`, which never starts a process, and still passes a strict
  `INativeProcessWrapper` mock, so an accidental launch or kill throws.
- To read real windows, wrap the real `NativeWindowWrapper` in `ReadOnlyNativeWindowWrapper`, as
  `CreateReadOnlySut` in `WindowServiceTests` does. It forwards the queries and throws on every
  call that would move, show, focus or close a window.
- Never call `KillProcess` on a real system process, even to check that the guard refuses it.
- A plain `[Trait("Category", "Desktop")]` (or `DesktopNoInput`) is not enough:
  `DesktopTestGateTests` fails if a test carries that trait without a gate attribute.
- A desktop test that needs a real app uses the fixture app through the E2E harness, never an app
  of the developer's (Notepad, a browser, a terminal).
- `NativeCallGuardTests` scans the server assembly and fails if anything other than
  `NativeInputWrapper` calls the `SendInput`/`SetCursorPos` APIs, anything other than
  `NativeWindowWrapper` calls the window-changing user32 APIs, or anything other than
  `NativeProcessWrapper` calls `Process.Start`, `Kill` or `CloseMainWindow`
  (`ProcessStartAndKill_AreOnlyCalledByNativeProcessWrapper`). It also fails if the server
  declares a P/Invoke that starts or terminates processes directly, such as `CreateProcess`,
  `ShellExecute` or `TerminateProcess` (`NoPlatformInvoke_StartsOrTerminatesProcesses`). New
  process code goes through `INativeProcessWrapper`.
- `NativeCallGuardTests` also works as an allow-list. Outside the action wrappers the tests always
  replace (`NativeWindowWrapper`, `NativeInputWrapper`, `NativeProcessWrapper`,
  `NativeThreadInputWrapper` and `NativeHotkeyWrapper`), code may call only the read-only
  `NativeMethods` APIs listed in `ReadOnlyNativeApis`, and P/Invoke declarations must live in
  `NativeMethods`. The queries of `NativeWindowWrapper` that `ReadOnlyNativeWindowWrapper` forwards
  may reach only those APIs and the window reads in `WindowQueryNativeApis`, followed through their
  lambdas and helpers. `AttachThreadInput` is pinned to `NativeThreadInputWrapper`, and the last Win32
  error is read only inside the Native wrapper that made the call (also through framework members
  that read it, such as `new Win32Exception()`). Outside `UIAutomationWrapper`, UI Automation
  elements, patterns and text ranges may only be read (`ReadOnlyUiAutomationMethods`): no `Invoke`,
  `SetValue`, `Toggle`, `Select`, `SetFocus` or other action. A new native call or UI Automation action therefore fails the guard
  until it goes through a mockable wrapper or is reviewed onto a list.
- Never register a real global hotkey, and never open the real shared pause. The pause hotkey (roadmap
  M1.10) registers its chord, and the input session opens the pause every server on the desktop shares
  (a named event the user's own servers follow), only through `INativeHotkeyWrapper`, and only when
  `InputSessionStartup.StartAsync` runs; tests use `FakeHotkeyWrapper`, whose message loop reads an
  in-memory queue (`Press()` posts WM_HOTKEY) and whose shared pause and chord marks live in a
  `FakeNamedObjects` two fakes can share (two servers on one desktop), and never start the hosted
  service with the real wrapper. A session that is not shared keeps its pause to itself, so
  `new InputSession(...)` touches nothing; `FakeDesktopPauseFlag` stands in for the shared pause.
  `HotkeyMethods_AreOnlyCalledByNativeHotkeyWrapper` pins the RegisterHotKey and GetMessage calls to
  that wrapper. `NamedWaitHandles_AreOnlyOpenedByNativeHotkeyWrapper` pins to it every constructor of a
  `WaitHandle` type (`EventWaitHandle`, `Mutex`, `Semaphore`) that takes a name, every `OpenExisting` and
  `TryOpenExisting`, the `System.Threading.AccessControl` factories (`EventWaitHandleAcl`, `MutexAcl`,
  `SemaphoreAcl`) and any method that returns a wait handle and takes a string, and
  `TheNamesOfTheSharedPauseAndTheChordMarks_AreOnlyUsedByNativeHotkeyWrapper` pins the names of the shared
  pause and the chord marks themselves, so no other code can open the objects every server on the
  desktop shares. The E2E `WrapperCatalog` classifies the wrapper's members, and the shared pause's Set
  and Reset, as `GlobalHotkey`, which no tier allows.
- For the input session (pause, held keys, stuck-input recovery), use `FakeInputWrapper`: it records
  every SendInput batch, keeps a key state the way Windows does (`Press()` and `Lift()` stand in for
  the user's fingers), and can insert fewer events than asked, throw or block, so short counts,
  exceptions and a pause in the middle of a batch can be tested without touching the desktop.
- The guards scan only the server assembly. They do not see code in the test project, so the
  rules above are up to you there.
- Keep timing tests far from the times they tell apart, because the default run is parallel and CI
  runners are slow: run a step whose timing matters on a dedicated thread, not on the thread pool;
  bound a time by what the test measured (for example the logged start of a call), not by the value
  passed to `Task.Delay`; and give a call that must answer before a deadline a deadline far above thread
  start-up (seconds, not 100 ms). Prefer a fake clock (`ManualClock`) or an explicit signal from the code
  under test over sleeping.

### End-to-end tests against the fixture app

`tests/WindowsAppManagerMcp.E2ETests` tests the real server services against an app the suite
owns, `tests/FixtureApp` (WamFixtureApp, a small WPF app). This is where Windows behaviour that
mocks cannot reproduce gets tested: the foreground lock, UI Automation trees, captures and DPI.

**The fixture app** has fixed AutomationIds (`FixtureContract.Ids`): an edit box, a check box, a
combo box, a button that opens a modal dialog (the main window is disabled while it is open), a
button that opens a document window that shows a Yes/No/Cancel save prompt when it is closed with
unsaved changes, a context menu, three solid color swatches and a virtualized list of 10,000
items. It has no WebView2 pane yet: that would add a package and a runtime dependency to every
build. Its command line (`FixtureOptions`) sets an instance token that appears in every window
title, shows the windows without activating them, opens the modal or the document at start, and
sets the process DPI awareness. A watchdog thread ends the fixture when the test host exits or
after its maximum lifetime (5 minutes by default), so a crashed run cannot leave it behind.

**The event log.** The fixture writes one JSON object per line to the file given with
`--event-log`: first a `ready` event with its process id, window handle, DPI, window and client
rectangles and the screen rectangle of every element, then every change it receives (text, check
box, selection, buttons, focus, modal, document, context menu). It is the fixture's own record of
what reached it, independent of what the server reports. `FixtureContract.Events` documents the
events; the harness reads them with `FixtureEventLogReader`.

**The harness.** `E2ESession.Start(tier, output)` checks the tier's opt-in again, starts the fixture
(`FixtureAppSession`), waits for its ready event, builds the server services as Program.cs registers
them (`ServerHarness`) and finds the fixture's main window through find_windows. Dispose kills the
fixture.

Every native wrapper of the server sits behind a fail-closed guard (`WrapperGuard`). That includes
the process and monitor wrappers that ProcessService and MonitorService would otherwise create
themselves: `ServerHarness` registers them, so the container passes the guarded ones in. An ordinary
test (`ServerHarnessTests`) resolves every service and fails if any of them holds a native wrapper
outside the guard. A process handle returned by `OpenProcess` is guarded as well. The guard does not
decide from member names: `WrapperCatalog` classifies every wrapper member explicitly (read, window
action, input, process action, process start, thread-input attach or global hotkey). A member it does not list is refused in every tier,
and `WrapperCatalogTests` fails until a new wrapper or member is classified. When you merge a change
that adds wrapper members, classify them there.

- In the no-input tier the server can only read. Input, window changes, process closes and kills
  throw `E2EGuardException` before they reach the wrapper.
- In the input tier the server can act only on the fixture process:
  - keyboard input goes out only while a fixture window is in front;
  - mouse input goes out only while the cursor is over a fixture window, and a batch that moves the
    pointer (a click carries an absolute move to its point) only when the move ends over a fixture window;
  - window calls go through only when every window they name is a fixture window;
  - closes and kills apply only to the fixture process, checked by id and process name.

  Two narrow exceptions serve verified activation (EnsureForeground): a batch of zero-distance mouse
  moves, which moves nothing and presses nothing, may be sent wherever the cursor is; and the server may
  attach its own calling thread's input to another thread (`AttachThreadInput`, never two other threads
  to each other), because the window calls it makes while attached are checked as window actions.

  The check runs before every `SendInput` batch, so a window that comes to the front mid-text stops
  the typing. It is a second, independent layer under the server's own input guard (roadmap M1.1 and
  M1.5, `InputTargetGuard`), so a bug in that guard cannot send input to a window outside the fixture;
  `FixtureInputGuardTests` checks the server's guard itself against the fixture.
- In both tiers, starting a process (including `where.exe`) is refused. CDP (`ICdpService`) refuses
  every call, because it talks to a browser's debug port over the network, outside the native
  wrappers, and no fixture has one yet.

UI Automation elements come back from the wrapper as objects that services only read: NativeCallGuardTests
forbids acting pattern calls outside the Native wrappers. The harness finds the fixture exe from a
path the build records in the test assembly; set `WAM_FIXTURE_APP_PATH` to use another build of
`WamFixtureApp.exe` (for example in Windows Sandbox). The harness refuses any exe with another name.

**Writing a scenario.** Put it in `Scenarios/`, mark it `[DesktopNoInputFact]` when it only reads or
captures and `[DesktopFact]` when it acts, and start from `E2ESession`:

```csharp
[DesktopNoInputFact]
public void GetUIElements_SeesTheEditBox()
{
    using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);

    var result = e2e.Server.UiElements.GetUIElements(e2e.Fixture.MainWindowHandle);

    UiaReads.Flatten(result.Elements).Should().Contain(e => e.AutomationId == FixtureContract.Ids.EditBox);
}
```

Compare the server's answers with the ready event (`e2e.Fixture.Ready`) and the event log
(`e2e.Fixture.WaitForEvent(...)`), not with fixed coordinates: positions depend on the monitor and
its DPI. A scenario that needs a kind of window the WPF fixture cannot provide may create its own,
as `MirroredWindowCaptureTests` does with `MirroredTestWindow`: a borderless popup of the test
process, shown without activation and destroyed in `Dispose`. `E2ETestGateTests` fails if a test outside `HarnessTests/` has no gate attribute. Tests in
`HarnessTests/` are ordinary tests of the harness and must never start the fixture.

**Running them.** Locally only with an opt-in, as above. On GitHub, `.github/workflows/e2e.yml`
runs them on demand (Actions, "E2E", "Run workflow"), with a choice of tier and runner. It first
checks that the runner has an interactive desktop and otherwise ends with a notice instead of
running anything; use a self-hosted runner in a logged-on desktop session then. It fails when no
test ran, so an opt-in that did not reach the tests cannot pass as green.

### Tool surface snapshot

`Contract/ToolSurfaceSnapshotTests` tests the server the way an agent sees it. It lists the tools of an
in-process server with the production registrations (`ServiceRegistration.AddWindowsAppManagerTools`, the
call Program.cs makes) and compares every tool object, as the server sends it (name, title, description,
annotations, inputSchema, outputSchema), with the checked-in
`tests/WindowsAppManagerMcp.Tests/Contract/tools.snapshot.json`, sorted by tool name. A failure names the
added, removed and changed tools and the JSON paths that changed.

When a change to the tool surface is intended (a new tool or parameter, a new default, a description, an
annotation, an SDK upgrade that changes the schemas), regenerate the snapshot and the generated docs in one
run, and review the diff before committing it:

```powershell
# PowerShell
$env:UPDATE_SNAPSHOTS = "1"
dotnet test --filter "FullyQualifiedName~ToolSurfaceSnapshotTests|FullyQualifiedName~GeneratedToolDocsTests"
Remove-Item Env:UPDATE_SNAPSHOTS
git diff
```

```bash
# Git Bash
UPDATE_SNAPSHOTS=1 dotnet test --filter "FullyQualifiedName~ToolSurfaceSnapshotTests|FullyQualifiedName~GeneratedToolDocsTests"
```

`UPDATE_SNAPSHOTS=1` only makes these tests rewrite their files instead of failing: the snapshot, and the
generated regions of README.md, docs/TOOLS.md and SKILL.md. It changes nothing else about a run (desktop
tests stay skipped). Without it, CI fails on a stale snapshot or table.

**The CI guard on the built exes.** A version or commit check cannot tell whether an exe serves the tools
its sources define. `.github/workflows/build.yml` therefore runs `scripts/Test-ToolsSnapshot.ps1`, which
starts an exe (with `WAM_PAUSE_HOTKEY=none`), sends `initialize` and `tools/list` over stdio, closes it, and
compares every tool object with the snapshot. It checks the exe the workflow has just published from the
checkout, always, and the committed `bin/WindowsAppManagerMcp.exe` with `-OnlyIfCurrent`: only when that exe
is a build of the current build inputs (the checks of `scripts/Test-PluginBinary.ps1`); a stale committed exe
is expected until the next release and is skipped with a notice. The script runs the server, so it is not
part of `dotnet test`; `ToolSurfaceSnapshotTests` checks that the workflow still calls it on both exes.

### Generated tool tables

The tool tables of the docs are generated into marked regions, between `<!-- tools:begin ID -->` and
`<!-- tools:end ID -->`, from the server's tools/list and `Fixtures/Docs/ToolDocCatalog.cs`
(`Contract/GeneratedToolDocsTests`, `Fixtures/Docs/ToolDocGenerator.cs`):

| File | Region | Contents |
|------|--------|----------|
| README.md | `tool-table` | "Available Tools (N)" and the category table |
| docs/TOOLS.md | `summary` | The introduction with the tool count, and the quick reference |
| docs/TOOLS.md | `params:TOOL` | The parameter table of each tool, and a field table for each array-of-objects parameter |
| SKILL.md | `count` | The sentence with the tool count |
| SKILL.md | `tool-tables` | The whole "Available Tools" section: a table per category with summaries and key parameters |

Tool names, counts, categories and the parameter tables' names, order, types, required flags and defaults
are generated. ToolDocCatalog holds what tools/list does not have: each tool's category, its one-line summary
and the key parameters SKILL.md names (the test checks they are real parameters). The Description cells of the
parameter tables, and a Default cell that explains a null default in words ("primary", "temp file"), are
written by hand in docs/TOOLS.md and kept when a table is regenerated; a new parameter starts with the
description from its schema. Everything outside the regions (the prose, examples and responses) is written by
hand.

To add a tool to the docs:

1. Add its entry to `ToolDocCatalog` (category, summary, key parameters).
2. Add its `### tool_name` section to docs/TOOLS.md, under its category's `##` heading, in catalog order,
   with an empty region pair: `<!-- tools:begin params:tool_name -->` and `<!-- tools:end params:tool_name -->`.
3. Run the tests with `UPDATE_SNAPSHOTS=1` (above), then edit the generated parameter descriptions in
   docs/TOOLS.md if they need it.

### Every tool over MCP and payload budgets

`Contract/EveryToolContractTests` calls every served tool through an in-process server with the production
tool and filter registrations and mocked services (`Fixtures/Contract/ContractServices.cs`; nothing native
runs). The typical call of each tool must return JSON content with `success` true and no `isError`, and a
capture its image block first. The failing call (the services report invalid handles, errors and empty lists)
must return `isError` equal to `!success`. Roadmap M2.1 makes every tool do that; until then the tools that
still return `isError: false` on failure, or a null result, are listed in `IsErrorPendingM21` and
`NullResultPendingM21`. These lists can only shrink: a listed tool that reports `isError` fails until its
entry is removed. A new tool needs typical arguments in `EveryToolContractTests.Arguments`.

`Contract/ToolPayloadBudgetTests` measures the typical result of every tool on `TypicalDesktop` (two
monitors, 15 windows, 40 processes, a 120-element UI Automation tree, a 15,000-character article, five layout
presets) and holds it to a per-tool token budget. Text is estimated at 4 UTF-8 bytes per token; a capture's
image at width × height / 750 tokens, at most 3,300 (the default 1568-pixel long edge). Each budget is about
one and a half times the measured size, at least 100 tokens; the measured size is in a comment next to it.
The largest typical results, measured in October 2026:

| Tool | Typical result | Tokens | Budget |
|------|----------------|-------:|-------:|
| `capture_with_elements` | Metadata of 120 elements (the image is budgeted separately) | 6,026 | 9,500 |
| `get_ui_elements` | 120 elements at the default depth | 5,397 | 8,500 |
| `get_browser_content` | A 15,000-character article | 3,815 | 6,000 |
| `list_processes` | 40 processes | 1,328 | 2,000 |
| `find_windows`, `get_all_windows` | 15 windows | 989 | 1,500 |

Every other typical result is under 300 tokens. When a change makes a result larger on purpose, raise its
budget in the same change (the failure message gives the new size); when a change shrinks one a lot, lower it.

### Protocol revisions, progress and cancellation

The server uses the stable MCP C# SDK, `ModelContextProtocol` 2.2.0. It speaks two kinds of protocol revision,
and the tests that depend on the difference run on both:

- **2026-07-28**, which the SDK's own client prefers, so `InProcessMcpServer` negotiates it by default
  (`InProcessMcpServer.LatestRevision`): `server/discover` instead of the `initialize` handshake, and
  elicitation as a multi-round-trip request (the tool call returns `input_required`, and the client retries it
  with the answer).
- **The earlier revisions with the `initialize` handshake**, which stdio clients such as Claude Code and the CI
  guard (`scripts/Test-ToolsSnapshot.ps1`, `2025-06-18`) use: elicitation is a request from the server while the
  call is open. Pass `InProcessMcpServer.WithRevision(options, InProcessMcpServer.InitializeRevision)` as the
  client options to test this path.

`Contract/ProtocolRevisionContractTests` checks that both list exactly the snapshot's tools and return captures
and tool errors alike. The elicitation (resume_input), cancellation and stdin EOF tests of
`InputControlToolContractTests`, and the progress and cancellation tests of `UIElementToolContractTests` and
`LongRunningToolContractTests`, are theories over `Revisions` (both paths). A new test of something
protocol-level (elicitation, progress, cancellation, notifications) should be one too.

**Progress.** Check progress notifications where the server wrote them: `InProcessMcpServer.ServerMessages`
records every message the server writes, and `server.WaitForProgressSent(n)` waits for `n` progress
notifications and checks that each one came before the call's result. The SDK client hands notifications to their
handlers concurrently, in any order, and drops a progress notification whose handler runs after the call's
result arrived, so an `IProgress` given to `Client.CallToolAsync` can see fewer reports, or the same reports in
another order. Services report `OperationProgress`; test them with `Fixtures/RecordingProgress<T>`, which records
on the reporting thread.

**Cancellation.** Send `notifications/cancelled` with the call's JSON-RPC id, which a test filter captures (see
`StartAsync` in `LongRunningToolContractTests`), as any MCP client does, instead of relying on when the SDK
client sends it for its own token. A tool that changes windows waits for the input gate first, and the gate is
shared by every test in the process: wait until the tool itself runs before you cancel, or the cancellation only
ends the wait for the gate. A cancelled call gets no result, and the call log reports it as `cancelled`.

### Visual Studio

| Action | Shortcut |
|--------|----------|
| Open Test Explorer | `Ctrl+E, T` |
| Run All Tests | `Ctrl+R, A` |
| Run Tests in Current Context | `Ctrl+R, T` |
| Debug Tests in Current Context | `Ctrl+R, Ctrl+T` |
| Run Failed Tests | `Ctrl+R, F` |

### Continuous Integration

`.github/workflows/test.yml` runs the suite on every push and pull request to `main`. It runs
every test project, leaves desktop tests out, kills a test that hangs for 5 minutes, caps the job
at 20 minutes, and enforces a line-coverage threshold. A second step then runs only the desktop
tests with both opt-in variables unset and fails unless every one of them is reported as skipped,
at least one exists, and the fixture app scenarios are among them, so CI proves the gate holds on
the runner. The fixture app scenarios themselves run only in `.github/workflows/e2e.yml`, on demand
(see [End-to-end tests against the fixture app](#end-to-end-tests-against-the-fixture-app)):

```yaml
# GitHub Actions (abridged from .github/workflows/test.yml)
- name: Test with coverage
  run: |
    dotnet test --no-build --configuration Release `
      --filter "Category!=Desktop" `
      --blame-hang-timeout 5m `
      --collect:"XPlat Code Coverage" `
      --results-directory ./TestResults `
      --logger trx

- name: Upload test results
  uses: actions/upload-artifact@v4
  if: always()
  with:
    name: test-results
    path: TestResults/**/*.trx
```

`.github/workflows/build.yml` also starts the exe it publishes, and the committed plugin exe when it is a
build of the current sources, and compares their tools/list with the snapshot (see
[Tool surface snapshot](#tool-surface-snapshot)).

---

## References

### Official Documentation
- [Microsoft: Unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)
- [Microsoft: Unit testing C# with xUnit](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-csharp-with-xunit)

### TDD Resources
- [Red, Green, Refactor | Codecademy](https://www.codecademy.com/article/tdd-red-green-refactor)
- [The Cycles of TDD | Uncle Bob](https://blog.cleancoder.com/uncle-bob/2014/12/17/TheCyclesOfTDD.html)

### Test Doubles
- [Mocks Aren't Stubs | Martin Fowler](https://martinfowler.com/articles/mocksArentStubs.html)
- [Test Double | Martin Fowler](https://martinfowler.com/bliki/TestDouble.html)

### Test Pyramid
- [Testing Pyramid | CircleCI](https://circleci.com/blog/testing-pyramid/)
- [Modern Test Pyramid Guide 2025](https://fullscale.io/blog/modern-test-pyramid-guide/)

---

*Last updated: January 2025*
