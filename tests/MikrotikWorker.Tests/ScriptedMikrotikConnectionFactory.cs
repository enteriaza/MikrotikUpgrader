using MikrotikWorker.MikroTik;
using tik4net;

namespace MikrotikWorker.Tests;

internal sealed class ScriptedMikrotikConnectionFactory(Func<MikrotikConnectionOptions, ITikConnection> factory)
    : IMikrotikConnectionFactory
{
    public List<MikrotikConnectionOptions> Connections { get; } = [];

    public Task<ITikConnection> CreateAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken)
    {
        MikrotikConnectionOptionsValidator.Validate(connection);
        cancellationToken.ThrowIfCancellationRequested();
        Connections.Add(connection);
        return Task.FromResult(factory(connection));
    }
}
