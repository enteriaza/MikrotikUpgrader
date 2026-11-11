using tik4net;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Issues only permitted read-only RouterOS print commands.
/// </summary>
internal interface IReadOnlyTikCommandExecutor
{
    IReadOnlyList<string> CommandsIssued { get; }

    Task<string?> ExecuteIdentityPrintAsync(ITikConnection connection, CancellationToken cancellationToken);

    Task<ITikReSentence> ExecuteResourcePrintAsync(ITikConnection connection, CancellationToken cancellationToken);

    Task<IReadOnlyList<ITikReSentence>> ExecutePackagePrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken);

    Task<ITikReSentence?> ExecuteRouterboardPrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken);
}
