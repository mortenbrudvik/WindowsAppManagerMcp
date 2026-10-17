using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Layout schema version 2 (roadmap M6.8, owner decision D15): a version 1 preset is upgraded in memory, a version 2 file
/// keeps every version 1 field filled for older servers, and the placements an agent writes are checked strictly.
/// </summary>
public class LayoutPresetSchemaTests
{
    private static readonly IReadOnlyList<MonitorInfo> Monitors =
    [
        new MonitorInfo(0, @"\\.\DISPLAY1", true, new MonitorRect(0, 0, 1920, 1080), new MonitorRect(0, 0, 1920, 1040), 1.0)
        {
            Id = "m-11111111", FriendlyName = "DELL U2720Q"
        },
        new MonitorInfo(1, @"\\.\DISPLAY2", false, new MonitorRect(1920, 0, 2560, 1440), new MonitorRect(1920, 0, 2560, 1400), 1.25)
        {
            Id = "m-22222222", FriendlyName = "LG HDR 4K"
        }
    ];

    private static WindowPlacement V1(string? launchCommand) =>
        new(new WindowMatcher(ProcessName: "Code"), 0, new RelativePosition(0, 0, 0.5, 1), launchCommand);

    #region Upgrade and the version 1 fields

    [Fact]
    public void Upgrade_OfAVersion1Preset_MakesItsLaunchCommandTheLaunchExecutable()
    {
        var preset = new LayoutPreset("p", null, [V1("code")]);

        var upgraded = LayoutPresetSchema.Upgrade(preset);

        upgraded.Version.Should().Be(LayoutPresetSchema.CurrentVersion);
        var placement = upgraded.Placements.Single();
        placement.Launch.Should().Be(new LayoutLaunch(Executable: "code"));
        placement.LaunchCommand.Should().Be("code", "the version 1 field stays as it was");
        placement.Monitor.Should().BeNull("a version 1 placement has its index alone");
    }

