using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Models;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="INativeClipboardWrapper"/> over the Win32 clipboard (roadmap M2.16).
/// </summary>
/// <remarks>
/// MCP calls arrive on thread-pool (MTA) threads, so every read and write runs on a dedicated STA thread of its own,
/// which creates a message-only window of the server, opens the clipboard with that window as the owner (with no owner
/// window, EmptyClipboard leaves the clipboard without an owner and SetClipboardData fails), does its work, closes the
/// clipboard and destroys the window. The data stays on the clipboard after the window is gone: it is not delay-rendered,
/// so the system owns it. One call runs at a time; a call waits at most <see cref="CallTimeout"/>, so an application that
/// does not answer (its clipboard data or a message the clipboard sends it) makes the call report the clipboard busy
/// instead of hanging the tool. NativeCallGuardTests keeps every NativeMethods.Clipboard call in this class.
/// </remarks>
public sealed class NativeClipboardWrapper : INativeClipboardWrapper
{
    /// <summary>How long a read or write may take, including the wait for the call before it.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often, and how far apart, OpenClipboard is tried while another app has the clipboard open.</summary>
    private const int OpenAttempts = 10;

    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(30);

    private const string ExcludeFromMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private const string CloudFormat = "CanUploadToCloudClipboard";
    private const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";
    private const string HtmlFormat = "HTML Format";

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public uint GetSequenceNumber() => NativeMethods.Clipboard.GetClipboardSequenceNumber();

    public ClipboardSnapshot Read(ClipboardFormat format, int maxCharacters, bool readPrivateContent)
    {
        var limit = Math.Max(1, maxCharacters);
        return RunOnClipboardThread(
            owner => ReadOpen(owner, format, limit, readPrivateContent),
            notStarted: ClipboardSnapshot.NotOpened,
            timedOut: ClipboardSnapshot.NotOpened,
            ex => new ClipboardSnapshot(true, Error: $"{ex.GetType().Name}: {ex.Message}"));
    }

