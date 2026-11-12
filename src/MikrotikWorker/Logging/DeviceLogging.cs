// -----------------------------------------------------------------------------
// File: DeviceLogging.cs
//
// Description:
// Thin LogContext helpers that mark events as service-only or service-summary
// while a DeviceLogScope is active, controlling which sinks receive them.
//
// -----------------------------------------------------------------------------

using Serilog.Context;

namespace MikrotikWorker.Logging;

/// <summary>
/// Helpers for routing events while a <see cref="DeviceLogScope"/> is active.
/// </summary>
internal static class DeviceLogging
{
    /// <summary>
    /// Pushes <see cref="DeviceLogNaming.ServiceOnlyPropertyName"/> so the event stays
    /// in the service log and is skipped by <see cref="DeviceLogSink"/>.
    /// </summary>
    public static IDisposable BeginServiceOnly() =>
        LogContext.PushProperty(DeviceLogNaming.ServiceOnlyPropertyName, true);

    /// <summary>
    /// Pushes <see cref="DeviceLogNaming.ServiceSummaryPropertyName"/> so an Information
    /// device-scoped event is still emitted to the service console/file sinks.
    /// </summary>
    public static IDisposable BeginServiceSummary() =>
        LogContext.PushProperty(DeviceLogNaming.ServiceSummaryPropertyName, true);
}
