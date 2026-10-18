using System.Runtime.InteropServices;

namespace WindowsAppManagerMcp.Tests.Unit.Native;

/// <summary>
/// How takeover detection's low-level hooks read what Windows hands them (roadmap M7.4, review F3): the kind of input from
/// the message, and the injected flag from KBDLLHOOKSTRUCT and MSLLHOOKSTRUCT. A wrong offset or mask would make the user's
/// input look injected, and takeover would never fire, so the parsing is tested over memory laid out like the real structs
/// (Marshal.StructureToPtr of their Win32 definitions), without installing a hook.
/// </summary>
public class NativeLowLevelInputWrapperTests
{
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint WmMouseMove = 0x0200, WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmRButtonDown = 0x0204;
    private const uint WmMouseWheel = 0x020A, WmMouseHWheel = 0x020E, WmXButtonDown = 0x020B;

    /// <summary>KBDLLHOOKSTRUCT as winuser.h declares it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    /// <summary>MSLLHOOKSTRUCT as winuser.h declares it (pt is a POINT: two LONGs).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint DwExtraInfo;
    }

    /// <summary>
    /// Runs <paramref name="read"/> over <paramref name="value"/> copied to unmanaged memory, as Windows passes the struct to the
    /// hook (its lParam).
    /// </summary>
    private static LowLevelInput Over<T>(T value, Func<nint, LowLevelInput> read) where T : struct
    {
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(value, memory, fDeleteOld: false);
            return read(memory);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>A key event whose other fields are all ones (<paramref name="noisy"/>) or zero, so a wrong offset reads them instead.</summary>
    private static KbdLlHookStruct Key(uint flags, bool noisy) => noisy
        ? new KbdLlHookStruct { VkCode = uint.MaxValue, ScanCode = uint.MaxValue, Flags = flags, Time = uint.MaxValue, DwExtraInfo = nuint.MaxValue }
        : new KbdLlHookStruct { Flags = flags };

    private static MsLlHookStruct Mouse(uint flags, bool noisy) => noisy
        ? new MsLlHookStruct { X = -1, Y = -1, MouseData = uint.MaxValue, Flags = flags, Time = uint.MaxValue, DwExtraInfo = nuint.MaxValue }
        : new MsLlHookStruct { Flags = flags };

    [Fact]
    public void TheFlagsOffsets_AreThoseOfTheWin32Structs()
    {
        NativeLowLevelInputWrapper.KeyboardFlagsOffset.Should().Be((int)Marshal.OffsetOf<KbdLlHookStruct>(nameof(KbdLlHookStruct.Flags)));
        NativeLowLevelInputWrapper.MouseFlagsOffset.Should().Be((int)Marshal.OffsetOf<MsLlHookStruct>(nameof(MsLlHookStruct.Flags)));
    }

    [Theory]
    [InlineData(0x00u, false, false)]
    [InlineData(0x00u, true, false)]
    [InlineData(0x10u, false, true)] // LLKHF_INJECTED
    [InlineData(0x10u, true, true)]
    [InlineData(0x12u, false, true)] // LLKHF_LOWER_IL_INJECTED, which Windows sets with LLKHF_INJECTED
    [InlineData(0x02u, false, true)] // LLKHF_LOWER_IL_INJECTED alone still counts as injected
    [InlineData(0x01u, true, false)] // LLKHF_EXTENDED
    [InlineData(0x20u, true, false)] // LLKHF_ALTDOWN
    [InlineData(0x80u, true, false)] // LLKHF_UP
    [InlineData(0xA1u, true, false)] // an extended key going up with Alt down: the user's
    public void ReadKeyboard_ReadsTheInjectedFlags_AtFlagsOffset(uint flags, bool noisyNeighbours, bool injected)
    {
        var input = Over(Key(flags, noisyNeighbours), data => NativeLowLevelInputWrapper.ReadKeyboard((nint)WmKeyDown, data));

        input.Should().Be(new LowLevelInput(LowLevelInputKind.KeyDown, injected));
    }

    [Theory]
    [InlineData(0x00u, false, false)]
    [InlineData(0x00u, true, false)]
    [InlineData(0x01u, false, true)] // LLMHF_INJECTED
    [InlineData(0x01u, true, true)]
    [InlineData(0x03u, false, true)] // LLMHF_LOWER_IL_INJECTED, which Windows sets with LLMHF_INJECTED
    [InlineData(0x02u, false, true)] // LLMHF_LOWER_IL_INJECTED alone still counts as injected
    public void ReadMouse_ReadsTheInjectedFlags_AtFlagsOffset(uint flags, bool noisyNeighbours, bool injected)
    {
        var input = Over(Mouse(flags, noisyNeighbours), data => NativeLowLevelInputWrapper.ReadMouse((nint)WmLButtonDown, data));

        input.Should().Be(new LowLevelInput(LowLevelInputKind.ButtonDown, injected));
    }

    [Theory]
    [InlineData(WmKeyDown, LowLevelInputKind.KeyDown)]
    [InlineData(WmSysKeyDown, LowLevelInputKind.KeyDown)]
    [InlineData(WmKeyUp, LowLevelInputKind.KeyUp)]
    [InlineData(WmSysKeyUp, LowLevelInputKind.KeyUp)]
    [InlineData(0x0102u, LowLevelInputKind.Other)] // WM_CHAR is not a low-level keyboard message
    public void ReadKeyboard_TellsTheKindFromTheMessage(uint message, LowLevelInputKind kind)
    {
        Over(Key(0, noisy: true), data => NativeLowLevelInputWrapper.ReadKeyboard((nint)message, data)).Kind.Should().Be(kind);
    }

    [Theory]
    [InlineData(WmMouseMove, LowLevelInputKind.Move)]
    [InlineData(WmLButtonDown, LowLevelInputKind.ButtonDown)]
    [InlineData(WmRButtonDown, LowLevelInputKind.ButtonDown)]
    [InlineData(WmXButtonDown, LowLevelInputKind.ButtonDown)]
    [InlineData(WmLButtonUp, LowLevelInputKind.ButtonUp)]
    [InlineData(WmMouseWheel, LowLevelInputKind.Wheel)]
    [InlineData(WmMouseHWheel, LowLevelInputKind.Wheel)]
    [InlineData(0x0203u, LowLevelInputKind.Other)] // WM_LBUTTONDBLCLK never reaches a low-level hook
    public void ReadMouse_TellsTheKindFromTheMessage(uint message, LowLevelInputKind kind)
    {
        Over(Mouse(0, noisy: true), data => NativeLowLevelInputWrapper.ReadMouse((nint)message, data)).Kind.Should().Be(kind);
    }

    [Fact]
    public void ANullStruct_IsNotRead_AndCountsAsTheUsersInput()
    {
        // Windows never passes one; reading it would end the process, and input that cannot be told apart counts as the user's
        NativeLowLevelInputWrapper.ReadKeyboard((nint)WmKeyDown, nint.Zero).Should().Be(new LowLevelInput(LowLevelInputKind.KeyDown, false));
        NativeLowLevelInputWrapper.ReadMouse((nint)WmMouseWheel, nint.Zero).Should().Be(new LowLevelInput(LowLevelInputKind.Wheel, false));
    }
}
