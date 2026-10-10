using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

namespace WindowsAppManagerMcp.Tests.Unit.Architecture;

/// <summary>
/// Guards that keep the default test run desktop-safe.
/// Service tests mock INativeInputWrapper, INativeWindowWrapper and INativeProcessWrapper. If a
/// service called SendInput, SetWindowPos or a similar API directly, its tests would act on the
/// real desktop again. These tests scan the server assembly's IL:
/// <list type="bullet">
/// <item>Allow-lists: outside the action wrappers (<see cref="ActionWrappers"/>), code may call only
/// the NativeMethods APIs in <see cref="ReadOnlyNativeApis"/>; and outside UIAutomationWrapper,
/// only property getters and <see cref="ReadOnlyUiAutomationMethods"/> of UI Automation elements,
/// patterns and text ranges. A new P/Invoke, a new NativeMethods class or a UI Automation action
/// (Invoke, SetValue, Toggle, SetFocus, ...) therefore fails until it is moved behind a mockable
/// wrapper or reviewed onto a list.</item>
/// <item>The NativeWindowWrapper queries that read-only tests run against the real desktop (through
/// ReadOnlyNativeWindowWrapper) may call only the read-only APIs.</item>
/// <item>Deny-lists that pin specific APIs to their wrapper: input, window changes, and starting
/// or stopping processes.</item>
/// </list>
/// </summary>
public class NativeCallGuardTests
{
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly ServerAssembly = typeof(WindowService).Assembly;

    private static readonly Type NativeMethodsType =
        ServerAssembly.GetType("WindowsAppManagerMcp.Native.NativeMethods", throwOnError: true)!;

    private static readonly HashSet<string> WindowChangingUser32Methods = new(StringComparer.Ordinal)
    {
        "SetWindowPos", "MoveWindow", "ShowWindow", "SetForegroundWindow", "BringWindowToTop",
        "SetWindowLongW", "SetWindowPlacement", "AllowSetForegroundWindow", "PostMessageW"
    };

    /// <summary>
    /// The wrappers that tests always replace with mocks: they may call any NativeMethods API. Every
    /// other type, including the read-only Native wrappers that some tests use for real, is held to
    /// <see cref="ReadOnlyNativeApis"/>.
    /// </summary>
    private static readonly Type[] ActionWrappers =
    [
        typeof(NativeWindowWrapper), typeof(NativeInputWrapper), typeof(NativeProcessWrapper)
    ];

