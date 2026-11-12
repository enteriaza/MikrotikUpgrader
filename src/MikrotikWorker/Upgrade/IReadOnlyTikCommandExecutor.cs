// -----------------------------------------------------------------------------
// File: IReadOnlyTikCommandExecutor.cs
//
// Description:
// Issues only permitted read-only RouterOS print commands.
//
// -----------------------------------------------------------------------------

using tik4net;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Issues only permitted read-only RouterOS print commands.
/// </summary>
internal interface IReadOnlyTikCommandExecutor
{
    /// <summary>
    /// Exact command texts issued on this executor instance (for audit reporting).
    /// </summary>
    IReadOnlyList<string> CommandsIssued { get; }

    /// <summary>Runs <c>/system/identity/print</c> and returns the identity name when present.</summary>
    Task<string?> ExecuteIdentityPrintAsync(ITikConnection connection, CancellationToken cancellationToken);

    /// <summary>Runs <c>/system/resource/print</c> for version/architecture fields.</summary>
    Task<ITikReSentence> ExecuteResourcePrintAsync(ITikConnection connection, CancellationToken cancellationToken);

    /// <summary>Runs <c>/system/package/print</c> and returns all reply sentences.</summary>
    Task<IReadOnlyList<ITikReSentence>> ExecutePackagePrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs <c>/system/routerboard/print</c> when supported; may return null when unavailable.
    /// </summary>
    Task<ITikReSentence?> ExecuteRouterboardPrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken);
}
