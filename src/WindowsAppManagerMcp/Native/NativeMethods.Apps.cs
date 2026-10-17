using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    /// <summary>
    /// Packages and the Applications folder (roadmap M6.5, NativeAppsWrapper). Every function here only reads: a process's
    /// package, a package family's installed packages and their folders, a known folder's shell item and a window's property
    /// store (which the wrapper never writes). Activation is a COM call of the wrapper, not a function here.
    /// </summary>
    internal static partial class Apps
    {
        /// <summary>ERROR_INSUFFICIENT_BUFFER: the length argument now holds the size needed.</summary>
        public const int ErrorInsufficientBuffer = 122;

        /// <summary>The package family name of a process; APPMODEL_ERROR_NO_PACKAGE (15700) when it has none.</summary>
        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int GetPackageFamilyName(nint hProcess, ref uint packageFamilyNameLength, [Out] char[]? packageFamilyName);

        /// <summary>
        /// The full names of the packages of a family installed for the user, written one after another (each ends in a NUL)
        /// to <paramref name="buffer"/>. The pointers in <paramref name="packageFullNames"/> point into that buffer while the
        /// call runs, so the caller reads the buffer, not them.
        /// </summary>
        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int GetPackagesByPackageFamily(
            string packageFamilyName, ref uint count, [Out] nint[]? packageFullNames, ref uint bufferLength, [Out] char[]? buffer);

        /// <summary>The folder a package is installed in.</summary>
        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);

        /// <summary>A known folder (FOLDERID_AppsFolder) as a shell item; KF_FLAG_DEFAULT, the user's own token.</summary>
        [DllImport("shell32.dll")]
        public static extern int SHGetKnownFolderItem(
            ref Guid rfid, uint flags, nint hToken, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);

        /// <summary>A window's property store (IPropertyStore), which the wrapper only reads.</summary>
        [DllImport("shell32.dll")]
        public static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);

        /// <summary>Frees what a PROPVARIANT the server read holds.</summary>
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant pvar);
    }

    /// <summary>A shell property's key: its format ID and property ID.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    /// <summary>A PROPVARIANT: its type, and the union the server reads as a pointer (24 bytes on x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Value;
        public nint Value2;
    }
}