    /// <summary>
    /// NativeMethods APIs ("Class.Method") that only read state or manage the server's own handles
    /// and memory device contexts. Add an API here only after checking that it cannot move, focus,
    /// close or otherwise change a window, send input, or start or stop a process.
    /// </summary>
    private static readonly Dictionary<string, string> ReadOnlyNativeApis = new(StringComparer.Ordinal)
    {
        // Process queries (ProcessService reads the image path of a process)
        ["Kernel32.OpenProcess"] = "opens a process handle; ProcessService asks only for query access",
        ["Kernel32.QueryFullProcessImageNameW"] = "reads the image path of a process",
        ["Kernel32.CloseHandle"] = "closes a handle the server opened",

        // Monitors (NativeMonitorWrapper)
        ["User32.EnumDisplayMonitors"] = "lists monitors",
        ["User32.GetMonitorInfoW"] = "reads monitor bounds",
        ["User32.MonitorFromPoint"] = "finds the monitor at a point",
        ["User32.MonitorFromWindow"] = "finds the monitor of a window",
        ["Shcore.GetDpiForMonitor"] = "reads the DPI of a monitor",

        // Window hit testing for get_element_at_point (NativeHitTestWrapper)
        ["User32.WindowFromPoint"] = "finds the window at a point",
        ["User32.GetAncestor"] = "finds the top-level window of a window",

        // Screen capture into memory device contexts owned by the server (ScreenCaptureWrapper)
        ["User32.GetDC"] = "gets a device context to copy pixels from",
        ["User32.GetWindowDC"] = "gets the device context of a window to copy pixels from",
        ["User32.ReleaseDC"] = "releases a device context the server got",
        ["User32.PrintWindow"] = "asks a window to draw itself into a memory DC of the server",
        ["User32.GetClientRect"] = "reads the client size of a window",
        ["User32.GetWindowRect"] = "reads the bounds of a window",
        ["User32.IsWindow"] = "checks that a handle is a window",
        ["User32.GetSystemMetrics"] = "reads the virtual screen size",
        ["Gdi32.CreateCompatibleDC"] = "creates a memory DC in the server",
        ["Gdi32.CreateCompatibleBitmap"] = "creates a bitmap in the server",
        ["Gdi32.SelectObject"] = "selects a bitmap into a memory DC of the server",
        ["Gdi32.BitBlt"] = "copies screen pixels into a memory DC of the server",
        ["Gdi32.GetDIBits"] = "reads the copied pixels",
        ["Gdi32.DeleteDC"] = "frees a memory DC of the server",
        ["Gdi32.DeleteObject"] = "frees a bitmap of the server",

        // CDP port owner (TcpPortOwnerResolver)
        ["Iphlpapi.GetExtendedTcpTable"] = "reads which process listens on a TCP port",

        // Integrity, elevation and desktop-state probes (NativeSecurityWrapper)
        ["Kernel32.GetProcessTimes"] = "reads when a process started",
        ["Kernel32.GetCurrentThreadId"] = "reads the ID of the calling thread",
        ["Advapi32.OpenProcessToken"] = "opens a process token; NativeSecurityWrapper asks only for TOKEN_QUERY",
        ["Advapi32.GetTokenInformation"] = "reads the elevation and integrity level of a token",
        ["Advapi32.GetSidSubAuthorityCount"] = "reads a SID in the server's own memory",
        ["Advapi32.GetSidSubAuthority"] = "reads a SID in the server's own memory",
        ["User32.OpenInputDesktop"] = "opens the input desktop with DESKTOP_READOBJECTS to see whether it can be reached; " +
                                      "the server never switches desktops",
        ["User32.CloseDesktop"] = "closes a desktop handle the server opened",
        ["User32.GetThreadDesktop"] = "reads which desktop a thread of the server is on",
        ["User32.GetUserObjectInformationW"] = "reads the name of a desktop",
        ["Wtsapi32.WTSQuerySessionInformationW"] = "reads whether the server's session is locked or disconnected",
        ["Wtsapi32.WTSFreeMemory"] = "frees memory that WTSQuerySessionInformationW returned",
        // Window reads: the NativeWindowWrapper queries that ReadOnlyNativeWindowWrapper forwards
        // (see NativeWindowWrapperQueries_CallOnlyReadOnlyNativeApis)
        ["User32.EnumWindows"] = "lists the top-level windows",
        ["User32.EnumChildWindows"] = "lists the child windows of a window",
        ["User32.IsWindowVisible"] = "reads whether a window has WS_VISIBLE",
        ["User32.IsIconic"] = "reads whether a window is minimized",
        ["User32.IsZoomed"] = "reads whether a window is maximized",
        ["User32.GetWindowTextLengthW"] = "reads the length of a window title",
        ["User32.GetWindowTextW"] = "reads a window title",
        ["User32.GetClassNameW"] = "reads the class name of a window",
        ["User32.GetWindowThreadProcessId"] = "reads which process owns a window",
        ["User32.GetForegroundWindow"] = "reads which window is in the foreground",
        ["User32.GetWindowLongW"] = "reads a style value of a window (SetWindowLongW, which changes it, is not listed)",
        ["User32.GetWindowLongPtrW"] = "reads a style value of a window",
        ["User32.GetWindow"] = "reads a related window, such as the owner or the enabled popup",
        ["User32.IsHungAppWindow"] = "reads whether the thread of a window is hung",
        ["User32.GetDpiForWindow"] = "reads the DPI of a window",
        ["Dwmapi.DwmGetWindowAttribute"] = "reads a DWM attribute such as cloaking or the visible frame (DwmSetWindowAttribute, which changes them, is not listed)",
        ["Kernel32.GetCurrentProcessId"] = "reads the server's own process ID",
    };

