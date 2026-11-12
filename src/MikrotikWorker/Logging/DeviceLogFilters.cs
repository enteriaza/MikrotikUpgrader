// -----------------------------------------------------------------------------
// File: DeviceLogFilters.cs
//
// Description:
// Serilog filter predicates that keep device-detail Information/Debug events out
// of the service console/file sinks while leaving warnings, errors, service
// summaries, and explicit service-only markers in the service log.
//
// -----------------------------------------------------------------------------

using Serilog.Events;

namespace MikrotikWorker.Logging;

/// <summary>
/// Shared Serilog filter helpers for device vs service sink routing.
/// </summary>
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

    /// <summary>
    /// True when the event carries <see cref="DeviceLogNaming.ServiceOnlyPropertyName"/>
    /// and must not be mirrored into a per-device file.
    /// </summary>
    public static bool IsServiceOnlyEvent(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        return logEvent.Properties.ContainsKey(DeviceLogNaming.ServiceOnlyPropertyName);
    }
}
