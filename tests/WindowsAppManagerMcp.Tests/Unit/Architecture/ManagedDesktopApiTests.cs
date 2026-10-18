using System.Reflection;
using System.Reflection.Emit;

namespace WindowsAppManagerMcp.Tests.Unit.Architecture;

/// <summary>
/// The doors around <see cref="NativeCallGuardTests"/>: that scan follows P/Invoke (NativeMethods), COM interfaces, UI Automation and
/// WinRT, but the server also references the Windows Desktop framework (Microsoft.WindowsDesktop.App.WPF, for UI Automation), whose
/// managed APIs reach the same desktop without a P/Invoke of the server's own: the clipboard (System.Windows.Clipboard), keyboard
/// and mouse state and focus (System.Windows.Input), windows of its own (System.Windows.Window, Application, HwndSource, a
/// MessageBox, WindowInteropHelper.EnsureHandle, an HwndHost, a Popup and the rest of WPF's UI), Windows Forms' SendKeys,
/// Clipboard and Cursor, and the Visual Basic runtime's Shell, AppActivate and SendKeys, which start a process, take the
/// foreground and type. And native code can be called without a declared P/Invoke at all: a library
/// loaded and an export looked up (NativeLibrary), a function pointer turned into a delegate (Marshal.GetDelegateForFunctionPointer)
/// or called directly (the calli instruction), a COM class created by its ProgID or CLSID, or a late-bound COM call (dynamic). So
/// the server's own code (its WindowsAppManagerMcp namespaces; the generated WinRT projection is pinned by NativeCallGuardTests)
/// may reference none of them, except the reviewed uses on <see cref="SanctionedUses"/>, each in a Native wrapper that tests fake.
/// </summary>
public class ManagedDesktopApiTests
{
    private static readonly Assembly ServerAssembly = typeof(NativeWindowWrapper).Assembly;

    /// <summary>
    /// The managed APIs, by declaring type's full name, base type, namespace or assembly, that would act on the desktop or call
    /// native code unseen. A use is named by the first entry it matches, so the specific entries come before the broad ones.
    /// </summary>
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
        ("System.Windows.MessageBox (shows a message box)", m => m.DeclaringType?.FullName == "System.Windows.MessageBox"),
        ("WPF window interop (WindowInteropHelper.EnsureHandle, HwndHost)", m => m.DeclaringType?.FullName == "System.Windows.Interop.WindowInteropHelper" ||
            IsOrDerivesFrom(m.DeclaringType, "System.Windows.Interop.HwndHost")),
        ("System.Windows.Controls.Primitives.Popup (opens a window of its own)", m => IsOrDerivesFrom(m.DeclaringType, "System.Windows.Controls.Primitives.Popup")),
        // Interaction.Shell starts a process, AppActivate brings a window to the front, CreateObject creates a COM class, and
        // Devices.Keyboard.SendKeys (Microsoft.VisualBasic.Forms) types
        ("Microsoft.VisualBasic (Interaction.Shell, AppActivate, CreateObject, Devices.Keyboard.SendKeys)", m => InNamespace(m, "Microsoft.VisualBasic")),
        // Last, so a use is named by the specific entries above: the rest of WPF's UI, which the server never needs (it uses
        // WPF's UI Automation client and WindowsBase's Point and Rect): a ToolTip or ContextMenu opens a window, a dialog shows one
        ("WPF's UI (PresentationFramework, PresentationCore: windows, dialogs, popups, menus, controls)", m =>
            m.DeclaringType?.Assembly.GetName().Name is { } assembly &&
            (assembly.StartsWith("PresentationFramework", StringComparison.Ordinal) ||
             assembly is "PresentationCore" or "PresentationUI" or "System.Windows.Controls.Ribbon" or "ReachFramework")),
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
            .Select(c => (c.Owner, c.Caller, c.Callee, Api: DeniedApiOf(c.Callee)))
            .Where(c => c.Api != null)
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

