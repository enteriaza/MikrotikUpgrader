using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using tik4net;

namespace MikrotikWorker.MikroTik;

internal sealed class Tik4netConnectionFactory(IOptions<MikroTikOptions> options) : IMikrotikConnectionFactory
{
    public async Task<ITikConnection> CreateAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken)
    {
        MikrotikConnectionOptionsValidator.Validate(connection);

        MikroTikOptions globalOptions = options.Value;
        int port = connection.Port ?? globalOptions.Port;

        TimeSpan connectTimeout = connection.ConnectTimeout ?? globalOptions.ConnectionTimeout;
        if (connectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(connection),
                connectTimeout,
                "MikroTik connection timeout must be greater than zero.");
        }

        TikConnectionSetup setup = new(connection.Host, connection.Username, connection.Password)
        {
            Port = port,
            ConnectTimeout = connectTimeout,
        };

        return await setup.CreateAsync(TikConnectionType.Api, cancellationToken).ConfigureAwait(false);
    }
}