    public ClipboardWriteOutcome WriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return RunOnClipboardThread(
            owner => WriteOpen(owner, text),
            notStarted: new ClipboardWriteOutcome(false, false),
            timedOut: new ClipboardWriteOutcome(false, false, Error:
                $"The clipboard did not answer within {CallTimeout.TotalSeconds:0} s (the application that owns it may not be " +
                "responding). The text may still be put on the clipboard once it answers."),
            ex => new ClipboardWriteOutcome(true, false, Error: $"{ex.GetType().Name}: {ex.Message}"));
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a new STA thread with a new message-only window as its argument (zero when the
    /// window could not be created), and returns its result: <paramref name="notStarted"/> when the call before it did
    /// not finish within <see cref="CallTimeout"/>, so this one never started; <paramref name="timedOut"/> when this one
    /// did not finish in that time (it goes on, and its result is lost); <paramref name="failed"/> when it threw.
    /// </summary>
    private T RunOnClipboardThread<T>(Func<nint, T> work, T notStarted, T timedOut, Func<Exception, T> failed)
    {
        var deadline = DateTime.UtcNow + CallTimeout;
        if (!_oneAtATime.Wait(CallTimeout))
        {
            return notStarted;
        }

        var result = new StrongBox<T>(timedOut);
        var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            var owner = nint.Zero;
            try
            {
                owner = NativeMethods.Clipboard.CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0,
                    NativeMethods.Clipboard.HWND_MESSAGE, nint.Zero, nint.Zero, nint.Zero);
                result.Value = work(owner);
            }
            catch (Exception ex)
            {
                result.Value = failed(ex);
            }
            finally
            {
                if (owner != nint.Zero)
                {
                    NativeMethods.Clipboard.DestroyWindow(owner);
                }

                _oneAtATime.Release();
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "WindowsAppManagerMcp clipboard"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var remaining = deadline - DateTime.UtcNow;
        return done.Wait(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) ? result.Value! : timedOut;
    }

    private static bool Open(nint owner)
    {
        for (var attempt = 1; attempt <= OpenAttempts; attempt++)
        {
            if (NativeMethods.Clipboard.OpenClipboard(owner))
            {
                return true;
            }

            if (attempt < OpenAttempts)
            {
                Thread.Sleep(OpenRetryDelay);
            }
        }

        return false;
    }

    private static ClipboardSnapshot ReadOpen(nint owner, ClipboardFormat format, int maxCharacters, bool readPrivateContent)
    {
        if (!Open(owner))
        {
            return ClipboardSnapshot.NotOpened;
        }

        try
        {
            var htmlFormat = NativeMethods.Clipboard.RegisterClipboardFormatW(HtmlFormat);
            var snapshot = new ClipboardSnapshot(
                Opened: true,
                SequenceNumber: NativeMethods.Clipboard.GetClipboardSequenceNumber(),
                Markers: ReadMarkers(),
                HasText: NativeMethods.Clipboard.IsClipboardFormatAvailable(NativeMethods.Clipboard.CF_UNICODETEXT),
                HasHtml: htmlFormat != 0 && NativeMethods.Clipboard.IsClipboardFormatAvailable(htmlFormat),
                HasFiles: NativeMethods.Clipboard.IsClipboardFormatAvailable(NativeMethods.Clipboard.CF_HDROP));

            // Private content is not even read into the server's memory unless it is the server's own
            if (snapshot.IsPrivate && !readPrivateContent)
            {
                return snapshot;
            }

            return format switch
            {
                ClipboardFormat.Text when snapshot.HasText => ReadText(snapshot, maxCharacters),
                ClipboardFormat.Html when snapshot.HasHtml => ReadHtml(snapshot, htmlFormat),
                ClipboardFormat.Files when snapshot.HasFiles => ReadFiles(snapshot, maxCharacters),
                _ => snapshot
            };
        }
        finally
        {
            NativeMethods.Clipboard.CloseClipboard();
        }
    }

    /// <summary>
    /// The privacy markers on the open clipboard. Fails closed: a marker whose format name cannot be registered, or a
    /// CanIncludeInClipboardHistory whose value cannot be read, counts as set.
    /// </summary>
    private static ClipboardPrivacyMarkers ReadMarkers()
    {
        var markers = ClipboardPrivacyMarkers.None;
        if (IsAvailableOrUnknown(ExcludeFromMonitorFormat, out _))
        {
            markers |= ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing;
        }

        if (IsAvailableOrUnknown(ViewerIgnoreFormat, out _))
        {
            markers |= ClipboardPrivacyMarkers.ClipboardViewerIgnore;
        }

        if (IsAvailableOrUnknown(HistoryFormat, out var history) && (history == 0 || ReadDword(history) is not { } value || value == 0))
        {
            markers |= ClipboardPrivacyMarkers.NotForClipboardHistory;
        }

        return markers;
    }

    private static bool IsAvailableOrUnknown(string name, out uint format)
    {
        format = NativeMethods.Clipboard.RegisterClipboardFormatW(name);
        return format == 0 || NativeMethods.Clipboard.IsClipboardFormatAvailable(format);
    }

    private static uint? ReadDword(uint format)
    {
        var handle = NativeMethods.Clipboard.GetClipboardData(format);
        if (handle == nint.Zero || NativeMethods.Clipboard.GlobalSize(handle) < sizeof(uint))
        {
            return null;
        }

        var data = NativeMethods.Clipboard.GlobalLock(handle);
        if (data == nint.Zero)
        {
            return null;
        }

        try
        {
            return unchecked((uint)Marshal.ReadInt32(data));
        }
        finally
        {
            NativeMethods.Clipboard.GlobalUnlock(handle);
        }
    }

    private static unsafe ClipboardSnapshot ReadText(ClipboardSnapshot snapshot, int maxCharacters)
    {
        var handle = NativeMethods.Clipboard.GetClipboardData(NativeMethods.Clipboard.CF_UNICODETEXT);
        if (handle == nint.Zero)
        {
            return snapshot with { Error = Win32Error("GetClipboardData") };
        }

        var size = NativeMethods.Clipboard.GlobalSize(handle);
        var data = NativeMethods.Clipboard.GlobalLock(handle);
        if (data == nint.Zero)
        {
            return snapshot with { Error = Win32Error("GlobalLock") };
        }

        try
        {
            var capacity = (int)Math.Min((ulong)size / sizeof(char), int.MaxValue);
            var characters = new ReadOnlySpan<char>((void*)data, capacity);
            var length = characters.IndexOf('\0') is var end and >= 0 ? end : capacity;
            return snapshot with { Text = new string(characters[..Math.Min(length, maxCharacters)]), TextLength = length };
        }
        finally
        {
            NativeMethods.Clipboard.GlobalUnlock(handle);
        }
    }

    private static unsafe ClipboardSnapshot ReadHtml(ClipboardSnapshot snapshot, uint htmlFormat)
    {
        var handle = NativeMethods.Clipboard.GetClipboardData(htmlFormat);
        if (handle == nint.Zero)
        {
            return snapshot with { Error = Win32Error("GetClipboardData") };
        }

        var size = (int)Math.Min((ulong)NativeMethods.Clipboard.GlobalSize(handle), ClipboardSnapshot.MaxHtmlBytes);
        var data = NativeMethods.Clipboard.GlobalLock(handle);
        if (data == nint.Zero)
        {
            return snapshot with { Error = Win32Error("GlobalLock") };
        }

        try
        {
            return snapshot with { Html = new ReadOnlySpan<byte>((void*)data, size).ToArray() };
        }
        finally
        {
            NativeMethods.Clipboard.GlobalUnlock(handle);
        }
    }

    private static unsafe ClipboardSnapshot ReadFiles(ClipboardSnapshot snapshot, int maxFiles)
    {
        var drop = NativeMethods.Clipboard.GetClipboardData(NativeMethods.Clipboard.CF_HDROP);
        if (drop == nint.Zero)
        {
            return snapshot with { Error = Win32Error("GetClipboardData") };
        }

        var count = (int)Math.Min(NativeMethods.Clipboard.DragQueryFileW(drop, NativeMethods.Clipboard.DragQueryCount, null, 0), int.MaxValue);
        var files = new List<string>();
        for (var i = 0; i < Math.Min(count, maxFiles); i++)
        {
            var length = NativeMethods.Clipboard.DragQueryFileW(drop, (uint)i, null, 0);
            if (length == 0 || length > 32_767)
            {
                continue;
            }

            var buffer = new char[length + 1];
            fixed (char* path = buffer)
            {
                var copied = NativeMethods.Clipboard.DragQueryFileW(drop, (uint)i, path, length + 1);
                files.Add(new string(path, 0, (int)Math.Min(copied, length)));
            }
        }

        return snapshot with { Files = files, FileCount = count };
    }

    private static unsafe ClipboardWriteOutcome WriteOpen(nint owner, string text)
    {
        if (owner == nint.Zero)
        {
            return new ClipboardWriteOutcome(true, false, Error: "The clipboard owner window could not be created (" +
                Win32Error("CreateWindowExW") + "), and without an owner Windows does not keep the data.");
        }

        if (!Open(owner))
        {
            return new ClipboardWriteOutcome(false, false);
        }

        bool excluded;
        try
        {
            if (!NativeMethods.Clipboard.EmptyClipboard())
            {
                return new ClipboardWriteOutcome(true, false, Error: Win32Error("EmptyClipboard"));
            }

            // The markers that keep the text out of Win+V history and cloud sync; Windows reads them when the clipboard is closed
            excluded = PutDword(ExcludeFromMonitorFormat, 0) & PutDword(HistoryFormat, 0) & PutDword(CloudFormat, 0);

            var bytes = (nuint)(((long)text.Length + 1) * sizeof(char));
            var memory = NativeMethods.Clipboard.GlobalAlloc(NativeMethods.Clipboard.GMEM_MOVEABLE, bytes);
            if (memory == nint.Zero)
            {
                return new ClipboardWriteOutcome(true, false, Error: Win32Error("GlobalAlloc"));
            }

            var data = NativeMethods.Clipboard.GlobalLock(memory);
            if (data == nint.Zero)
            {
                var error = Win32Error("GlobalLock");
                NativeMethods.Clipboard.GlobalFree(memory);
                return new ClipboardWriteOutcome(true, false, Error: error);
            }

            var destination = new Span<char>((void*)data, text.Length + 1);
            text.AsSpan().CopyTo(destination);
            destination[text.Length] = '\0';
            NativeMethods.Clipboard.GlobalUnlock(memory);

            if (NativeMethods.Clipboard.SetClipboardData(NativeMethods.Clipboard.CF_UNICODETEXT, memory) == nint.Zero)
            {
                var error = Win32Error("SetClipboardData");
                NativeMethods.Clipboard.GlobalFree(memory);
                return new ClipboardWriteOutcome(true, false, Error: error);
            }
        }
        finally
        {
            NativeMethods.Clipboard.CloseClipboard();
        }

        return new ClipboardWriteOutcome(true, true, NativeMethods.Clipboard.GetClipboardSequenceNumber(), excluded);
    }

    /// <summary>Puts a DWORD in the named format on the open clipboard; false when that failed.</summary>
    private static bool PutDword(string name, uint value)
    {
        var format = NativeMethods.Clipboard.RegisterClipboardFormatW(name);
        if (format == 0)
        {
            return false;
        }

        var memory = NativeMethods.Clipboard.GlobalAlloc(NativeMethods.Clipboard.GMEM_MOVEABLE, sizeof(uint));
        if (memory == nint.Zero)
        {
            return false;
        }

        var data = NativeMethods.Clipboard.GlobalLock(memory);
        if (data == nint.Zero)
        {
            NativeMethods.Clipboard.GlobalFree(memory);
            return false;
        }

        Marshal.WriteInt32(data, unchecked((int)value));
        NativeMethods.Clipboard.GlobalUnlock(memory);
        if (NativeMethods.Clipboard.SetClipboardData(format, memory) == nint.Zero)
        {
            NativeMethods.Clipboard.GlobalFree(memory);
            return false;
        }

        return true;
    }

    /// <summary>The last Win32 error of <paramref name="api"/>, read right after the call that set it.</summary>
    private static string Win32Error(string api)
    {
        var code = Marshal.GetLastPInvokeError();
        return $"{api} failed (Win32 error {code}: {new Win32Exception(code).Message})";
    }
}
