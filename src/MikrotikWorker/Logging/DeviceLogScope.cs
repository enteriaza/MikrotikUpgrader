using MikrotikWorker.Scheduling;
using Serilog.Context;

namespace MikrotikWorker.Logging;

/// <summary>
/// Pushes device-scoped Serilog context properties for the duration of a router job.
/// File naming uses RouterName + RouterIp so the key is stable before identity is known.
/// </summary>
internal sealed class DeviceLogScope : IDisposable
{
    private readonly List<IDisposable> _pushed = [];

    private DeviceLogScope()
    {
    }

    public string DeviceLogKey { get; private set; } = string.Empty;

    public static DeviceLogScope Begin(MikrotikScheduledJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        DeviceLogScope scope = new()
        {
            DeviceLogKey = DeviceLogNaming.BuildDeviceLogKey(job.RouterName, job.RouterIp),
        };

        scope._pushed.Add(LogContext.PushProperty(DeviceLogNaming.DeviceLogKeyPropertyName, scope.DeviceLogKey));
        scope._pushed.Add(LogContext.PushProperty("JobId", job.JobId));
        scope._pushed.Add(LogContext.PushProperty("ImportId", job.ImportId));
        scope._pushed.Add(LogContext.PushProperty("RouterName", job.RouterName));
        scope._pushed.Add(LogContext.PushProperty("RouterIp", job.RouterIp.ToString()));
        return scope;
    }

    public void SetRouterIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return;
        }

        _pushed.Add(LogContext.PushProperty("RouterIdentity", identity));
    }

    public void SetAssessmentProperties(string? routerOsVersion, string? architecture, string targetVersion, string assessmentStatus)
    {
        if (!string.IsNullOrWhiteSpace(routerOsVersion))
        {
            _pushed.Add(LogContext.PushProperty("RouterOsVersion", routerOsVersion));
        }

        if (!string.IsNullOrWhiteSpace(architecture))
        {
            _pushed.Add(LogContext.PushProperty("Architecture", architecture));
        }

        _pushed.Add(LogContext.PushProperty("TargetRouterOsVersion", targetVersion));
        _pushed.Add(LogContext.PushProperty("AssessmentStatus", assessmentStatus));
    }

    public void Dispose()
    {
        for (int i = _pushed.Count - 1; i >= 0; i--)
        {
            _pushed[i].Dispose();
        }

        _pushed.Clear();
    }
}
