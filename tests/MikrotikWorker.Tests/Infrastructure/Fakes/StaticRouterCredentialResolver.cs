// -----------------------------------------------------------------------------
// File: StaticRouterCredentialResolver.cs
//
// Description:
// IRouterCredentialResolver fake returning fixed credentials for every job.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
/// <summary>
/// Credential resolver fake with fixed username/password.
/// </summary>
/// <param name="username">User name returned for every job.</param>
/// <param name="password">Password returned for every job.</param>
internal sealed class StaticRouterCredentialResolver(string username = "test-user", string password = "test-pass")
    : IRouterCredentialResolver
{
    /// <summary>Returns the fixed credentials regardless of <paramref name="job"/>.</summary>
    /// <param name="job">Job being resolved (ignored).</param>
    /// <param name="cancellationToken">Token checked before returning.</param>
    /// <returns>The configured user name and password.</returns>
    public ValueTask<RouterCredentials?> ResolveAsync(
        MikrotikScheduledJob job,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RouterCredentials?>(new RouterCredentials(username, password));
    }
}
