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

        TikConnectionSetup setup = new(connection.Host, connection.Username, connection.Password)
        {
            Port = port,
            ConnectTimeout = globalOptions.ConnectionTimeout,
        };

        return await setup.CreateAsync(TikConnectionType.Api, cancellationToken).ConfigureAwait(false);
    }
}
