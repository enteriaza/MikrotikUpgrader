using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace MikrotikWorker;

internal sealed class KestrelListeningConfigureOptions(IOptions<ListeningOptions> listeningOptions)
    : IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ListeningOptions listening = listeningOptions.Value;
        options.Listen(IPAddress.Parse(listening.IpAddress), listening.Port);
    }
}
