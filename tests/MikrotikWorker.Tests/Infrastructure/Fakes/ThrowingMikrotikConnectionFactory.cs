// -----------------------------------------------------------------------------
// File: ThrowingMikrotikConnectionFactory.cs
//
// Description:
// IMikrotikConnectionFactory fake that throws a configured exception on CreateAsync.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;
using tik4net;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
/// <summary>
/// Connection factory fake that throws on create.
/// </summary>
/// <param name="exception">Exception surfaced by the task returned from <see cref="CreateAsync"/>.</param>
internal sealed class ThrowingMikrotikConnectionFactory(Exception exception) : IMikrotikConnectionFactory
{
    /// <summary>Validates <paramref name="connection"/> and returns a faulted task carrying the configured exception.</summary>
    /// <param name="connection">Connection options; validated before the failure is returned.</param>
    /// <param name="cancellationToken">Token checked before the failure is returned.</param>
    /// <returns>A task faulted with the configured exception.</returns>
    public Task<ITikConnection> CreateAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken)
    {
        MikrotikConnectionOptionsValidator.Validate(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<ITikConnection>(exception);
    }
}
