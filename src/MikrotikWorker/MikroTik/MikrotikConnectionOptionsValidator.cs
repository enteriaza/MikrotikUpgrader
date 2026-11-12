// -----------------------------------------------------------------------------
// File: MikrotikConnectionOptionsValidator.cs
//
// Description:
// Runtime guard for MikrotikConnectionOptions before opening a tik4net session:
// non-empty host/username, non-null password, and optional port/timeout ranges.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Throws <see cref="ArgumentException"/> / <see cref="ArgumentOutOfRangeException"/> when
/// a connection request is incomplete or out of range.
/// </summary>
internal static class MikrotikConnectionOptionsValidator
{
    /// <summary>
    /// Validates required fields and optional port/timeout bounds on <paramref name="connection"/>.
    /// </summary>
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
