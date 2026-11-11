namespace MikrotikWorker.Configuration;

/// <summary>
/// Explicit opt-in for live, read-only package-inventory fleet audits.
/// Does not enable firmware upgrades.
/// </summary>
public sealed class LivePackageCompatibilityAuditOptions
{
    public const string SectionName = "LivePackageCompatibilityAudit";

    /// <summary>
    /// When false (default), live router connections for package audits are refused
    /// even if the CLI flag is present.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum concurrent read-only API connections. Default 5.
    /// </summary>
    public int MaxConcurrentConnections { get; set; } = 5;

    /// <summary>
    /// Optional absolute deadline for the entire fleet audit. Null = no fleet-wide deadline.
    /// </summary>
    public TimeSpan? TotalAuditTimeout { get; set; }

    /// <summary>
    /// Per-router API connect/login timeout. When null, uses <c>Mikrotik:ConnectionTimeout</c>.
    /// </summary>
    public TimeSpan? ConnectionTimeout { get; set; }
}