    /// <summary>
    /// Reviewed calls outside the action wrappers that change something, keyed by (full name of the
    /// calling type, "Class.Method").
    /// </summary>
    private static readonly Dictionary<(string Caller, string Api), string> SanctionedNativeCalls = new()
    {
        [("Program", "Shcore.SetProcessDpiAwareness")] = "sets the DPI awareness of the server process itself, at startup",
    };

    /// <summary>
    /// Methods ("Type.Method") of UI Automation elements, patterns and text ranges that only read
    /// (property getters are always allowed). Actions such as InvokePattern.Invoke,
    /// ValuePattern.SetValue, TogglePattern.Toggle, ExpandCollapsePattern.Expand,
    /// SelectionItemPattern.Select, ScrollPattern.Scroll, WindowPattern.Close,
    /// WindowPattern.SetWindowVisualState or AutomationElement.SetFocus must stay off this list.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyUiAutomationMethods = new(StringComparer.Ordinal)
    {
        "AutomationElement.FromHandle", "AutomationElement.FromPoint", "AutomationElement.FindFirst",
        "AutomationElement.FindAll", "AutomationElement.GetCurrentPattern", "AutomationElement.TryGetCurrentPattern",
        "AutomationElement.GetCurrentPropertyValue", "AutomationElement.GetSupportedPatterns",
        "AutomationElement.GetRuntimeId", "AutomationElement.Equals", "AutomationElement.GetHashCode",
        "AutomationElement.op_Equality", "AutomationElement.op_Inequality",
        "TextPattern.GetSelection", "TextPattern.GetVisibleRanges",
        "TextPatternRange.GetText", "TextPatternRange.GetBoundingRectangles", "TextPatternRange.GetAttributeValue",
    };

