using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Native;

internal static partial class NativeMethods
{
    // Windows.Graphics.Capture (NativeGraphicsCaptureWrapper, roadmap M7.1): the WinRT activation factories of the capture
    // classes, a Direct3D 11 device of the server's own and its WinRT wrapper. The capture interfaces themselves are COM
    // vtables the wrapper calls through function pointers. In their own class so the guard tests can pin these calls to their
    // wrapper.
    internal static partial class GraphicsCapture
    {
        /// <summary>The activation factory (or statics) of a WinRT class, by class name, as interface <paramref name="iid"/>.</summary>
        [LibraryImport("combase.dll")]
        public static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

        /// <summary>Makes an HSTRING of <paramref name="sourceString"/>; the caller deletes it.</summary>
        [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int WindowsCreateString(string sourceString, uint length, out nint hstring);

        /// <summary>Deletes an HSTRING.</summary>
        [LibraryImport("combase.dll")]
        public static partial int WindowsDeleteString(nint hstring);

        /// <summary>Creates a Direct3D 11 device and its immediate context, owned by the server.</summary>
        [LibraryImport("d3d11.dll")]
        public static partial int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels,
            uint featureLevelCount, uint sdkVersion, out nint device, out int featureLevel, out nint immediateContext);

        /// <summary>The WinRT IDirect3DDevice (as IInspectable) of a DXGI device, which the capture frame pool takes.</summary>
        [LibraryImport("d3d11.dll")]
        public static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
    }
}
