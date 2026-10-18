using System.Reflection;
using System.Reflection.Emit;

namespace WindowsAppManagerMcp.Tests.Unit.Architecture;

/// <summary>
/// The doors around <see cref="NativeCallGuardTests"/>: that scan follows P/Invoke (NativeMethods), COM interfaces, UI Automation and
/// WinRT, but the server also references the Windows Desktop framework (Microsoft.WindowsDesktop.App.WPF, for UI Automation), whose
/// managed APIs reach the same desktop without a P/Invoke of the server's own: the clipboard (System.Windows.Clipboard), keyboard
/// and mouse state and focus (System.Windows.Input), windows of its own (System.Windows.Window, Application, HwndSource), and
/// Windows Forms' SendKeys, Clipboard and Cursor. And native code can be called without a declared P/Invoke at all: a library
/// loaded and an export looked up (NativeLibrary), a function pointer turned into a delegate (Marshal.GetDelegateForFunctionPointer)
/// or called directly (the calli instruction), a COM class created by its ProgID or CLSID, or a late-bound COM call (dynamic). So
/// the server's own code (its WindowsAppManagerMcp namespaces; the generated WinRT projection is pinned by NativeCallGuardTests)
/// may reference none of them, except the reviewed uses on <see cref="SanctionedUses"/>, each in a Native wrapper that tests fake.
/// </summary>
public class ManagedDesktopApiTests
{
    private static readonly Assembly ServerAssembly = typeof(NativeWindowWrapper).Assembly;

    /// <summary>The managed APIs, by declaring type's full name or namespace, that would act on the desktop or call native code unseen.</summary>
    private static readonly (string Name, Func<MethodBase, bool> Matches)[] DeniedApis =
    [
        ("System.Windows.Clipboard", m => m.DeclaringType?.FullName == "System.Windows.Clipboard"),
        ("System.Windows.Input (keyboard, mouse, focus, input manager)", m => InNamespace(m, "System.Windows.Input")),
        ("System.Windows.Forms (SendKeys, Clipboard, Cursor, windows)", m => InNamespace(m, "System.Windows.Forms")),
        ("WPF windows (Window, Application, HwndSource)", m => m.DeclaringType?.FullName is "System.Windows.Window" or "System.Windows.Application"
            or "System.Windows.Interop.HwndSource" or "System.Windows.Interop.HwndTarget"),
        ("NativeLibrary (load a library, look up an export)", m => m.DeclaringType == typeof(System.Runtime.InteropServices.NativeLibrary)),
        ("Marshal.GetDelegateForFunctionPointer", m => m.DeclaringType == typeof(System.Runtime.InteropServices.Marshal) &&
            m.Name == nameof(System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer)),
        ("Type.GetTypeFromProgID or GetTypeFromCLSID (create a COM class)", m => m.DeclaringType == typeof(Type) &&
            m.Name is nameof(Type.GetTypeFromProgID) or nameof(Type.GetTypeFromCLSID)),
        ("late-bound calls (dynamic, Microsoft.CSharp.RuntimeBinder)", m => InNamespace(m, "Microsoft.CSharp.RuntimeBinder")),
    ];

    /// <summary>The reviewed uses: (the server type, the denied API's name) and why it is safe. Each is in a Native wrapper tests fake.</summary>
    private static readonly Dictionary<(Type Owner, string Api), string> SanctionedUses = new()
    {
        [(typeof(NativeAppsWrapper), "Type.GetTypeFromProgID or GetTypeFromCLSID (create a COM class)")] =
            "creates the ApplicationActivationManager to start a packaged app (INativeAppsWrapper; the E2E guard classifies it as a process start)",
        [(typeof(NativeShellWindowsWrapper), "Type.GetTypeFromProgID or GetTypeFromCLSID (create a COM class)")] =
            "creates ShellWindows to read File Explorer's folders (INativeShellWindowsWrapper, read only)",
        [(typeof(NativeShellWindowsWrapper), "late-bound calls (dynamic, Microsoft.CSharp.RuntimeBinder)")] =
            "reads each shell window's LocationURL and folder path through IDispatch (INativeShellWindowsWrapper, read only)",
        [(typeof(NativeVirtualDesktopWrapper), "Type.GetTypeFromProgID or GetTypeFromCLSID (create a COM class)")] =
            "creates the VirtualDesktopManager, whose declared interface only reads (NativeCallGuardTests: MoveWindowToDesktop stays undeclared)",
    };