    private static readonly Dictionary<ushort, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => unchecked((ushort)op.Value));

    [Fact]
    public void NativeMethods_OutsideTheActionWrappers_AreOnlyReadOnlyApis()
    {
        // Act - every call into NativeMethods (any nested class, so a new class is covered too)
        var calls = FindCalls(IsNativeMethodsApi);

        // Assert
        calls.Should().Contain(c => c.Owner == typeof(ScreenCaptureWrapper) && c.Api == "Gdi32.BitBlt",
            "the scan must see the read-only wrappers' own native calls");
        calls.Should().Contain(c => c.Owner == typeof(NativeWindowWrapper) && c.Api == "User32.SetWindowPos",
            "the scan must see the action wrappers' native calls");

        calls
            .Where(c => !ActionWrappers.Contains(c.Owner) && c.Owner != NativeMethodsType)
            .Where(c => !ReadOnlyNativeApis.ContainsKey(c.Api))
            .Where(c => !SanctionedNativeCalls.ContainsKey((c.Owner.FullName ?? c.Owner.Name, c.Api)))
            .Select(c => c.Description)
            .Should().BeEmpty(
                "outside {0} only the read-only NativeMethods APIs on the allow-list may be called; move other " +
                "calls behind a mockable wrapper, or review them onto ReadOnlyNativeApis",
                string.Join(", ", ActionWrappers.Select(w => w.Name)));
    }

    [Fact]
    public void NativeWindowWrapperQueries_CallOnlyReadOnlyNativeApis()
    {
        // Arrange - ReadOnlyNativeWindowWrapper forwards every query to the real NativeWindowWrapper,
        // so read-only tests run these methods against the user's desktop. NativeWindowWrapper is an
        // action wrapper and may call anything, so check its queries separately: each must reach only
        // read-only APIs, or a "read" could change a real window during the default test run.
        var windowChanges = Tests.Unit.Infrastructure.ReadOnlyNativeWindowWrapperTests.WindowChanges()
            .Select(row => (string)row[0])
            .ToHashSet(StringComparer.Ordinal);
        var queries = typeof(INativeWindowWrapper).GetMethods()
            .Select(m => m.Name)
            .Where(name => !windowChanges.Contains(name))
            .ToList();

        // Act - native calls made by each query and by the lambdas it declares ("<Query>b__0")
        var queryCalls = FindCalls(IsNativeMethodsApi)
            .Where(c => c.Owner == typeof(NativeWindowWrapper))
            .Where(c => queries.Any(q => IsCallFrom(c, q)))
            .ToList();

        // Assert
        queries.Should().Contain(nameof(INativeWindowWrapper.IsCloaked)).And.NotContain(nameof(INativeWindowWrapper.SetWindowPos));
        queryCalls.Should().Contain(c => c.Api == "Dwmapi.DwmGetWindowAttribute",
            "the scan must see the queries' own native calls");
        queryCalls
            .Where(c => !ReadOnlyNativeApis.ContainsKey(c.Api))
            .Select(c => c.Description)
            .Should().BeEmpty(
                "a NativeWindowWrapper query must only read; put a new read API on ReadOnlyNativeApis after review, " +
                "and make a method that changes a window throw in ReadOnlyNativeWindowWrapper");

        static bool IsCallFrom(Call call, string method)
        {
            var caller = call.Caller[(call.Caller.LastIndexOf('.') + 1)..];
            return caller == method || caller.StartsWith($"<{method}>", StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NativeApiAllowLists_NameExistingNativeMethods()
    {
        // A stale or misspelled entry would hide a review gap the next time that name is reused.
        var existing = NativeMethodsType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .SelectMany(type => type.GetMethods(DeclaredMembers).Select(m => $"{type.Name}.{m.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        ReadOnlyNativeApis.Keys.Concat(SanctionedNativeCalls.Keys.Select(k => k.Api))
            .Where(api => !existing.Contains(api))
            .Should().BeEmpty();
    }

    [Fact]
    public void PlatformInvokeDeclarations_AreOnlyInNativeMethods()
    {
        // Arrange - a [DllImport] declared elsewhere would bypass the allow-list above
        var imports = ServerAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(DeclaredMembers))
            .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .ToList();

        // Assert
        imports.Should().NotBeEmpty("the scan must see the server's own P/Invoke declarations");
        imports.Where(m => OutermostType(m.DeclaringType!) != NativeMethodsType)
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .Should().BeEmpty("P/Invoke declarations belong in NativeMethods, where the allow-list sees their callers");
    }

    [Fact]
    public void UiAutomationActions_AreOnlyCalledByUIAutomationWrapper()
    {
        // Act
        var calls = FindCalls(callee => callee.DeclaringType != null && IsActionCapableUiAutomationType(callee.DeclaringType));

        // Assert
        calls.Should().Contain(c => c.Owner == typeof(UiaTree),
            "the scan must see the UI Automation tree adapter's own reads");
        calls
            .Where(c => c.Owner != typeof(UIAutomationWrapper))
            .Where(c => !IsReadOnlyUiAutomationCall(c.Callee))
            .Select(c => c.Description)
            .Should().BeEmpty(
                "UI Automation actions (Invoke, SetValue, Toggle, Expand, Select, Scroll, Close, SetFocus, ...) must go " +
                "through IUIAutomationWrapper so tests can mock them; reads must be on ReadOnlyUiAutomationMethods");
    }

    [Theory]
    [InlineData(typeof(UIElementService))]
    [InlineData(typeof(ElementInspectionService))]
    public void UiaTreeServices_ReadUiAutomationOnlyThroughIUiaTree(Type service)
    {
        // Act - any call into System.Windows.Automation, reads included
        var calls = FindCalls(callee =>
            callee.DeclaringType?.Namespace?.StartsWith("System.Windows.Automation", StringComparison.Ordinal) == true);

        // Assert - otherwise the fake-tree unit tests no longer cover what the service does
        calls.Should().Contain(c => c.Owner == typeof(UiaTree), "the scan must see the adapter's own UI Automation calls");
        calls.Where(c => c.Owner == service).Select(c => c.Description).Should().BeEmpty(
            "{0} must read UI Automation through IUiaTree (adapter: UiaTree), so its walk and filters stay unit-testable",
            service.Name);
    }

    [Fact]
    public void NativeInputMethods_AreOnlyCalledByNativeInputWrapper()
    {
        // Arrange
        var inputType = NativeMethodsType.GetNestedType("Input", BindingFlags.NonPublic | BindingFlags.Public);
        inputType.Should().NotBeNull();

        // Act
        var callers = FindCallers(callee => callee.DeclaringType == inputType);

        // Assert
        callers.Keys.Should().Contain(typeof(NativeInputWrapper),
            "the scan must see the wrapper's own SendInput and SetCursorPos calls");
        Unexpected(callers, typeof(NativeInputWrapper), NativeMethodsType).Should().BeEmpty(
            "mouse and keyboard input must go through INativeInputWrapper so tests can mock it");
    }

    [Fact]
    public void WindowChangingUser32Methods_AreOnlyCalledByNativeWindowWrapper()
    {
        // Arrange
        var user32Type = NativeMethodsType.GetNestedType("User32", BindingFlags.NonPublic | BindingFlags.Public);
        user32Type.Should().NotBeNull();

        // Act
        var callers = FindCallers(callee =>
            callee.DeclaringType == user32Type && WindowChangingUser32Methods.Contains(callee.Name));

        // Assert
        callers.Keys.Should().Contain(typeof(NativeWindowWrapper),
            "the scan must see the wrapper's own SetWindowPos and ShowWindow calls");
        Unexpected(callers, typeof(NativeWindowWrapper), NativeMethodsType).Should().BeEmpty(
            "moving, resizing, showing and focusing windows must go through INativeWindowWrapper so tests can mock it");
    }

    [Fact]
    public void ProcessStartAndKill_AreOnlyCalledByNativeProcessWrapper()
    {
        // Act
        var callers = FindCallers(callee =>
            callee.DeclaringType == typeof(Process) &&
            callee.Name is nameof(Process.Start) or nameof(Process.Kill) or nameof(Process.CloseMainWindow));

        // Assert - NativeProcessWrapper (with its nested ProcessHandle) is the process seam that
        // ProcessService and its tests mock. Services and tools must not start, close or kill
        // processes themselves; route new callers through INativeProcessWrapper or IProcessService.
        callers.Keys.Should().Contain(typeof(NativeProcessWrapper),
            "the scan must see the wrapper's own Process.Start, Kill and CloseMainWindow calls");
        callers[typeof(NativeProcessWrapper)].Should().Contain(c => c.EndsWith($"Process.{nameof(Process.Start)}"))
            .And.Contain(c => c.EndsWith($"Process.{nameof(Process.Kill)}"))
            .And.Contain(c => c.EndsWith($"Process.{nameof(Process.CloseMainWindow)}"));
        Unexpected(callers, typeof(NativeProcessWrapper)).Should().BeEmpty(
            "starting and stopping processes must go through INativeProcessWrapper so tests can mock it");
    }

    [Fact]
    public void NoPlatformInvoke_StartsOrTerminatesProcesses()
    {
        // Arrange - Win32 entry points that would bypass the Process class (and so the guard above)
        string[] processControlPrefixes =
        [
            "CreateProcess", "ShellExecute", "WinExec", "TerminateProcess", "TerminateJobObject", "NtTerminateProcess"
        ];

        // Act
        var imports = ServerAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(DeclaredMembers))
            .Select(m => (Method: m, Import: m.GetCustomAttribute<System.Runtime.InteropServices.DllImportAttribute>()))
            .Where(m => m.Import != null)
            .Select(m => (Name: $"{m.Method.DeclaringType?.FullName}.{m.Method.Name}", EntryPoint: m.Import!.EntryPoint ?? m.Method.Name))
            .ToList();

        // Assert
        imports.Should().NotBeEmpty("the scan must see the server's own P/Invoke declarations");
        imports
            .Where(i => processControlPrefixes.Any(prefix => i.EntryPoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(i => $"{i.Name} imports {i.EntryPoint}")
            .Should().BeEmpty("processes must be started and stopped through NativeProcessWrapper, not by direct Win32 calls");
    }

    private static List<string> Unexpected(Dictionary<Type, List<string>> callers, params Type[] allowedOwners) =>
        callers.Where(c => !allowedOwners.Contains(c.Key)).SelectMany(c => c.Value).ToList();

    /// <summary>
    /// Returns every top-level type in the server assembly whose code (including lambdas,
    /// iterators and async state machines) calls a method matching <paramref name="isTarget"/>.
    /// </summary>
    private static Dictionary<Type, List<string>> FindCallers(Func<MethodBase, bool> isTarget) =>
        FindCalls(isTarget)
            .GroupBy(c => c.Owner)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Description).ToList());

    /// <summary>
    /// Returns every call in the server assembly's code (including lambdas, iterators and async
    /// state machines) to a method matching <paramref name="isTarget"/>.
    /// </summary>
    private static List<Call> FindCalls(Func<MethodBase, bool> isTarget)
    {
        var calls = new List<Call>();

        foreach (var type in ServerAssembly.GetTypes())
        {
            var owner = OutermostType(type);
            var members = type.GetMethods(DeclaredMembers).Cast<MethodBase>()
                .Concat(type.GetConstructors(DeclaredMembers));

            foreach (var method in members)
            {
                calls.AddRange(GetCalledMethods(method).Where(isTarget)
                    .Select(callee => new Call(owner, $"{type.FullName}.{method.Name}", callee)));
            }
        }

        return calls;
    }

    private static Type OutermostType(Type type)
    {
        while (type.DeclaringType != null)
            type = type.DeclaringType;
        return type;
    }

    /// <summary>
    /// A method of a class nested in NativeMethods (User32, Kernel32, ... or any class added later).
    /// Delegate types such as EnumWindowsProc are callbacks, not APIs.
    /// </summary>
    private static bool IsNativeMethodsApi(MethodBase callee) =>
        callee is MethodInfo &&
        callee.DeclaringType is { } declaring &&
        declaring != NativeMethodsType &&
        OutermostType(declaring) == NativeMethodsType &&
        !typeof(Delegate).IsAssignableFrom(declaring);

    /// <summary>
    /// The UI Automation types whose methods can act on the UI: AutomationElement (SetFocus), the
    /// control patterns (InvokePattern, ValuePattern, ..., and their nested information types) and
    /// TextPatternRange.
    /// </summary>
    private static bool IsActionCapableUiAutomationType(Type type)
    {
        var outer = OutermostType(type);
        return outer.Namespace?.StartsWith("System.Windows.Automation", StringComparison.Ordinal) == true &&
               (outer == typeof(System.Windows.Automation.AutomationElement) ||
                outer == typeof(System.Windows.Automation.Text.TextPatternRange) ||
                outer.Name.EndsWith("Pattern", StringComparison.Ordinal));
    }

    /// <summary>
    /// A constructor, a property getter, or a method on <see cref="ReadOnlyUiAutomationMethods"/>.
    /// </summary>
    private static bool IsReadOnlyUiAutomationCall(MethodBase callee) =>
        callee is ConstructorInfo ||
        (callee.IsSpecialName && callee.Name.StartsWith("get_", StringComparison.Ordinal)) ||
        ReadOnlyUiAutomationMethods.Contains($"{callee.DeclaringType?.Name}.{callee.Name}");

    /// <summary>A call found in the IL: the top-level type that owns the calling code, and the callee.</summary>
    private sealed record Call(Type Owner, string Caller, MethodBase Callee)
    {
        /// <summary>The callee as "Class.Method"; for native APIs the class directly below NativeMethods.</summary>
        public string Api => $"{Callee.DeclaringType?.Name}.{Callee.Name}";

        public string Description => $"{Caller} calls {Api}";
    }

    /// <summary>Decodes a method body's IL and returns the methods it calls or references.</summary>
    private static List<MethodBase> GetCalledMethods(MethodBase method)
    {
        var called = new List<MethodBase>();
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
            return called;

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        var position = 0;
        while (position < il.Length)
        {
            ushort value = il[position++];
            if (value == 0xFE)
                value = (ushort)(0xFE00 | il[position++]);

            var opCode = OpCodesByValue[value];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, position);
                try
                {
                    if (method.Module.ResolveMethod(token, typeArguments, methodArguments) is { } callee)
                        called.Add(callee);
                }
                catch (ArgumentException)
                {
                    // Token that cannot be resolved in this generic context; not a call we track.
                }
            }

            position += opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
        }

        return called;
    }
}
