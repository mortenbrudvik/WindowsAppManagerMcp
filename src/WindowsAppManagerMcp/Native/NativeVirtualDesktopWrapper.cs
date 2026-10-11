using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Reads where windows are through the shell's IVirtualDesktopManager (CLSID_VirtualDesktopManager). Each call creates
/// the manager on a thread of its own, in the multithreaded apartment, asks it about every window and releases it; the
/// caller waits at most <see cref="Deadline"/>. The manager answers from the shell's process (explorer.exe), so a hung
/// shell would otherwise hang the tool: a call that misses the deadline is abandoned, and while it is still blocked every
/// further call returns <see cref="VirtualDesktopPlacement.Unknown"/> at once.
/// </summary>
public sealed class NativeVirtualDesktopWrapper : INativeVirtualDesktopWrapper
{
    /// <summary>How long a caller waits for the manager's answers.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(1);

    /// <summary>TYPE_E_ELEMENTNOTFOUND: the manager does not track the window (it is on no virtual desktop).</summary>
    private const int ElementNotFound = unchecked((int)0x8002802B);

    private static readonly Guid VirtualDesktopManagerClsid = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    private const int Running = 0, Finished = 1, Abandoned = 2;

    /// <summary>Calls that missed the deadline and have not returned yet.</summary>
    private int _abandoned;

    public IReadOnlyList<VirtualDesktopPlacement> GetPlacements(IReadOnlyList<nint> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var unknown = Enumerable.Repeat(VirtualDesktopPlacement.Unknown, windows.Count).ToArray();
        if (windows.Count == 0 || Volatile.Read(ref _abandoned) > 0)
        {
            return unknown;
        }

        var handles = windows.ToArray();
        VirtualDesktopPlacement[]? answers = null;
        var state = Running;
        var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                answers = Read(handles);
            }
            catch (Exception)
            {
                // No manager (an older or stripped-down Windows), or it failed: the placements stay unknown
            }
            finally
            {
                if (Interlocked.Exchange(ref state, Finished) == Abandoned)
                {
                    Interlocked.Decrement(ref _abandoned);
                }

                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Virtual desktop read"
        };

        try
        {
            worker.SetApartmentState(ApartmentState.MTA);
            worker.UnsafeStart();
        }
        catch (Exception)
        {
            // The thread could not be started
            return unknown;
        }

        // The event is not disposed: an abandoned worker still sets it when the manager answers
        if (!finished.Wait(Deadline) && Interlocked.CompareExchange(ref state, Abandoned, Running) == Running)
        {
            Interlocked.Increment(ref _abandoned);
            return unknown;
        }

        return Volatile.Read(ref answers) ?? unknown;
    }

    private static VirtualDesktopPlacement[] Read(nint[] windows)
    {
        var type = Type.GetTypeFromCLSID(VirtualDesktopManagerClsid, throwOnError: true)!;
        var instance = Activator.CreateInstance(type)!;
        try
        {
            var manager = (IVirtualDesktopManager)instance;
            return windows.Select(window => Placement(manager, window)).ToArray();
        }
        finally
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private static VirtualDesktopPlacement Placement(IVirtualDesktopManager manager, nint window)
    {
        var hr = manager.IsWindowOnCurrentVirtualDesktop(window, out var onCurrent);
        if (hr == ElementNotFound)
        {
            return VirtualDesktopPlacement.NoDesktop;
        }

        if (hr < 0)
        {
            return VirtualDesktopPlacement.Unknown;
        }

        if (onCurrent != 0)
        {
            return VirtualDesktopPlacement.CurrentDesktop;
        }

        // Not on this desktop: on another one only when it is on a desktop at all
        hr = manager.GetWindowDesktopId(window, out var desktop);
        return hr == ElementNotFound || (hr >= 0 && desktop == Guid.Empty)
            ? VirtualDesktopPlacement.NoDesktop
            : VirtualDesktopPlacement.OtherDesktop;
    }

    /// <summary>
    /// IVirtualDesktopManager (shobjidl_core.h), up to the methods the server reads. The third method,
    /// MoveWindowToDesktop, is left out on purpose: the server never moves a window to another desktop, and a method that
    /// is not declared cannot be called. NativeCallGuardTests checks that it stays out.
    /// </summary>
    [ComImport]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(nint topLevelWindow, out Guid desktopId);
    }
}
