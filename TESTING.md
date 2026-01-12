# Testing Guide

> **WindowsAppManagerMcp** | .NET 8.0 | xUnit | Moq

A comprehensive guide for Test-Driven Development (TDD) using the Red-Green-Refactor methodology.

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
│       ├── Services/
│       │   ├── Interfaces/
│       │   │   ├── IWindowService.cs
│       │   │   ├── IMonitorService.cs
│       │   │   ├── IProcessService.cs
│       │   │   └── ILayoutService.cs
│       │   ├── WindowService.cs
│       │   ├── MonitorService.cs
│       │   ├── ProcessService.cs
│       │   └── LayoutService.cs
│       └── Models/
│
└── tests/
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
        │       └── WindowInfoTests.cs
        ├── Integration/
        │   ├── LayoutPersistenceTests.cs
        │   └── ServiceInteractionTests.cs
        ├── Fixtures/
        │   ├── TestDataFactory.cs
        │   └── MockNativeWrapper.cs
        └── Helpers/
            └── AssertExtensions.cs
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
| Native | 0% | N/A | Excluded - can't unit test P/Invoke |

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
# Run tests with coverage
dotnet test /p:CollectCoverage=true

# Generate HTML report
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:./coverage/coverage.cobertura.xml -targetdir:./coverage/report

# Open report
start ./coverage/report/index.html
```

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
# Run all tests
dotnet test

# Run with verbose output
dotnet test --logger "console;verbosity=detailed"

# Run specific test class
dotnet test --filter "FullyQualifiedName~WindowServiceTests"

# Run specific test method
dotnet test --filter "FullyQualifiedName~IsValidWindow_WithZeroHandle"

# Run tests matching pattern
dotnet test --filter "ClassName~Layout"

# Run with coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura

# Run and generate coverage report
dotnet test /p:CollectCoverage=true && reportgenerator -reports:./coverage/*.xml -targetdir:./coverage/report
```

### Visual Studio

| Action | Shortcut |
|--------|----------|
| Open Test Explorer | `Ctrl+E, T` |
| Run All Tests | `Ctrl+R, A` |
| Run Tests in Current Context | `Ctrl+R, T` |
| Debug Tests in Current Context | `Ctrl+R, Ctrl+T` |
| Run Failed Tests | `Ctrl+R, F` |

### Continuous Integration

Add to your CI pipeline:

```yaml
# GitHub Actions example
- name: Run tests
  run: dotnet test --configuration Release --logger trx --results-directory TestResults

- name: Upload test results
  uses: actions/upload-artifact@v3
  with:
    name: test-results
    path: TestResults
```

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
