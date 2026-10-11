using ModelContextProtocol;
using WindowsAppManagerMcp.Models;

namespace WindowsAppManagerMcp.Infrastructure;

/// <summary>
/// Passes a service's <see cref="OperationProgress"/> on to the MCP client. A tool takes an
/// <see cref="IProgress{T}"/> of <see cref="ProgressNotificationValue"/>, which the SDK binds to the call (it is not
/// part of the input schema): when the client sent a progress token with the call, every report becomes a
/// notifications/progress message; without one the SDK drops the reports. This is the only place that builds
/// <see cref="ProgressNotificationValue"/>, so the services stay free of SDK types.
/// </summary>
public static class ToolProgress
{
    /// <summary>
    /// The tool's progress as the services report it, or null when the tool got none (a direct call in a test).
    /// </summary>
    public static IProgress<OperationProgress>? ForServices(IProgress<ProgressNotificationValue>? progress) =>
        progress == null ? null : new Forwarder(progress);

    /// <summary>The notification value for <paramref name="progress"/>.</summary>
    public static ProgressNotificationValue ToNotification(OperationProgress progress) => new()
    {
        Progress = progress.Completed,
        Total = progress.Total,
        Message = progress.Message
    };

    /// <summary>
    /// Forwards reports to the client. A progress notification is a courtesy: one that cannot be sent (the client has
    /// gone, the transport is closing) never fails the operation that reports it.
    /// </summary>
    private sealed class Forwarder(IProgress<ProgressNotificationValue> client) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            try
            {
                client.Report(ToNotification(value));
            }
            catch (Exception)
            {
                // Best effort, see above
            }
        }
    }
}
