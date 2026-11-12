// -----------------------------------------------------------------------------
// File: ScriptedMikrotikConnectionFactory.cs
//
// Description:
// Connection factory that returns scripted fake connections in sequence for API client tests.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using MikrotikWorker.MikroTik;
using tik4net;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
/// <summary>
/// Scripted connection factory for sequenced fake sessions.
/// </summary>
/// <param name="factory">Callback that produces the fake connection for each requested set of options.</param>
internal sealed class ScriptedMikrotikConnectionFactory(Func<MikrotikConnectionOptions, ITikConnection> factory)
    : IMikrotikConnectionFactory
{
    /// <summary>Thread-safe record of every validated connection request.</summary>
    private readonly ConcurrentQueue<MikrotikConnectionOptions> _connections = new();

    /// <summary>Snapshot of the connection options passed to <see cref="CreateAsync"/>, in call order.</summary>
    public IReadOnlyList<MikrotikConnectionOptions> Connections => _connections.ToArray();

    /// <summary>Validates and records <paramref name="connection"/>, then returns the scripted fake connection.</summary>
    /// <param name="connection">Connection options; validated before use.</param>
    /// <param name="cancellationToken">Token checked before the request is recorded.</param>
    /// <returns>A completed task containing the connection produced by the factory callback.</returns>
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
