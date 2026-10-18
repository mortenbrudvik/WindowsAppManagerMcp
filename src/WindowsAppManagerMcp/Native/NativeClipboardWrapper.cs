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

    // The registered format names below are what password managers and Windows put on the clipboard, letter for letter: a
    // name that differs registers another format, which no copy carries, and switches the protection off without any error.
    // NativeClipboardWrapperTests pins them.

    /// <summary>The privacy marker password managers put on a copy that clipboard monitors must not process.</summary>
    public const string ExcludeFromMonitorFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>The DWORD a copy carries to say whether it may go into clipboard history (Win+V): 0 keeps it out.</summary>
    public const string HistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>The DWORD a copy carries to say whether it may be synced to the cloud clipboard: 0 keeps it local.</summary>
    public const string CloudFormat = "CanUploadToCloudClipboard";

    /// <summary>The older marker that tells clipboard viewers to ignore a copy.</summary>
    public const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    private const string HtmlFormat = "HTML Format";

    /// <summary>
    /// Windows Information Protection's label on copied content: empty on a copy no enterprise identity protects, which
    /// Windows 11 adds to every copy (found by roadmap M4.4's check on a real desktop, a copy from Notepad).
    /// </summary>
    public const string EnterpriseDataProtectionFormat = "EnterpriseDataProtectionId";

    /// <summary>The largest protection label read to see whether it is empty; a longer one names an identity.</summary>
    private const int MaxEmptyLabelBytes = 1024;

    /// <summary>
    /// The registered formats that say nothing about the content: the privacy markers, and the formats OLE adds for its
    /// data object (OleSetClipboard), whose real formats are listed on their own.
    /// </summary>
    public static readonly IReadOnlyList<string> BookkeepingFormats =
    [
        ExcludeFromMonitorFormat, HistoryFormat, CloudFormat, ViewerIgnoreFormat, "DataObject", "Ole Private Data",
        "OleClipboardPersistOnFlush"
    ];

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public uint GetSequenceNumber() => NativeMethods.Clipboard.GetClipboardSequenceNumber();

    public int GetOwnerProcessId()
    {
        var owner = NativeMethods.Clipboard.GetClipboardOwner();
        if (owner == nint.Zero || NativeMethods.Clipboard.GetWindowThreadProcessId(owner, out var processId) == 0)
        {
            return 0;
        }

        return (int)processId;
    }

    public ClipboardSnapshot Read(ClipboardFormat format, int maxCharacters, uint privateContentSequence)
    {
        var limit = Math.Max(1, maxCharacters);
        return RunOnClipboardThread(
            owner => ReadOpen(owner, format, limit, privateContentSequence),
            notStarted: ClipboardSnapshot.NotOpened,
            timedOut: ClipboardSnapshot.NotOpened,
            ex => new ClipboardSnapshot(true, Error: $"{ex.GetType().Name}: {ex.Message}"));
    }

    public ClipboardWriteOutcome WriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Write(text, expectedSequence: null);
    }

    public ClipboardWriteOutcome ReplaceText(string? text, uint expectedSequence) => Write(text, expectedSequence);

    private ClipboardWriteOutcome Write(string? text, uint? expectedSequence)
    {
        return RunOnClipboardThread(
            owner => WriteOpen(owner, text, expectedSequence),
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

    private static ClipboardSnapshot ReadOpen(nint owner, ClipboardFormat format, int maxCharacters, uint privateContentSequence)
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
                HasFiles: NativeMethods.Clipboard.IsClipboardFormatAvailable(NativeMethods.Clipboard.CF_HDROP),
                HasOtherFormats: HasOtherFormats());

            // Private content is not even read into the server's memory unless it is the server's own, by the number of
            // the content that is open now (the clipboard may have changed since the caller looked)
            if (snapshot.IsPrivate && (privateContentSequence == 0 || snapshot.SequenceNumber != privateContentSequence))
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

    /// <summary>What the clipboard says about a registered format name, for <see cref="PrivacyMarkers"/>.</summary>
    public enum FormatPresence
    {
        /// <summary>The format is not on the clipboard.</summary>
        Absent,

        /// <summary>The format is on the clipboard.</summary>
        Present,

        /// <summary>The name could not be registered (RegisterClipboardFormat failed), so whether it is there is not known.</summary>
        Unregistered
    }

    /// <summary>The privacy markers on the open clipboard (see <see cref="PrivacyMarkers"/>).</summary>
    private static ClipboardPrivacyMarkers ReadMarkers() => PrivacyMarkers(
        name => NativeMethods.Clipboard.RegisterClipboardFormatW(name) is var format and not 0
            ? NativeMethods.Clipboard.IsClipboardFormatAvailable(format) ? FormatPresence.Present : FormatPresence.Absent
            : FormatPresence.Unregistered,
        name => ReadDword(NativeMethods.Clipboard.RegisterClipboardFormatW(name)));

    /// <summary>
    /// The privacy markers, decided from what the clipboard says about each marker's format name (<paramref name="presence"/>)
    /// and the DWORD CanIncludeInClipboardHistory holds (<paramref name="readDword"/>: null when it cannot be read; asked
    /// only when the format is there). Pure, so that the names and the rule are tested without the user's clipboard. Fails
    /// closed: a marker whose name cannot be registered counts as set, and so does a CanIncludeInClipboardHistory whose
    /// value cannot be read. CanUploadToCloudClipboard is not a marker: a copy kept off the cloud may still be read.
    /// </summary>
    public static ClipboardPrivacyMarkers PrivacyMarkers(Func<string, FormatPresence> presence, Func<string, uint?> readDword)
    {
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(readDword);
        var markers = ClipboardPrivacyMarkers.None;
        if (presence(ExcludeFromMonitorFormat) != FormatPresence.Absent)
        {
            markers |= ClipboardPrivacyMarkers.ExcludeFromMonitorProcessing;
        }

        if (presence(ViewerIgnoreFormat) != FormatPresence.Absent)
        {
            markers |= ClipboardPrivacyMarkers.ClipboardViewerIgnore;
        }

        switch (presence(HistoryFormat))
        {
            case FormatPresence.Unregistered:
            case FormatPresence.Present when readDword(HistoryFormat) is not { } value || value == 0:
                markers |= ClipboardPrivacyMarkers.NotForClipboardHistory;
                break;
        }

        return markers;
    }

    /// <summary>
    /// True when the open clipboard holds a format besides plain text (see <see cref="HasOtherFormats(IEnumerable{uint}, Func{string, uint}, Func{uint, bool})"/>).
    /// </summary>
    private static bool HasOtherFormats() => HasOtherFormats(
        EnumerateFormats(),
        name => NativeMethods.Clipboard.RegisterClipboardFormatW(name),
        format => IsEmptyData(format));

    /// <summary>The formats on the open clipboard, read one at a time (EnumClipboardFormats), so a caller can stop early.</summary>
    private static IEnumerable<uint> EnumerateFormats()
    {
        for (var format = NativeMethods.Clipboard.EnumClipboardFormats(0); format != 0; format = NativeMethods.Clipboard.EnumClipboardFormats(format))
        {
            yield return format;
        }
    }

    /// <summary>
    /// True when <paramref name="formats"/> (the clipboard's, in order) hold one besides plain text (see
    /// <see cref="ClipboardSnapshot.HasOtherFormats"/>): the text formats Windows makes from text, the
    /// <see cref="BookkeepingFormats"/> (by the IDs <paramref name="register"/> gives their names) and an empty
    /// EnterpriseDataProtectionId (<paramref name="isEmptyData"/>, asked only for it) do not count. Pure, so that the names
    /// and the rule are tested without the user's clipboard. Fails closed: a bookkeeping name that cannot be registered (0)
    /// is not recognised, so its format counts.
    /// </summary>
    public static bool HasOtherFormats(IEnumerable<uint> formats, Func<string, uint> register, Func<uint, bool> isEmptyData)
    {
        ArgumentNullException.ThrowIfNull(formats);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(isEmptyData);
        var bookkeeping = BookkeepingFormats.Select(register).Where(f => f != 0).ToHashSet();
        var protectionLabel = register(EnterpriseDataProtectionFormat);
        foreach (var format in formats)
        {
            if (format is NativeMethods.Clipboard.CF_UNICODETEXT or NativeMethods.Clipboard.CF_TEXT or
                    NativeMethods.Clipboard.CF_OEMTEXT or NativeMethods.Clipboard.CF_LOCALE || bookkeeping.Contains(format))
            {
                continue;
            }

            // Windows 11 puts an empty protection label on every copy (Notepad's too): only a label that names an
            // enterprise identity protects the content, and that one counts, so a paste never strips it
            if (protectionLabel != 0 && format == protectionLabel && isEmptyData(format))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// True when the open clipboard's <paramref name="format"/> holds no data: none rendered, or only zero bytes. A
    /// format with data that cannot be read, or with more than <see cref="MaxEmptyLabelBytes"/> bytes, is not empty.
    /// </summary>
    private static bool IsEmptyData(uint format)
    {
        var handle = NativeMethods.Clipboard.GetClipboardData(format);
        if (handle == nint.Zero)
        {
            return true;
        }

        var size = (long)NativeMethods.Clipboard.GlobalSize(handle);
        if (size == 0)
        {
            return true;
        }

        if (size > MaxEmptyLabelBytes)
        {
            return false;
        }

        var data = NativeMethods.Clipboard.GlobalLock(handle);
        if (data == nint.Zero)
        {
            return false;
        }

        try
        {
            for (var i = 0; i < size; i++)
            {
                if (Marshal.ReadByte(data, i) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            NativeMethods.Clipboard.GlobalUnlock(handle);
        }
    }

    /// <summary>The DWORD in the open clipboard's <paramref name="format"/>; null when there is none or it cannot be read.</summary>
    private static uint? ReadDword(uint format)
    {
        if (format == 0)
        {
            return null;
        }

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

    /// <summary>
    /// Replaces the clipboard's content with <paramref name="text"/> and the privacy markers, or empties it when
    /// <paramref name="text"/> is null; with <paramref name="expectedSequence"/>, only while the clipboard still has that
    /// sequence number (read once it is open, so nobody can change it in between).
    /// </summary>
    private static unsafe ClipboardWriteOutcome WriteOpen(nint owner, string? text, uint? expectedSequence)
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

        var excluded = false;
        try
        {
            if (expectedSequence is { } expected && NativeMethods.Clipboard.GetClipboardSequenceNumber() != expected)
            {
                return new ClipboardWriteOutcome(true, false, Changed: true);
            }

            if (!NativeMethods.Clipboard.EmptyClipboard())
            {
                return new ClipboardWriteOutcome(true, false, Error: Win32Error("EmptyClipboard"));
            }

            // Null empties the clipboard: nothing goes on it
            if (text != null)
            {
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
        }
        finally
        {
            NativeMethods.Clipboard.CloseClipboard();
        }

        // The number is the server's only while its window still owns the clipboard after reading it: an application that
        // replaced the text in between emptied the clipboard, which made its own window (or none) the owner
        var sequenceNumber = NativeMethods.Clipboard.GetClipboardSequenceNumber();
        if (NativeMethods.Clipboard.GetClipboardOwner() != owner)
        {
            sequenceNumber = 0;
        }

        return new ClipboardWriteOutcome(true, true, sequenceNumber, excluded);
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
