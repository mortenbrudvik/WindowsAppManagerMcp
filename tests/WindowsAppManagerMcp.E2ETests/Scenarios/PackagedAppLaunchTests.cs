using System.Diagnostics;
using WindowsAppManagerMcp.Services;

namespace WindowsAppManagerMcp.E2ETests.Scenarios;

/// <summary>
/// Packaged app launch and installed-app discovery (roadmap M6.5) on the real desktop. The no-input scenarios only read
/// the Start menu's apps and plan launches (nothing is started). The input-tier scenarios start one app, Calculator, which
/// holds none of the user's data: only when no Calculator runs before the test, activated without arguments (the guard
/// allows that activation and nothing else, <see cref="FixtureOnlyCallPolicy"/>), and ended by the test itself through the
/// process ID the launch returned, after checking that the process is a CalculatorApp that started during the test.
/// </summary>
public class PackagedAppLaunchTests
{
    private const string CalculatorAppId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string CalculatorProcessName = "CalculatorApp";
    private const string TerminalAppId = "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";

    /// <summary>A cold activation with a splash screen can take seconds.</summary>
    private const int WindowWaitMs = 15_000;

    private readonly ITestOutputHelper _output;

    public PackagedAppLaunchTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [DesktopNoInputFact]
    public void ListInstalledApps_ReadsTheStartMenu_PackagedAppsFirst()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var apps = e2e.Server.Get<IInstalledAppsService>();

        // Act - the first read (target: under 2 seconds), then one from the cache
        var watch = Stopwatch.StartNew();
        var listing = apps.List(nameFilter: null, limit: 200);
        var firstRead = watch.ElapsedMilliseconds;
        apps.List("calc", 25);
        _output.WriteLine($"Read {listing.Total} apps in {firstRead} ms (the cached read after it: {watch.ElapsedMilliseconds - firstRead} ms).");
        foreach (var app in listing.Apps.Take(15))
        {
            _output.WriteLine($"  {app.Name} | {app.AppId} | packaged={app.Packaged} | {app.Path}");
        }

