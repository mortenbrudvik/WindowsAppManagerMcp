namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// InputBatchBuilder (roadmap M4.2): the one SendInput batch of a mouse operation. The normalized coordinates of its move
/// are checked against the mappings Windows may apply, not against the builder's own arithmetic; its buttons against the
/// physical buttons Windows swaps.
/// </summary>
public class InputBatchBuilderTests
{
    // Win32 mouse flags (NativeEnums is internal to the server assembly)
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, MiddleDown = 0x0020, MiddleUp = 0x0040;
    private const uint AbsoluteMove = 0x0001 | 0x4000 | 0x8000;
    private const uint XDown = 0x0080, XUp = 0x0100;
    private const uint KeyDown = 0x0000, ExtendedKey = 0x0001, KeyUp = 0x0002;
    private const ushort VkLShift = 0xA0, VkLControl = 0xA2, VkLMenu = 0xA4, VkLWin = 0x5B;

    private static readonly VirtualScreenBounds OneMonitor = new(0, 0, 1920, 1080);

    /// <summary>What Windows does with n (as observed, and as ReactOS implements it): floor(n * size / 65536).</summary>
    private static long Floor(int n, int size) => (long)n * size / 65536;

    /// <summary>The alternative that rounds n * size / 65536 to the nearest pixel.</summary>
    private static long Nearest(int n, int size) => (long)Math.Floor((double)n * size / 65536 + 0.5);

    /// <summary>The alternative that reads "65535 maps onto the lower-right corner" literally.</summary>
    private static long LastPixel(int n, int size) => (long)Math.Floor((double)n * (size - 1) / 65535 + 0.5);

