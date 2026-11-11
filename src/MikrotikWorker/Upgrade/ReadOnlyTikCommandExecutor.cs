using tik4net;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Issues only permitted read-only RouterOS print commands for live inventory audits.
/// </summary>
internal sealed class ReadOnlyTikCommandExecutor : IReadOnlyTikCommandExecutor
{
    internal static readonly HashSet<string> AllowedCommands = new(StringComparer.Ordinal)
    {
        "/system/identity/print",
        "/system/resource/print",
        "/system/package/print",
        "/system/routerboard/print",
    };

    private readonly List<string> _commands = [];

    public IReadOnlyList<string> CommandsIssued => _commands;

    public async Task<string?> ExecuteIdentityPrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        ITikCommand command = CreateAllowedCommand(connection, "/system/identity/print");
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ITikReSentence> ExecuteResourcePrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        ITikCommand command = CreateAllowedCommand(connection, "/system/resource/print");
        return await command.ExecuteSingleRowAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ITikReSentence>> ExecutePackagePrintAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        ITikCommand command = CreateAllowedCommand(connection, "/system/package/print");
        IEnumerable<ITikReSentence> rows = await command.ExecuteListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToList();
    }

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

    private ITikCommand CreateAllowedCommand(ITikConnection connection, string commandText)
    {
        EnsureAllowed(commandText);
        _commands.Add(commandText);
        // No parameters: print-only, no write-capable arguments.
        return connection.CreateCommand(commandText);
    }
}