        // Assert
        listing.Unreadable.Should().BeFalse("the Start menu's apps must be readable within the 3-second deadline");
        listing.Apps.Should().NotBeEmpty();
        listing.Apps.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.AppId));
        listing.Apps.Where(a => a.Packaged).Select(a => (a.AppId, a.Path)).ToList()
            .ForEach(a => (PackagedAppId.TryParse(a.AppId, out var id) && id != null && a.Path == null).Should().BeTrue(a.AppId));
        listing.Apps.SkipWhile(a => a.Packaged).Should().OnlyContain(a => !a.Packaged, "packaged apps come first");
    }

    [DesktopNoInputFact]
    public void PlanLaunch_CalculatorsInstalledName_IsItsActivation_AndStartsNothing()
    {
        // Arrange - the name is localized (Calculator, Kalkulator, Rechner, ...), so it is read from the Start menu
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var calculator = e2e.Server.Get<IInstalledAppsService>().FindByAppId(CalculatorAppId)
            ?? throw new InvalidOperationException("Calculator is not installed for this user; this scenario needs it.");
        _output.WriteLine($"Calculator's name here: '{calculator.Name}'.");

        // Act - planning starts nothing
        var plan = ((ProcessService)e2e.Server.Get<IProcessService>()).PlanLaunch(calculator.Name);

        // Assert - one app of that name is its activation; another app of the same name makes it ambiguous
        _output.WriteLine($"Plan: {plan.TargetKind} {plan.StartInfo?.FileName} {plan.ErrorCode} {plan.Warning}");
        if (plan.ErrorCode == nameof(LaunchErrorCode.AmbiguousApp))
        {
            plan.ErrorMessage.Should().Contain(CalculatorAppId);
            return;
        }

        plan.IsValid.Should().BeTrue(plan.ErrorMessage);
        plan.TargetKind.Should().Be(LaunchTargetKind.PackagedApp);
        plan.StartInfo!.FileName.Should().Be(@"shell:AppsFolder\" + CalculatorAppId);
        plan.Warning.Should().Contain(CalculatorAppId);
    }

    [DesktopNoInputFact]
    public void PlanLaunch_ArgumentsToWindowsTerminal_AreRefused_AndItsProgramIsReadFromItsManifest()
    {
        // Arrange
        using var e2e = E2ESession.Start(DesktopTier.NoInput, _output);
        var program = e2e.Server.Get<IInstalledAppsService>().ProgramOf(new PackagedAppId("Microsoft.WindowsTerminal_8wekyb3d8bbwe", "App"));
        _output.WriteLine($"Windows Terminal's program, from its manifest: {program ?? "(not installed or not readable)"}.");

        // Act - planning starts nothing
        var plan = ((ProcessService)e2e.Server.Get<IProcessService>()).PlanLaunch(TerminalAppId, ["-c", "echo"]);

        // Assert - refused whether Terminal is installed (an interpreter) or not (its program cannot be told)
        plan.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments), plan.ErrorMessage);
        if (program != null)
        {
            program.Should().BeEquivalentTo("WindowsTerminal.exe");
        }
    }

    [DesktopTheory]
    [InlineData(CalculatorAppId)]
    [InlineData(@"shell:AppsFolder\" + CalculatorAppId)]
    public async Task Calculator_ByItsAppId_ReturnsItsOwnProcessAndWindow_AgainRightAfterItWasEnded(string executable)
    {
        // Arrange - never touch a Calculator the user has open
        RequireNoCalculatorRunning();
        using var e2e = E2ESession.Start(DesktopTier.Input, _output, allowedActivations: [CalculatorAppId]);
        var processes = e2e.Server.Get<IProcessService>();

        // Act & Assert - twice: the second activation may find the frame Windows kept cloaked for the first (roadmap M3.F3)
        for (var round = 1; round <= 2; round++)
        {
            var testStart = DateTime.UtcNow;
            var result = await processes.LaunchApplicationAsync(new LaunchRequest(executable), waitForWindow: true, WindowWaitMs);
            _output.WriteLine($"Round {round}: {result.Status} processId={result.ProcessId} window={result.Window} warning={result.Warning}");
            try
            {
                result.Success.Should().BeTrue(result.ErrorMessage);
                result.Status.Should().Be(LaunchStatus.WindowObserved, "a fresh activation opens a new (or uncloaked) window");
                result.ProcessId.Should().NotBeNull("an activation returns the app's own process");
                result.Window!.ProcessId.Should().Be(result.ProcessId!.Value);
                result.Window.ProcessName.Should().BeEquivalentTo(CalculatorProcessName);
            }
            finally
            {
                if (result.ProcessId is { } processId)
                {
                    EndCalculatorThisTestStarted(processId, testStart);
                }
            }
        }
    }

    /// <summary>Fails the scenario, starting nothing, when a Calculator runs already: its windows are the user's.</summary>
    private static void RequireNoCalculatorRunning()
    {
        var running = Process.GetProcessesByName(CalculatorProcessName);
        foreach (var process in running)
        {
            process.Dispose();
        }

        if (running.Length > 0)
        {
            throw new InvalidOperationException(
                "Calculator is running: close it first. This scenario starts Calculator and ends only the one it started.");
        }
    }

    /// <summary>
    /// Ends the Calculator the launch returned, by its process ID, only when that process is a CalculatorApp that started
    /// after the launch (so it cannot be a process that got the ID since, or the user's own Calculator).
    /// </summary>
    private void EndCalculatorThisTestStarted(int processId, DateTime launchedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, CalculatorProcessName, StringComparison.OrdinalIgnoreCase) ||
                process.StartTime.ToUniversalTime() < launchedAtUtc.AddSeconds(-1))
            {
                _output.WriteLine($"Process {processId} is not the Calculator this test started; it was left running.");
                return;
            }

            process.Kill();
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // It exited already
        }
    }
}
