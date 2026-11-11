namespace MikrotikWorker.MikroTik;

internal static class MikrotikConnectionOptionsValidator
{
    public static void Validate(MikrotikConnectionOptions connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (string.IsNullOrWhiteSpace(connection.Host))
        {
            throw new ArgumentException("MikroTik connection host is required.", nameof(connection));
        }

        if (string.IsNullOrWhiteSpace(connection.Username))
        {
            throw new ArgumentException("MikroTik connection username is required.", nameof(connection));
        }

        ArgumentNullException.ThrowIfNull(connection.Password);

        if (connection.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connection),
                connection.Port,
                "MikroTik connection port must be between 1 and 65535 when specified.");
        }

        if (connection.ConnectTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connection),
                timeout,
                "MikroTik connection timeout must be greater than zero when specified.");
        }
    }
}
