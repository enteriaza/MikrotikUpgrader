using System.Collections.Concurrent;
using MikrotikWorker.MikroTik;
using tik4net;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
internal sealed class ScriptedMikrotikConnectionFactory(Func<MikrotikConnectionOptions, ITikConnection> factory)
    : IMikrotikConnectionFactory
{
    private readonly ConcurrentQueue<MikrotikConnectionOptions> _connections = new();

    public IReadOnlyList<MikrotikConnectionOptions> Connections => _connections.ToArray();

    public Task<ITikConnection> CreateAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken)
    {
        MikrotikConnectionOptionsValidator.Validate(connection);
        cancellationToken.ThrowIfCancellationRequested();
        _connections.Enqueue(connection);
        return Task.FromResult(factory(connection));
    }
}
