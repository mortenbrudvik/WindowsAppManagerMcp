using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeShellWindowsWrapper"/> (roadmap M7.5): the shell's ShellWindows collection
/// (CLSID_ShellWindows), read late-bound (IDispatch) on a single-threaded apartment thread of its own, as the shell's objects
/// expect. The shell (explorer.exe) answers, so a hung shell would otherwise hang the tool: the caller waits at most
/// <see cref="Deadline"/>, and while an abandoned read is still blocked every further read returns nothing at once. It only
/// reads each window's handle and folder; it never navigates or closes one.
/// </summary>
public sealed class NativeShellWindowsWrapper : INativeShellWindowsWrapper
{
    /// <summary>How long a caller waits for the shell's answer.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    private int _abandoned;

    public IReadOnlyList<ExplorerFolder>? GetExplorerFolders()
    {
        if (Volatile.Read(ref _abandoned) > 0)
        {
            return null;
        }

        IReadOnlyList<ExplorerFolder>? folders = null;
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

    private static List<ExplorerFolder> Read()
    {
        var type = Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!;
        dynamic shellWindows = Activator.CreateInstance(type)!;
        var found = new List<ExplorerFolder>();
        try
        {
            int count = shellWindows.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic? window = null;
                try
                {
                    window = shellWindows.Item(i);
                    if (window == null)
                    {
                        continue;
                    }

                    var handle = new nint(Convert.ToInt64(window.HWND));
                    found.Add(new ExplorerFolder(handle, FolderOf(window)));
                }
                catch (Exception)
                {
                    // A window that closed while it was read, or one that is not File Explorer (an old Internet Explorer frame)
                }
                finally
                {
                    if (window != null)
                    {
                        Marshal.FinalReleaseComObject(window);
                    }
                }
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellWindows);
        }

        return found;
    }

    /// <summary>The window's folder when it is a folder on disk (a drive or UNC path); null for This PC, a search and the like.</summary>
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
        catch (Exception)
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
}