    /// <summary>The server types that may call a function pointer (calli), and why.</summary>
    private static readonly Dictionary<Type, string> SanctionedFunctionPointerCalls = new()
    {
        [typeof(NativeGraphicsCaptureWrapper)] =
            "calls the vtables of Windows.Graphics.Capture and Direct3D 11 to copy one frame of a window (IGraphicsCaptureWrapper, read only; " +
            "NativeCallGuardTests.GraphicsCaptureCalls_AreOnlyMadeByNativeGraphicsCaptureWrapper)",
    };

    [Fact]
    public void TheServer_ReferencesNoManagedDesktopApi_OrDynamicNativeCall_OutsideTheReviewedUses()
    {
        // Act
        var uses = ServerCode()
            .SelectMany(m => IlCalls.CalledMethods(m).Select(callee => (Owner: IlCalls.OutermostType(m.DeclaringType!), Caller: m, Callee: callee)))
            .SelectMany(c => DeniedApis.Where(d => d.Matches(c.Callee)).Select(d => (c.Owner, c.Caller, c.Callee, Api: d.Name)))
            .ToList();

        // Assert - the scan sees framework calls of the WPF reference, the reviewed uses, and the Marshal class
        ServerCode().SelectMany(IlCalls.CalledMethods)
            .Should().Contain(m => m.DeclaringType == typeof(System.Windows.Automation.AutomationElement),
                "the scan must see the server's calls into the Windows Desktop framework (UiaTree's AutomationElement)")
            .And.Contain(m => m.DeclaringType == typeof(System.Runtime.InteropServices.Marshal),
                "the scan must see the server's Marshal calls");
        uses.Should().Contain(u => u.Owner == typeof(NativeAppsWrapper) && u.Callee.Name == nameof(Type.GetTypeFromCLSID),
            "the scan must see a reviewed use");
        uses.Should().Contain(u => u.Owner == typeof(NativeShellWindowsWrapper) && u.Api.StartsWith("late-bound", StringComparison.Ordinal),
            "the scan must see the shell wrapper's late-bound reads");
        uses.Where(u => !SanctionedUses.ContainsKey((u.Owner, u.Api)))
            .Select(u => $"{u.Caller.DeclaringType?.FullName}.{u.Caller.Name} uses {u.Api}: {u.Callee.DeclaringType?.Name}.{u.Callee.Name}")
            .Should().BeEmpty(
                "these reach the desktop or native code past the native wrappers the tests fake and the scans that pin them; put the " +
                "call in a Native wrapper behind an interface, and review it onto SanctionedUses");
    }

    [Fact]
    public void FunctionPointerCalls_AreOnlyInTheReviewedWrappers()
    {
        // Act - calli: a call through a function pointer (delegate* unmanaged), which names no method the scans can check
        var callers = ServerCode()
            .Where(m => IlCalls.Uses(m, OpCodes.Calli))
            .Select(m => IlCalls.OutermostType(m.DeclaringType!))
            .Distinct()
            .ToList();

        // Assert
        callers.Should().Contain(typeof(NativeGraphicsCaptureWrapper), "the scan must see the capture wrapper's vtable calls");
        callers.Where(type => !SanctionedFunctionPointerCalls.ContainsKey(type)).Select(type => type.FullName).Should().BeEmpty(
            "a function pointer call reaches native code without a P/Invoke the allow-lists see; review it onto SanctionedFunctionPointerCalls");
    }

    [Fact]
    public void TheReviewedUses_NameDeniedApis()
    {
        // A stale entry would hide a review gap the next time that name is reused
        SanctionedUses.Keys.Select(k => k.Api).Should().BeSubsetOf(DeniedApis.Select(d => d.Name));
    }

    /// <summary>
    /// The server's own code: its WindowsAppManagerMcp namespaces and Program.cs's top-level statements; not the generated WinRT
    /// projection, Coverlet's trackers or the compiler's own helper types.
    /// </summary>
    private static IEnumerable<MethodBase> ServerCode() =>
        ServerAssembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("WindowsAppManagerMcp", StringComparison.Ordinal) == true ||
                        (IlCalls.OutermostType(t) is { Namespace: null } outer && !outer.Name.StartsWith('<')))
            .SelectMany(t => t.GetMethods(IlCalls.DeclaredMembers).Cast<MethodBase>().Concat(t.GetConstructors(IlCalls.DeclaredMembers)));

    private static bool InNamespace(MethodBase method, string ns) =>
        method.DeclaringType?.Namespace is { } name && (name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal));
}
