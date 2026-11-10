namespace MikrotikWorker;

/// <summary>
/// Kestrel listen endpoint configuration.
/// </summary>
public sealed class ListeningOptions
{
    public const string SectionName = "Listening";

    /// <summary>
    /// IP address to bind, for example <c>0.0.0.0</c> or <c>127.0.0.1</c>.
    /// </summary>
    public string IpAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// TCP port to bind.
    /// </summary>
    public int Port { get; set; } = 8080;
}
