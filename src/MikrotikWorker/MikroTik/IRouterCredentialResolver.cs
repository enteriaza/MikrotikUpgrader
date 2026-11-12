// -----------------------------------------------------------------------------
// File: IRouterCredentialResolver.cs
//
// Description:
// Credential resolution for scheduled router jobs. CSV rows never supply
// passwords; the production resolver returns shared Mikrotik:Username/Password.
//
// -----------------------------------------------------------------------------

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
/// <param name="Username">RouterOS API username.</param>
/// <param name="Password">RouterOS API password.</param>
internal sealed record RouterCredentials(string Username, string Password);

/// <summary>
/// Returns the shared username/password from <see cref="MikroTikOptions"/> for every router.
/// </summary>
internal sealed class AppSettingsRouterCredentialResolver(IOptions<MikroTikOptions> options)
    : IRouterCredentialResolver
{
    /// <inheritdoc />
    /// <remarks>
    /// Returns <c>null</c> when username is whitespace or password is null/empty
    /// (password may be intentionally empty only if configured as non-null empty — empty fails here).
    /// </remarks>
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
