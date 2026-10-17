# Testing Guide

> **WindowsAppManagerMcp** | .NET 10.0 | xUnit | Moq

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
    │   ├── Fixtures/              # TestDataFactory, RepositoryFiles, RawMcpConnection, FakeCdp, ...
    │   ├── Integration/           # DI, file persistence, layout service, screen capture (read-only)
    │   └── Unit/
    │       ├── Architecture/      # NativeCallGuardTests (IL scan for native calls)
    │       ├── Infrastructure/    # DesktopTestGateTests, InProcessMcpServerTests
    │       ├── Models/
    │       ├── Native/
    │       ├── Services/
    │       └── Tools/
    ├── WindowsAppManagerMcp.Tests.Infrastructure/  # Shared desktop test gate ([DesktopFact], [DesktopNoInputFact], ...)
    │                                               # and the in-process MCP test host (InProcessMcpServer)
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
| WinRT projection | - | N/A | The OCR types CsWinRT generates into the server assembly (`Generated Files/CsWinRT`, roadmap M6.3): not the server's code, so CI's coverage step leaves them out |

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
    <TargetFramework>net10.0-windows</TargetFramework>
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
real system state (the monitor, window and process lists, the UI Automation root in `UiaTreeTests`,
a small screen region in `ScreenCaptureWrapperIntegrationTests`,
process tokens, the input desktop, the session state and the agent's host chain in
`SecurityProbeIntegrationTests`) but change nothing. Test classes that reach the real UI Automation
client belong to the `RealUiAutomationCollection` (`[Collection(RealUiAutomationCollection.Name)]`),
which runs them one at a time: the client's first call in a process fails with E_FAIL when two
threads make it at once. `RealUiAutomationCollectionTests` scans the test assembly and fails for a
class that calls the real client (the production `UiaTree`, `Uia3Tree` or `UIAutomationWrapper`,
`UiaClientStartup.Shared`, `AutomationElement` or the COM client's `CUIAutomation*` classes) outside that
collection, directly or through a
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
and one screen read in `ScreenCaptureWrapperIntegrationTests`
(`CaptureScreenRegion_ReportsTheRegionOriginAsScreenOrigin`), the desktop tests are the fixture app
scenarios (see
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
- One exception, approved by the repository owner on 2026-10-15 for the M5 browser bridge: a desktop
  check of the CDP tools may start a separate debug instance of Edge or Chrome (launch_browser_with_debug)
  with a throwaway `--user-data-dir` under `%TEMP%`, on an explicit free port or port 0 (the browser picks a free
  port, roadmap M5.1). Before it starts, record
  every top-level window and every msedge, chrome and brave process; if the launch hands off to a browser
  that was already running, stop and close nothing. Open only `data:` URLs, `file://` pages the check
  wrote itself, or a `127.0.0.1` listener (no network), act only on that process's windows and targets,
  and close it by its process tree, never by window title. Then check that the developer's own browser
  processes are unchanged and delete the profile. The developer's browser windows, tabs and profile are
  never read, acted on or closed. The steps are in
  [Desktop check: M5 browser bridge](#desktop-check-m5-browser-bridge).
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
  `NativeThreadInputWrapper`, `NativeHotkeyWrapper` and `NativeClipboardWrapper`), code may call only the read-only
  `NativeMethods` APIs listed in `ReadOnlyNativeApis`, and P/Invoke declarations must live in
  `NativeMethods`. The queries of `NativeWindowWrapper` that `ReadOnlyNativeWindowWrapper` forwards
  may reach only those APIs and the window reads in `WindowQueryNativeApis`, followed through their
  lambdas and helpers. `AttachThreadInput` is pinned to `NativeThreadInputWrapper`, and the last Win32
  error is read only inside the Native wrapper that made the call (also through framework members
  that read it, such as `new Win32Exception()`). Outside `UIAutomationWrapper` and the two UI Automation
  engine adapters (`UiaTree`, `Uia3Tree`, whose pattern objects `IUiaTree.GetPattern` hands out), UI Automation
  elements, patterns and text ranges may only be read (`ReadOnlyUiAutomationMethods`): no `Invoke`,
  `SetValue`, `Toggle`, `Select`, `SetFocus` or other action
  (`UiAutomationActions_AreOnlyCalledByTheWrapperAndTheEngineAdapters`). A new native call or UI Automation action therefore fails the guard
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
- Never install a real WinEvent hook. wait_for_window's wake-ups (roadmap M3.F22) listen to window events through
  `INativeWinEventWrapper`, on the event thread of `WindowEventSource`, which starts with the first wait that sleeps;
  tests use `FakeWinEventWrapper`, whose message loop reads an in-memory queue (`Raise()` queues a window event and the
  loop calls the hooks that cover it, on the loop's own thread), or `FakeWindowEventSource` in front of a wait.
  `WinEventHooks_AreOnlyInstalledByNativeWinEventWrapper` pins SetWinEventHook, UnhookWinEvent and that thread's message
  calls to the wrapper. The E2E `WrapperCatalog` classifies its members as reads: an out-of-context hook loads nothing
  into another process and changes no window. In the same way, no unit test registers a real UI Automation event handler:
  wait_for_element's wake-ups add theirs through `IUiaTree` (`AddStructureChangedHandler`, `AddPropertyChangedHandler`),
  which `FakeUiaTree` replaces (`RaiseStructureChanged()`, `RaisePropertyChanged()`, and `OnAddHandler` or
  `OnRemoveHandler` to block or fail a call as a hung provider does), and the `Uia3Tree` tests run them over mocks of the
  COM client; `FakeElementEventSource` stands in front of a wait.
- For the input session (pause, held keys, stuck-input recovery), use `FakeInputWrapper`: it records
  every SendInput batch, keeps a key state the way Windows does (`Press()` and `Lift()` stand in for
  the user's fingers), puts the pointer where an absolute move takes it (across its `VirtualScreen`, as Windows maps
  it) and can confine it (`Clip`, as ClipCursor does), and can insert fewer events than asked, throw or block, so short
  counts, exceptions, a pause in the middle of a batch and a pointer that does not get to its point can be tested without
  touching the desktop.
- The guards scan only the server assembly. They do not see code in the test project, so the
  rules above are up to you there.
- Keep timing tests far from the times they tell apart, because the default run is parallel and CI
  runners are slow: run a step whose timing matters on a dedicated thread, not on the thread pool;
  bound a time by what the test measured (for example the logged start of a call), not by the value
  passed to `Task.Delay`; and give a call that must answer before a deadline a deadline far above thread
  start-up (seconds, not 100 ms). Prefer a fake clock (`ManualClock`) or an explicit signal from the code
  under test over sleeping.

**Timing-sensitive tests under heavy load.** A test that waits for a deadline should not let a busy machine (a build,
another test run, a virus scan) decide when the deadline passes (roadmap M3.F29). The deadline tests of
`DesktopStateServiceTests` run get_desktop_state's deadline on a `ManualTimerClock` (`Fixtures/`), whose timers fire only
when the test moves it: the test moves it to the parts' budget or to the deadline once the parts are where it expects
them, and checks that the call returns then and not before, instead of bounding the call's wall time. The real time a
test then waits for the call (`HangGuard`, 30 s) only guards against a call that never returns. Each step of the
in-process MCP server (`InProcessMcpServer`, `RawMcpConnection`: starting the host, connecting, stopping) may take 30 s,
as long as a call: at 10 s, contract tests failed now and then after 11 to 14 seconds on a busy machine
(close_windows_batch in `UserConfirmationContractTests`, the click case of `ToolPayloadBudgetTests` and
`EveryToolContractTests`, `CaptureCoordinateContractTests`, `LayoutToolErrorContractTests`), where a full run under load
takes some contract tests up to 10 s. `LayoutToolErrorContractTests` also ignores the slow-call Warning when it checks
that a not-found is not logged as a server error. When a test still fails alone in a full run, run it again on its own
(`--filter "FullyQualifiedName~<class>"`) before you look for a regression; a failure that repeats on an idle machine is
a real one.

### End-to-end tests against the fixture app

`tests/WindowsAppManagerMcp.E2ETests` tests the real server services against an app the suite
owns, `tests/FixtureApp` (WamFixtureApp, a small WPF app). This is where Windows behaviour that
mocks cannot reproduce gets tested: the foreground lock, UI Automation trees, captures and DPI.

**The fixture app** has fixed AutomationIds (`FixtureContract.Ids`): an edit box, a password box (UI
Automation reports it as a password field, whose value the server never reads), a check box, a combo
box, a button that opens a modal dialog (the main window is disabled while it is open), a button
that opens a document window that shows a Yes/No/Cancel save prompt when it is closed with unsaved
changes, a button that shows the common Save As dialog, a button that shows a task dialog, a context
menu, three solid color swatches and a virtualized list of 10,000 items. Its command line
(`FixtureOptions`) sets an instance token that appears in every window title, shows the windows
without activating them, opens the modal, the document, the table window, the menu window or the
WebView2 window at start, and sets the process DPI awareness. A watchdog thread ends the fixture
when the test host exits or after its maximum lifetime (5 minutes by default), so a crashed run
cannot leave it behind.

**The surfaces of the M3 paths** (roadmap M3.F4). The Save As dialog is the common IFileSaveDialog;
it creates no test file, and the fixture writes the file it returns only when it is directly in the
test's own folder under the temp folder, the folder of its event log (`FixtureSaveFolder`), so
nothing is written anywhere else. The task dialog (TaskDialogIndirect) has two command links and a
Cancel button; the fixture's manifest asks for version 6 of the common controls for it, and sets no
DPI awareness. The table window holds a WPF DataGrid of 1,000 rows with column and row headers, of
which it realizes only the rows around what it shows. The menu window has a Win32 menu bar (a real
HMENU, whose menus open as #32768 popup windows) with a submenu. The WebView2 window shows local
HTML (NavigateToString: no network, no debug port) in a WebView2 pane, Chromium content in a
Chrome_RenderWidgetHostHWND of msedgewebview2.exe, for the accessibility warm-up (M3.3); its browser
data goes into the test's own folder. The Microsoft.Web.WebView2 package is in every build, but only
`--open-web-view` starts WebView2, so an ordinary fixture start needs no WebView2 runtime. The
WebView2 scenarios name the runtime as a requirement, `Requires = typeof(WebView2Runtime)` on their
gate attribute: on a machine without it they are skipped with a reason that says what to install,
rather than failed.

The fixture has no WebView2 hosted as a visual (roadmap M3.F23, the CoreWebView2CompositionController
of the WinUI 3 WebView2 control). The WPF package's WebView2CompositionControl draws the page into a
WPF Image through a screen capture and gives UI Automation no element of the page (it has no
automation peer, up to package 1.0.4191.47), so the server could not read its page there whatever it
did. The warm-up's detection of such content (an msedgewebview2.exe the window's process started, and
a Document with frameworkId Chrome below the window) is covered by the unit tests in
`AccessibilityWarmupTests`; check it on a real desktop with a WinUI 3 app that shows a WebView2.

**The event log.** The fixture writes one JSON object per line to the file given with
`--event-log`: first a `ready` event with its process id, window handle, DPI, window and client
rectangles and the screen rectangle of every element, then every change it receives (text, check
box, selection, buttons, focus, modal, document, context menu). It is the fixture's own record of
what reached it, independent of what the server reports. `FixtureContract.Events` documents the
events; the harness reads them with `FixtureEventLogReader`.

**The harness.** `E2ESession.Start(tier, output)` checks the tier's opt-in again, records the agent's
host (`ServerHarness.RecordAgentHost`), starts the fixture (`FixtureAppSession`), waits for its ready
event, builds the server services as Program.cs registers them (`ServerHarness`, with that host) and
finds the fixture's main window through find_windows. Dispose kills the fixture. The server counts the
window in front when it starts as part of the agent's host; the harness runs no hosted service, so it
records the host itself, before the fixture exists, and the window in front is then the terminal that
runs the tests. A session whose server still counts the fixture's main window as the host fails at
once, and the scenarios that need the host in front (`E2ESession.RequireAgentHostInFront`) fail when
another window is: keep the terminal in front, and do not click the fixture, during a run.

Every native wrapper of the server sits behind a fail-closed guard (`WrapperGuard`). That includes
the process and monitor wrappers that ProcessService and MonitorService would otherwise create
themselves: `ServerHarness` registers them, so the container passes the guarded ones in. An ordinary
test (`ServerHarnessTests`) resolves every service and fails if any of them holds a native wrapper
outside the guard. A process handle returned by `OpenProcess` is guarded as well. The guard does not
decide from member names: `WrapperCatalog` classifies every wrapper member explicitly (read, window
action, input, process action, process start, thread-input attach, global hotkey, clipboard or UI Automation
action). A member it does not list is refused in every tier, and `WrapperCatalogTests` fails until a new wrapper or
member is classified. When you merge a change that adds wrapper members, classify them there (`TestingGuideTests`
fails until a new kind is named in this list).

- In the no-input tier the server can only read. Input, window changes, UI Automation actions, and
  process closes and kills throw `E2EGuardException` before they reach the wrapper.
- In the input tier the server can act only on the fixture process:
  - keyboard input goes out only while a fixture window is in front;
  - mouse input goes out only while the cursor is over a fixture window, and a batch that moves the
    pointer (a click carries an absolute move to its point) only when the move ends over a fixture window; a batch
    that only moves the pointer (mouse_move's) is judged by where it ends, as SetCursorPos is;
  - a mouse wheel batch goes out only while a fixture window is also in front, because Windows can send the
    wheel to the focus window instead of the one under the cursor;
  - window calls go through only when every window they name is a fixture window;
  - closes and kills apply only to the fixture process, checked by id and process name;
  - UI Automation actions (a pattern's `Invoke`, `Toggle`, `SetValue` and the other actions, and
    `IUiaTree.SetFocus`, roadmap M3.6) go through only on an element of the fixture process. The guard reads the
    element's process with `ReadIdentity`, through the tree that handed out the pattern (or that `SetFocus` was
    called on), and refuses the call when it cannot read it. Unlike input, a UI Automation action gets no
    foreground or hit-test check: a pattern acts on its element wherever the element is.

  Two narrow exceptions serve verified activation (EnsureForeground): a batch of zero-distance mouse
  moves, which moves nothing and presses nothing, may be sent wherever the cursor is; and the server may
  attach its own calling thread's input to another thread (`AttachThreadInput`, never two other threads
  to each other), because the window calls it makes while attached are checked as window actions.

  The check runs before every `SendInput` batch, so a window that comes to the front mid-text stops
  the typing. It is a second, independent layer under the server's own input guard (roadmap M1.1 and
  M1.5, `InputTargetGuard`), so a bug in that guard cannot send input to a window outside the fixture;
  `FixtureInputGuardTests` checks the server's guard itself against the fixture.
- In both tiers, starting a process, registering a global hotkey and reading
  or writing the clipboard's content are refused. One narrow exception (roadmap M6.5): an input-tier scenario may name
  packaged apps it activates (`E2ESession.Start`'s `allowedActivations`, today only Calculator, which holds none of the
  user's data); `INativeAppsWrapper.ActivateApplication` then goes through for those AppUserModelIDs only, without
  arguments, while `StartProcess` (the shell fallback of an elevated server) stays refused, and the scenario ends the app
  itself by the process ID the launch returned (`PackagedAppLaunchTests`). CDP (`ICdpService`, and `ICdpTransport` under it, the only
  type that opens the network connections, roadmap M5.2) refuses every call, because it talks to a browser's
  debug port over the network, outside the native wrappers, and the fixture's WebView2 window opens no debug
  port. `ServerHarnessTests.EveryRegisteredCdpService_IsRefused` fails when a registered `ICdp*` service is not
  in `ServerHarness.RefusedServices`. The CDP port-owner check (`ITcpPortOwnerResolver`) is a guarded read.

**The MCP pipeline.** The scenarios call the server's services directly. The in-process MCP test host the contract
tests use (`InProcessMcpServer`) lives in `WindowsAppManagerMcp.Tests.Infrastructure`, so this tier can run the real
MCP pipeline (argument binding, filters, result serialization) too; `HarnessTests/InProcessMcpServerTests` runs it with
a fake tool. A scenario that hosts the server's own tools through it (roadmap M6.2) must pass them the harness's guarded
services (`configureServices`), never the defaults of `AddWindowsAppManagerServices`, which would put unguarded native
wrappers behind the tools: `ServerHarness.StartMcpServerAsync` does, with the server's tools and call filters over the
harness's own instances (one input session, element registry and audit log for the harness and the host) and none of its
hosted services (no pause hotkey is registered). `FixtureRunStepsTests` runs run_steps through it.

UI Automation elements come back from the wrapper as objects that services only read: NativeCallGuardTests
forbids acting pattern calls outside the Native wrappers and the two UI Automation engine adapters, and the
pattern objects `IUiaTree.GetPattern` hands out are guarded in turn: their actions (`UiaAction` in
`WrapperCatalog`) are refused in the no-input tier and allowed in the input tier only on elements of the fixture
process, as above. A scenario can choose the engine with `E2ESession.Start(..., uiaEngine: UiaEngine.Uia2)` (or
`ServerHarness.Create(tier, pid, engine)`); without it the server reads with the engine `WAM_UIA_ENGINE` chooses in
the test process (by default uia3), and `E2ESession.Start` writes the engine and where it came from to the test output
(`UI Automation engine: Uia3 (default)`). `FixtureUiaEngineTests` and `FixtureUiElementsTests` read the fixture with
both engines in every run (see
[Release gate: the UI Automation engines](#release-gate-the-ui-automation-engines)). The Uia3Tree unit tests never
create the COM client: they run over Moq mocks of its interfaces. The harness finds the fixture exe from a
path the build records in the test assembly; set `WAM_FIXTURE_APP_PATH` to use another build of
`WamFixtureApp.exe` (for example in Windows Sandbox). The harness refuses any exe with another name.

**Writing a scenario.** Put it in `Scenarios/`, mark it `[DesktopNoInputFact]` when it only reads or
captures and `[DesktopFact]` when it acts, and start from `E2ESession`:

```csharp
[DesktopNoInputFact]
public async Task GetUIElements_SeesTheEditBox()
{
    using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);

    var result = await e2e.Server.UiElements.GetUIElementsAsync(e2e.Fixture.MainWindowHandle);

    UiaReads.Flatten(result.Elements).Should().Contain(e => e.AutomationId == FixtureContract.Ids.EditBox);
}
```

Compare the server's answers with the ready event (`e2e.Fixture.Ready`) and the event log
(`e2e.Fixture.WaitForEvent(...)`), not with fixed coordinates: positions depend on the monitor and
its DPI. The fixture's main and document windows open centred on the primary monitor, never on the monitor under the
mouse: a window WPF moves to a monitor with another scale while showing it is activated by WPF's DPI change, even
with `--no-activate`, and can take the foreground from the terminal. The fixture also refuses to start with another DPI
awareness than the one asked for (`FixtureAppSession.DpiAwarenessMismatch`). A scenario that needs a monitor at another scale asks for `OtherScaleMonitor` (`--other-scale-monitor`, roadmap M3.F30): the main window is centred on the first such monitor and, with `--no-activate`, shown with the extended style WS_EX_NOACTIVATE until it has rendered (`NoActivateWhileShown`), because WPF's DPI change inside Show activates it; marking that SetWindowPos or its WM_WINDOWPOSCHANGING non-activating does not stop it, and refusing the activation leaves no window in front. A scenario that needs a kind of window the WPF fixture cannot provide may create its own,
as `MirroredWindowCaptureTests` does with `MirroredTestWindow`: a borderless popup of the test
process, shown without activation and destroyed in `Dispose`. `E2ETestGateTests` fails if a test outside `HarnessTests/` has no gate attribute. Tests in
`HarnessTests/` are ordinary tests of the harness and must never start the fixture. A scenario that needs
something the machine may lack names it with its gate attribute's `Requires` (an
`IDesktopTestRequirement`, such as `WebView2Runtime`), and is then skipped with the requirement's reason
on such a machine; the requirement is only checked once the tier is opted in.

**Fixture surfaces for M6.** Five more launch options, none of which changes the fixture unless a scenario asks for it.
The one change to every start is that the main window's status text now reads Ready from its first frame, instead of
reading Starting until the ready event.
`OpenCanvas` (`--open-canvas`, M6.3) opens the window `WAM Fixture Canvas <instance>`, which draws
`FixtureContract.CanvasLines` (dark and light text, down to 9 pt) in OnRender. UI Automation finds only its
`canvasSurface` pane there, with no name, text or children. Every canvas word has a character that is not a hex digit, so
it cannot turn up in a title's random instance token. Its `canvasOpened` event gives each line's and word's screen
rectangle, which `FixtureCanvas.From` reads, and `canvasMouseDown` logs the word under a press. Those rectangles are cell
boxes (the line's full height, as a selection highlights it), not ink, so an OCR box lies inside one: check containment,
not equality. The canvas follows `--other-scale-monitor`. `ChangeAfter` (`--change-after-ms N`, M6.4) adds the 40x40 `changePanel` right of the swatches
and recolors and relabels it once, N ms after the ready event (`panelChanged`). `AnimateFor` (`--animate-for-ms N`)
adds the `spinner` there and turns it every 50 ms for N ms, then leaves it still (`animationStarted`,
`animationStopped`). Both count from the ready event but the lifetime counts from the start, so each must be due at least
`FixtureOptions.TimedChangeMargin` (30 s for the wait for ready, plus a spinner tick) before the end of `MaxLifetime`;
the harness refuses a later one when it builds the options, where the test would otherwise only time out waiting for an
event the watchdog cut off. Neither touches the status text, which already reads Ready in the first frame, so after the ready
event the panel or spinner is the only region of the main window that changes. That holds while the fixture is not
activated (the default for a no-input test): an activated fixture focuses the edit box, whose caret blinks.
`OpenOverlay` (`--open-overlay`, M6.7) opens `WAM Fixture Overlay <instance>`, an unowned WS_EX_TOPMOST tool window
centred on the primary monitor. That is over the main window's middle unless `--other-scale-monitor` moved the main
window to another monitor: the overlay does not follow it. Its `overlayOpened` event reports `topmost`, `toolWindow` and `ownerHwnd`.
`ExtraArguments` (M6.6) plants arguments after `--`, where the fixture ignores them and logs only how many
(`argumentsIgnored`). Before `--` an unknown option is still refused, so a misspelt option fails the start instead of
being ignored.

**The scenarios.** Each class in `Scenarios/` covers one area; a class can hold tests of both tiers.

| Scenario class | Tier | What it checks against the fixture |
|----------------|------|------------------------------------|
| `FixtureWindowTests` | No input | find_windows and get_window_info |
| `FixtureUiElementsTests` | No input | get_ui_elements: automation ids, control types, bounds, the realized rows of the virtualized list (with each engine) |
| `FixtureCaptureTests`, `MirroredWindowCaptureTests` | No input | capture_window's size and origin (the color swatches); a right-to-left mirrored window |
| `FixtureInputGuardTests` | Both | The input guard: refusals in the no-input tier, input that must be sent in the input tier |
| `FixtureForegroundTests`, `FixtureTypingTests`, `FixtureSessionTests` | Input | Verified activation, focus_window and type_text, the user's pause |
| `FixtureCloseTests` | Input | close_window's guard and its report of the save prompt |
| `FixtureUiaEngineTests` | No input | The two UI Automation engines read the same tree and find the same elements (roadmap M3.1, M3.2) |
| `FixtureElementRefsTests` | No input | find_ui_elements and element refs (M3.4) |
| `FixturePopupTests` | Input | The open context menu found by the default scope and listed by get_active_popups, and the combo box list found by scope windowAndPopups (M3.5) |
| `FixtureDialogTests` | Input | respond_to_dialog on the save prompt and the WPF modal, a one-item menu_path (M3.5); the task dialog answered by its common button's `CommandButton_2` id and by a command link's text, and a menu_path through the menu window's Win32 menu bar and its #32768 submenus (M3.F4) |
| `FixtureFileDialogTests` | Input | file_dialog end to end in the fixture's Save As dialog: a new file, and a replaced one through the replace prompt (M3.14, M3.F4) |
| `FixtureElementActionTests` | Input | element_action's pattern actions, checked against the fixture's event log (M3.6) |
| `FixtureWindowWaitTests` | No input | wait_for_window (M3.7) |
| `FixtureElementWaitTests` | No input | wait_for_element (M3.8) |
| `FixtureElementReadTests` | No input | read_element_text and read_table (M3.9); read_table of the table window's DataGrid through its realized rows, with column and row headers (M3.F4) |
| `FixtureVirtualizedListTests` | Both | searchVirtualized in the no-input tier, element_action realize in the input tier (M3.10) |
| `FixtureCaptureIdTests`, `FixtureSetOfMarksTests` | No input | captureId points, Set-of-Marks marks and legend centres, capture_window's ref crop (M3.11) |
| `FixtureDesktopStateTests` | No input | get_desktop_state (M3.12) |
| `FixtureVisualWaitTests` | No input | wait_for_visual (M6.4): a changed wait on the main window sees the change panel (`--change-after-ms`) where the fixture reports it and nothing else; a metadata capture before the change is a `since` satisfied at the first check; a still window times out with 0 % changed (no caret or noise), also against a wait's own last frame; a stable wait ends only after the spinner (`--animate-for-ms`) stopped, and times out over the spinner while it turns; a region around the panel (a screen copy) sees the change |
| `FixtureWebViewTests` | No input | The accessibility warm-up of the WebView2 window's live Chromium content: host WebView2, status Ready, the page read, and no second wait (M3.3, M3.F4); skipped without the WebView2 runtime |
| `FixtureOtherScaleMonitorTests` | No input | The fixture on a monitor whose scale differs from the primary's (`--other-scale-monitor`): it opens at that monitor's DPI and leaves the agent's host in front, get_ui_elements' bounds and capture_window's client area match what the fixture draws (M3.F30); skipped without such a monitor (`MonitorAtAnotherScale`) |
| `FixturePlacementTests` | Input | The launch tools' placement of a window (M3.13) |
| `FixtureLayoutTests` | Both | Layout schema version 2 (M6.8) through the real MCP pipeline, saving the fixture's process only into the harness's layouts folder: a maximized fixture comes back maximized on its monitor with its restore rectangle, a minimized one gets its restore rectangle without being shown (its state is sampled during the apply), an agent-written snap lands on its dry-run target, and on a monitor at another scale it comes back at its window rect; the dry run runs in the no-input tier, whose guard refuses any window change |
| `FixtureMonitorSelectorTests` | Both | The `monitor` parameter (M6.7) through the real MCP pipeline: get_monitors' ids are the same on a second call and every monitor but the primary has a relation (no input); snap_window by `primary`, a device name, an id and `current` lands on that monitor's left half without activating the fixture, and `left` and `right` on the outermost monitors (`TwoMonitors`), or `AmbiguousMonitor` for stacked ones |
| `FixturePercentPlacementTests` | Input | set_window_bounds with `unit: "percent"` (M6.7): 50/0/50/100 and the three thirds give exactly snap_window's frames on the primary, and a placement on the monitor at another scale (`MonitorAtAnotherScale`) is exact after the app's DPI change |
| `FixtureTakeoverTests` | Input | Takeover detection (M7.4) with `WAM_TAKEOVER_DETECTION=stop`: Windows installs the real low-level keyboard and mouse hooks while type_text runs (state `Watching`) and removes them afterwards (`Ready`), and the server's own typing, which Windows marks as injected, never counts as the user's, so the text goes in whole. Run it while nobody uses the keyboard or mouse: the user's input would stop the typing (`UserInterrupted`), which is the feature |
| `FixtureWindowEventTests` | Input | The window event journal (M7.2) over the real WinEvent hooks: after the first read starts it, minimizing and restoring the fixture's window are recorded in order with the window's title, class and process, and nothing of the server's own process is |
| `FixtureZOrderTests` | Input | set_window_zorder (M6.7) with the fixture's topmost overlay (`--open-overlay`): `topmost` puts the main window above the overlay and get_server_info lists it in `pinnedWindows`, `notopmost` puts it back below and drops the pin, `above` the overlay makes it topmost directly above it, `bottom` sends it behind every listed window and `top` raises it above the normal ones; the foreground never moves, and every test unpins the window before the fixture closes |
| `FixtureMouseTests` | Input | click, right_click, double_click and mouse_move land on the exact pixel aimed at (the corners and centres of the swatches), on the primary and on a monitor at another scale (skipped without one); click presses the user's primary button and right_click the secondary one, as the fixture's `mouseDown` events record them after Windows applied the user's swap; a double click is one press with `clickCount` 1 and one with 2 (M4.2) |
| `FixtureMouseClickDragTests` | Input | mouse_click: a Ctrl+Shift click reaches the fixture with both modifiers down and neither stays down (a plain click after it sees none); the middle, X1 and X2 buttons arrive as themselves; a triple click is three presses with `clickCount` 1, 2 and 3 at the pixel. drag: a Ctrl+drag presses on one swatch and releases on another, with Ctrl down at both and no other press or release between them, and leaves nothing held; a drop point right of the fixture window is refused before anything is pressed (M4.3). The fixture logs presses and releases with their modifiers (`mouseDown`, `mouseUp`) |
| `FixtureRunStepsTests` | Input | run_steps through the real MCP pipeline (`ServerHarness.StartMcpServerAsync`, M6.2): a form fill (focus, a click at a found element's clickPoint by `$steps` reference, ctrl+a, typing, a toggle, a wait that verifies the text) in one call, the fixture's events in step order, one audit line per audited step under `<run's id>#<step>`, no modifier left down; a failed step stops the run and the step after it does nothing; a direct element_action made during the run waits for the gate and lands after the run's last step; a pause between steps stops the run with `PausedByUser`; a wait longer than `maxDurationMs` stops it with `OperationTimedOut`; a second fixture, a process of its own, taking the foreground stops it with `TargetNotForeground` and keeps the foreground (the terminal is never touched) |
| `FixtureOcrTests` | Both | read_screen_text and find_text_on_screen on the canvas window (M6.3): every line read inside its drawn rectangle at both scales, a find's click point on its word and back through the captureId, a regex; in the input tier a covered canvas still reads, a minimized one is `WindowMinimized`, a click through the captureId lands on the word and is `StaleCapture` once the canvas moved, and a canvas a policy denies is blacked out of a region read. Skipped without an OCR language |

**Running them.** Locally only with an opt-in, as above. On GitHub, `.github/workflows/e2e.yml`
runs them on demand (Actions, "E2E", "Run workflow"), with a choice of tier, UI Automation engine (`engine`: uia3,
the default, or uia2, which the test step sets as `WAM_UIA_ENGINE`) and runner. It first
checks that the runner has an interactive desktop and otherwise ends with a notice instead of
running anything; use a self-hosted runner in a logged-on desktop session then (runner version 2.327.1 or later,
which the workflows' Node.js 24 actions need). It fails when no
test ran, so an opt-in that did not reach the tests cannot pass as green.

### Release gate: the UI Automation engines

The server reads UI Automation with the UIA3 COM client by default, and with the managed UIA2 client
(System.Windows.Automation) when `WAM_UIA_ENGINE=uia2` (roadmap M3.2). Before a release that ships
uia3 as the default, the repository owner runs the whole no-input tier twice on a real desktop: once
with `WAM_UIA_ENGINE` unset (uia3) and once with `WAM_UIA_ENGINE=uia2`. If the engines differ, the
release ships with uia2 as the default (`UiaEngineOptions.Default`). The owner runs it, never an
agent. The tier only starts the fixture and reads it, but the fixture's windows appear on the screen:
leave the desktop alone during the runs, because a window opened over the fixture can fail a capture
scenario.

`.github/workflows/e2e.yml` can run either engine: start it twice on a self-hosted runner in a logged-on desktop
session, with tier `no-input` and engine `uia3`, then engine `uia2`. Each run uploads its trx files as
`e2e-results-no-input-<engine>`, and its summary names the tier and the engine. The workflow sets `WAM_UIA_ENGINE` in
both runs, so the first run's output reads `UI Automation engine: Uia3 (WAM_UIA_ENGINE)` instead of `(default)`: the
same engine, chosen by name. Or run both from the repository root, on a real desktop. The trx logger keeps every test's
output, so the two runs can be compared afterwards (`TestResults/` is gitignored):

```bash
# Git Bash
# Run 1: the default engine (uia3)
env -u WAM_UIA_ENGINE WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1 dotnet test --filter "Category=DesktopNoInput" --logger trx --results-directory TestResults/release-gate/uia3
# Run 2: uia2
WAM_UIA_ENGINE=uia2 WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1 dotnet test --filter "Category=DesktopNoInput" --logger trx --results-directory TestResults/release-gate/uia2
```

```powershell
# PowerShell
# Run 1: the default engine (uia3)
Remove-Item Env:WAM_UIA_ENGINE -ErrorAction SilentlyContinue
$env:WAM_ALLOW_DESKTOP_NOINPUT_TESTS = "1"
dotnet test --filter "Category=DesktopNoInput" --logger trx --results-directory TestResults/release-gate/uia3

# Run 2: uia2
$env:WAM_UIA_ENGINE = "uia2"
dotnet test --filter "Category=DesktopNoInput" --logger trx --results-directory TestResults/release-gate/uia2

Remove-Item Env:WAM_UIA_ENGINE
Remove-Item Env:WAM_ALLOW_DESKTOP_NOINPUT_TESTS
```

What the runs cover:

- Every no-input scenario that leaves the engine to the server reads with the engine of the run:
  `FixtureElementRefsTests` (find_ui_elements and refs), `FixtureElementReadTests` (read_element_text and
  read_table), `FixtureElementWaitTests` (wait_for_element), `FixtureSetOfMarksTests` (Set-of-Marks),
  `FixtureDesktopStateTests` (get_desktop_state) and `FixtureVirtualizedListTests` (the virtualized list search).
  The rest of the tier (see [the scenarios](#end-to-end-tests-against-the-fixture-app)) runs too. Each test's output
  names the engine and where it came from:
  `UI Automation engine: Uia3 (default)` in the first run (`Uia3 (WAM_UIA_ENGINE)` from the workflow),
  `UI Automation engine: Uia2 (WAM_UIA_ENGINE)` in the second. A value of `WAM_UIA_ENGINE` other than `uia3` or `uia2` fails these tests instead of
  falling back to uia3, so a mistyped second run cannot pass as a uia2 run.
- `FixtureUiaEngineTests` and `FixtureUiElementsTests` choose the engine themselves, so they give the
  same results in both runs (`UiaEngineScenarioTests` keeps it so). Each of their theories runs once
  per engine (`engine: Uia3` and `engine: Uia2`). Among them, the find_ui_elements theory checks with
  each engine that every element found at detail full is the fixture's (its process id), has a click
  point in its bounds and a ref that resolves to the live element, and that the password box alone is a
  password field, whose text neither find_ui_elements nor read_element_text reads. The `BothEngines_*`
  facts start a server per engine against one fixture and compare what the two read: the
  get_ui_elements tree (control types, names, automation ids, states, frameworks, a ref for every
  element, bounds), and the elements find_ui_elements finds at detail full, by control types and by an
  automation id in other letter case (identity: process, window handle and password flag; patterns;
  toggle, expand/collapse and selection state; value; focus; description; bounds). Patterns are
  compared by name without LegacyIAccessible, which uia3 reports on nearly every element and uia2
  never does.

What to compare:

1. Both runs pass, with the same counts: the engines agree. Keep uia3 as the default.
2. A test that fails in one run only, a theory that fails for one `engine` only, or a `BothEngines_*`
   fact that fails: the engines differ. The failure message names the test's engine, or, for a
   `BothEngines_*` fact, the first element that differs. The release defaults to uia2; record the
   failing tests and their messages for the follow-up.
3. A test that fails in both runs and, for a theory, with both engines, is a problem of the test or
   the fixture, not an engine difference. Fix it and run the gate again. A `BothEngines_*` fact is the
   exception: it compares the engines, so it fails in both runs when they differ (point 2).

One caveat for point 2: the window's title bar, its system menu bar and the minimize, maximize or restore and close
buttons are not the server's or the fixture's. Windows serves that Win32 non-client area itself, through its native
proxy to uia3 and through .NET's managed client-side proxy to uia2, and the two disagree on details that have nothing
to do with the server's code, from run to run: in the second run of the first 1.15.0 gate (on c2db508), uia2 read the
Minimize button with automation id `Minimize-Restore` and no framework, where uia3 read `Minimize` and `Win32`, while
the first run's uia2 agreed, and in both runs of the gate that passed (on 293cacd) both engines read `Minimize` and
`Win32`. So the `BothEngines_*` facts compare those elements (the TitleBar, MenuBar, MenuItem and Button elements that
lie wholly outside the client area the fixture reports) by control type and place in the tree first, with every other
element in full, and then by name. Their names are the proxies' own: on a Windows display language other than English,
Windows' native proxy names them in that language, and .NET's managed proxy can name them from resources in another
(roadmap M3.F29). A name that differs there does not fail the fact: the output reports it, with the element, both
names and the display language. On 2026-10-14 the same commit passed and then, on the same en-GB desktop, read UIA3
'Minimise' and UIA2 'Minimize' every time, so the proxies' names change between desktop sessions as well as languages,
and a failure there said nothing about the server's engine code. Record such a difference, and compare the fixture's own controls in the output of the find_ui_elements
theory, which writes every element it finds with each engine, before you change the default.

### Desktop check: provider-side contains (`WAM_UIA_PROVIDER_CONTAINS`)

With `WAM_UIA_PROVIDER_CONTAINS=1`, the UIA3 engine gives the provider a substring condition for find_ui_elements'
`nameMatch: "contains"` (PropertyConditionFlags_MatchSubstring with IgnoreCase, on the text's longest run of ASCII
characters other than `i` and `I`), and the server still tests every name it returns (roadmap M3.F12). The unit tests
check the server's side over the fake tree; what Windows does with the condition has not been checked, so the option is
off by default. Off, `contains` is the server's test alone. Before it can be turned on by default, the repository owner
checks on a real desktop (Windows 10 1809 or later, the UIA3 engine), comparing find_ui_elements with the option set and
unset:

1. **Windows takes the flag and narrows.** A `contains` search with no other criterion on a large window (a browser page)
   that ends in `SearchLimit` with the option unset finds the same first matches with it set, in the same order, and
   does not end in `SearchLimit` (or ends there later). The same result and the same `SearchLimit` would mean that
   Windows refused the flag and the engine fell back to the server's test.
2. **Nothing is lost.** On the fixture and on a few real apps, every `contains` search returns the same `matchCount`,
   elements and refs with the option set as unset, also for a text in other letter case than the name's.
3. **Names with combining marks still match.** A control named with a decomposed accent, such as `Cafe` followed by
   U+0301 COMBINING ACUTE ACCENT, is found by `contains` `cafe` with the option set, as it is unset (the server's
   ordinal test accepts it; a provider that compared by locale might not). The fixture has no such control yet: adding
   one, with a gated no-input scenario that runs both ways, would make this check repeatable.

Any difference in points 2 or 3 keeps the option off; record the search, the window and both results.

### Desktop check: M5 browser bridge

The CDP code is tested over in-memory fakes and one loopback endpoint the test serves itself
(`Integration/CdpLoopbackIntegrationTests`); what a real Chromium does (dialogs, detaches, crashes, its limits) is
checked by hand. This is the owner-approved exception to "never an app of the developer's" above: the check starts its
own debug browsers, separate instances with throwaway profiles, and never reads, acts on or closes the user's browser.
Every run keeps these rules, for every debug instance it starts:

1. Before starting, record every top-level window handle and every `msedge`, `chrome` and `brave` process ID.
2. Start the browser only with launch_browser_with_debug (`edge` or `chrome`), with no `userDataDir` or a new
   `%TEMP%\WamM5Check-<guid>` folder, on an explicit free port or port 0. Its `processId` must be new and its command line must
   carry that `--user-data-dir`; if the process or the window was already there, the launch was handed to the user's
   browser: stop and close nothing.
3. Open only `data:` URLs, `file:///` pages the check wrote under `%TEMP%`, or a 127.0.0.1 HttpListener. No network.
4. Act only on windows and targets of those processes. No window of the user's browser is passed to any call.
5. Clean up by process ID only, never by window title: check that the process tree has exited and that the user's
   browser process IDs are unchanged, then delete the profile folder. After roadmap M5.4, close_debug_browser with the
   launch's `processId` does both; the checks after it stay.

Roadmap M5.2 (after its merge, on a real Edge):

- **Plain read.** get_browser_content with `cdpPort` on a `data:` page returns `source: "cdp"`. Ten reads in a row
  leave no DevTools client attached (`edge://inspect` lists none).
- **Dialog before the read.** A page that calls `alert('x')` on load returns `CdpDialogOpen` within `timeoutMs`, not
  `CdpTimeout`: Chromium reports an open dialog again after Page.enable on a new connection (M5.8's dialog tool relies on
  it). Record the result either way.
- **Dialog during the read.** A page that opens `confirm()` after 500 ms while a slow read runs returns `CdpDialogOpen`.
- **Tab closed during the read.** Closing the tab (by its own process's CDP, or close_window on a window the check
  opened) during a 3-second busy loop returns `CdpTargetClosed`, with the detach reason in the message.
- **Same titles.** Two tabs with the same title return `CdpTargetAmbiguous` with both ids; passing one as `targetId`
  reads that tab; after closing it, the same `targetId` returns `CdpTargetNotFound`.
- **Browser gone.** After the debug process is killed, a read returns `CdpUnavailable`, not `CdpTargetClosed`.
- **Oversize page.** A local page of about 70 MB of text read with `textOnly: false` returns `CdpResponseTooLarge`, not
  `HtmlRequiresCdp`.
- **Port owner.** Start a second debug instance, B, by the same rules, on its own port and profile folder. get_browser_url
  on a window of the first instance, A, with B's `cdpPort` and `requireCdp: true` returns `CdpPortOwnerMismatch` and none
  of B's data; so does the same call with one of B's tab ids as `targetId`. Use get_browser_url, not
  get_browser_content, so that even a mistaken call reads no page text. A result with `source: "uia"` means the call fell
  back to UI Automation: the check failed; stop and record it.
- **No Origin header.** The reads above work without `--remote-allow-origins`.

Roadmap M5.1 readiness (on a real Edge, then on a real Chrome with `browser: "chrome"`, which rule 2 allows for this
block; every other rule is unchanged, including the throwaway profile, the new process ID and cleanup by process ID;
only the check's own debug instances):

- **Port 0** (an exception to rule 2's explicit port, and to the explicit free port of the owner-approved exception above). launch_browser_with_debug without `port` returns `success: true` and `cdpReady: true`; `cdpPort` is the
  first line of the profile's `DevToolsActivePort`; `browserVersion` starts with `Edg/` (Chrome: `Chrome/`);
  `cdpEndpoint` is `http://127.0.0.1:<cdpPort>`; `browserWsEndpoint` is `ws://127.0.0.1:<cdpPort>/devtools/browser/...`
  and matches the profile file's second line. Record how much longer the launch took than before (expected well under
  a second).
- **Explicit free port.** The same with `port` set to a free port from 1024-65535: `cdpPort` is that port.
- **Read after ready.** get_browser_content with the returned `cdpPort` and `requireCdp: true` on the launch's own
  `windowHandle`, on a `data:` page, returns `source: "cdp"` at once, without a retry.
- **Endpoint not ready** (the 10-second wait, a non-DevTools answer, a port another process holds or whose owner cannot
  be read, a browser that exits during the wait) is covered by the fakes (`DebugBrowserReadinessProbeTests`,
  `DebugBrowserLaunchTests`); it is not provoked on a real browser.

Roadmap M5.3 (on a real Edge, then on a real Chrome with `browser: "chrome"` as in the M5.1 block; every other rule is
unchanged). A second window of a debug instance is opened through that instance's own DevTools endpoint
(`Target.createTarget` with `newWindow: true`, from a script the check runs against the launch's `browserWsEndpoint`),
never with keystrokes; windows are moved only with set_window_bounds (`x` and `y`; move_window before 2.2.0) on the check's own windows:

- **No cdpPort.** On instance A, launched with port 0, get_browser_url and get_browser_content on the launch's
  `windowHandle` without `cdpPort` return `source: "cdp"` and `cdpPort` equal to the launch's `cdpPort`;
  get_browser_content with `textOnly: false` returns the HTML. The same on an instance launched on an explicit free port.
- **Window binding.** Two normal, non-overlapping windows of A, each showing
  `data:text/html,<title>Same</title>`: get_browser_url on each window returns a different `targetId`, the one whose
  `Browser.getWindowForTarget` window is that window (check from the script). Repeat with the second window's tab in
  the background behind a tab titled `Other`: the first window still returns its own tab. With one window maximized,
  the maximized one returns its own tab; with both maximized, record the result (`CdpTargetAmbiguous` is expected).
  Record the DPI scale of the monitor; if a monitor with another scale is attached, record whether a window there
  binds (a fallback to the title is expected).
- **Covered window.** A window of A covered by another of the check's windows since its page loaded: get_browser_content
  without `cdpPort` returns `source: "cdp"` and the text (the M5.7 limit stays for browsers without a debug port).
- **Forcing UI Automation.** `useCdp: false` on A's window returns `source: "uia"`, with `urlSource` and no
  `cdpPort`; with `cdpPort` as well it is `InvalidArguments` and nothing is read. This also checks the M5.7 UI
  Automation path on a debug instance.
- **No client left.** After these reads, `edge://inspect` (Chrome: `chrome://inspect`) lists no DevTools client, and
  no infobar appeared in A's windows.
- **Port that is not the window's** (another process listening on the switch's port, an unreadable owner, port 0
  without a port file) and **a browser without the switch** are covered by the fakes (`DebugPortLocatorTests`,
  `BrowserReadingServiceTests`); the user's own browser is not passed to any call (rule 4).

Roadmap M5.4 (on a real Edge, then on a real Chrome with `browser: "chrome"` as in the M5.1 block; every other rule is
unchanged). Pages are `data:` URLs or a 127.0.0.1 HttpListener page under 1 MB. A named profile is only
`wam-m54-check-<8 hex digits>`, made by this check and deleted at its end. No call is given a process ID, port or window
of the user's browser: the refusals of the user's browser are covered by the fakes (`DebugBrowserLifecycleTests`):

- **Close a temporary-profile browser.** Launch without `profile` or `userDataDir`: `userDataDir` is under
  `%TEMP%\WindowsAppManagerMcp\browser-debug\` with a 32-hex-digit name. close_debug_browser with its `processId`
  (answer the question when the client asks; otherwise `confirm: true`) returns `success: true`, `closedGracefully: true`,
  `forced: false`, `exited: true` and `profileDeleted: true`; list_processes with `includeWindowless: true` lists none of
  the processes of that tree, the folder is gone, and the user's browser process IDs are unchanged. Record how long the
  call took. Repeat with only `cdpPort`.
- **Refusals on the check's own processes.** close_debug_browser with the process ID of a renderer of the check's debug
  browser (a child `msedge.exe` whose command line has `--type=renderer`) returns `NotADebugBrowser` and the browser
  keeps running; so does the process ID of the check's own HttpListener process. Without `confirm` on a client that
  cannot ask: `ConfirmationRequired`, nothing closed.
- **Named profile and reuse.** Launch with `profile` and a 127.0.0.1 page that stores a value in `localStorage`. A second
  launch with the same `profile` returns `DebugLaunchFailed` with nothing started; with `reuseIfRunning: true` it returns
  `launchStatus: "alreadyRunning"` with the same `processId` and `cdpPort`, `cdpReady: true`, and a `warning` that the
  url was not opened, within about a second. close_debug_browser without `deleteProfile` keeps the folder
  (`profileDeleted` absent); a new launch with the same `profile` reads the stored value back. close_debug_browser with
  `deleteProfile: true` deletes the folder.
- **Placement.** Launch with `placement: {"snap": "left_half"}`: `placement.finalBounds` is the left half of the monitor's
  work area and the window is there. A `reuseIfRunning` launch with `placement: {"snap": "right_half"}` moves the reused
  window to the right half.
- **Cancelled launch.** With a client that can cancel a call (`notifications/cancelled` about 1 second after
  launch_browser_with_debug starts), record the process IDs before; after the cancellation, list_processes with
  `includeWindowless: true` lists no new `msedge` process with the launch's profile, and no folder was left under
  `%TEMP%\WindowsAppManagerMcp\browser-debug\`. If the client cannot cancel a call, skip it and record that.
- **Leftover sweep.** A folder with a 32-hex-digit name that the check creates under
  `%TEMP%\WindowsAppManagerMcp\browser-debug\`, with its creation time set an hour back, is deleted by the next launch
  with a temporary profile; the folder of a debug browser that is still running is kept.
- **A browser that does not close**, a port that is not the browser's, a PID reused while the user was asked, and the
  user's own browser (with or without a debug port) are covered by the fakes (`DebugBrowserLifecycleTests`,
  `CdpServiceTests`, `DebugBrowserLaunchTests`).

Roadmap M5.5 (on a real Edge, then on a real Chrome with `browser: "chrome"` as in the M5.1 block; every other rule is
unchanged). browser_navigate refuses `data:` and `file:` URLs, so the pages come from a 127.0.0.1 HttpListener the check
runs (rule 3), each under 1 MB: `/a` (title `A`), `/b` (title `B`, whose script adds an element `#late` after one
second), `/redirect` (302 to `/a`), `/missing` (404 with a small page), `/slow` (answers after 5 seconds), `/jsredirect`
(an inline `<script>location.replace('/a')</script>`), `/empty500` (status 500 with no body) and `/unload` (title `U`,
sets `onbeforeunload`). The server runs with `WAM_TOOLSETS=+browser-actions` and an audit folder of the
check's own (`WAM_AUDIT_PATH`); launch the browser with `url` set to `/a`. Windows are only those of the check's debug
instance, named by the launch's `windowHandle`:

- **Default surface.** Without `WAM_TOOLSETS`, tools/list has list_tabs and wait_for_browser but not activate_tab or
  browser_navigate, and get_server_info reports `browser-actions` off.
- **List.** list_tabs on the window returns one tab with `inWindow: true`, `windowBound: true`, `shownTargetId` that
  tab's id and `cdpPort` the launch's; list_tabs with that `cdpPort` alone returns the same tab without `windowBound`.
- **New tab.** browser_navigate with `newTab: true` to `/b` returns a new `targetId`, `newTab: true`, `loadState:
  "load"`, `httpStatus: 200` and `title: "B"`; list_tabs then lists both tabs in the window, with `shownTargetId` the
  new one (record if the new tab opened in another window).
- **Activate.** activate_tab with the first tab's id: the window's title shows `A` (get_window_info) and list_tabs'
  `shownTargetId` is that tab. Record whether Windows brought the window to the front or flashed it in the taskbar.
- **Navigate and load.** In the shown tab: `/redirect` returns `url` ending in `/a` and `httpStatus: 200`; `/missing`
  returns success with `httpStatus: 404`; a 127.0.0.1 port nothing listens on returns `NavigationFailed` with
  `errorText` `net::ERR_CONNECTION_REFUSED`; `/slow` with `timeoutMs: 2000` returns `WaitTimedOut` with `loadState:
  "loading"` within about 2 seconds, and wait_for_browser with `urlContains: "slow"` then returns satisfied once it
  loads; `/a#part` returns `loadState: "sameDocument"`; `waitUntil: "none"` returns `loadState: "notWaited"` at once, also
  to `/slow`; `/jsredirect` returns `loadState: "load"` with `url` ending in `/a` well within the time limit; `/empty500`
  returns success with `httpStatus: 500` (record the `url` and `title`).
  During the slow load, set_window_bounds with `x` and `y` (move_window before 2.2.0) on the debug window returns at once (the load does not hold the input gate).
- **Refused URLs.** `file:///C:/Windows/win.ini`, `data:text/html,x`, `javascript:alert(1)`, `chrome://settings` and
  `edge://settings` each return `InvalidArguments`, and so do `http://127.0.0.1:<cdpPort>/json/list` and
  `http://localhost:<cdpPort>/` (the debug browser's own DevTools endpoint); get_browser_url shows the tab did not change.
  A download, a server that never answers while the tab does not either, and a gate refused after `newTab` opened its
  tab are covered by the fakes (`CdpTabServiceTests`, `BrowserTabServiceTests`); the check downloads nothing.
- **Wait.** browser_navigate to `/b` with `waitUntil: "none"`, then wait_for_browser with `selector: "#late"` is
  satisfied after about a second; `textContains` of text the page does not have with `timeoutMs: 1500` returns
  `WaitTimedOut` with the page's `url` and `title`; `selector: "##"` returns `CdpEvaluationFailed` at once.
- **Leaving a page with onbeforeunload.** browser_navigate from `/unload` to `/a`: record whether a dialog appeared
  (Chromium shows one only after a user gesture on the page, which the check does not make) and the result
  (`CdpDialogOpen` if one did).
- **Audit and clients.** The audit log has one line per activate_tab and browser_navigate call, with `url` as scheme
  and host only (`http://127.0.0.1:<port>`); after the block, `edge://inspect` (Chrome: `chrome://inspect`) lists no
  DevTools client and no infobar appeared.
- **Policy, pause, Firefox, another process's port** are covered by the fakes (`BrowserTabServiceTests`,
  `BrowserTabToolsTests`); they are not provoked on the desktop.

Roadmap M5.8 (on a real Edge, then on a real Chrome with `browser: "chrome"` as in the M5.1 block; every other rule is
unchanged: a throwaway-profile debug instance started by launch_browser_with_debug, cleanup by process ID with
close_debug_browser, and no window of the user's browser passed to any call). The server runs with
`WAM_TOOLSETS=+browser-actions,+browser-script` and an audit folder of the check's own. The pages come from a 127.0.0.1
HttpListener the check runs, each under 1 MB: `/form` (title `Form`: a labelled text input `#q`, a button `Save draft`
that writes `saved` into `#out`, a disabled button `Archive`, two buttons `Delete` that write `d0` and `d1`, a button
`Hidden` under a `div#cover` overlay, a `contenteditable` div `#note`, an input `#short` with `maxlength="3"`, and a form
around `#q` whose submit goes to `/done`), `/dialogs` (buttons that open `alert('a')`, `confirm('c')` and
`prompt('p', 'd')`, each writing the answer into `#out`), `/alert-on-load` (calls `alert('loaded')` on load), `/long`
(a button `Bottom` 3000 CSS pixels down) and `/frame` (`/form` in a same-origin iframe). Launch with `url` set to `/form`:

- **Default surface.** Without `WAM_TOOLSETS`, tools/list has none of the six tools; with `+browser-actions` it has
  five and not browser_evaluate, and get_server_info reports `browser-script` off; with
  `+browser-actions,+browser-script` it has all six.
- **Rect on the 150 % monitor.** With the debug window on the primary monitor (150 %): browser_element_rect with
  `role: "button"`, `name: "Save draft"` returns `anchor: "document"` and `devicePixelRatio: 1.5`; get_element_at_point
  at its `clickPoint` returns that button (UI Automation), and capture_region of its `screenRect` shows the button.
  Record the anchor's distance from the window's client area.
- **Rect on the 100 % monitor at negative x.** Move the debug window with set_window_bounds (`x` and `y`) to the secondary monitor (100 %, left of
  the primary): the same call returns `devicePixelRatio: 1`, a negative `screenRect.x`, and get_element_at_point at the
  new `clickPoint` returns the button. Browser zoom other than 100 % is not provoked (it needs keyboard input to the
  browser or an edited profile); its scaling is covered by the fakes (`BrowserPageServiceTests`).
- **Covered window.** Cover the debug window with a window the check opened (and closes by its handle afterwards)
  since a reload of `/form`: browser_element_rect returns `ContentAreaNotFound` when the page reports `visibilityState`
  `hidden` (Chromium's occlusion tracking), else `anchor: "renderWidget"`; record which, and that the call never
  returned a rect another window's page holds. **Background tab:** open `/long` with browser_navigate `newTab: true`,
  then browser_element_rect with the `/form` tab's `targetId` and `text: "Save draft"`: `ContentAreaNotFound` naming the
  tab the window shows, not a rect on `/long`.
- **Click without the foreground.** With the check's own terminal in front: browser_click with `text: "Save draft"`
  returns success with `point`; `#out` reads `saved` (get_browser_content with `selector: "#out"`), and
  get_foreground_window returns the same window before and after. `text: "Delete"` returns `AmbiguousElement` with
  `matchCount` 2 in the error; with `index: 1`, `#out` reads `d1`. `role: "button"`, `name: "Archive"` returns
  `ElementNotEnabled`, and so does `text: "Archive"` (the text search may find a span inside the button); `text: "Hidden"` returns `ElementObscured` naming `div#cover`; `selector: "##"` returns
  `CdpEvaluationFailed`. On `/long`, `text: "Bottom"` clicks it (the page scrolled).
- **Type without the foreground.** browser_type with `selector: "#q"`, `text: "hello"`: browser_evaluate
  `document.querySelector('#q').value` returns `hello`; again with `replace: true` and `bye`: `bye`. Into `#note`
  (contenteditable): its text ends with the typed text. Into `#short` with `abcdef`: record the value and whether a
  `warning` came. With no selector after clicking `Save draft` (a button has the focus): `FocusNotOnElement`. With
  `selector: "#q"`, `pressEnter: true`: browser_navigate is not needed, the tab's URL ends in `/done` (record it).
- **Dialogs.** On `/dialogs`, browser_click on the `alert` button returns success with `dialog.type` `alert`; then
  browser_handle_dialog with `accept: true` returns success (record whether `dialogType` came: Chrome, not Edge, is
  expected to report it), and the page answers again. `confirm` with `accept: false`: `#out` reads `false`. `prompt`
  with `accept: true`, `promptText: "x"`: `#out` reads `x`. With no dialog open: `DialogNotFound`. **Dialog before the
  connection:** browser_navigate to `/alert-on-load` with `waitUntil: "none"`, then get_browser_url with `requireCdp:
  true` (record `CdpDialogOpen` or `CdpTimeout`), then browser_handle_dialog with `accept: true`: on Edge 155 it
  returns `CdpDialogOpen` (the dialog was not reported, so it is not accepted unread) and the dialog stays open; on
  Chrome 155 record whether it returns success with `dialogType`. Then browser_handle_dialog with `accept: false`: record
  whether it returns success with a `warning` on Edge. If the dialog is still open, close it with respond_to_dialog on
  the debug window and record that.
- **Screenshot.** On the shown tab: an image of the page without the browser's frame, `activated` absent, `width`
  1568 or less. Open `/long` with browser_navigate `newTab: true`, then browser_screenshot with the first tab's
  `targetId`: `activated: true`, an image of `/form`; record whether Windows brought the window to the front or
  flashed it. With the window covered by the check's window: record the result (an image or `CdpTimeout`).
- **Script.** browser_evaluate `1 + 1` returns `value: 2`, `valueType: "number"`; `Promise.resolve({ a: 1 })` returns
  `{ "a": 1 }`; `throw new Error('x')` returns `CdpEvaluationFailed` with `exception.text` starting `Error: x`;
  `'x'.repeat(30000)` returns `truncated: true` and `valueLength: 30002`; `document.body` returns
  `CdpEvaluationFailed` or `{}` (record which); `while (true) {}` with `timeoutMs: 1000` returns within about 2 seconds
  and the next call on the page answers.
- **Frames.** On `/frame` (the iframe placed lower than its own height, `margin-top: 200px`): `role: "button"`,
  `name: "Save draft"` finds the frame's button (record `matchCount`), browser_element_rect places it (get_element_at_point
  at its `clickPoint` returns the frame's button), and browser_click on it writes `saved` in the frame;
  `selector: "button"` finds only the main document's elements.
- **Audit and clients.** The audit log has one line per browser_click, browser_type, browser_handle_dialog,
  browser_evaluate and browser_screenshot call; none holds the typed text, the expression, a selector, a name, a
  prompt answer or browser_evaluate's value (only their lengths). After the block, `edge://inspect` (Chrome:
  `chrome://inspect`) lists no DevTools client and no infobar appeared.
- **The browser's own pages, policy, pause, a port of another process and Firefox** are covered by the fakes
  (`BrowserPageServiceTests`, `BrowserPageToolsTests`); they are not provoked on the desktop.

### Desktop check: M6.1 additive consolidations

M6.1a's consolidations are tested over fakes: set_window_bounds' pairs in `WindowManagerToolsTests` (each pair goes to
the service call move_window or resize_window makes, half a pair is `InvalidArguments` with no call) and
`WindowServiceGeometryTests` (a resize that raises keeps SWP_NOACTIVATE), get_monitors' `virtualScreen` in
`MonitorInfoToolsTests` (the same rectangle as list_screens over the same wrappers), get_browser_url's bare foreground
selector in `BrowserReadingToolsTests`, and mouse_click's description in `ToolDescriptionTests`. Not yet run on a real
desktop; before the release that carries it, the owner checks:

1. **Partial set_window_bounds on the fixture.** On the WamFixtureApp main window the harness started (and only on it),
   by its handle: `x` and `y` alone keep the visible frame's size to the pixel; `width` and `height` alone keep its
   top-left corner and leave the z-order and the foreground as they were (read get_foreground_window before and after:
   the agent's host stays in front); `x` and `y` alone onto the monitor with the other scale go through the second
   placement, as move_window does (skip with one monitor); half a pair is `InvalidArguments` and the window does not
   move. The harness closes only the fixture windows it opened.
2. **get_monitors' `virtualScreen`** (no input, no window, no pixels): equal to the deprecated list_screens' `virtualScreen` (with `WAM_TOOLSETS=+deprecated`), and to
   take_screenshot's `capturedRegion` with `monitorIndex: -1` and `output: "metadata"`. Always pass `output: "metadata"`:
   the default output returns a picture of every monitor, with whatever the owner has open, to read four integers.
3. **get_browser_url with `window: {"foreground": true}`** (optional, as in "Desktop check: M5 browser bridge"): with the
   agent's terminal in front the result is `NoBrowserInForeground`. With a throwaway debug browser from
   launch_browser_with_debug, focused by its handle, it returns that browser's URL; close it with close_debug_browser by
   its processId. Never focus or close a browser by its title.
4. **The deprecations in a real client** (M6.1b; no window, no input): with the built exe in Claude Code, `/mcp` lists 58
   tools without toolset variables, and none of click, right_click, double_click, move_window, resize_window,
   get_all_windows, get_primary_monitor, list_screens, get_active_browser_url or the three geometry batches;
   `WAM_TOOLSETS=+deprecated` adds the nine with their one-line descriptions; `WAM_EXCLUDE_TOOLS=click` still lists the
   58 (it does not fail closed); and get_server_info's `toolsets.moved` names the deprecated and windows-batch toolsets.
   The in-process contract tests (`DeprecatedToolsContractTests`) cover all of it except the client's own rendering.

### Desktop check: M6.3 OCR

read_screen_text and find_text_on_screen are tested over a fake OCR engine (`FakeOcrEngine`) and a mocked raw pixel seam:
`OcrServiceTests` (the language, the capture's refusals, the mask of denied windows, the 2x enlargement and its mapping
back, tiles and their seams, cancellation between tiles, registration after OCR), `OcrTextMatcherTests` (contains,
exact, regex, whitespace, case, a language written without spaces, the regex timeout), `DeniedWindowMaskTests`,
`OcrImageTests` and `OcrToolsTests` (the one target, the limits, an invalid regex as `InvalidArguments` before anything
is read). `OcrEngineWrapperIntegrationTests` runs the real Windows OCR on text drawn into an in-memory bitmap (no screen,
no window), and skips with the reason on a machine without an OCR language (some CI images). The guards: a WinRT call
goes through a COM vtable, not P/Invoke, so `NativeCallGuardTests` pins the projected namespaces
(`WinRtProjection_HoldsOnlyTheOcrNamespaces`) and allows only `OcrEngineWrapper` to call into the projection
(`WinRtProjection_IsOnlyCalledByOcrEngineWrapper`), and `WrapperCatalog` classifies `IOcrEngineWrapper`'s two members as
reads. CI's coverage step leaves the generated projection (`obj/.../Generated Files/CsWinRT`) out of the measured lines.

**The binding.** The server stays on `net10.0-windows` (no Windows SDK TFM): the `Microsoft.Windows.CsWinRT` package
generates a projection of only `Windows.Media.Ocr` and the few types it needs (`CsWinRTIncludes` in the csproj) into the
server assembly, and the single-file publish bundles `WinRT.Runtime.dll`. The Windows metadata it reads comes from the
`Microsoft.Windows.SDK.Contracts` package, which `dotnet restore` downloads (`PackageDownload`, never referenced), so a
build machine or CI runner needs no Windows SDK. The published exe grew from 7,030,598 to 7,977,432 bytes (+0.9 MB,
October 2026); `scripts/Publish-Plugin.ps1`'s only-exe check still passes.

The E2E scenarios ran on the owner's desktop on 2026-10-17 (see Desktop run: M6 scenarios). By hand, the owner checks:

1. **Languages (read only).** `Get-WindowsCapability -Online -Name 'Language.OCR*' | ? State -eq Installed` lists the
   installed OCR languages (it reads; install nothing). With `WAM_TOOLSETS=+ocr`: read_screen_text with no `language`
   reports the profile language it used (on a Norwegian profile without a Norwegian OCR language, the first installed one
   with an `OcrLanguageFallback` warning); `language: "en-US"` works; a language that is not installed (`ja-JP` when it
   is absent) is `OcrLanguageNotInstalled` with `installedLanguages`.
2. **The fixture's canvas (E2E).** `FixtureOcrTests` on `WAM Fixture Canvas`, which the harness opens and closes. No-input
   tier: every canvas line read inside the rectangle the fixture drew it in (its text compared with O/0 and l/I/1 folded and
   no spaces next to digits: on the owner's desktop real Windows OCR read `4711km` as `471 1 km` and `3141pt` as `314 Ipt`,
   which find_text_on_screen's contains and exact now find, October 2026), at the primary monitor's scale and on a
   monitor at another scale (skipped without one), with `ocrScale` 2 below 150%; find_text_on_screen's click point on its
   word, converting back through the captureId 1:1; a regex that allows spaces inside the numbers finds them with their units. Input tier: the canvas
   covered by the main window still reads (PrintWindow); minimized it is `WindowMinimized` and stays minimized; a click
   through a find's `captureId` and `imagePoint` lands on the word (the canvas logs it), and once the canvas moved the same
   point is `StaleCapture`; a region over a canvas a policy denies (the scenario's own policy) reads none of its words and
   reports `maskedWindows`. Record the time per call and which lines OCR misread (the 9-point line, the light-on-dark one),
   at 100%, 125%, 150% and 175% where you have such a monitor.
3. **The user's policy.** Start the server with a deny list naming `WamFixtureApp`: read_screen_text on the canvas
   window is `TargetDeniedByPolicy`; a monitor read with the canvas in front returns none of its words and
   `maskedWindows` of at least 1.
4. **Real canvas content, opened by the check.** A debug Edge from launch_browser_with_debug on a throwaway profile,
   with local pages written to a scratch folder: a canvas `fillText` page and a small PDF in Edge's viewer.
   find_text_on_screen on that window by its handle finds the words; close only that browser with close_debug_browser by
   its processId. Never focus or close a browser by its title.
5. **Performance and tiles.** A full 1080p monitor, and a 4K one if there is one (over 5 megapixels, so read at 1x): record
   the time per call. `monitorIndex: -1` on a virtual screen wider than OCR's limit (about 10,000 pixels: three 4K
   monitors side by side) is read in tiles with an `OcrTiled` warning; nothing smaller is.

Not checkable here, and so untested: RDP and Citrix sessions, games, and GPU or protected windows that PrintWindow draws
black (they come back with `ImageBlank` and no lines).

### Desktop check: M6.9 resources and prompts

The resources and prompts are tested in-process (`ResourceContractTests`, `PromptContractTests`, the resources snapshot
and `ServerInfoToolContractTests`). Run on the built exe so far (October 2026, no window, no input):
`scripts/Test-ToolsSnapshot.ps1` passes on a Debug build (78 tools, 7 resources, templates and prompts) and fails on a
snapshot with a changed title and a renamed prompt; and prompts/get over stdio rendered the four recipes with the
default toolsets, the one with `WAM_READ_ONLY=1` (diagnose_click without capture_window and the steps that act) and
the three with `WAM_TOOLSETS=-input`. Before the release that carries it, the owner checks, reading only:

1. **MCP Inspector** on the built exe with `WAM_LAYOUTS_PATH` set to a fresh temp folder (so the owner's own presets are
   neither read nor seeded): initialize advertises `resources`, `prompts` and `completions`; the three lists match
   `Contract/resources.snapshot.json`; `wam://layouts/work-setup` (a seeded sample) reads, `wam://layouts/nope` is a
   not-found error with the PresetNotFound message, and completion offers the sample names.
2. **wam://windows and wam://monitors against the tools**: with the WamFixtureApp window the harness started, read
   `wam://windows` and call find_windows at once (same handle, title and bounds), and compare `wam://monitors` with
   get_monitors; on a mixed-DPI machine the negative coordinates and scale factors match.
3. **Claude Code** with the plugin built from the branch: `/mcp` lists the resources and prompts, a prompt runs as a
   `/mcp__<server>__diagnose_click` command and expands with its arguments quoted, and an `@` mention of `wam://windows`
   attaches the JSON.

No check applies a layout, sends input or closes anything but the fixture window the harness started, by its PID.

### Desktop check: M6.2 run_steps

run_steps is tested over fakes (`StepRunnerTests`: the checks before a run, the arguments each step gets, when a run
stops, the input gate held for the whole run; `InputGateTests`: the lease) and through the real MCP pipeline with mocked
services (`RunStepsContractTests`: one audit line per audited step under `<run's id>#<step>`, the toolsets and
read-only mode, a direct call that waits for the run, a cancelled run that frees the gate, run-level progress only). Not
yet run on a real desktop; before the release that carries it, the owner checks:

1. **The fixture scenarios.** `FixtureRunStepsTests` with `WAM_ALLOW_DESKTOP_TESTS=1` (input tier), with the terminal
   that runs the tests in front and untouched: the form fill, a failed step, a direct call during a run, a pause between
   steps, a run out of time, and a second fixture taking the foreground. The harness closes only the fixtures it started.
2. **Notepad, by hand, in a real client** (no window the check did not open): launch a new Notepad with
   launch_application and `waitForWindow`, and note its handle and processId. On that handle, run_steps with
   `type_text`, `press_keys` `ctrl+s` and `wait_for_window` for the Save As dialog it owns; then answer the dialog with
   file_dialog, a direct call, to a file under `%TEMP%\wam-m62`. Check that the file holds the text, close Notepad with
   close_window by the noted handle only, and delete the file. Never close or act on a window by its title.
3. **The pause hotkey, by hand:** during a run of many type_text steps into that Notepad, press the pause hotkey. The
   run reports `PausedByUser`, nothing more is typed, and the user resumes; the agent never does.
4. **Taking the foreground, by hand:** during a run with `delayBetweenStepsMs: 2000` on that Notepad, click a second
   Notepad the check opened. The run stops with `TargetNotForeground` and the second Notepad stays in front.

### Desktop check: M6.4 visual waits

wait_for_visual is tested over fakes: the signature and the comparison in `VisualSignatureTests` and `VisualDiffTests`
(a hue change of the same brightness, the undefined 4th byte, odd edges, a crop with its parent's stride, at most 8
regions, excluded rectangles where either frame had them), the waits in `VisualChangeServiceTests` (scripted frames on a
manual clock: changed against the first frame and against `since`, resize, close, minimize and hide, stable after an
animation and its timeout, the policy's refusal and mask (also kept with a frame for since, and over a window copied from
the screen), the caret, a window that does not answer (from the start or mid-wait), the area cap, cancellation and
progress), the signatures in `CaptureRegistryTests` (kept only while the registry keeps them, the 16 MB
cap drops signatures and keeps the captures), `ScreenshotCaptureIdTests` (the full-size pixels, before
capture_with_elements' marks; none for a ref crop; the denied windows over a screen capture) and `CaptureHealthTests` (none
for a window restoreIfMinimized restored), the mask in `DeniedWindowMaskTests`, the arguments in
`WaitForVisualToolTests` (one target; a value out of range refused, never clamped), the signatures kept only while the
tool is served in `DependencyInjectionTests`, and the tool over MCP in the contract tests. `Scenarios/FixtureVisualWaitTests` (no-input tier, the fixture's `--change-after-ms` and
`--animate-for-ms`) checks them on a real window. They passed on the owner's desktop on 2026-10-17 (see Desktop run: M6 scenarios); the
owner runs that class and checks:

1. **The scenarios.** `WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1 dotnet test tests/WindowsAppManagerMcp.E2ETests --filter
   FullyQualifiedName~FixtureVisualWaitTests`. The harness owns and closes the fixture; nothing is clicked or typed. The
   output logs every result's regions, method and warnings. The region scenario reads the screen, so it needs the
   fixture's change panel uncovered (the fixture comes up in front); the window scenarios do not.
2. **The caret.** In `Changed_Since_TheLastFrame_OfAStillWindow_TimesOut` the fixture's text box has keyboard focus. When
   the fixture is the active window (its caret blinks), the waits must still time out with 0 % changed: WPF places a
   system caret for screen readers, which the wait leaves out. If they report a change at the text box, record whether
   GetGUIThreadInfo's `rcCaret` matches the drawn caret.
3. **Another scale** (optional, `MonitorAtAnotherScale`): run a changed wait on a fixture started with
   `--other-scale-monitor --change-after-ms 1500` and compare `changedRegions` with the `panelChanged` rect.
4. **Cost** (no input): a `stable` wait on one monitor (`monitorIndex`) at the default `pollMs` 200 for 30 s, with nothing
   opened or closed, while the owner watches the mouse cursor for flicker (CAPTUREBLT on repeated screen copies) and notes
   the server's CPU and working set. Then the same on a maximized window of a GPU app the check opened itself (a throwaway
   debug browser from launch_browser_with_debug on a local page, closed with close_debug_browser by its processId, never
   by title), to measure PrintWindow per check.
5. **Real clients** (no input): with the built exe and `WAM_TOOLSETS=+visual`, `/mcp` lists wait_for_visual, and a
   wait with `since` set to the `captureId` of a `capture_window` with `output: "metadata"` of a window the check opened
   compares with that capture (`checks` 1 with `timeoutMs` 0 when nothing changed: `WaitTimedOut` and 0 %); without
   `+visual` the tool is not listed.

### Desktop run: M6 scenarios (2026-10-17)

Run on the owner's desktop (Windows 11 26200, a 150% primary and a second monitor at another scale) while the owner was
away, after release 2.2.0, with both tiers on (`WAM_ALLOW_DESKTOP_TESTS=1 WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1`). The whole
E2E project passed 486 of 488; the two failures are the AltGr keyboard scenarios, which fail on this machine because
another program registered Ctrl+Alt+2. Every M6 scenario passed:

- `FixtureOcrTests` (7, both tiers), `FixtureVisualWaitTests` (6, the region scenario included, with the fixture
  uncovered), `FixtureZOrderTests` (3), `FixturePercentPlacementTests` (2), `FixtureMonitorSelectorTests` (3) and
  `FixtureLayoutTests` (5: maximized and minimized restore rectangles, the dry run, an agent-written snap, another scale).
- `PackagedAppLaunchTests` (5), after a fix: the server asked IApplicationActivationManager for option 0x1, which is
  AO_DESIGNMODE, not AO_NOERRORUI (0x2). Calculator then exited at once and the call returned 0x80040904, so every
  packaged-app launch by its appId failed in 2.2.0. `NativeAppsWrapperTests` pins the value.
- `FixtureMonitorSelectorTests`' snap by primary, device name, current and id first failed on its focus check: a snap
  onto the monitor at another scale left the fixture in front. WPF's own WM_DPICHANGED handler activates the window with
  a SetWindowPos of its own, whatever flags the server's move had (the fixture's startup works around the same thing,
  roadmap M3.F30), so the scenario now checks the focus only for snaps at the same scale.

Still for the owner, by hand: layouts after docking and undocking, with the taskbar on the left or top, saving
Calculator or Terminal in a layout (its appId), and runAsAdmin where Windows shows no UAC prompt.

### Desktop run: takeover detection (2026-10-17)

After M7.4 (2.3.0), the input tier ran `FixtureTakeoverTests`, `FixtureSessionTests`, `FixtureTypingTests`, `FixtureMouseClickDragTests` and `FixtureRunStepsTests` on a real desktop while it was idle: 16 of 16 passed. With `WAM_TAKEOVER_DETECTION=stop`, Windows installed the real low-level hooks while type_text ran (state `Watching`), removed them afterwards (`Ready`), and the server's own typing never counted as the user's: the text went in whole.

### Tool surface snapshot

`Contract/ToolSurfaceSnapshotTests` tests the server the way an agent sees it. It lists the tools of an
in-process server with the production registrations (`ServiceRegistration.AddWindowsAppManagerTools`, the
call Program.cs makes) and compares every tool object, as the server sends it (name, title, description,
annotations, inputSchema, outputSchema), with the checked-in
`tests/WindowsAppManagerMcp.Tests/Contract/tools.snapshot.json`, sorted by tool name. A failure names the
added, removed and changed tools and the JSON paths that changed. The same class snapshots what the server answers a
client's first request on each protocol revision into `Contract/initialize.snapshot.json`: the `initialize` result on
2025-06-18 and 2025-11-25, and the `server/discover` result on 2026-07-28 (where serverInfo sits in `_meta`), with
`serverInfo.version` masked. A client acts on more than the tool list (the negotiated revision, the capabilities object,
the instructions), so an SDK upgrade that starts advertising a capability, or drops a revision, fails here too. The
whole tool surface is snapshotted: `AddWindowsAppManagerTools()` without a toolset selection serves every toolset.
The resources and prompts (roadmap M6.9) are surface too: `Contract/resources.snapshot.json` holds the
resources/list, resources/templates/list and prompts/list results, each sorted, and fails the same way. Since M6.9 the
initialize snapshot advertises `resources`, `prompts` (both `listChanged: true`, as `tools` has) and `completions` on
every revision, and never resource subscriptions.

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

`UPDATE_SNAPSHOTS=1` only makes these tests rewrite their files instead of failing: the three snapshots, and the
generated regions of README.md, docs/TOOLS.md and SKILL.md. It changes nothing else about a run (desktop
tests stay skipped). Without it, CI fails on a stale snapshot or table. A run where `CI`, `GITHUB_ACTIONS` or
`TF_BUILD` is set to anything but `false` or `0` refuses it (the tests fail with a message), because there it would turn the guards into rewrites
that pass. Each regenerated file replaces the old one in one step, so a test that reads it in parallel never sees it
half written. Set the variable after any `env -u` options: `env -u WAM_ALLOW_DESKTOP_TESTS UPDATE_SNAPSHOTS=1 dotnet test ...`.

**The CI guard on the built exes.** A version or commit check cannot tell whether an exe serves the tools
its sources define. `.github/workflows/build.yml` therefore runs `scripts/Test-ToolsSnapshot.ps1`, which
starts an exe (with `WAM_PAUSE_HOTKEY=none`, `WAM_TOOLSETS=all` and neither `WAM_EXCLUDE_TOOLS` nor `WAM_READ_ONLY`,
so it serves the whole surface), sends `initialize` and `tools/list` over stdio, closes it, and compares every tool
object with the snapshot. It then lists the resources, resource templates and prompts and compares each item with
`Contract/resources.snapshot.json` (`-ResourcesSnapshotPath`), so an exe that serves stale resources or prompts fails
too. It asks for the newest revision with the `initialize` handshake (`-ProtocolVersion`,
default `2025-11-25`, the one Claude Code most likely negotiates); `ProtocolRevisionContractTests` checks that this is
the revision the SDK answers an unknown request with, and `ToolSurfaceSnapshotTests` that the script still asks for
it, so an SDK upgrade cannot leave the guard on a stale revision. Relative `-ExePath` and `-SnapshotPath` values are
resolved against the PowerShell location. It checks the exe the workflow has just published from the
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
| docs/TOOLS.md | `toolsets` | The toolsets of `Toolsets.Server`: default, description and tools (generated from the catalog, not from tools/list) |
| docs/TOOLS.md | `read-only` | The tools served in read-only mode (generated from the readOnlyHint annotations) |
| docs/TOOLS.md | `params:TOOL` | The parameter table of each tool, and a field table for each array-of-objects parameter |
| docs/TOOLS.md | `resources` | The resources and prompts (roadmap M6.9): each resource's URI, mirrored tool and description, and each prompt's arguments, needed tools and description (generated from `ResourcesAndPrompts` and the attributes, not from the lists) |
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

A new tool also needs a toolset: add it to one of `Toolsets.Server` (src/WindowsAppManagerMcp/Infrastructure/Toolsets.cs);
`ToolsetContractTests` fails until every served tool is in one, and the `toolsets` region lists it.

### Every tool over MCP and payload budgets

`Contract/EveryToolContractTests` calls every served tool through an in-process server with the production
tool and filter registrations and mocked services (`Fixtures/Contract/ContractServices.cs`; nothing native
runs). The typical call of each tool must return JSON content with `success` true and no `isError`, and a
capture its image block first. The failing call (the services report invalid handles, errors and empty lists)
must return non-empty content and `isError` equal to `!success` (roadmap M2.1: the outermost call filter sets
`isError` from a result's top-level `success`, see `Infrastructure/ToolOutcome.cs`; no tool returns null). A
list or a batch summary has no top-level `success` and keeps `isError` unset. A new tool needs typical
arguments in `EveryToolContractTests.Arguments`.

`Contract/ToolArgumentContractTests` covers the fixed-value parameters (roadmap M2.2) the same way, through the
production registrations: the input schema lists every allowed value, an unknown value fails with the
parameter's own errorCode and the allowed values while nothing is done, the documented aliases and other
casings still bind, and a bad batch item fails alone. The enums themselves (names, aliases, collisions) are
checked by `Unit/Infrastructure/ToolEnumTests`. A test that serves one tool class with its own services
registers it with `WithServerTools(instance)`, not the SDK's `WithTools(instance)`, so its arguments are bound
as the server binds them.

`Contract/ToolAnnotationContractTests` holds every tool to a title and all four hints (roadmap M2.10), and pins
the read-only, destructive and open-world sets. `Contract/UserConfirmationContractTests` covers kill_process,
close_windows_batch, close_application and close_window's `force` asking the user through elicitation (the main cases
on both protocol paths). Its elicitation handler takes the
input gate (`InputGate.TryEnterAsync`) while the question is open: a tool that asked while holding the gate would
fail it after 5 seconds instead of blocking every input call.

`Contract/ToolPayloadBudgetTests` measures the typical result of every tool on `TypicalDesktop` (two
monitors, 15 windows, 40 processes, a 120-element UI Automation tree, a 15,000-character article, five layout
presets) and holds it to a per-tool token budget. Text is estimated at 4 UTF-8 bytes per token; a capture's
image at width × height / 750 tokens, at most 3,300 (the default 1568-pixel long edge). Each budget is about
one and a half times the measured size, at least 100 tokens; the measured size is in a comment next to it. Every
typical result over 300 tokens, and tools/list itself, measured in October 2026:

| Tool | Typical result | Tokens | Budget |
|------|----------------|-------:|-------:|
| `get_ui_elements` | 120 elements at the default depth, each with its ref | 6,000 | 8,500 |
| `get_browser_content` | A 15,000-character article | 3,815 | 6,000 |
| `list_processes` | 40 processes, each with its parent, start time, integrity and up to two windows (1,328 before roadmap M6.6) | 2,976 | 4,500 |
| `get_desktop_state` | 15 window rows, the focused element, 2 popups and 80 element lines with refs (roadmap M3.12) | 2,129 | 3,200 |
| `capture_with_elements` | Metadata with the legend of 120 marks, one line each, and the tags' `markPrefix` (the image is budgeted separately; roadmap M3.11, M3.F38) | 1,416 | 2,150 |
| `read_screen_text` | The 40 lines OCR reads in a window, each with its bounds (roadmap M6.3) | 1,276 | 1,900 |
| `find_windows`, `get_all_windows` | 15 windows, each with `visibleBounds` | 1,213 | 1,850 |
| `read_element_text` | The first page of a 3,000-character e-mail | 802 | 1,200 |
| `list_installed_apps` | The first 25 of 140 Start menu apps, packaged first, desktop apps with their paths (roadmap M6.5) | 761 | 1,150 |
| `read_table` | Task Manager's first 50 processes, 5 columns with headers | 643 | 1,000 |
| `get_server_info` | The default deny list, five host processes, two monitors, the resources and prompts (634 before roadmap M6.9) | 707 | 850 |
| `get_layout` | A preset of 6 placements, each with the captured window identity, its monitor's id and name and its program (529 before M6.8) | 805 | 841 |
| tools/list | Every tool's name, title, description, schemas and annotations: 84 tools, 276,974 bytes (`ToolList_StaysWithinItsBudget`; M5.8's six DOM bridge tools added 2,970, 936 of them their window selectors; M5.8's dialog option and the UI Automation browser_handle_dialog 199 more; M6.1a's additive consolidations 405 more: mouse_click's own click checks 249, set_window_bounds' optional pairs 87, get_browser_url's foreground sentence 41, get_monitors' virtualScreen 28; M6.1b's one-line descriptions of the nine deprecated tools and the descriptions that no longer send the agent to them 2,121 fewer; M6.6's list_processes parameters and fields 361 more and get_window_info's integrity fields 31 more; M6.2's run_steps 1,054 more; M6.4's wait_for_visual 1,072 more; M6.3's read_screen_text and find_text_on_screen in the opt-in `ocr` toolset 2,088 more; M6.7's set_window_zorder (522), the `monitor` parameters beside the deprecated monitorIndex, set_window_bounds' percent placement (233), get_monitors' identity fields and get_server_info's pinnedWindows 1,514 more; M6.5's list_installed_apps and runAsAdmin 357 more; M6.8's save_layout placements and includeMinimized and apply_layout dryRun and placements 775 more) | 69,244 | 69,280 |
| Default tools/list | The 61 tools a server serves without WAM_TOOLSETS, WAM_EXCLUDE_TOOLS and WAM_READ_ONLY, 219,224 bytes (`DefaultToolList_StaysWithinItsBudget`, roadmap M6.1; 59,627 before M6.1a, 60,032 after it with 70 tools, 9,027 fewer once M6.1b moved the nine deprecated tools and the three geometry batches to opt-in toolsets, 392 more with M6.6's list_processes and get_window_info fields, 1,055 more with M6.2's run_steps in the `steps` toolset, on by default, 1,187 more with M6.7's set_window_zorder in `windows`, the `monitor` parameters, set_window_bounds' percent placement and the get_monitors and get_server_info fields; 357 more with M6.5's list_installed_apps in the `launch` toolset and runAsAdmin; 775 more with M6.8's layout schema version 2 in the `layouts` toolset) | 54,806 | 54,842 |

Every other typical result is at most 300 tokens. When a change makes a result larger on purpose, raise its
budget in the same change (the failure message gives the new size); when a change shrinks one a lot, lower it.
`ToolList_StaysWithinItsBudget` bounds tools/list, which a client that loads every tool up front keeps in context
for the whole session, serialized as the SDK writes it. Its budget is a ratchet, not a margin: raise it only in a
change that adds to the surface on purpose, by what the failure message measures, and lower it when descriptions are
trimmed. `DefaultToolList_StaysWithinItsBudget` (roadmap M6.1) is the same ratchet for the default surface, the one
nearly every user pays for: a tool added to a toolset that is off by default leaves it alone, and
`DefaultToolList_IsTheToolsOfTheToolsetsOnByDefault` checks that it measures exactly those toolsets.
`EveryToolset_StaysWithinItsBudget` holds each toolset's share of tools/list (the sum of its tool objects as the SDK
writes them) to a ratchet just above its measured size, and docs/TOOLS.md publishes the sizes, to the nearest 100, in
the generated Tokens column of its toolsets table, with the default surface's and the whole list's totals under it,
measured as the two list budgets measure them (`GeneratedToolDocsTests`); `EveryToolset_HasATokenBudget` makes a
new toolset come with a budget. `EveryTool_SchemaWithinCap` caps every single tool object at 2,500 tokens: a cap, not a
ratchet, so a tool that needs more is split or trimmed rather than the cap raised. It sits about 100 tokens above the
largest tool (the roadmap's 2,400 came from compact estimates). Measured in October 2026 after M6.1a (the largest tools
were capture_with_elements at 2,396 and element_action at 2,386, so they have about 100 tokens of room left):

| Toolset | Default | Tools | Tokens | Budget |
|---------|---------|------:|-------:|-------:|
| `server` | always on | 4 | 1,056 | 1,092 |
| `windows` | on | 13 | 10,884 | 10,920 |
| `monitors` | on | 1 | 167 | 203 |
| `capture` | on | 4 | 5,914 | 5,950 |
| `elements` | on | 9 | 8,262 | 8,300 |
| `input` | on | 10 | 14,758 | 14,800 |
| `clipboard` | on | 2 | 734 | 750 |
| `launch` | on | 3 | 3,336 | 3,372 |
| `process` | on | 3 | 2,282 | 2,318 |
| `layouts` | on | 5 | 1,992 | 2,028 |
| `steps` | on | 1 | 1,055 | 1,091 |
| `browser` | on | 6 | 4,356 | 4,392 |
| `browser-actions` | off | 7 | 3,577 | 3,600 |
| `browser-script` | off | 1 | 468 | 500 |
| `visual` | off | 1 | 1,145 | 1,181 |
| `windows-batch` | off | 3 | 2,066 | 2,102 |
| `deprecated` | off | 9 | 4,925 | 4,950 |
| `ocr` | off | 2 | 2,254 | 2,290 |

The rows are measured after M6.1b (October 2026), which moved six tools out of `windows`, two out of `monitors`, three
out of `input` and one out of `browser` into the opt-in `windows-batch` and `deprecated` toolsets. The deprecated tools'
one-line descriptions keep their full parameters, which are most of the `deprecated` row. M6.6 (October 2026) raised
`process` by 361 (list_processes' five parameters and its result fields in the description; 1,921 before) and
`windows` by 31 (get_window_info's integrity fields in 'full'; 9,921 before), each budget to the measured size plus 36. The `steps` row is measured
after M6.2 added run_steps (October 2026). M6.4 added the `visual` row
(wait_for_visual: handle, the window selector, a region, monitorIndex and since). The `ocr` row is M6.3's two tools
(October 2026), which share their target and result descriptions. M6.7 (October 2026) raised `windows` by 932
(set_window_zorder, 522; set_window_bounds' unit, relativeTo and monitor; snap_window's and move_window_to_monitor's
`monitor`), `monitors` by 49 (get_monitors' identity fields in its description), `server` by 18 (pinnedWindows),
`capture` by 58, `launch` by 89, `browser` by 41, `visual` by 73, `windows-batch` by 88 and `ocr` by 167 (each tool's
`monitor` beside the deprecated monitorIndex, whose description shrank to one line), each budget to the measured size plus
36. M6.5 (October 2026) raised `launch` by 357 (2,979 before):
list_installed_apps (about 200), launch_application's runAsAdmin and installed-app wording (about 115) and the batch item's
runAsAdmin (about 40), and the budget to the measured size plus 36.

A change states its default, per-toolset and all-tools deltas, and raises only the ratchets it moves: a new opt-in
toolset moves the all-tools figure and its own row, never the default figure.
`Contract/ElementReadContractTests` also bounds the largest result read_element_text and read_table can return
(a million-character document of English, Cyrillic, CJK, emoji or quotes, a 1000 x 100 grid of long, short or empty
cells in English, Cyrillic or CJK, read end to end through the real read service over a fake tree) at 16,000 tokens:
the reads bound a page in bytes of JSON, not only in characters.

The resources and prompts (roadmap M6.9) cost nothing in tools/list. `ResourcesAndPromptsLists_StayWithinTheirBudget`
holds resources/list, resources/templates/list and prompts/list together to a ratchet of 655 tokens (646 measured,
October 2026, for two resources, one template and four prompts), and `Contract/PromptContractTests` holds each prompt's
text, every step served and with sample arguments, to 480 tokens (automate_app 464, arrange_workspace 393, fill_form 375,
diagnose_click 447).

### Resources and prompts

`Contract/ResourceContractTests` reads the three wam:// resources over MCP with the contract services, on every revision:
each is `application/json` text equal to the result of the tool it mirrors (`get_monitors`, `find_windows` with no
argument, `get_layout`), the windows resource asks the window service the same query as find_windows, a window of an
app the target policy denies is listed as find_windows lists it, a non-ASCII layout name round-trips without escapes,
and a name no preset has gets the error code the SDK sends for a URI no resource matches on that revision (`-32002`
up to 2025-11-25, `-32602` on 2026-07-28). It also checks the call-log line of each read, the error that names an
unexpected exception, the completion of layout names, and that a resource is served exactly while the tool it
mirrors is (toolsets, exclusions, read-only mode), with the code of an unknown URI when it is not, even when no
resource is served at all. A cancellation the client did not send (a wait given up) is logged as cancelled, not as a
failure, and a URI with line breaks is logged on one line. `Contract/PromptContractTests` gets every served prompt
under ten selections (default, all, read-only, toolsets off, tools excluded) and checks that every tool name in its
text is served, that its steps are numbered without a gap where a step was left out, and that a prompt is served
only while the tools it needs are; that arguments are required, quoted as JSON strings and never logged; that an
empty or missing argument, and a prompt that is not served, is an invalid-params error; and that a name with line
breaks is logged on one line.

### Protocol revisions, progress and cancellation

The server uses the stable MCP C# SDK, `ModelContextProtocol` 2.2.0. It speaks two kinds of protocol revision,
and the tests that depend on the difference run on both:

- **2026-07-28**, which the SDK's own client prefers, so `InProcessMcpServer` negotiates it by default
  (`InProcessMcpServer.LatestRevision`): `server/discover` instead of the `initialize` handshake, and
  elicitation as a multi-round-trip request (the tool call returns `input_required`, and the client retries it
  with the answer).
- **The revisions with the `initialize` handshake**, which stdio clients use: `2025-11-25`
  (`InProcessMcpServer.NewestInitializeRevision`), the newest one, which the SDK prefers on that path, Claude Code
  most likely negotiates and the CI guard (`scripts/Test-ToolsSnapshot.ps1`) asks for; and `2025-06-18`
  (`InProcessMcpServer.InitializeRevision`), which older clients still use. Elicitation is a request from the server
  while the call is open (2025-11-25 added its form and url modes). Pass
  `InProcessMcpServer.WithRevision(options, revision)` as the client options to test this path.

`Contract/ProtocolRevisionContractTests` checks that every revision lists exactly the snapshot's tools and returns
captures and tool errors alike, and (through `Fixtures/RawMcpConnection`, which writes JSON-RPC by hand) that an
`initialize` for a revision the SDK does not know is answered with `2025-11-25`. The elicitation (resume_input),
cancellation and stdin EOF tests of `InputControlToolContractTests`, the progress and cancellation tests of
`UIElementToolContractTests` and `LongRunningToolContractTests`, and the session report of
`ServerInfoToolContractTests` are theories over `InProcessMcpServer.Revisions()` (all three). A new test of something
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
ends the wait for the gate. A cancelled call gets no result, and the call log reports it as `cancelled`: the
cancellation tests check the wire with `server.AssertNoResponseSentAsync(id)` (no result or error with that id in
`ServerMessages`). The client's own call is no evidence, because the SDK client ends its pending call as soon as
`notifications/cancelled` is sent for it, answered or not.

### Toolsets and server info

`Contract/ToolsetContractTests` checks the toolset mechanism (roadmap M2.14) over MCP with fixture tools and a
fixture catalog that has an opt-in toolset: `WAM_TOOLSETS`, `WAM_EXCLUDE_TOOLS` and `WAM_READ_ONLY` decide what
tools/list lists, and a call to a tool that is not served is an unknown tool. Against the production catalog it
checks that every served tool is in exactly one toolset, that the default serves every toolset except the opt-in
`browser-actions`, `browser-script`, `visual`, `windows-batch` and `deprecated`, that browser_evaluate is served only with `browser-script` (turning on
`browser-actions` does not serve it, and `browser-script` alone serves none of the other DOM bridge tools), that read-only
mode serves exactly the tools annotated `readOnlyHint: true` (browser_element_rect only once `browser-actions` is on,
wait_for_visual only once `visual` is on, browser_evaluate never), and that each of those is a read-only tool of the input
gate. `Unit/Infrastructure/ToolsetSelectionTests` covers the parsing, and over a fixture catalog with moved tools the
rules of roadmap M6.1: a moved tool is served only while the toolset it came from is on too, a toolset's name in
`WAM_EXCLUDE_TOOLS` also excludes the tools that moved out of it, excluding a tool that is not served is no error (no
fail-closed), `moved` says where a tool that is out of reach went, and the warnings name a moved tool its toolset cannot
serve and the replaced tools that are served. `Contract/DeprecatedToolsContractTests` checks the production catalog: the
nine tools of `deprecated` and the three of `windows-batch`, both off by default, every deprecated tool's one-line
description with its replacement and "removed in 3.0.0", a call to a moved tool on a default server being an unknown
tool (never forwarded), `all,-input` serving no click, `WAM_EXCLUDE_TOOLS=click` on a default server serving the whole
default, the deprecated tools staying on the input gate's and the audit log's lists, and get_server_info's `moved` and
warnings. `Contract/ServerInfoToolContractTests` calls
`get_server_info` over MCP (a real `ServerInfoService` over the contract services' mocked sources),
`Unit/Services/ServerInfoServiceTests` checks the report and its warnings, and `Contract/ServerInstructionsTests`
keeps the server instructions at 15 to 20 lines and under 3,000 characters, naming only tools the default serves; its
Act rule names every input tool the default serves and lists exactly those (click, right_click and double_click stay on
the input gate's list while the opt-in `deprecated` toolset serves them, but the instructions send the agent to
mouse_click).

### Audit log and clipboard in tests

Two production services write to the user's machine, so a test never uses them as they are configured for the
server:

- **The audit log** (roadmap M2.17). `ServiceRegistration.AddWindowsAppManagerServices` defaults to
  `AuditLogOptions.FromEnvironment`, the real `%LOCALAPPDATA%\WindowsAppManagerMcp\audit` folder (or
  `WAM_AUDIT_PATH`). A test that builds the server with the production registrations and calls an audited tool must
  pass a temporary folder: `auditLog: () => AuditLogOptions.ForFolder(<temp folder>)`, as
  `Integration/DependencyInjectionTests` does; the contract services (`Fixtures/Contract/ContractServices.cs`) write
  their audit log into a temporary folder of their own, and the E2E `ServerHarness` into its temporary layouts folder.
  Nothing is written until an audited tool is called. `Unit/Services/AuditLogTests` covers the writer (retention, the
  background writer and its 2-second wait, shared files), `Unit/Infrastructure/AuditRecordTests` what a line holds, and
  `Contract/AuditLogContractTests` which tools are audited and that no string argument reaches the file.
- **The clipboard** (roadmap M2.16). Every clipboard test runs against `Fixtures/FakeClipboardWrapper`, an in-memory
  `INativeClipboardWrapper` with a sequence number, formats, an owner process and the privacy markers, or against a
  mocked `IClipboardService` (the contract services); none opens the real clipboard, which belongs to the user. The E2E
  wrapper catalog refuses reading or writing the clipboard's content in every tier. `ClipboardOriginTests` covers which copies
  count as the agent's, `ClipboardPasteGuardTests` the paste rule, `ClipboardServiceTests` the reads and writes, and
  `Contract/ClipboardToolContractTests` the two tools over MCP, the user's consent included.

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
  uses: actions/upload-artifact@v7
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

*Last updated: October 2026*
