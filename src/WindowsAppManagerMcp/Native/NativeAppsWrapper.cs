using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// See <see cref="INativeAppsWrapper"/> (roadmap M6.5). The COM interfaces declare only the methods the server calls:
/// IApplicationActivationManager only ActivateApplication (not ActivateForFile or ActivateForProtocol), IPropertyStore
/// only its reads (not SetValue or Commit). NativeCallGuardTests checks that they stay so.
/// </summary>
public sealed class NativeAppsWrapper : INativeAppsWrapper
{
    /// <summary>How long a caller waits for the Applications folder to be listed.</summary>
    public static readonly TimeSpan EnumerationDeadline = TimeSpan.FromSeconds(3);

    private static readonly Guid ApplicationActivationManagerClsid = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Guid AppsFolderId = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    private static readonly Guid ShellItemIid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid EnumItemsHandlerId = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static readonly Guid EnumShellItemsIid = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid PropertyStoreIid = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    private static readonly Guid AppUserModelProperties = new("9f4c2855-9f79-4b39-a8d0-e1d42de1d5f3");
    private static readonly NativeMethods.PropertyKey AppUserModelId = new(AppUserModelProperties, 5);
    private static readonly NativeMethods.PropertyKey PackageFamilyNameKey = new(AppUserModelProperties, 17);
    private static readonly NativeMethods.PropertyKey LinkTargetParsingPath = new(new Guid("b9b4b3fc-2b51-4a42-b5d8-324146afcf25"), 2);

    /// <summary>AO_NOERRORUI: Windows shows no error dialog of its own when the activation fails.</summary>
    private const int NoErrorUi = 0x1;

    private const uint SigdnNormalDisplay = 0x00000000;
    private const uint SigdnParentRelativeParsing = 0x80018001;
    private const int GpsBestEffort = 0x40;
    private const ushort VtLpwstr = 31;
    private const ushort VtBstr = 8;
    private const int Running = 0, Finished = 1, Abandoned = 2;

    /// <summary>Lists that missed the deadline and have not returned yet; while one is blocked, no other is started.</summary>
    private int _abandonedEnumerations;

    public AppActivationResult ActivateApplication(string appUserModelId, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        AppActivationResult result = new(unchecked((int)0x80004005), null); // E_FAIL until the call answers
        var worker = new Thread(() =>
        {
            object? instance = null;
            try
            {
                var type = Type.GetTypeFromCLSID(ApplicationActivationManagerClsid, throwOnError: true)!;
                instance = Activator.CreateInstance(type)!;
                var manager = (IApplicationActivationManager)instance;
                var hr = manager.ActivateApplication(appUserModelId, string.IsNullOrEmpty(arguments) ? null : arguments, NoErrorUi, out var processId);
                result = new AppActivationResult(hr, hr >= 0 && processId != 0 ? (int)processId : null);
            }
            catch (Exception ex)
            {
                result = new AppActivationResult(ex.HResult, null);
            }
            finally
            {
                if (instance != null)
                    Marshal.FinalReleaseComObject(instance);
            }
        })
        {
            IsBackground = true,
            Name = "App activation"
        };

        try
        {
            worker.SetApartmentState(ApartmentState.STA);
            worker.UnsafeStart();
            worker.Join();
        }
        catch (Exception ex)
        {
            return new AppActivationResult(ex.HResult, null);
        }

        return result;
    }

    public IReadOnlyList<AppsFolderEntry>? EnumerateAppsFolder()
    {
        if (Volatile.Read(ref _abandonedEnumerations) > 0)
            return null;

        List<AppsFolderEntry>? entries = null;
        var state = Running;
        var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                entries = ReadAppsFolder();
            }
            catch (Exception)
            {
                // No Applications folder (a stripped-down Windows), or a property handler failed: the list stays unknown
            }
            finally
            {
                if (Interlocked.Exchange(ref state, Finished) == Abandoned)
                    Interlocked.Decrement(ref _abandonedEnumerations);

                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "Applications folder read"
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
        if (!finished.Wait(EnumerationDeadline) && Interlocked.CompareExchange(ref state, Abandoned, Running) == Running)
        {
            Interlocked.Increment(ref _abandonedEnumerations);
            return null;
        }

        return Volatile.Read(ref entries);
    }

    private static List<AppsFolderEntry>? ReadAppsFolder()
    {
        var folderId = AppsFolderId;
        var itemIid = ShellItemIid;
        if (NativeMethods.Apps.SHGetKnownFolderItem(ref folderId, 0, nint.Zero, ref itemIid, out var folderObject) < 0 || folderObject == null)
            return null;

        object? enumObject = null;
        try
        {
            var handler = EnumItemsHandlerId;
            var enumIid = EnumShellItemsIid;
            if (folderObject is not IShellItem2 folder ||
                folder.BindToHandler(nint.Zero, ref handler, ref enumIid, out enumObject) < 0 || enumObject is not IEnumShellItems items)
                return null;

            var entries = new List<AppsFolderEntry>();
            while (items.Next(1, out var itemObject, out var fetched) == 0 && fetched == 1 && itemObject != null)
            {
                try
                {
                    if (itemObject is IShellItem2 item && ReadEntry(item) is { } entry)
                        entries.Add(entry);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(itemObject);
                }
            }

            return entries;
        }
        finally
        {
            if (enumObject != null)
                Marshal.FinalReleaseComObject(enumObject);
            Marshal.FinalReleaseComObject(folderObject);
        }
    }

