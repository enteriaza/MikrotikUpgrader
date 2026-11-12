using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
internal sealed class StaticRouterCredentialResolver(string username = "test-user", string password = "test-pass")
    : IRouterCredentialResolver
{
    public ValueTask<RouterCredentials?> ResolveAsync(
        MikrotikScheduledJob job,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RouterCredentials?>(new RouterCredentials(username, password));
    }
}
