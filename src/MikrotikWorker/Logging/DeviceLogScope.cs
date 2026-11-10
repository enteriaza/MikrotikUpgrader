using System.Text;
using MikrotikWorker.Scheduling;
using Serilog.Context;

namespace MikrotikWorker.Logging;

/// <summary>
/// Pushes device-scoped Serilog context for a router job and writes a one-time session header.
/// File naming uses a pending IP-based name until RouterOS identity is known, then switches to
/// the identity-based stem shared with backup artefacts.
/// </summary>
internal sealed class DeviceLogScope : IDisposable
{
    private readonly List<IDisposable> _pushed = [];
    private readonly DeviceLogWriter? _writer;
    private readonly DeviceLogIdentityRegistry? _registry;
    private readonly MikrotikScheduledJob _job;
    private readonly DateTimeOffset _startedUtc;
    private bool _headerWritten;

    private DeviceLogScope(
        MikrotikScheduledJob job,
        DeviceLogWriter? writer,
        DeviceLogIdentityRegistry? registry,
        DateTimeOffset startedUtc)
    {
        _job = job;
        _writer = writer;
        _registry = registry;
        _startedUtc = startedUtc;
    }

    public string DeviceLogKey { get; private set; } = string.Empty;

    public static DeviceLogScope Begin(
        MikrotikScheduledJob job,
        DeviceLogWriter? writer = null,
        DeviceLogIdentityRegistry? registry = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        DateTimeOffset started = (timeProvider ?? TimeProvider.System).GetUtcNow();
        DeviceLogScope scope = new(job, writer, registry, started)
        {
            DeviceLogKey = DeviceLogNaming.BuildPendingLogFileName(job.RouterIp),
        };

        scope._pushed.Add(LogContext.PushProperty(DeviceLogNaming.DeviceLogKeyPropertyName, scope.DeviceLogKey));
        return scope;
    }

    public void SetRouterIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return;
        }

        string previousKey = DeviceLogKey;
        string finalKey = DeviceLogNaming.ResolveIdentityLogFileName(_registry, identity, _job.RouterIp);

        if (!string.Equals(DeviceLogKey, finalKey, StringComparison.Ordinal))
        {
            DeviceLogKey = finalKey;
            _pushed.Add(LogContext.PushProperty(DeviceLogNaming.DeviceLogKeyPropertyName, DeviceLogKey));
        }

        _pushed.Add(LogContext.PushProperty("RouterIdentity", identity));
        // Header must be queued before Promote so the identity file reads: header, then pending lines.
        WriteSessionHeaderIfNeeded(identity);
        if (!string.Equals(previousKey, finalKey, StringComparison.Ordinal))
        {
            _writer?.PromoteKey(previousKey, finalKey);
        }
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

    private void WriteSessionHeaderIfNeeded(string identity)
    {
        if (_headerWritten || _writer is null)
        {
            return;
        }

        _headerWritten = true;
        StringBuilder sb = new();
        sb.AppendLine("============================================================");
        sb.Append("Device: ").AppendLine(identity.Trim());
        sb.Append("Address: ").AppendLine(_job.RouterIp.ToString());
        if (!string.Equals(_job.RouterName, identity, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("CSV name: ").AppendLine(_job.RouterName);
        }

        sb.Append("Import ID: ").AppendLine(_job.ImportId.ToString("D"));
        sb.Append("Job ID: ").AppendLine(_job.JobId);
        sb.Append("Started: ")
            .Append(_startedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"))
            .AppendLine(" UTC");
        sb.AppendLine("============================================================");
        _writer.WriteRaw(DeviceLogKey, sb.ToString());
    }
}