    #region The normalized coordinates

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(768)]
    [InlineData(1024)] // a power of two: the middle of a pixel's floor range is exactly a rounding boundary
    [InlineData(1080)]
    [InlineData(1366)]
    [InlineData(1440)]
    [InlineData(1920)]
    [InlineData(2160)]
    [InlineData(2560)]
    [InlineData(3600)]
    [InlineData(3840)]
    [InlineData(5760)]
    [InlineData(6400)]
    [InlineData(7680)]
    [InlineData(15360)]
    [InlineData(32768)]
    public void Normalize_EveryPixel_ComesBackExactly_UnderEveryMapping(int size)
    {
        var misses = new List<string>();
        for (var pixel = 0; pixel < size; pixel++)
        {
            var n = InputBatchBuilder.Normalize(pixel, size);
            if (n is < 0 or > 65535 || Floor(n, size) != pixel || Nearest(n, size) != pixel || LastPixel(n, size) != pixel)
            {
                misses.Add($"pixel {pixel}: n {n} maps to {Floor(n, size)} (floor), {Nearest(n, size)} (nearest), {LastPixel(n, size)} (last pixel)");
            }
        }

        misses.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1920)]
    [InlineData(1080)]
    [InlineData(1)]
    public void Normalize_TheFirstAndLastPixels_ReachTheEnds(int size)
    {
        Floor(InputBatchBuilder.Normalize(0, size), size).Should().Be(0);
        Floor(InputBatchBuilder.Normalize(size - 1, size), size).Should().Be(size - 1, "the last column or row is reachable");
    }

    [Fact]
    public void Normalize_OffTheScreen_GoesToTheNearestEdge()
    {
        InputBatchBuilder.Normalize(-1, 1920).Should().Be(0);
        InputBatchBuilder.Normalize(-50_000, 1920).Should().Be(0);
        InputBatchBuilder.Normalize(1920, 1920).Should().Be(65535);
        InputBatchBuilder.Normalize(int.MaxValue, 1920).Should().Be(65535);
    }

    [Fact]
    public void Normalize_PastTheSizeEveryPixelCanHave_StillMapsBackUnderFloor()
    {
        // 40000 > 32768: not every pixel has an n that all three mappings send back to it; then the floor mapping's middle is used
        const int Size = 40_000;
        Enumerable.Range(0, Size).Where(pixel => Floor(InputBatchBuilder.Normalize(pixel, Size), Size) != pixel).Should().BeEmpty();
    }

    /// <summary>
    /// The virtual screen of a mixed-DPI desktop, in physical pixels (what a per-monitor aware process reads): the primary at
    /// 150% (3840x2160), a 100% monitor left of it at a negative x, and a 125% monitor above it at a negative y. The scales do
    /// not enter the mapping: Windows maps the normalized move across the physical virtual screen.
    /// </summary>
    private static readonly MonitorInfo[] MixedDpiMonitors =
    [
        TestDataFactory.CreateMonitorInfo(0, @"\\.\DISPLAY1", true, 0, 0, 3840, 2160, scaleFactor: 1.5),
        TestDataFactory.CreateMonitorInfo(1, @"\\.\DISPLAY2", false, -1920, 300, 1920, 1080, scaleFactor: 1.0),
        TestDataFactory.CreateMonitorInfo(2, @"\\.\DISPLAY3", false, 640, -1440, 2560, 1440, scaleFactor: 1.25)
    ];

    [Fact]
    public void VirtualScreenOf_IsTheMonitorsBoundingRectangle_WithANegativeOrigin()
    {
        InputBatchBuilder.VirtualScreenOf(MixedDpiMonitors).Should().Be(new VirtualScreenBounds(-1920, -1440, 5760, 3600));
    }

    [Fact]
    public void ToAbsolute_OnAMixedDpiDesktopWithNegativeOrigin_HitsEveryCornerAndCentreOfEveryMonitor()
    {
        var screen = InputBatchBuilder.VirtualScreenOf(MixedDpiMonitors);
        foreach (var monitor in MixedDpiMonitors)
        {
            var b = monitor.Bounds;
            foreach (var (x, y) in new[]
                     {
                         (b.X, b.Y), (b.X + b.Width - 1, b.Y), (b.X, b.Y + b.Height - 1), (b.X + b.Width - 1, b.Y + b.Height - 1),
                         (b.X + b.Width / 2, b.Y + b.Height / 2)
                     })
            {
                var (dx, dy) = InputBatchBuilder.ToAbsolute(x, y, screen);
                (screen.X + Floor(dx, screen.Width)).Should().Be(x, $"x of ({x}, {y}) on {monitor.DeviceName}");
                (screen.Y + Floor(dy, screen.Height)).Should().Be(y, $"y of ({x}, {y}) on {monitor.DeviceName}");
            }
        }
    }

    [Fact]
    public void ToAbsolute_ManyPointsOfAMixedDpiDesktop_RoundTripToTheSamePixel()
    {
        var screen = InputBatchBuilder.VirtualScreenOf(MixedDpiMonitors);
        var random = new Random(4242);
        var misses = new List<string>();
        for (var i = 0; i < 20_000; i++)
        {
            var monitor = MixedDpiMonitors[random.Next(MixedDpiMonitors.Length)].Bounds;
            var x = monitor.X + random.Next(monitor.Width);
            var y = monitor.Y + random.Next(monitor.Height);

            var (dx, dy) = InputBatchBuilder.ToAbsolute(x, y, screen);

            var back = (screen.X + Floor(dx, screen.Width), screen.Y + Floor(dy, screen.Height));
            if (back != (x, y))
            {
                misses.Add($"({x}, {y}) comes back as {back}");
            }
        }

        misses.Should().BeEmpty();
    }

    [Fact]
    public void MoveTo_IsOneAbsoluteMoveAcrossTheVirtualDesktop()
    {
        var screen = new VirtualScreenBounds(-1920, 0, 3840, 1080);

        var batch = new InputBatchBuilder(screen, buttonsSwapped: false).MoveTo(-1, 1079).Build();

        var move = batch.Events.Should().ContainSingle().Subject;
        move.Kind.Should().Be(InputEventKind.Mouse);
        move.Flags.Should().Be(AbsoluteMove);
        (screen.X + Floor(move.Dx, screen.Width), screen.Y + Floor(move.Dy, screen.Height)).Should().Be((-1, 1079));
        batch.AsUnswapped.Should().Equal(batch.Events);
    }

    [Fact]
    public void TheVirtualScreen_MustHaveASize()
    {
        var act = () => new InputBatchBuilder(new VirtualScreenBounds(0, 0, 0, 1080), buttonsSwapped: false);

        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Buttons

    [Theory]
    [InlineData(MouseButton.Primary, false, LeftDown, LeftUp)]
    [InlineData(MouseButton.Primary, true, RightDown, RightUp)]
    [InlineData(MouseButton.Secondary, false, RightDown, RightUp)]
    [InlineData(MouseButton.Secondary, true, LeftDown, LeftUp)]
    [InlineData(MouseButton.Middle, false, MiddleDown, MiddleUp)]
    [InlineData(MouseButton.Middle, true, MiddleDown, MiddleUp)]
    public void Click_SendsThePhysicalButton_WindowsSwapsBackToTheUsersButton(MouseButton button, bool swapped, uint down, uint up)
    {
        var batch = new InputBatchBuilder(OneMonitor, swapped).MoveTo(500, 300).Click(button).Build();

        batch.Events.Skip(1).Should().Equal(InputEvent.Mouse(down), InputEvent.Mouse(up));
    }

    [Theory]
    [InlineData(MouseButton.Primary, LeftDown, LeftUp)]
    [InlineData(MouseButton.Secondary, RightDown, RightUp)]
    [InlineData(MouseButton.Middle, MiddleDown, MiddleUp)]
    public void AsUnswapped_NamesTheUsersButton_WhetherOrNotTheButtonsAreSwapped(MouseButton button, uint down, uint up)
    {
        var swapped = new InputBatchBuilder(OneMonitor, buttonsSwapped: true).MoveTo(500, 300).Click(button).Build();
        var unswapped = new InputBatchBuilder(OneMonitor, buttonsSwapped: false).MoveTo(500, 300).Click(button).Build();

        swapped.AsUnswapped.Skip(1).Should().Equal(InputEvent.Mouse(down), InputEvent.Mouse(up));
        swapped.AsUnswapped.Should().Equal(unswapped.Events, "the clipboard rules see the same batch either way");
        swapped.AsUnswapped[0].Should().Be(swapped.Events[0], "the move is the same");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Click_SeveralTimes_PressesAtTheSamePoint_WithNothingInBetween(int count)
    {
        var batch = new InputBatchBuilder(OneMonitor, buttonsSwapped: false).MoveTo(600, 350).Click(MouseButton.Primary, count).Build();

        // One move, then the presses: no move between them, so every press is within SM_CXDOUBLECLK of the first, and one
        // SendInput call gives them the same time stamp, within any double-click time
        batch.Events.Should().HaveCount(1 + 2 * count);
        batch.Events[0].Flags.Should().Be(AbsoluteMove);
        batch.Events.Skip(1).Should().Equal(Enumerable.Range(0, count).SelectMany(_ => new[] { InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp) }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void Click_CountsOneToThree(int count)
    {
        var act = () => new InputBatchBuilder(OneMonitor, buttonsSwapped: false).Click(MouseButton.Primary, count);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PressAndRelease_CanBeSentApart_ForADrag()
    {
        // M4.3's drag: the button goes down at one point and up at another, in the same batch
        var batch = new InputBatchBuilder(OneMonitor, buttonsSwapped: true)
            .MoveTo(10, 10).Press(MouseButton.Primary).MoveTo(200, 10).Release(MouseButton.Primary).Build();

        batch.Events.Select(e => e.Flags).Should().Equal(AbsoluteMove, RightDown, AbsoluteMove, RightUp);
        batch.AsUnswapped.Select(e => e.Flags).Should().Equal(AbsoluteMove, LeftDown, AbsoluteMove, LeftUp);
    }

    [Theory]
    [InlineData(MouseButton.X1, 1u)]
    [InlineData(MouseButton.X2, 2u)]
    public void Click_XButton_SendsXDownAndXUp_NamingTheButtonInMouseData(MouseButton button, uint xButton)
    {
        // Swapping does not change the X buttons (roadmap M4.3)
        foreach (var swapped in new[] { false, true })
        {
            var batch = new InputBatchBuilder(OneMonitor, swapped).MoveTo(500, 300).Click(button, 2).Build();

            batch.Events.Skip(1).Should().Equal(InputEvent.Mouse(XDown, xButton), InputEvent.Mouse(XUp, xButton),
                InputEvent.Mouse(XDown, xButton), InputEvent.Mouse(XUp, xButton));
            batch.AsUnswapped.Should().Equal(batch.Events);
        }
    }

    [Fact]
    public void Modifiers_GoDownBeforeTheMouseEvents_AndUpAfterThem_LastPressedFirst()
    {
        var batch = new InputBatchBuilder(OneMonitor, buttonsSwapped: false)
            .ModifiersDown([MouseModifier.Ctrl, MouseModifier.Shift]).MoveTo(500, 300).Click(MouseButton.Primary)
            .ModifiersUp([MouseModifier.Ctrl, MouseModifier.Shift]).Build();

        batch.Events.Should().Equal(
            InputEvent.Key(VkLControl, KeyDown), InputEvent.Key(VkLShift, KeyDown),
            batch.Events[2], InputEvent.Mouse(LeftDown), InputEvent.Mouse(LeftUp),
            InputEvent.Key(VkLShift, KeyUp), InputEvent.Key(VkLControl, KeyUp));
        batch.Events[2].Flags.Should().Be(AbsoluteMove);
        batch.AsUnswapped.Should().Equal(batch.Events);
    }

    [Theory]
    [InlineData(MouseModifier.Alt, VkLMenu, 0u)]
    [InlineData(MouseModifier.Win, VkLWin, ExtendedKey)]
    public void ModifiersUp_AltOrWin_IsMaskedSoNoMenuOpens(MouseModifier modifier, ushort key, uint extended)
    {
        // Alt or Windows going up with no key pressed since it went down opens the menu bar or the Start menu; the
        // unassigned masking key in between stops that, as the session's releases do
        var batch = new InputBatchBuilder(OneMonitor, buttonsSwapped: false)
            .ModifiersDown([modifier]).MoveTo(5, 5).Click(MouseButton.Primary).ModifiersUp([modifier]).Build();

        batch.Events[0].Should().Be(InputEvent.Key(key, KeyDown | extended));
        batch.Events.TakeLast(3).Should().Equal(
            InputEvent.Key(HeldInputTracker.MaskKey, KeyDown), InputEvent.Key(HeldInputTracker.MaskKey, KeyUp),
            InputEvent.Key(key, KeyUp | extended));
    }

    [Fact]
    public void Modifiers_NamedTwice_GoDownOnce()
    {
        var batch = new InputBatchBuilder(OneMonitor, buttonsSwapped: false)
            .ModifiersDown([MouseModifier.Ctrl, MouseModifier.Ctrl]).ModifiersUp([MouseModifier.Ctrl, MouseModifier.Ctrl]).Build();

        batch.Events.Should().Equal(InputEvent.Key(VkLControl, KeyDown), InputEvent.Key(VkLControl, KeyUp));
    }

    [Fact]
    public void UpFlag_IsTheDownFlagsRelease()
    {
        foreach (var button in Enum.GetValues<MouseButton>())
        {
            foreach (var swapped in new[] { false, true })
            {
                InputBatchBuilder.UpFlag(button, swapped).Should().Be(InputBatchBuilder.DownFlag(button, swapped) << 1);
            }
        }
    }

    #endregion

    #region Wheel

    [Fact]
    public void Wheel_OneEventPerNotch_UpIsPositive_AndHorizontalComesAfterVertical()
    {
        var batch = new InputBatchBuilder(OneMonitor, buttonsSwapped: false).MoveTo(10, 20).Wheel(deltaY: -2, deltaX: 3).Build();

        batch.Events.Skip(1).Should().Equal(
            InputEvent.Wheel(120), InputEvent.Wheel(120),
            InputEvent.HorizontalWheel(120), InputEvent.HorizontalWheel(120), InputEvent.HorizontalWheel(120));
        new InputBatchBuilder(OneMonitor, buttonsSwapped: false).Wheel(0, -1).Build().Events.Should().Equal(InputEvent.HorizontalWheel(-120));
        new InputBatchBuilder(OneMonitor, buttonsSwapped: false).Wheel(3, 0).Build().Events.Should().Equal(
            InputEvent.Wheel(-120), InputEvent.Wheel(-120), InputEvent.Wheel(-120));
    }

    #endregion
}
