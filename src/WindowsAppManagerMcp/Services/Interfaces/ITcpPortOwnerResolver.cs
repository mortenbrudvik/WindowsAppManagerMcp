namespace WindowsAppManagerMcp.Services.Interfaces;

/// <summary>
/// Finds which process is listening on a local TCP port.
/// Used to check that a CDP debug port belongs to the same browser process as a window.
/// </summary>
public interface ITcpPortOwnerResolver
{
    /// <summary>
    /// Gets the ID of the process that accepts connections to 127.0.0.1 on the given port.
    /// </summary>
    /// <param name="port">The TCP port.</param>
    /// <returns>The owning process ID, or null if no listener was found or the lookup failed.</returns>
    int? GetListeningProcessId(int port);

    /// <summary>
    /// Like <see cref="GetListeningProcessId"/>, but tells a port nothing listens on from a listener table that cannot be
    /// read.
    /// </summary>
    /// <param name="port">The TCP port.</param>
    /// <param name="processId">The owning process ID, or null when nothing listens on the port.</param>
    /// <returns>False when the listener table could not be read, so the owner is not known.</returns>
    bool TryGetListeningProcessId(int port, out int? processId);
}
