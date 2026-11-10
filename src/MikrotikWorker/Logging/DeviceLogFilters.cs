using Serilog.Events;

namespace MikrotikWorker.Logging;

internal static class DeviceLogFilters
{
    /// <summary>
    /// Device-detail Information/Debug events belong in the per-device file, not the service sinks.
    /// Warnings/errors, explicit service summaries, and service-only markers remain in the service log.
    /// </summary>
    public static bool IsDeviceDetailExcludedFromServiceLog(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (!logEvent.Properties.ContainsKey(DeviceLogNaming.DeviceLogKeyPropertyName))
        {
            return false;
        }

        if (logEvent.Properties.ContainsKey(DeviceLogNaming.ServiceOnlyPropertyName))
        {
            return false;
        }

        if (logEvent.Properties.ContainsKey(DeviceLogNaming.ServiceSummaryPropertyName))
        {
            return false;
        }

        return logEvent.Level < LogEventLevel.Warning;
    }

    public static bool IsServiceOnlyEvent(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return logEvent.Properties.ContainsKey(DeviceLogNaming.ServiceOnlyPropertyName);
    }
}
