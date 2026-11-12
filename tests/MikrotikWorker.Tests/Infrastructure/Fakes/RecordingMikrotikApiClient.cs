// -----------------------------------------------------------------------------
// File: RecordingMikrotikApiClient.cs
//
// Description:
// Recording IMikrotikApiClient / router-operations fake capturing calls for assertions.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
/// <summary>
/// Recording API client fake for call assertions.
/// </summary>
internal sealed class RecordingMikrotikApiClient : IMikrotikApiClient
{
    /// <summary>Backing counter for <see cref="CallCount"/>; updated with <see cref="Interlocked"/>.</summary>
    private int _callCount;

    /// <summary>Number of <see cref="GetDeviceInfoAsync"/> calls made so far.</summary>
    public int CallCount => _callCount;

    /// <summary>Connection options received by each call, in call order.</summary>
    public List<MikrotikConnectionOptions> Connections { get; } = [];

    /// <summary>Records the call and returns a placeholder device info.</summary>
    /// <param name="connection">Connection options to record.</param>
    /// <param name="cancellationToken">Token checked after recording the call.</param>
    /// <returns>A device info with identity <c>recorded</c> and version <c>0.0</c>.</returns>
    /// <exception cref="OperationCanceledException">When <paramref name="cancellationToken"/> is cancelled.</exception>
    public Task<MikrotikDeviceInfo> GetDeviceInfoAsync(
        MikrotikConnectionOptions connection,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        Connections.Add(connection);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new MikrotikDeviceInfo
        {
            Identity = "recorded",
            Version = "0.0",
        });
    }
}
