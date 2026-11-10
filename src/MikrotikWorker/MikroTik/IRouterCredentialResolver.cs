using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Resolves RouterOS API credentials for a scheduled job.
/// Credentials must not come from CSV files.
/// </summary>
internal interface IRouterCredentialResolver
{
    /// <summary>
    /// Returns credentials for <paramref name="job"/>, or <c>null</c> when none are available.
    /// </summary>
    ValueTask<RouterCredentials?> ResolveAsync(
        MikrotikScheduledJob job,
        CancellationToken cancellationToken);
}

/// <summary>
/// Username/password for a single RouterOS API connection. Never log these values.
/// </summary>
internal sealed record RouterCredentials(string Username, string Password);

/// <summary>
/// Returns the shared username/password from <see cref="MikroTikOptions"/> for every router.
/// </summary>
internal sealed class AppSettingsRouterCredentialResolver(IOptions<MikroTikOptions> options)
    : IRouterCredentialResolver
{
    public ValueTask<RouterCredentials?> ResolveAsync(
        MikrotikScheduledJob job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        MikroTikOptions settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrEmpty(settings.Password))
        {
            return ValueTask.FromResult<RouterCredentials?>(null);
        }

        return ValueTask.FromResult<RouterCredentials?>(
            new RouterCredentials(settings.Username, settings.Password));
    }
}
