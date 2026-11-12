// -----------------------------------------------------------------------------
// File: DeviceLogSink.cs
//
// Description:
// Serilog ILogEventSink that forwards events carrying DeviceLogKey into
// DeviceLogWriter, skipping events marked ServiceOnly.
//
// -----------------------------------------------------------------------------

using Serilog.Core;
using Serilog.Events;

namespace MikrotikWorker.Logging;

/// <summary>
/// Mirrors device-scoped events into per-device files. Service-only markers are skipped.
/// </summary>
internal sealed class DeviceLogSink(DeviceLogWriter writer) : ILogEventSink
{
    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        if (DeviceLogFilters.IsServiceOnlyEvent(logEvent))
        {
            return;
        }

        if (!logEvent.Properties.TryGetValue(DeviceLogNaming.DeviceLogKeyPropertyName, out LogEventPropertyValue? value))
        {
            return;
        }

        string? key = value switch
        {
            ScalarValue { Value: string s } => s,
            _ => value.ToString().Trim('"'),
        };

        if (!string.IsNullOrWhiteSpace(key))
        {
            writer.Emit(key, logEvent);
        }
    }
}
