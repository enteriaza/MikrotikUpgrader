// -----------------------------------------------------------------------------
// File: ListeningOptions.cs
//
// Description:
// Options bound from the "Listening" section for the Kestrel HTTP listen address
// used to serve firmware packages to routers.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Configuration;

/// <summary>
/// Kestrel listen endpoint configuration.
/// </summary>
public sealed class ListeningOptions
{
    /// <summary>Configuration section name: <c>Listening</c>.</summary>
    public const string SectionName = "Listening";

    /// <summary>
    /// IP address to bind, for example <c>0.0.0.0</c> or <c>127.0.0.1</c>.
    /// Default: <c>0.0.0.0</c>.
    /// </summary>
    public string IpAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// TCP port to bind.
    /// Default: 8080.
    /// </summary>
    public int Port { get; set; } = 8080;
}
