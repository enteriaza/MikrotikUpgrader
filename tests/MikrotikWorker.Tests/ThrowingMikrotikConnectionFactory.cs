using MikrotikWorker.MikroTik;
using tik4net;

namespace MikrotikWorker.Tests;

internal sealed class ThrowingMikrotikConnectionFactory(Exception exception) : IMikrotikConnectionFactory
{
    public Task<ITikConnection> CreateAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken)
    {
        MikrotikConnectionOptionsValidator.Validate(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<ITikConnection>(exception);
    }
}
