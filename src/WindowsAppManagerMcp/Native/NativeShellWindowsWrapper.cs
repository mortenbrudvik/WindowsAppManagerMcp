using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeShellWindowsWrapper"/> (roadmap M7.5): the shell's ShellWindows collection
/// (CLSID_ShellWindows), read late-bound (IDispatch) on a single-threaded apartment thread of its own, as the shell's objects
/// expect. The shell (explorer.exe) answers, so a hung shell would otherwise hang the tool: the caller waits at most
/// <see cref="Deadline"/>, and while an abandoned read is still blocked every further read returns nothing at once. It only
/// reads each window's handle and folder; it never navigates or closes one. A window that fails to read is skipped and
/// counted (<see cref="ExplorerFolderListing.SkippedWindows"/>), as NativeAppsWrapper counts the apps it skips.
/// </summary>
public sealed class NativeShellWindowsWrapper : INativeShellWindowsWrapper
{
    /// <summary>How long a caller waits for the shell's answer.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    private int _abandoned;

    public ExplorerFolderListing? GetExplorerFolders()
    {
        if (Volatile.Read(ref _abandoned) > 0)
        {
            return null;
        }

        ExplorerFolderListing? folders = null;
        const int Running = 0, Finished = 1, Abandoned = 2;
        var state = Running;
        var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                folders = Read();
            }
            catch (Exception)
            {
                // No shell (a server core), or it failed: no folders
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
            Name = "Shell windows read"
        };

        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.UnsafeStart();
        }
        catch (Exception)
        {
            return null;
        }

        // The event is not disposed: an abandoned worker still sets it when the shell answers
        if (!finished.Wait(Deadline) && Interlocked.CompareExchange(ref state, Abandoned, Running) == Running)
        {
            Interlocked.Increment(ref _abandoned);
            return null;
        }

        return Volatile.Read(ref folders);
    }

    private static ExplorerFolderListing Read()
    {
        var type = Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!;
        object shellWindows = Activator.CreateInstance(type)!;
        try
        {
            int count = ((dynamic)shellWindows).Count;
            return Collect(count, i => ReadWindow(shellWindows, i));
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellWindows);
        }
    }

    /// <summary>
    /// The windows at 0 to <paramref name="count"/> - 1 of the shell's list, read one by one: a window whose read throws (one
    /// that closed while it was read, or that the shell failed) is skipped and counted, never dropped silently, so the caller
    /// knows a folder may be missing; an empty slot (the list shrank meanwhile) is neither. Pure, so it is tested directly.
    /// </summary>
    /// <param name="readWindow">Reads the window at an index; null for an empty slot.</param>
    public static ExplorerFolderListing Collect(int count, Func<int, ExplorerFolder?> readWindow)
    {
        ArgumentNullException.ThrowIfNull(readWindow);
        var found = new List<ExplorerFolder>();
        var skipped = 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                if (readWindow(i) is { } folder)
                {
                    found.Add(folder);
                }
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        return new ExplorerFolderListing(found, skipped);
    }

    /// <summary>The shell window at <paramref name="index"/> and its folder; null for an empty slot. Throws when it cannot be read.</summary>
    private static ExplorerFolder? ReadWindow(object shellWindows, int index)
    {
        dynamic? window = null;
        try
        {
            window = ((dynamic)shellWindows).Item(index);
            if (window == null)
            {
                return null;
            }

            var handle = new nint(Convert.ToInt64(window.HWND));
            return new ExplorerFolder(handle, FolderOf(window));
        }
        finally
        {
            if (window != null)
            {
                Marshal.FinalReleaseComObject(window);
            }
        }
    }

    /// <summary>
    /// The window's folder when it is a folder on disk (a drive or UNC path); null for This PC, a search and the like, and for a
    /// window that shows no folder view (<see cref="IsNoFolderView"/>). A read that fails otherwise throws, so
    /// <see cref="Collect"/> counts the window as skipped: it may show a folder that would be missing silently (review finding F8).
    /// </summary>
    private static string? FolderOf(dynamic window)
    {
        string? path = null;
        dynamic? document = null, folder = null, self = null;
        try
        {
            document = window.Document;
            folder = document?.Folder;
            self = folder?.Self;
            path = (string?)self?.Path;
        }
        catch (Exception ex) when (IsNoFolderView(ex))
        {
            return null;
        }
        finally
        {
            foreach (var com in new object?[] { self, folder, document })
            {
                if (com != null && Marshal.IsComObject(com))
                {
                    Marshal.FinalReleaseComObject(com);
                }
            }
        }

        return path is { Length: > 2 } && (path[1] == ':' || path.StartsWith(@"\\", StringComparison.Ordinal)) ? path : null;
    }

    /// <summary>
    /// Whether reading a shell window's Document.Folder.Self.Path failed because the window shows no folder view, so it has no
    /// folder to keep: its document has no such member (the late binder finds none, or IDispatch answers DISP_E_UNKNOWNNAME or
    /// DISP_E_MEMBERNOTFOUND) or does not implement it (E_NOTIMPL, E_NOINTERFACE). Any other failure (the window went away, the
    /// shell failed) means the window could not be read. Pure, so it is tested directly.
    /// </summary>
    public static bool IsNoFolderView(Exception exception) => exception switch
    {
        Microsoft.CSharp.RuntimeBinder.RuntimeBinderException => true,
        COMException com => com.HResult is DispUnknownName or DispMemberNotFound or NotImplemented or NoInterface,
        _ => false
    };

    private const int DispUnknownName = unchecked((int)0x80020006);
    private const int DispMemberNotFound = unchecked((int)0x80020003);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int NoInterface = unchecked((int)0x80004002);
}
