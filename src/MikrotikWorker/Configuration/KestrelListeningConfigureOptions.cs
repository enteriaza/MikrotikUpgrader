// -----------------------------------------------------------------------------
// File: KestrelListeningConfigureOptions.cs
//
// Description:
// IConfigureOptions that binds KestrelServerOptions.Listen to ListeningOptions
// IP and port so PreferHostingUrls(false) uses the configured endpoint only.
//
// -----------------------------------------------------------------------------

using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace MikrotikWorker.Configuration;

/// <summary>
/// Applies <see cref="ListeningOptions"/> to Kestrel's listen endpoints.
/// </summary>
internal sealed class KestrelListeningConfigureOptions(IOptions<ListeningOptions> listeningOptions)
    : IConfigureOptions<KestrelServerOptions>
{
    /// <inheritdoc />
    public void Configure(KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ListeningOptions listening = listeningOptions.Value;
        options.Listen(IPAddress.Parse(listening.IpAddress), listening.Port);
    }
}
