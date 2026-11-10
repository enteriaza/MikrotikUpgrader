using Serilog.Context;

namespace MikrotikWorker.Logging;

/// <summary>
/// Helpers for routing events while a <see cref="DeviceLogScope"/> is active.
/// </summary>
internal static class DeviceLogging
{
    public static IDisposable BeginServiceOnly() =>
        LogContext.PushProperty(DeviceLogNaming.ServiceOnlyPropertyName, true);

    public static IDisposable BeginServiceSummary() =>
        LogContext.PushProperty(DeviceLogNaming.ServiceSummaryPropertyName, true);
}
