using System.Runtime.InteropServices;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Native;

/// <summary>
/// Production implementation of <see cref="IGraphicsCaptureWrapper"/> (roadmap M7.1): one frame of a window through
/// Windows.Graphics.Capture, copied from the GPU into memory through a Direct3D 11 staging texture.
/// </summary>
/// <remarks>
/// <para>The WinRT capture classes are reached through their activation factories and called as COM vtables, like the
/// Direct3D 11 interfaces, rather than through a CsWinRT projection: projecting Windows.Graphics.Capture pulls in
/// Windows.System and Windows.UI.Composition, which the single-file exe would carry for nothing. The vtable slots are those of
/// the Windows SDK headers (windows.graphics.capture.h, d3d11.h); a wrong one fails the capture, which falls back.</para>
/// <para>Each capture runs on a thread of its own, in the multithreaded apartment WinRT needs, and the caller waits at most
/// <see cref="Deadline"/>: the compositor answers, not the window, but a stuck graphics driver would otherwise hang the tool.
/// A capture that misses it is abandoned, and while it is still blocked <see cref="IsSupported"/> is false. The Direct3D
/// device is made once and kept; captures take turns on it (its immediate context is not thread-safe), and a device the
/// driver lost is made again.</para>
/// <para>On Windows 11 the yellow border Windows draws around a captured window is turned off where Windows allows it
/// (IGraphicsCaptureSession3); the mouse pointer is never in the frame.</para>
/// </remarks>
public sealed unsafe class NativeGraphicsCaptureWrapper : IGraphicsCaptureWrapper, IDisposable
{
    /// <summary>How long a caller waits for one capture, the first frame included.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    /// <summary>How long a capture waits for the compositor's first frame.</summary>
    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(1.5);

    // The WinRT and COM interfaces, as the Windows SDK headers declare them
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid IidSessionStatics = new("2224A540-5974-49AA-B232-0882536F4CB5");
    private static readonly Guid IidSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid IidSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid IidClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");
    private static readonly Guid IidDirect3DDevice = new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private const string FramePoolClass = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";
    private const string SessionClass = "Windows.Graphics.Capture.GraphicsCaptureSession";

