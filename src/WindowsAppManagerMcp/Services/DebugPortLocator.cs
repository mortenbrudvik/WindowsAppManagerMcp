using System.Globalization;
using WindowsAppManagerMcp.Services.Interfaces;

namespace WindowsAppManagerMcp.Services;

/// <summary>
/// Finds the DevTools port of a browser window's process (roadmap M5.3): the --remote-debugging-port on its command line,
/// or for port 0 the first line of DevToolsActivePort in its --user-data-dir, used only when the listener on 127.0.0.1 at
/// that port is the process itself.
/// </summary>
/// <remarks>
/// Only the window's own process is looked at, never its children: a WebView2 or Electron host whose DevTools server
/// runs in a child process has no port here (decided in M5.3; the reading tools refuse those windows as NotABrowser in
/// any case). A browser started without the switch has none either, so it is read through UI Automation as before; the
/// switch is required because Chrome's own "remote debugging" setting (chrome://inspect) asks the user to allow each
/// connection. The listener table's null (nothing found, or the table could not be read) fails closed: a port nothing
/// shows to be the process's own is never used. Nothing is connected and no port is tried.
/// </remarks>
public sealed class DebugPortLocator : IDebugPortLocator
{
    private const string PortSwitch = "remote-debugging-port";
    private const string ProfileSwitch = "user-data-dir";

    private readonly INativeProcessWrapper _processes;
    private readonly ITcpPortOwnerResolver _portOwners;
    private readonly Func<string, int?> _readPortFile;

    /// <param name="processes">Reads the process's command line.</param>
    /// <param name="portOwners">Tells which process listens on a port.</param>
    /// <param name="readPortFile">Reads the port from a profile's DevToolsActivePort; tests replace it.</param>
    public DebugPortLocator(INativeProcessWrapper processes, ITcpPortOwnerResolver portOwners, Func<string, int?>? readPortFile = null)
    {
        _processes = processes;
        _portOwners = portOwners;
        _readPortFile = readPortFile ?? DevToolsActivePortFile.TryReadPort;
    }

    /// <inheritdoc />
    public DebugPortLookup Find(int processId)
    {
        var process = processId.ToString(CultureInfo.InvariantCulture);
        var commandLine = _processes.GetProcessCommandLine(processId);
        if (commandLine == null)
        {
            return new DebugPortLookup(DebugPortLookupStatus.CommandLineUnreadable, null,
                $"The command line of the browser's process {process} could not be read, so its debug port is not known.");
        }

        var switches = ChromiumCommandLine.Parse(commandLine);
        if (!switches.TryGetValue(PortSwitch, out var value))
        {
            return new DebugPortLookup(DebugPortLookupStatus.NoDebugPort, null,
                $"The browser (process {process}) was not started with a debug port (--{PortSwitch}).");
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
        {
            return new DebugPortLookup(DebugPortLookupStatus.PortNotKnown, null,
                $"The browser (process {process}) was started with --{PortSwitch}={value}, which is not a port.");
        }

        if (port == 0)
        {
            var picked = ReadPickedPort(switches, process);
            if (picked.Port is not int filePort)
                return picked.Unknown!;

            port = filePort;
        }

        var owner = _portOwners.GetListeningProcessId(port);
        if (owner == null)
        {
            return new DebugPortLookup(DebugPortLookupStatus.NotListening, null,
                $"The browser (process {process}) was started with debug port {port}, but no listener of that process was " +
                $"found on {CdpEndpointValidator.Host}:{port}, so the port was not used (Chrome 136 and later ignore the " +
                "switch for the default profile).");
        }

        if (owner != processId)
        {
            return new DebugPortLookup(DebugPortLookupStatus.HeldByOtherProcess, null,
                $"The browser (process {process}) was started with debug port {port}, but process " +
                $"{owner.Value.ToString(CultureInfo.InvariantCulture)} listens on it, so the port was not used.");
        }

        return new DebugPortLookup(DebugPortLookupStatus.Found, port,
            $"The browser (process {process}) listens on debug port {port}.");
    }

    /// <summary>The port a browser started with port 0 picked, from its profile's port file, or why it cannot be read.</summary>
    private (int? Port, DebugPortLookup? Unknown) ReadPickedPort(IReadOnlyDictionary<string, string> switches, string process)
    {
        if (!switches.TryGetValue(ProfileSwitch, out var profile) || string.IsNullOrWhiteSpace(profile))
        {
            return (null, new DebugPortLookup(DebugPortLookupStatus.PortNotKnown, null,
                $"The browser (process {process}) was started with --{PortSwitch}=0 but without --{ProfileSwitch}, so the " +
                "port it picked cannot be read."));
        }

        var path = profile.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            return (null, new DebugPortLookup(DebugPortLookupStatus.PortNotKnown, null,
                $"The browser (process {process}) was started with --{PortSwitch}=0 and a --{ProfileSwitch} that is not a " +
                "full path, so the port it picked cannot be read."));
        }

        // The path is the process's own word: a share, a pipe or a device (\\host\..., \\?\..., \\.\...) could block the
        // call outside the CDP time budget or reach another host, so only a folder on a local drive is read
        if (!IsLocalDrivePath(path))
        {
            return (null, new DebugPortLookup(DebugPortLookupStatus.PortNotKnown, null,
                $"The browser (process {process}) was started with --{PortSwitch}=0 and a --{ProfileSwitch} that is not on " +
                "a local drive, so the port it picked is not read."));
        }

        var port = _readPortFile(path);
        return port != null
            ? (port, null)
            : (null, new DebugPortLookup(DebugPortLookupStatus.PortNotKnown, null,
                $"The browser (process {process}) was started with --{PortSwitch}=0, but its profile's " +
                $"{DevToolsActivePortFile.FileName} names no port."));
    }

    /// <summary>
    /// Whether <paramref name="path"/> is rooted at a drive letter (C:\...) of a drive that is not a network drive; a UNC,
    /// device (\\?\, \\.\) or drive-relative path is not.
    /// </summary>
    private static bool IsLocalDrivePath(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            return false;

        try
        {
            return new DriveInfo(path[..1]).DriveType != DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
