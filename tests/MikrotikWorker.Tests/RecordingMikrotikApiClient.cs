using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Tests;

internal sealed class RecordingMikrotikApiClient : IMikrotikApiClient
{
    private int _callCount;

    public int CallCount => _callCount;

    public List<MikrotikConnectionOptions> Connections { get; } = [];

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
