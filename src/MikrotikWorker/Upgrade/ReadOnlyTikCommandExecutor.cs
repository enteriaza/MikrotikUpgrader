// -----------------------------------------------------------------------------
// File: ReadOnlyTikCommandExecutor.cs
//
// Description:
// Issues only permitted read-only RouterOS print commands for live inventory
// audits.
//
// -----------------------------------------------------------------------------

using tik4net;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Issues only permitted read-only RouterOS print commands for live inventory audits.
/// </summary>
internal sealed class ReadOnlyTikCommandExecutor : IReadOnlyTikCommandExecutor
{
    /// <summary>
    /// Exact RouterOS print commands permitted by the read-only audit.
    /// </summary>
    internal static readonly HashSet<string> AllowedCommands = new(StringComparer.Ordinal)
    {
        "/system/identity/print",
        "/system/resource/print",
        "/system/package/print",
        "/system/routerboard/print",
    };

    /// <summary>
    /// Commands issued so far, in order.
    /// </summary>
    private readonly List<string> _commands = [];

    /// <summary>
    /// Exact command paths this executor has sent to the router, in issue order. Every entry is a member of
    /// <see cref="AllowedCommands"/>, so the list serves as audit evidence that only read-only prints ran.
    /// </summary>
    public IReadOnlyList<string> CommandsIssued => _commands;

    /// <summary>
    /// Reads the router identity with <c>/system/identity/print</c>.
    /// </summary>
    /// <param name="connection">Open RouterOS connection.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>The identity text, or null when none is returned.</returns>
    public async Task<string?> ExecuteIdentityPrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        ITikCommand command = CreateAllowedCommand(connection, "/system/identity/print");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the single resource row with <c>/system/resource/print</c>.
    /// </summary>
    /// <param name="connection">Open RouterOS connection.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>The resource row.</returns>
    public async Task<ITikReSentence> ExecuteResourcePrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        ITikCommand command = CreateAllowedCommand(connection, "/system/resource/print");
        return await command.ExecuteSingleRowAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads all installed package rows with <c>/system/package/print</c>.
    /// </summary>
    /// <param name="connection">Open RouterOS connection.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>The package rows.</returns>
    public async Task<IReadOnlyList<ITikReSentence>> ExecutePackagePrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        ITikCommand command = CreateAllowedCommand(connection, "/system/package/print");
        IEnumerable<ITikReSentence> rows = await command.ExecuteListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>
    /// Reads the RouterBOARD row with <c>/system/routerboard/print</c>. Any failure is treated as the data being unavailable, for example on x86 or CHR.
    /// </summary>
    /// <param name="connection">Open RouterOS connection.</param>
    /// <param name="cancellationToken">Token to cancel the request.</param>
    /// <returns>The RouterBOARD row, or null when unavailable.</returns>
    public async Task<ITikReSentence?> ExecuteRouterboardPrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        ITikCommand command = CreateAllowedCommand(connection, "/system/routerboard/print");
        try
        {
            return await command.ExecuteSingleRowAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Exact allowlist match only — prefix/substring matches are rejected.
    /// </summary>
    internal static void EnsureAllowed(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!AllowedCommands.Contains(command))
        {
            throw new InvalidOperationException(
                $"Read-only inventory collector refused disallowed RouterOS command '{command}'.");
        }
    }

    /// <summary>
    /// Creates a parameterless command after checking it against the allowlist, and records it.
    /// </summary>
    /// <param name="connection">Open RouterOS connection.</param>
    /// <param name="commandText">Command to create.</param>
    /// <returns>The command, ready to execute.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the command is not on the allowlist.</exception>
    private ITikCommand CreateAllowedCommand(ITikConnection connection, string commandText)
    {
        EnsureAllowed(commandText);
        _commands.Add(commandText);
        // No parameters: print-only, no write-capable arguments.
        return connection.CreateCommand(commandText);
    }
}