    private const int B8G8R8A8UIntNormalized = 87;
    private const int D3DDriverTypeHardware = 1, D3DDriverTypeWarp = 5;
    private const uint D3D11CreateDeviceBgraSupport = 0x20, D3D11SdkVersion = 7;
    private const uint D3D11UsageStaging = 3, D3D11CpuAccessRead = 0x20000, D3D11MapRead = 1;
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005), DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    private readonly object _deviceLock = new();
    private readonly object _supportLock = new();
    private nint _device, _context, _graphicsDevice;
    private int _abandoned;
    private bool? _supported;
    private volatile bool _disposed;

    /// <inheritdoc />
    public bool IsSupported
    {
        get
        {
            if (Volatile.Read(ref _abandoned) > 0)
            {
                return false;
            }

            lock (_supportLock)
            {
                // Only an answer is kept: a probe that missed its deadline (a busy machine) asks again next time
                _supported ??= OnMtaThread<bool?>(() => QuerySupported(), fallback: null);
                return _supported == true;
            }
        }
    }

    /// <inheritdoc />
    public CaptureResult CaptureWindow(nint windowHandle, bool includeFrame)
    {
        if (!IsSupported)
        {
            throw new GraphicsCaptureException("Windows.Graphics.Capture is not available on this computer.");
        }

        // The visible frame and the client area are read here, before the frame: the window's place on the screen
        if (NativeMethods.Dwmapi.DwmGetWindowAttribute(windowHandle, NativeEnums.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame,
                Marshal.SizeOf<RECT>()) != 0 || frame.Width <= 0 || frame.Height <= 0)
        {
            throw new GraphicsCaptureException("The window's visible frame could not be read.");
        }

        RECT client = default;
        if (!includeFrame)
        {
            if (!NativeMethods.User32.GetClientRect(windowHandle, out client) ||
                (NativeMethods.User32.MapWindowPoints(windowHandle, nint.Zero, ref client, 2) == 0 && Marshal.GetLastPInvokeError() != 0))
            {
                throw new GraphicsCaptureException("The window's client area could not be read.");
            }
        }

        Frame? captured = null;
        Exception? failure = null;
        var completed = OnMtaThread(() =>
        {
            // A capture stuck in the driver holds the device: wait for it no longer than the caller does, so a waiting
            // worker never runs a capture after its caller gave up
            if (!Monitor.TryEnter(_deviceLock, Deadline))
            {
                failure = new GraphicsCaptureException("Another Windows.Graphics.Capture capture is still running.");
                return true;
            }

            try
            {
                captured = ReadFrame(windowHandle);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Monitor.Exit(_deviceLock);
            }

            return true;
        }, fallback: false);

        if (!completed)
        {
            throw new GraphicsCaptureException(
                $"Windows.Graphics.Capture did not deliver a frame within {Deadline.TotalSeconds:0} seconds (the graphics driver may be busy).");
        }

        if (failure != null || captured is not { } pixels)
        {
            throw failure as GraphicsCaptureException ?? new GraphicsCaptureException(
                $"Windows.Graphics.Capture failed: {failure?.Message ?? "no frame"}", failure);
        }

        // The frame is the visible frame, top-left at the frame's corner; a client capture is the client area's part of it
        var area = includeFrame
            ? new RECT { Left = frame.Left, Top = frame.Top, Right = frame.Left + pixels.Width, Bottom = frame.Top + pixels.Height }
            : Intersect(client, new RECT { Left = frame.Left, Top = frame.Top, Right = frame.Left + pixels.Width, Bottom = frame.Top + pixels.Height });
        if (area.Width <= 0 || area.Height <= 0)
        {
            throw new GraphicsCaptureException("The captured frame does not cover the window's client area.");
        }

        return Crop(pixels, area.Left - frame.Left, area.Top - frame.Top, area.Width, area.Height, area.Left, area.Top);
    }

    public void Dispose()
    {
        _disposed = true;

        // A capture stuck in the driver keeps the device: the server's exit must not wait for it
        if (!Monitor.TryEnter(_deviceLock, TimeSpan.FromSeconds(1)))
        {
            return;
        }

        try
        {
            ReleaseDevice();
        }
        finally
        {
            Monitor.Exit(_deviceLock);
        }
    }

    // ---- The capture, on an MTA thread, under the device lock ----

    private bool QuerySupported()
    {
        var statics = Factory(SessionClass, IidSessionStatics);
        if (statics == nint.Zero)
        {
            return false;
        }

        try
        {
            byte supported = 0;
            return Call(statics, 6, &supported) >= 0 && supported != 0;
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    private Frame ReadFrame(nint window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return ReadFrameOnce(window);
        }
        catch (DeviceLostException)
        {
            // The driver was updated or reset: a new device, once
            ReleaseDevice();
            return ReadFrameOnce(window);
        }
    }

    private Frame ReadFrameOnce(nint window)
    {
        EnsureDevice();
        nint interop = 0, item = 0, statics = 0, pool = 0, session = 0, frame = 0, surface = 0, access = 0, texture = 0, staging = 0;
        try
        {
            interop = Factory(GraphicsCaptureItemClass, IidGraphicsCaptureItemInterop);
            if (interop == nint.Zero)
            {
                throw new GraphicsCaptureException("The GraphicsCaptureItem factory is not available.");
            }

            // IGraphicsCaptureItemInterop.CreateForWindow
            var iidItem = IidGraphicsCaptureItem;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(interop, 3))(interop, window, &iidItem, &item),
                "the window cannot be captured (GraphicsCaptureItem)");

            SizeInt32 size;
            Check(Call(item, 7, &size), "the window's capture size");
            if (size.Width <= 0 || size.Height <= 0)
            {
                throw new GraphicsCaptureException("The window has no size to capture (it may be minimized).");
            }

            statics = Factory(FramePoolClass, IidFramePoolStatics2);
            if (statics == nint.Zero)
            {
                throw new GraphicsCaptureException("The capture frame pool is not available.");
            }

            // Direct3D11CaptureFramePool.CreateFreeThreaded(device, format, 1 buffer, size)
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, nint*, int>)Slot(statics, 6))(
                statics, _graphicsDevice, B8G8R8A8UIntNormalized, 1, size, &pool), "the capture frame pool");
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(pool, 10))(pool, item, &session), "the capture session");

            // No pointer in the frame, and no yellow border where Windows lets the server turn it off (Windows 11)
            SetFlag(session, IidSession2, value: false);
            SetFlag(session, IidSession3, value: false);
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(session, 6))(session), "starting the capture");

            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                CheckDevice(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pool, 7))(pool, &frame), "the captured frame");
                if (frame != nint.Zero)
                {
                    break;
                }

                if (waited.Elapsed > FrameWait)
                {
                    throw new GraphicsCaptureException(
                        $"The compositor delivered no frame of the window within {FrameWait.TotalSeconds:0.#} seconds (a minimized or hidden window has none).");
                }

                Thread.Sleep(5);
            }

            SizeInt32 content;
            Check(Call(frame, 8, &content), "the frame's size");
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(frame, 6))(frame, &surface), "the frame's surface");
            Check(QueryInterface(surface, IidDxgiInterfaceAccess, out access), "the frame's Direct3D surface");
            var iidTexture = IidTexture2D;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(access, 3))(access, &iidTexture, &texture), "the frame's texture");

            // ID3D11Texture2D.GetDesc, then a staging copy the CPU can read
            Texture2DDesc desc;
            ((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, void>)Slot(texture, 10))(texture, &desc);
            var width = (int)Math.Min(desc.Width, (uint)Math.Max(content.Width, 0));
            var height = (int)Math.Min(desc.Height, (uint)Math.Max(content.Height, 0));
            if (width <= 0 || height <= 0)
            {
                throw new GraphicsCaptureException("The captured frame is empty.");
            }

            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.SampleCount = 1;
            desc.SampleQuality = 0;
            desc.Usage = D3D11UsageStaging;
            desc.BindFlags = 0;
            desc.CpuAccessFlags = D3D11CpuAccessRead;
            desc.MiscFlags = 0;
            CheckDevice(((delegate* unmanaged[Stdcall]<nint, Texture2DDesc*, nint, nint*, int>)Slot(_device, 5))(_device, &desc, 0, &staging),
                "the staging texture");
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(_context, 47))(_context, staging, texture);

            MappedSubresource mapped;
            CheckDevice(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)Slot(_context, 14))(
                _context, staging, 0, D3D11MapRead, 0, &mapped), "reading the frame");
            try
            {
                var rowBytes = width * 4;
                var pixels = GC.AllocateUninitializedArray<byte>(rowBytes * height);
                var source = (byte*)mapped.Data;
                fixed (byte* target = pixels)
                {
                    for (var row = 0; row < height; row++)
                    {
                        Buffer.MemoryCopy(source + (long)row * mapped.RowPitch, target + (long)row * rowBytes, rowBytes, rowBytes);
                    }
                }

                return new Frame(pixels, width, height);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 15))(_context, staging, 0);
            }
        }
        finally
        {
            Release(staging);
            Release(texture);
            Release(access);
            Release(surface);
            Close(frame);
            Close(session);
            Close(pool);
            Release(statics);
            Release(item);
            Release(interop);
        }
    }

    private void EnsureDevice()
    {
        if (_graphicsDevice != nint.Zero)
        {
            return;
        }

        nint device = 0, context = 0, dxgi = 0, inspectable = 0, graphics = 0;
        try
        {
            var hr = NativeMethods.GraphicsCapture.D3D11CreateDevice(0, D3DDriverTypeHardware, 0, D3D11CreateDeviceBgraSupport, 0, 0,
                D3D11SdkVersion, out device, out _, out context);
            if (hr < 0)
            {
                // No usable GPU (a virtual machine, a remote session): the software rasterizer
                hr = NativeMethods.GraphicsCapture.D3D11CreateDevice(0, D3DDriverTypeWarp, 0, D3D11CreateDeviceBgraSupport, 0, 0,
                    D3D11SdkVersion, out device, out _, out context);
            }

            Check(hr, "creating a Direct3D 11 device");
            Check(QueryInterface(device, IidDxgiDevice, out dxgi), "the DXGI device");
            Check(NativeMethods.GraphicsCapture.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable), "the WinRT Direct3D device");
            Check(QueryInterface(inspectable, IidDirect3DDevice, out graphics), "the WinRT Direct3D device");
            (_device, _context, _graphicsDevice) = (device, context, graphics);
            device = context = graphics = 0;
        }
        finally
        {
            Release(inspectable);
            Release(dxgi);
            Release(graphics);
            Release(context);
            Release(device);
        }
    }

    private void ReleaseDevice()
    {
        Release(_graphicsDevice);
        Release(_context);
        Release(_device);
        _graphicsDevice = _context = _device = 0;
    }

    // ---- Helpers ----

    /// <summary>Runs <paramref name="work"/> on a new MTA thread and waits at most <see cref="Deadline"/>; <paramref name="fallback"/> when it does not finish.</summary>
    private T OnMtaThread<T>(Func<T> work, T fallback)
    {
        if (Volatile.Read(ref _abandoned) > 0)
        {
            return fallback;
        }

        T result = fallback;
        const int Running = 0, Finished = 1, Abandoned = 2;
        var state = Running;
        var finished = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception)
            {
                // The result stays the fallback
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
            Name = "Windows.Graphics.Capture"
        };

        try
        {
            worker.SetApartmentState(ApartmentState.MTA);
            worker.UnsafeStart();
        }
        catch (Exception)
        {
            return fallback;
        }

        // The event is not disposed: an abandoned worker still sets it when it returns
        if (!finished.Wait(Deadline) && Interlocked.CompareExchange(ref state, Abandoned, Running) == Running)
        {
            Interlocked.Increment(ref _abandoned);
            return fallback;
        }

        return result;
    }

    private static nint Factory(string className, Guid iid)
    {
        if (NativeMethods.GraphicsCapture.WindowsCreateString(className, (uint)className.Length, out var name) < 0)
        {
            return 0;
        }

        try
        {
            return NativeMethods.GraphicsCapture.RoGetActivationFactory(name, iid, out var factory) >= 0 ? factory : 0;
        }
        finally
        {
            NativeMethods.GraphicsCapture.WindowsDeleteString(name);
        }
    }

    /// <summary>Sets a session's boolean property (put_IsCursorCaptureEnabled, put_IsBorderRequired) when it has that interface.</summary>
    private static void SetFlag(nint session, Guid iid, bool value)
    {
        if (QueryInterface(session, iid, out var flags) < 0)
        {
            return;
        }

        try
        {
            ((delegate* unmanaged[Stdcall]<nint, byte, int>)Slot(flags, 7))(flags, value ? (byte)1 : (byte)0);
        }
        finally
        {
            Release(flags);
        }
    }

    private static int QueryInterface(nint unknown, Guid iid, out nint result)
    {
        nint found = 0;
        var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(unknown, 0))(unknown, &iid, &found);
        result = found;
        return hr;
    }

    private static int Call<T>(nint instance, int slot, T* result) where T : unmanaged =>
        ((delegate* unmanaged[Stdcall]<nint, T*, int>)Slot(instance, slot))(instance, result);

    private static void* Slot(nint instance, int slot) => (*(void***)instance)[slot];

    /// <summary>Closes a WinRT object (IClosable.Close), then releases it.</summary>
    private static void Close(nint instance)
    {
        if (instance == nint.Zero)
        {
            return;
        }

        if (QueryInterface(instance, IidClosable, out var closable) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(closable, 6))(closable);
            Release(closable);
        }

        Release(instance);
    }

    private static void Release(nint instance)
    {
        if (instance != nint.Zero)
        {
            Marshal.Release(instance);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new GraphicsCaptureException($"Windows.Graphics.Capture failed at {what} (HRESULT 0x{hr:X8}).");
        }
    }

    private static void CheckDevice(int hr, string what)
    {
        if (hr is DxgiErrorDeviceRemoved or DxgiErrorDeviceReset)
        {
            throw new DeviceLostException();
        }

        Check(hr, what);
    }

    private static RECT Intersect(RECT a, RECT b) => new()
    {
        Left = Math.Max(a.Left, b.Left),
        Top = Math.Max(a.Top, b.Top),
        Right = Math.Min(a.Right, b.Right),
        Bottom = Math.Min(a.Bottom, b.Bottom)
    };

    private static CaptureResult Crop(Frame frame, int left, int top, int width, int height, int originX, int originY)
    {
        if (left == 0 && top == 0 && width == frame.Width && height == frame.Height)
        {
            return new CaptureResult(frame.Bgra, width, height, originX, originY, IGraphicsCaptureWrapper.Method);
        }

        var rowBytes = width * 4;
        var pixels = GC.AllocateUninitializedArray<byte>(rowBytes * height);
        for (var row = 0; row < height; row++)
        {
            Buffer.BlockCopy(frame.Bgra, ((top + row) * frame.Width + left) * 4, pixels, row * rowBytes, rowBytes);
        }

        return new CaptureResult(pixels, width, height, originX, originY, IGraphicsCaptureWrapper.Method);
    }

    private sealed record Frame(byte[] Bgra, int Width, int Height);

    private sealed class DeviceLostException : Exception;

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeInt32
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public nint Data;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
