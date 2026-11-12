// -----------------------------------------------------------------------------
// File: DeviceLogScope.cs
//
// Description:
// Pushes Serilog device context for a scheduled job, writes a one-time session header,
// and promotes pending IP-based log keys to identity-based keys after identity is known.
//
// -----------------------------------------------------------------------------

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
    /// <summary>
    /// Serilog context property registrations, disposed in reverse order.
    /// </summary>
    private readonly List<IDisposable> _pushed = [];
    /// <summary>
    /// Writer for the session header and key promotion, or null when device logging is unavailable.
    /// </summary>
    private readonly DeviceLogWriter? _writer;
    /// <summary>
    /// Registry used to resolve a unique identity-based log name, or null.
    /// </summary>
    private readonly DeviceLogIdentityRegistry? _registry;
    /// <summary>
    /// Scheduled job this scope logs for.
    /// </summary>
    private readonly MikrotikScheduledJob _job;
    /// <summary>
    /// UTC time the scope began, shown in the session header.
    /// </summary>
    private readonly DateTimeOffset _startedUtc;
    /// <summary>
    /// Whether the session header has been queued, so it is written only once.
    /// </summary>
    private bool _headerWritten;

    /// <summary>
    /// Creates the scope; use <see cref="Begin"/> to create one with its initial log context pushed.
    /// </summary>
    /// <param name="job">Scheduled job being logged.</param>
    /// <param name="writer">Device log writer, or null.</param>
    /// <param name="registry">Identity registry, or null.</param>
    /// <param name="startedUtc">UTC start time.</param>
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

    /// <summary>Active device log file key (pending or identity-based).</summary>
    public string DeviceLogKey { get; private set; } = string.Empty;

    /// <summary>Starts a scope with a pending IP-based key for <paramref name="job"/>.</summary>
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

    /// <summary>
    /// Switches the log key to the identity-based filename, writes the session header once,
    /// and asks <see cref="DeviceLogWriter"/> to promote any pending file content.
    /// </summary>
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

    /// <summary>
    /// Pushes assessment correlation properties onto the Serilog context for subsequent events.
    /// </summary>
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

    /// <summary>
    /// Pops all pushed LogContext properties in reverse order.
    /// </summary>
    public void Dispose()
    {
        for (int i = _pushed.Count - 1; i >= 0; i--)
        {
            _pushed[i].Dispose();
        }

        _pushed.Clear();
    }

    /// <summary>
    /// Queues a human-readable session banner into the current device log key (once per scope).
    /// </summary>
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