    [Theory]
    [InlineData(nameof(ManagedDesktopApiSamples.ShowsAMessageBox), "System.Windows.MessageBox (shows a message box)")]
    [InlineData(nameof(ManagedDesktopApiSamples.CreatesAWindowsHandle), "WPF window interop (WindowInteropHelper.EnsureHandle, HwndHost)")]
    [InlineData(nameof(ManagedDesktopApiSamples.HostsAWindow), "WPF window interop (WindowInteropHelper.EnsureHandle, HwndHost)")]
    [InlineData(nameof(ManagedDesktopApiSamples.OpensAPopup), "System.Windows.Controls.Primitives.Popup (opens a window of its own)")]
    [InlineData(nameof(ManagedDesktopApiSamples.OpensAContextMenu), "WPF's UI (PresentationFramework, PresentationCore: windows, dialogs, popups, menus, controls)")]
    [InlineData(nameof(ManagedDesktopApiSamples.StartsAProcessThroughVisualBasic), "Microsoft.VisualBasic (Interaction.Shell, AppActivate, CreateObject, Devices.Keyboard.SendKeys)")]
    [InlineData(nameof(ManagedDesktopApiSamples.ActivatesAWindowThroughVisualBasic), "Microsoft.VisualBasic (Interaction.Shell, AppActivate, CreateObject, Devices.Keyboard.SendKeys)")]
    public void TheScan_SeesADeniedApi(string sample, string api)
    {
        // Arrange - a sample's calls, as the scan reads a server method's
        var calls = IlCalls.MethodsOf(typeof(ManagedDesktopApiSamples))
            .Where(m => m.Name == sample)
            .SelectMany(IlCalls.CalledMethods)
            .ToList();

        // Assert
        calls.Should().NotBeEmpty("the sample {0} makes calls", sample);
        calls.Select(DeniedApiOf).Should().Contain(api, "the scan must see what {0} reaches", sample);
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

    /// <summary>The first of <see cref="DeniedApis"/> that <paramref name="callee"/> is, or null: the specific ones come first.</summary>
    private static string? DeniedApiOf(MethodBase callee) => DeniedApis.FirstOrDefault(d => d.Matches(callee)).Name;

    /// <summary>True when <paramref name="type"/> is the type named <paramref name="fullName"/> or derives from it.</summary>
    private static bool IsOrDerivesFrom(Type? type, string fullName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InNamespace(MethodBase method, string ns) =>
        method.DeclaringType?.Namespace is { } name && (name == ns || name.StartsWith(ns + ".", StringComparison.Ordinal));
}

/// <summary>
/// Samples for <see cref="ManagedDesktopApiTests"/>' canaries, which only scan their IL. Nothing calls them, and nothing may: they
/// would show a message box, open windows, start a process or bring another app's window to the front for real.
/// </summary>
internal static class ManagedDesktopApiSamples
{
    public static void ShowsAMessageBox() => System.Windows.MessageBox.Show("sample");

    public static nint CreatesAWindowsHandle(System.Windows.Window window) => new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();

    public static object HostsAWindow() => new SampleHwndHost();

    public static object OpensAPopup() => new System.Windows.Controls.Primitives.Popup { IsOpen = true };

    public static object OpensAContextMenu() => new System.Windows.Controls.ContextMenu { IsOpen = true };

    public static int StartsAProcessThroughVisualBasic() => Microsoft.VisualBasic.Interaction.Shell("notepad.exe");

    public static void ActivatesAWindowThroughVisualBasic() => Microsoft.VisualBasic.Interaction.AppActivate(4711);

    /// <summary>A WPF host of a native child window: its base class creates the window.</summary>
    private sealed class SampleHwndHost : System.Windows.Interop.HwndHost
    {
        protected override System.Runtime.InteropServices.HandleRef BuildWindowCore(System.Runtime.InteropServices.HandleRef hwndParent) => new(this, nint.Zero);

        protected override void DestroyWindowCore(System.Runtime.InteropServices.HandleRef hwnd)
        {
        }
    }
}
