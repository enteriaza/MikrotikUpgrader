using Serilog.Core;
using Serilog.Events;

namespace MikrotikWorker.Logging;

/// <summary>
/// Mirrors events that carry <see cref="DeviceLogNaming.DeviceLogKeyPropertyName"/> into per-device files.
/// </summary>
internal sealed class DeviceLogSink(DeviceLogWriter writer) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

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