    private static AppsFolderEntry? ReadEntry(IShellItem2 item)
    {
        var name = DisplayName(item, SigdnNormalDisplay);
        string? appId = null, family = null, target = null;
        var storeIid = PropertyStoreIid;
        if (item.GetPropertyStore(GpsBestEffort, ref storeIid, out var storeObject) >= 0 && storeObject is IPropertyStore store)
        {
            try
            {
                appId = ReadString(store, AppUserModelId);
                family = ReadString(store, PackageFamilyNameKey);
                target = ReadString(store, LinkTargetParsingPath);
            }
            finally
            {
                Marshal.FinalReleaseComObject(storeObject);
            }
        }

        appId ??= DisplayName(item, SigdnParentRelativeParsing);
        return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appId)
            ? null
            : new AppsFolderEntry(name, appId, string.IsNullOrWhiteSpace(family) ? null : family, string.IsNullOrWhiteSpace(target) ? null : target);
    }

    private static string? DisplayName(IShellItem2 item, uint form)
    {
        if (item.GetDisplayName(form, out var text) < 0 || text == nint.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static string? ReadString(IPropertyStore store, NativeMethods.PropertyKey key)
    {
        if (store.GetValue(ref key, out var value) < 0)
            return null;

        try
        {
            return value.Type switch
            {
                VtLpwstr when value.Value != nint.Zero => Marshal.PtrToStringUni(value.Value),
                VtBstr when value.Value != nint.Zero => Marshal.PtrToStringBSTR(value.Value),
                _ => null
            };
        }
        finally
        {
            NativeMethods.Apps.PropVariantClear(ref value);
        }
    }

    public string? GetPackageFamilyName(int processId)
    {
        if (processId <= 0)
            return null;

        var process = NativeMethods.Kernel32.OpenProcess(NativeEnums.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == nint.Zero)
            return null;

        try
        {
            var buffer = new char[256];
            var length = (uint)buffer.Length;
            if (NativeMethods.Apps.GetPackageFamilyName(process, ref length, buffer) != 0 || length == 0)
                return null;

            return new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length)).TrimEnd('\0');
        }
        finally
        {
            NativeMethods.Kernel32.CloseHandle(process);
        }
    }

    public IReadOnlyList<string>? ReadPackageManifests(string packageFamilyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        try
        {
            uint count = 0, bufferLength = 0;
            var status = NativeMethods.Apps.GetPackagesByPackageFamily(packageFamilyName, ref count, null, ref bufferLength, null);
            if (status != NativeMethods.Apps.ErrorInsufficientBuffer || count == 0 || bufferLength == 0)
                return null;

            var pointers = new nint[count];
            var buffer = new char[bufferLength];
            if (NativeMethods.Apps.GetPackagesByPackageFamily(packageFamilyName, ref count, pointers, ref bufferLength, buffer) != 0)
                return null;

            var manifests = new List<string>();
            foreach (var fullName in new string(buffer, 0, (int)Math.Min(bufferLength, (uint)buffer.Length))
                         .Split('\0', StringSplitOptions.RemoveEmptyEntries).Take((int)count))
            {
                if (PackagePath(fullName) is { } folder && ReadManifest(folder) is { } manifest)
                    manifests.Add(manifest);
            }

            return manifests;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static string? PackagePath(string fullName)
    {
        uint length = 0;
        if (NativeMethods.Apps.GetPackagePathByFullName(fullName, ref length, null) != NativeMethods.Apps.ErrorInsufficientBuffer || length == 0)
            return null;

        var path = new char[length];
        return NativeMethods.Apps.GetPackagePathByFullName(fullName, ref length, path) == 0
            ? new string(path, 0, (int)Math.Min(length, (uint)path.Length)).TrimEnd('\0')
            : null;
    }

    private static string? ReadManifest(string folder)
    {
        try
        {
            return File.ReadAllText(Path.Combine(folder, "AppxManifest.xml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public string? GetWindowAppUserModelId(nint window)
    {
        if (window == nint.Zero)
            return null;

        try
        {
            var storeIid = PropertyStoreIid;
            if (NativeMethods.Apps.SHGetPropertyStoreForWindow(window, ref storeIid, out var storeObject) < 0 || storeObject == null)
                return null;

            try
            {
                return storeObject is IPropertyStore store ? ReadString(store, AppUserModelId) : null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(storeObject);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// IApplicationActivationManager (shobjidl_core.h), up to the one method the server calls. ActivateForFile and
    /// ActivateForProtocol are left out on purpose: a method that is not declared cannot be called.
    /// </summary>
    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            int options,
            out uint processId);
    }

    /// <summary>
    /// IShellItem2 (shobjidl_core.h), up to GetPropertyStore: IShellItem's five methods, in their vtable order, then the
    /// first of its own. GetParent, GetAttributes and Compare are declared for the order only and never called.
    /// </summary>
    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem2
    {
        [PreserveSig]
        int BindToHandler(nint bindContext, ref Guid handlerId, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? handler);

        [PreserveSig]
        int GetParent(out nint parent);

        [PreserveSig]
        int GetDisplayName(uint form, out nint name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(nint other, uint hint, out int order);

        [PreserveSig]
        int GetPropertyStore(int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? store);
    }

    /// <summary>IEnumShellItems (shobjidl_core.h), up to Next.</summary>
    [ComImport]
    [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out object? item, out uint fetched);
    }

    /// <summary>
    /// IPropertyStore (propsys.h), up to GetValue. SetValue and Commit, which write a property, are left out on purpose.
    /// </summary>
    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out NativeMethods.PropertyKey key);

        [PreserveSig]
        int GetValue(ref NativeMethods.PropertyKey key, out NativeMethods.PropVariant value);
    }
}
