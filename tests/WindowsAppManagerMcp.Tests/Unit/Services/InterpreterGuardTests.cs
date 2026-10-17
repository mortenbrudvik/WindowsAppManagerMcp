namespace WindowsAppManagerMcp.Tests.Unit.Services;

/// <summary>
/// Unit tests for <see cref="InterpreterGuard"/> (roadmap M3.F8): launch_application refuses a command interpreter or
/// script host started with arguments, and a script file that Windows would run with one. Only names are read; the 8.3
/// short-name expansion is a fake, so nothing touches the file system.
/// </summary>
public class InterpreterGuardTests
{
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    private readonly Dictionary<string, string> _longPaths = new(StringComparer.OrdinalIgnoreCase);

    private InterpreterGuard CreateSut() => new(path => _longPaths.TryGetValue(path, out var longPath) ? longPath : null);

    #region Interpreters with arguments

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Windows\System32\wscript.exe")]
    [InlineData(@"C:\Windows\System32\cscript.exe")]
    [InlineData(@"C:\Windows\System32\mshta.exe")]
    [InlineData(@"C:\Windows\System32\rundll32.exe")]
    [InlineData(@"C:\Windows\System32\regsvr32.exe")]
    [InlineData(@"C:\Windows\System32\wsl.exe")]
    [InlineData(@"C:\Windows\System32\bash.exe")]
    [InlineData(@"C:\Python312\python.exe")]
    [InlineData(@"C:\Python312\pythonw.exe")]
    [InlineData(@"C:\Windows\py.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\python3.12.exe")]
    [InlineData(@"C:\Program Files\nodejs\node.exe")]
    [InlineData(@"C:\Program Files\Java\jdk-21\bin\java.exe")]
    [InlineData(@"C:\Program Files\Java\jdk-21\bin\javaw.exe")]
    [InlineData(@"C:\Windows\System32\msiexec.exe")]
    [InlineData(@"C:\Windows\System32\conhost.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\wt.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\ubuntu2204.exe")]
    public void Check_AnInterpreterWithArguments_IsRefused(string interpreter)
    {
        // Act
        var refusal = CreateSut().Check(interpreter, interpreter, ["-c", "calc"]);

        // Assert
        refusal.Should().NotBeNull();
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain(interpreter).And.Contain("does not run commands or scripts")
            .And.Contain("Ask the user").And.Contain("Nothing was started");
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("CMD.EXE")]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\"")]
    [InlineData(@"C:/Windows/System32/Cmd.Exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe.")]
    [InlineData(@"C:\Windows\System32\cmd.exe ")]
    [InlineData(@"C:\Windows\System32\cmd.exe::$DATA")]
    [InlineData(@"C:\Windows\System32\cmd")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public void Check_TheInterpreterName_IsReadRobustly(string executable)
    {
        // Act - the requested name alone, as when the resolver handed it back unchanged
        var refusal = CreateSut().Check(executable, executable, ["/c", "calc"]);

        // Assert
        refusal.Should().NotBeNull();
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Fact]
    public void Check_TheResolvedProgram_Counts_WhateverNameWasAskedFor()
    {
        // Act - an App Paths entry may map any name to an interpreter
        var refusal = CreateSut().Check("myshell", Cmd, ["/c", "calc"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain("myshell").And.Contain(Cmd);
    }

    [Fact]
    public void Check_AnEightDotThreeShortName_IsExpandedFirst()
    {
        // Arrange
        const string shortPath = @"C:\Windows\System32\WINDOW~1\v1.0\POWERS~1.EXE";
        _longPaths[shortPath] = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

        // Act
        var refusal = CreateSut().Check(shortPath, shortPath, ["-Command", "calc"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Check_AnInterpreterWithoutArguments_IsAllowed(bool emptyArray)
    {
        // Act - an empty console or terminal runs nothing on its own
        var refusal = CreateSut().Check("cmd", Cmd, emptyArray ? [] : null);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void Check_AnEmptyArgument_StillCounts()
    {
        // Act
        var refusal = CreateSut().Check("cmd", Cmd, [""]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Program Files\Microsoft VS Code\Code.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Tools\cmdline-helper.exe")]
    [InlineData(@"C:\Tools\pythonista.exe")]
    [InlineData("https://example.com/cmd.exe")]
    [InlineData("ms-settings:display")]
    public void Check_OtherProgramsAndUris_WithArguments_AreAllowed(string target)
    {
        // Act
        var refusal = CreateSut().Check(target, target, ["--flag", "value"]);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void Interpreters_CoverTheShellsScriptHostsAndRuntimes()
    {
        // Assert - the names are kept without .exe, in lower case
        InterpreterGuard.Interpreters.Should().Contain([
            "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "wsl", "bash",
            "python", "pythonw", "py", "node", "java", "javaw", "msiexec", "conhost", "wt"]);
        InterpreterGuard.Interpreters.Should().OnlyContain(name => name == name.ToLowerInvariant() && !name.EndsWith(".exe"));
    }

    #endregion

    #region Script files

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\run.bat")]
    [InlineData(@"C:\Users\me\Desktop\run.CMD")]
    [InlineData(@"C:\Users\me\Desktop\run.ps1")]
    [InlineData(@"C:\Users\me\Desktop\run.vbs")]
    [InlineData(@"C:\Users\me\Desktop\run.js")]
    [InlineData(@"C:\Users\me\Desktop\run.wsf")]
    [InlineData(@"C:\Users\me\Desktop\run.hta")]
    [InlineData(@"C:\Users\me\Desktop\run.py")]
    [InlineData(@"C:\Users\me\Desktop\run.jar")]
    [InlineData(@"C:\Users\me\Desktop\setup.msi")]
    [InlineData(@"C:\Users\me\Desktop\run.bat.")]
    [InlineData(@"C:\Users\me\Desktop\run.bat::$DATA")]
    public void Check_AScriptFile_IsRefused_WithOrWithoutArguments(string script)
    {
        // Act
        var bare = CreateSut().Check(script, script, null);
        var withArguments = CreateSut().Check(script, script, ["x"]);

        // Assert
        bare!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        bare.Message.Should().Contain(script).And.Contain("does not run commands or scripts").And.Contain("Nothing was started");
        withArguments!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
    }

    [Fact]
    public void Check_ABareNameThatResolvedToABatchFile_IsRefused_AndPointsAtTheProgramsExe()
    {
        // Act - VS Code's 'code' is bin\code.cmd on PATH
        var refusal = CreateSut().Check("code", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd", [@"C:\Projects\MyApp"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        refusal.Message.Should().Contain("code.cmd").And.Contain("code.exe");
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents\report.pdf")]
    [InlineData(@"C:\Users\me\Documents\notes.txt")]
    [InlineData(@"C:\Users\me\Documents")]
    [InlineData("https://example.com/install.ps1")]
    public void Check_DocumentsFoldersAndWebAddresses_AreAllowed(string target)
    {
        // Act
        var refusal = CreateSut().Check(target, target, null);

        // Assert
        refusal.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\run.bat")]
    [InlineData("\"C:\\Users\\me\\Desktop\\run.vbs\"")]
    [InlineData("file:///C:/Users/me/Desktop/run.hta")]
    [InlineData(@"/select,C:\Users\me\Desktop\run.ps1")]
    public void Check_ExplorerGivenAScript_IsRefused(string argument)
    {
        // Act - explorer.exe opens its argument with the associated program, which runs a script
        var refusal = CreateSut().Check("explorer", @"C:\Windows\explorer.exe", [argument]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        refusal.Message.Should().Contain("explorer");
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents")]
    [InlineData(@"C:\Users\me\Documents\report.pdf")]
    [InlineData(@"/select,C:\Users\me\Documents\report.pdf")]
    public void Check_ExplorerGivenAFolderOrDocument_IsAllowed(string argument)
    {
        // Act
        var refusal = CreateSut().Check("explorer", @"C:\Windows\explorer.exe", [argument]);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void Check_AnotherProgramGivenAScriptAsItsDocument_IsAllowed()
    {
        // Act - Notepad opens the script as text
        var refusal = CreateSut().Check("notepad", Notepad, [@"C:\Users\me\Desktop\run.bat"]);

        // Assert
        refusal.Should().BeNull();
    }

    [Fact]
    public void ScriptExtensions_CoverBatchPowerShellScriptHostAndPythonFiles()
    {
        // Assert
        InterpreterGuard.ScriptExtensions.Should().Contain([".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".py", ".pyw"]);
        InterpreterGuard.ScriptExtensions.Should().NotContain([".exe", ".com", ".txt", ".pdf", ".lnk"]);
    }

    #endregion

    #region Packaged apps and Applications folder entries (roadmap M6.5)

    [Theory]
    [InlineData("WindowsTerminal.exe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    [InlineData("ubuntu.exe", "CanonicalGroupLimited.UbuntuonWindows_79rhkp1fndgsc")]
    [InlineData("pwsh.exe", "Microsoft.PowerShell_8wekyb3d8bbwe")]
    [InlineData("python3.12.exe", "PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0")]
    [InlineData("SomeLauncher.exe", "PythonSoftwareFoundation.Python.3.13_qbz5n2kfra8p0")]
    [InlineData("SomeLauncher.exe", "CanonicalGroupLimited.Ubuntu24.04LTS_79rhkp1fndgsc")]
    [InlineData("debian.exe", "TheDebianProject.DebianGNULinux_76v4gfsz19hv4")]
    [InlineData("App.exe", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe")]
    [InlineData("AppInstallerCLI.exe", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe")] // winget (review M6.5 #9)
    public void CheckApp_AnInterpreterPackageWithArguments_IsRefused(string program, string family)
    {
        // Act
        var refusal = InterpreterGuard.CheckApp($"{family}!App", program, family, ["-c", "echo"]);

        // Assert
        refusal.Should().NotBeNull();
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain("Nothing was started");
    }

    [Fact]
    public void CheckApp_AnAppWhoseProgramIsNotKnown_IsRefusedWithArguments_FailClosed()
    {
        // Act
        var refusal = InterpreterGuard.CheckApp("Vendor.App_8wekyb3d8bbwe!App", program: null, "Vendor.App_8wekyb3d8bbwe", ["file.txt"]);

        // Assert
        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        refusal.Message.Should().Contain("cannot tell which program");
    }

    [Theory]
    [InlineData("Notepad.exe", "Microsoft.WindowsNotepad_8wekyb3d8bbwe", true)]
    [InlineData(null, "Vendor.App_8wekyb3d8bbwe", false)]
    [InlineData("pwsh.exe", "Microsoft.PowerShell_8wekyb3d8bbwe", false)]
    public void CheckApp_WithoutArguments_OrAnotherProgram_IsAllowed(string? program, string family, bool withArguments)
    {
        // Act
        var refusal = InterpreterGuard.CheckApp($"{family}!App", program, family, withArguments ? [@"C:\notes\todo.txt"] : []);

        // Assert
        refusal.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Vendor\setup.hta", true)]
    [InlineData(@"C:\Vendor\run.bat", false)]
    [InlineData(@"C:\Vendor\tool.ps1", true)]
    [InlineData(@"C:\Vendor\package.msi", false)]
    public void CheckApp_AnEntryWhoseProgramIsAScript_IsRefused_WithOrWithoutArguments(string program, bool withArguments)
    {
        // Review M6.5 #3: the shell would run the script with its interpreter
        var refusal = InterpreterGuard.CheckApp("Vendor.Setup", program, null, withArguments ? ["x"] : []);

        refusal!.ErrorCode.Should().Be(nameof(LaunchErrorCode.ScriptFile));
        refusal.Message.Should().Contain("Nothing was started");
    }

    [Fact]
    public void CheckApp_ADesktopAppEntry_IsCheckedByItsProgram()
    {
        // Act
        var refused = InterpreterGuard.CheckApp("PowerShell7", @"C:\Program Files\PowerShell\7\pwsh.exe", null, ["-c", "x"]);
        var allowed = InterpreterGuard.CheckApp("MSEdge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", null, ["https://example.com"]);

        // Assert
        refused!.ErrorCode.Should().Be(nameof(LaunchErrorCode.InterpreterWithArguments));
        allowed.Should().BeNull();
    }

    #endregion
}