    [Fact]
    public void Upgrade_OfAnAppsFolderLaunchCommand_MakesItTheLaunchAppId()
    {
        var preset = new LayoutPreset("p", null, [V1(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]);

        LayoutPresetSchema.Upgrade(preset).Placements.Single().Launch.Should()
            .Be(new LayoutLaunch(AppId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
    }

    [Fact]
    public void Upgrade_LeavesAPlacementWithoutALaunchCommand_WithoutLaunch()
    {
        LayoutPresetSchema.Upgrade(new LayoutPreset("p", null, [V1(null)])).Placements.Single().Launch.Should().BeNull();
    }

    [Fact]
    public void Upgrade_KeepsTheVersionOfAPresetANewerServerWrote()
    {
        var preset = new LayoutPreset("p", null, [V1(null)]) { Version = 3 };

        LayoutPresetSchema.Upgrade(preset).Version.Should().Be(3);
        LayoutPresetSchema.IsNewer(preset).Should().BeTrue();
        LayoutPresetSchema.IsNewer(preset with { Version = 2 }).Should().BeFalse();
    }

    [Fact]
    public void ForWriting_WritesVersion2_AndFillsTheVersion1LaunchCommand()
    {
        var preset = new LayoutPreset("p", null,
        [
            V1(null) with { Launch = new LayoutLaunch(Executable: @"C:\Tools\app.exe") },
            V1(null) with { Launch = new LayoutLaunch(AppId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") },
            V1("kept") with { Launch = new LayoutLaunch(Executable: "other") }
        ]);

        var written = LayoutPresetSchema.ForWriting(preset);

        written.Version.Should().Be(2);
        written.Placements.Select(p => p.LaunchCommand).Should().Equal(
            @"C:\Tools\app.exe", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "kept");
    }

    /// <summary>The shape a version 1 server reads a preset into (Models/LayoutPreset.cs before roadmap M6.8).</summary>
    private sealed record V1Preset(string Name, string? Description, List<V1Placement> Placements);

    private sealed record V1Placement(V1Matcher Matcher, int MonitorIndex, RelativePosition Position, string? LaunchCommand, bool Focus, int Order);

    private sealed record V1Matcher(string? TitleContains, string? ProcessName, string? ClassName);

    [Fact]
    public void AVersion2File_ReadsAsAVersion1Preset_WithEveryVersion1Field()
    {
        // An older plugin version, or another one running alongside, reads the file with its own records and ignores the rest
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        LayoutPresetSchema.TryBuildPlacement(
            new LayoutPlacementSpec(ProcessName: "Code", Snap: "right_half", Monitor: MonitorSelector.Of("left"),
                Launch: new LayoutLaunch(Executable: "code"), Focus: true), 0, Monitors, out var placement, out var error).Should().BeTrue(error);
        var json = JsonSerializer.Serialize(LayoutPresetSchema.ForWriting(new LayoutPreset("p", "d", [placement!])), options);

        var old = JsonSerializer.Deserialize<V1Preset>(json, options)!;

        var read = old.Placements.Single();
        read.Matcher.ProcessName.Should().Be("Code");
        read.MonitorIndex.Should().Be(0);
        read.Position.Should().Be(new RelativePosition(0.5, 0, 0.5, 1));
        read.LaunchCommand.Should().Be("code");
        read.Focus.Should().BeTrue();
    }

    [Fact]
    public void LaunchTarget_IsTheAppsFolderEntryOfAnAppId_ElseTheExecutable_ElseTheLaunchCommand()
    {
        LayoutPresetSchema.LaunchTarget(V1(null) with { Launch = new LayoutLaunch(AppId: "MSEdge") }).Should().Be(@"shell:AppsFolder\MSEdge");
        LayoutPresetSchema.LaunchTarget(V1(null) with { Launch = new LayoutLaunch(Executable: "notepad") }).Should().Be("notepad");
        LayoutPresetSchema.LaunchTarget(V1("calc")).Should().Be("calc");
        LayoutPresetSchema.LaunchTarget(V1(null)).Should().BeNull();
    }

    #endregion

    #region Agent-written placements

    public static TheoryData<string, LayoutPlacementSpec, string> InvalidPlacements() => new()
    {
        { "no matcher", new LayoutPlacementSpec(Snap: "left_half"), "processName or titleContains" },
        { "snap and position", new LayoutPlacementSpec("Code", Snap: "left_half", Position: new RelativePosition(0, 0, 1, 1)), "snap or position" },
        { "neither snap nor position", new LayoutPlacementSpec("Code"), "snap or position" },
        { "maximized with snap", new LayoutPlacementSpec("Code", Snap: "left_half", State: "maximized"), "maximized" },
        { "unknown snap", new LayoutPlacementSpec("Code", Snap: "middle"), "snap" },
        { "unknown state", new LayoutPlacementSpec("Code", Snap: "left_half", State: "floating"), "state" },
        { "focus with minimized", new LayoutPlacementSpec("Code", Snap: "left_half", State: "minimized", Focus: true), "focus" },
        { "x past the work area", new LayoutPlacementSpec("Code", Position: new RelativePosition(1.5, 0, 0.5, 0.5)), "position" },
        { "zero width", new LayoutPlacementSpec("Code", Position: new RelativePosition(0, 0, 0, 0.5)), "position" },
        { "negative y", new LayoutPlacementSpec("Code", Position: new RelativePosition(0, -0.1, 0.5, 0.5)), "position" },
        {
            "normalPosition of a normal window",
            new LayoutPlacementSpec("Code", Position: new RelativePosition(0, 0, 0.5, 1), NormalPosition: new RelativePosition(0, 0, 0.5, 1)),
            "normalPosition"
        },
        {
            "normalPosition out of range",
            new LayoutPlacementSpec("Code", State: "maximized", NormalPosition: new RelativePosition(0, 0, 2, 1)), "normalPosition"
        },
        { "monitor current", new LayoutPlacementSpec("Code", Snap: "left_half", Monitor: MonitorSelector.Of("current")), "current" },
        { "monitor all", new LayoutPlacementSpec("Code", Snap: "left_half", Monitor: MonitorSelector.Of("all")), "all" },
        { "monitor -1", new LayoutPlacementSpec("Code", Snap: "left_half", Monitor: MonitorSelector.OfIndex(-1)), "monitor" },
        { "monitor no name", new LayoutPlacementSpec("Code", Snap: "left_half", Monitor: MonitorSelector.Of("middle")), "monitor" },
        {
            "launch with both",
            new LayoutPlacementSpec("Code", Snap: "left_half", Launch: new LayoutLaunch("code", "Microsoft.VisualStudioCode_8wekyb3d8bbwe!App")),
            "launch"
        },
        { "launch with neither", new LayoutPlacementSpec("Code", Snap: "left_half", Launch: new LayoutLaunch()), "launch" },
        { "a matcher of spaces", new LayoutPlacementSpec("  ", Snap: "left_half"), "processName or titleContains" },
    };

    [Theory]
    [MemberData(nameof(InvalidPlacements))]
    public void AnInvalidPlacement_IsRefused_WithItsIndex(string why, LayoutPlacementSpec spec, string expected)
    {
        LayoutPresetSchema.TryBuildPlacement(spec, 3, Monitors, out var placement, out var error).Should().BeFalse(why);

        placement.Should().BeNull();
        error.Should().StartWith("placements[3]").And.Contain(expected, why);
    }

    [Fact]
    public void ASnap_IsKept_AndItsRectangleOnTheMonitorIsThePosition_ForOlderServers()
    {
        var spec = new LayoutPlacementSpec("Code", Snap: "left_half", Monitor: MonitorSelector.Of("right"), Focus: true);

        LayoutPresetSchema.TryBuildPlacement(spec, 2, Monitors, out var placement, out var error).Should().BeTrue(error);

        placement!.Snap.Should().Be("left_half");
        placement.Monitor.Should().Be("right", "a keyword is resolved when the layout is applied");
        placement.MonitorIndex.Should().Be(1, "where it is now, for older servers");
        placement.Position.Should().Be(new RelativePosition(0, 0, 0.5, 1));
        placement.Focus.Should().BeTrue();
        placement.Order.Should().Be(2);
        placement.Matcher.Should().Be(new WindowMatcher(ProcessName: "Code"));
    }

    [Fact]
    public void ASnapAlias_IsWrittenByItsName()
    {
        LayoutPresetSchema.TryBuildPlacement(new LayoutPlacementSpec("Code", Snap: "FULL"), 0, Monitors, out var placement, out var error)
            .Should().BeTrue(error);

        placement!.Snap.Should().Be("fullscreen");
    }

    [Fact]
    public void AMaximizedPlacement_NeedsNoPosition_AndFillsTheWorkArea()
    {
        var spec = new LayoutPlacementSpec(TitleContains: "Inbox", State: "maximize", NormalPosition: new RelativePosition(0.1, 0.1, 0.6, 0.7));

        LayoutPresetSchema.TryBuildPlacement(spec, 0, Monitors, out var placement, out var error).Should().BeTrue(error);

        placement!.State.Should().Be(WindowState.Maximized);
        placement.Position.Should().Be(new RelativePosition(0, 0, 1, 1));
        placement.NormalPosition.Should().Be(new RelativePosition(0.1, 0.1, 0.6, 0.7));
    }

    [Fact]
    public void WithoutAMonitor_ThePlacementIsOnThePrimary()
    {
        LayoutPresetSchema.TryBuildPlacement(new LayoutPlacementSpec("Code", Snap: "left_half"), 0, Monitors, out var placement, out _)
            .Should().BeTrue();

        placement!.Monitor.Should().Be("primary");
        placement.MonitorIndex.Should().Be(0);
    }

    [Fact]
    public void AnIndex_IsTheMonitorIndexAlone()
    {
        LayoutPresetSchema.TryBuildPlacement(new LayoutPlacementSpec("Code", Snap: "left_half", Monitor: MonitorSelector.OfIndex(1)), 0,
            Monitors, out var placement, out _).Should().BeTrue();

        placement!.Monitor.Should().BeNull();
        placement.MonitorIndex.Should().Be(1);
    }

    [Fact]
    public void AnIdOfAMonitorThatIsNotConnectedNow_IsKept_ForWhenItIs()
    {
        // get_layout's output of a layout saved while docked goes back in while undocked
        var spec = new LayoutPlacementSpec("Code", Position: new RelativePosition(0, 0, 0.5, 1), Monitor: MonitorSelector.Of("m-99999999"),
            MonitorName: "BenQ GW2780");

        LayoutPresetSchema.TryBuildPlacement(spec, 0, Monitors, out var placement, out var error).Should().BeTrue(error);

        placement!.Monitor.Should().Be("m-99999999");
        placement.MonitorName.Should().Be("BenQ GW2780");
        placement.MonitorIndex.Should().Be(0, "the primary's index is the last resort");
    }

    [Fact]
    public void AnIdOfAConnectedMonitor_KeepsItsIndex_ForOlderServers()
    {
        LayoutPresetSchema.TryBuildPlacement(new LayoutPlacementSpec("Code", Snap: "left_half", Monitor: MonitorSelector.Of("M-22222222")), 0,
            Monitors, out var placement, out _).Should().BeTrue();

        placement!.Monitor.Should().Be("m-22222222");
        placement.MonitorIndex.Should().Be(1);
    }

    [Fact]
    public void ADeviceName_IsKept()
    {
        LayoutPresetSchema.TryBuildPlacement(new LayoutPlacementSpec("Code", Snap: "left_half", Monitor: MonitorSelector.Of(@"\\.\DISPLAY2")), 0,
            Monitors, out var placement, out _).Should().BeTrue();

        placement!.Monitor.Should().Be(@"\\.\DISPLAY2");
        placement.MonitorIndex.Should().Be(1);
    }

    [Fact]
    public void ALaunchAndTheRecordedIdentity_AreKept()
    {
        var spec = new LayoutPlacementSpec("CalculatorApp", Snap: "right_third", Launch: new LayoutLaunch(AppId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
            ExactProcessName: true, ClassName: "ApplicationFrameWindow", TitlePattern: "Calculator", ExePath: @"C:\Program Files\WindowsApps\calc.exe",
            Ordinal: 1, State: "minimized", NormalPosition: new RelativePosition(0.5, 0.1, 0.3, 0.5));

        LayoutPresetSchema.TryBuildPlacement(spec, 0, Monitors, out var placement, out var error).Should().BeTrue(error);

        placement!.Launch.Should().Be(new LayoutLaunch(AppId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"));
        placement.LaunchCommand.Should().Be(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");
        placement.Matcher.Should().Be(new WindowMatcher(ProcessName: "CalculatorApp", ClassName: "ApplicationFrameWindow")
        {
            ExactProcessName = true, TitlePattern = "Calculator", ExePath = @"C:\Program Files\WindowsApps\calc.exe", Ordinal = 1
        });
        placement.State.Should().Be(WindowState.Minimized);
        placement.NormalPosition.Should().Be(new RelativePosition(0.5, 0.1, 0.3, 0.5));
    }

    #endregion

    #region Review M6.8: strictness, limits, fingerprints and the author

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static LayoutPlacementSpec Valid => new("Code", Snap: "left_half");

    public static TheoryData<string, LayoutPlacementSpec, string> MoreInvalidPlacements() => new()
    {
        { "an unknown member", Valid with { UnknownMembers = new() { ["windowClass"] = Json("x") } }, "placements[3].windowClass" },
        {
            "launch arguments",
            Valid with { Launch = new LayoutLaunch("code.exe") { UnknownMembers = new() { ["arguments"] = Json(new[] { "--new-window" }) } } },
            "placements[3].launch.arguments"
        },
        { "an order that is not its place", Valid with { UnknownMembers = new() { ["order"] = Json(7) } }, "order" },
        { "a launchCommand of its own", Valid with { UnknownMembers = new() { ["launchCommand"] = Json("cmd.exe /c x") } }, "launchCommand" },
        { "monitorIndex without monitor", Valid with { UnknownMembers = new() { ["monitorIndex"] = Json(1) } }, "use monitor" },
        {
            "monitorIndex against an index monitor",
            Valid with { Monitor = MonitorSelector.OfIndex(0), UnknownMembers = new() { ["monitorIndex"] = Json(1) } }, "monitorIndex"
        },
        { "x + width past 1", new LayoutPlacementSpec("Code", Position: new RelativePosition(0.8, 0, 0.5, 1)), "at most 1" },
        { "y + height past 1", new LayoutPlacementSpec("Code", Position: new RelativePosition(0, 0.5, 0.5, 0.6)), "at most 1" },
        {
            "normalPosition past the edge",
            new LayoutPlacementSpec("Code", State: "maximized", NormalPosition: new RelativePosition(0.7, 0, 0.5, 1)), "normalPosition"
        },
        { "a long processName", new LayoutPlacementSpec(new string('p', 261), Snap: "left_half"), "processName" },
        { "a long titlePattern", Valid with { TitlePattern = new string('t', 261) }, "titlePattern" },
        { "a long className", Valid with { ClassName = new string('c', 261) }, "className" },
        { "a long exePath", Valid with { ExePath = @"C:\" + new string('e', 1022) }, "exePath" },
        { "a large ordinal", Valid with { Ordinal = 1000 }, "ordinal" },
        { "a negative ordinal", Valid with { Ordinal = -1 }, "ordinal" },
        { "a long executable", Valid with { Launch = new LayoutLaunch(@"C:\" + new string('x', 258) + ".exe") }, "260" },
        { "a long appId", Valid with { Launch = new LayoutLaunch(AppId: new string('a', 261)) }, "260" },
    };

    [Theory]
    [MemberData(nameof(MoreInvalidPlacements))]
    public void AnotherInvalidPlacement_IsRefused_WithItsIndex(string why, LayoutPlacementSpec spec, string expected)
    {
        LayoutPresetSchema.TryBuildPlacement(spec, 3, Monitors, out var placement, out var error).Should().BeFalse(why);

        placement.Should().BeNull();
        error.Should().StartWith("placements[3]").And.Contain(expected, why);
    }

    [Fact]
    public void GetLayoutsOwnFields_PassWhenTheyAgree()
    {
        // An unchanged get_layout placement carries order, monitorIndex and launchCommand
        var spec = new LayoutPlacementSpec("Code", Position: new RelativePosition(0, 0, 0.5, 1), Monitor: MonitorSelector.Of("m-99999999"),
            Launch: new LayoutLaunch(AppId: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"))
        {
            UnknownMembers = new()
            {
                ["order"] = Json(2),
                ["monitorIndex"] = Json(1),
                ["launchCommand"] = Json(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")
            }
        };

        LayoutPresetSchema.TryBuildPlacement(spec, 2, Monitors, out var placement, out var error).Should().BeTrue(error);

        placement!.MonitorIndex.Should().Be(1, "the saved index of last resort is kept for a monitor that is not connected");
    }

    [Fact]
    public void ARectangleWithinTheInvisibleBorders_Passes()
    {
        // A window snapped to the right half reaches about 7 px past the work area with its invisible border
        LayoutPresetSchema.TryBuildPlacement(new LayoutPlacementSpec("Code", Position: new RelativePosition(0.4964, 0, 0.5073, 1)), 0, Monitors,
            out _, out var error).Should().BeTrue(error);
    }

    [Fact]
    public void InsideWorkArea_MovesARectangleThatReachesPastTheEdgeBack_AndKeepsTheBorders()
    {
        LayoutPresetSchema.InsideWorkArea(new RelativePosition(0.8, 0.7, 0.5, 0.5)).Should().Be(new RelativePosition(0.5, 0.5, 0.5, 0.5));
        LayoutPresetSchema.InsideWorkArea(new RelativePosition(0.4964, 0, 0.5073, 1)).Should().Be(new RelativePosition(0.4964, 0, 0.5073, 1));
    }

    private static LayoutPreset Agent(params string[] executables) =>
        new("desk", null, executables.Select((e, i) => V1(null) with { Order = i, Launch = new LayoutLaunch(Executable: e) }).ToList())
        {
            AuthoredBy = LayoutPresetSchema.AgentAuthor,
            UpdatedAt = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc)
        };

    [Fact]
    public void TheFingerprint_ChangesWithTheName_TheSaveTime_AndEachTarget()
    {
        var preset = Agent("a.exe", "b.exe");
        var fingerprint = LayoutPresetSchema.LaunchFingerprint(preset);

        LayoutPresetSchema.LaunchFingerprint(Agent("a.exe", "b.exe")).Should().Be(fingerprint, "the same version");
        LayoutPresetSchema.LaunchFingerprint(preset with { Name = "desk2" }).Should().NotBe(fingerprint);
        LayoutPresetSchema.LaunchFingerprint(preset with { UpdatedAt = preset.UpdatedAt.AddTicks(1) }).Should().NotBe(fingerprint);
        LayoutPresetSchema.LaunchFingerprint(Agent("a.exe", "c.exe")).Should().NotBe(fingerprint);
        LayoutPresetSchema.LaunchFingerprint(Agent("b.exe", "a.exe")).Should().NotBe(fingerprint);

        // The same text as an executable and as an app starts different things
        var asApp = preset with { Placements = [preset.Placements[0] with { Launch = new LayoutLaunch(AppId: "a.exe") }, preset.Placements[1]] };
        LayoutPresetSchema.LaunchFingerprint(asApp).Should().NotBe(fingerprint);
    }

    [Theory]
    [InlineData("agent", true)]
    [InlineData("Agent", true)]
    [InlineData(" agent ", true)]
    [InlineData("agnet", true)]
    [InlineData("user", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void AnyAuthor_MakesAPresetAgentWritten(string? authoredBy, bool agent)
    {
        LayoutPresetSchema.IsAgentAuthored(Agent("a.exe") with { AuthoredBy = authoredBy }).Should().Be(agent);
    }

    [Fact]
    public void ThePresetsLaunches_CannotBeConfirmed_WhenTheQuestionCannotShowThemAll()
    {
        LayoutPresetSchema.ConfirmationRefusal(Agent(Enumerable.Range(0, 8).Select(i => $"p{i}.exe").ToArray())).Should().BeNull();
        LayoutPresetSchema.ConfirmationRefusal(Agent(Enumerable.Range(0, 9).Select(i => $"p{i}.exe").ToArray())).Should().Contain("9");
        LayoutPresetSchema.ConfirmationRefusal(Agent("same.exe", "SAME.EXE")).Should().BeNull("a program counts once");
        LayoutPresetSchema.ConfirmationRefusal(Agent(@"C:\" + new string('x', 300) + ".exe")).Should().Contain("260");
    }

    #endregion

    [Theory]
    [InlineData("cmd.exe /c x")]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"tools\app.exe")]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    public void AnExecutableThatIsNoProgramNameOrLocalPath_IsRefused(string executable)
    {
        LayoutPresetSchema.TryBuildPlacement(Valid with { Launch = new LayoutLaunch(executable) }, 0, Monitors, out _, out var error)
            .Should().BeFalse();

        error.Should().StartWith("placements[0].launch");
    }

    [Theory]
    [InlineData("shell:Foo")]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App /c x")]
    public void AnAppIdThatIsNoAppUserModelId_IsRefused(string appId)
    {
        LayoutPresetSchema.TryBuildPlacement(Valid with { Launch = new LayoutLaunch(AppId: appId) }, 0, Monitors, out _, out var error)
            .Should().BeFalse();

        error.Should().StartWith("placements[0].launch");
    }

    [Theory]
    [InlineData("notepad", null)]
    [InlineData("code.exe", null)]
    [InlineData(@"C:\Program Files\Microsoft VS Code\Code.exe", null)]
    [InlineData("ms-settings:display", null)]
    [InlineData(null, "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(null, "MSEdge")]
    public void AProgramNameALocalPathAUriOrAnAppId_IsTaken(string? executable, string? appId)
    {
        LayoutPresetSchema.TryBuildPlacement(Valid with { Launch = new LayoutLaunch(executable, appId) }, 0, Monitors, out _, out var error)
            .Should().BeTrue(error);
    }
}
