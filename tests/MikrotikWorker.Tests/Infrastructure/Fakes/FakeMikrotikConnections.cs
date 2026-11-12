// -----------------------------------------------------------------------------
// File: FakeMikrotikConnections.cs
//
// Description:
// Fake ITikConnection implementations and helpers used across MikroTik and upgrade tests.
//
// -----------------------------------------------------------------------------

using System.Text;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;

/// <summary>
/// Factory helpers that build <c>TikFakeConnection</c> scripts for device-info and API tests.
/// </summary>
internal static class FakeMikrotikConnections
{
    /// <summary>Builds a fake with identity/resource and optional routerboard responses.</summary>
    /// <param name="identity">Value returned as the system identity name.</param>
    /// <param name="version">RouterOS version string returned by resource print.</param>
    /// <param name="architecture">Value returned as <c>architecture-name</c>.</param>
    /// <param name="boardName">Value returned as <c>board-name</c> and routerboard model.</param>
    /// <param name="includeRouterboard">
    /// When true, routerboard print succeeds; otherwise it returns a "no such command" trap.
    /// </param>
    /// <returns>The scripted fake connection.</returns>
    public static TikFakeConnection CreateFullyPopulated(
        string identity = "Office-AP",
        string version = "6.49.10 (long-term)",
        string architecture = "arm",
        string boardName = "RB4011iGS+",
        bool includeRouterboard = true)
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = identity }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = version,
                        ["architecture-name"] = architecture,
                        ["board-name"] = boardName,
                        ["platform"] = "MikroTik",
                        ["uptime"] = "1d2h3m4s",
                    }),
                    new TikFakeDoneSentence(),
                ]);

        if (includeRouterboard)
        {
            return fake.WithResponse(
                static rows => rows.First() == "/system/routerboard/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["model"] = boardName,
                        ["firmware-type"] = "alaxar3",
                        ["factory-firmware"] = "7.12",
                        ["current-firmware"] = "7.15.3",
                        ["upgrade-firmware"] = "7.16",
                    }),
                    new TikFakeDoneSentence(),
                ]);
        }

        return fake.WithTrap(
            static rows => rows.First() == "/system/routerboard/print",
            "no such command");
    }

    /// <summary>Fully populated fake with blank identity.</summary>
    /// <returns>A fake whose <c>/system/identity/print</c> returns a whitespace-only name.</returns>
    public static TikFakeConnection CreateMissingIdentity() =>
        CreateFullyPopulated(identity: " ");

    /// <summary>Fake whose resource print omits architecture-name.</summary>
    /// <returns>A fake with identity and resource responses but no <c>architecture-name</c> field.</returns>
    public static TikFakeConnection CreateMissingArchitecture()
    {
        return new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = "NoArch" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = "7.15.3",
                        ["board-name"] = "RB4011iGS+",
                        ["platform"] = "MikroTik",
                        ["uptime"] = "1m",
                    }),
                    new TikFakeDoneSentence(),
                ]);
    }

    /// <summary>Fake whose <c>/system/identity/print</c> returns a trap, simulating a device-info read failure.</summary>
    public static TikFakeConnection CreateInfoRetrievalFailure() =>
        new TikFakeConnection()
            .WithTrap(
                static rows => rows.First() == "/system/identity/print",
                "cannot read identity");

    /// <summary>
    /// Fully populated device info plus installed package inventory for live read-only audits.
    /// </summary>
    /// <param name="identity">Value returned as the system identity name.</param>
    /// <param name="version">RouterOS version reported for the device and every package.</param>
    /// <param name="architecture">Value returned as <c>architecture-name</c>.</param>
    /// <param name="mainPackageName">Name of the main (bundle) package row.</param>
    /// <param name="includeBundled">When true, adds bundled <c>system</c> and disabled <c>ipv6</c> package rows.</param>
    /// <param name="includeRouterboard">When true, routerboard print succeeds; otherwise it traps.</param>
    /// <returns>The scripted fake connection including <c>/system/package/print</c>.</returns>
    public static TikFakeConnection CreateWithPackageInventory(
        string identity = "AP01.POP001.001.access",
        string version = "6.49.10",
        string architecture = "mipsbe",
        string mainPackageName = "routeros-mipsbe",
        bool includeBundled = true,
        bool includeRouterboard = true)
    {
        TikFakeConnection fake = CreateFullyPopulated(
            identity: identity,
            version: version,
            architecture: architecture,
            includeRouterboard: includeRouterboard);

        List<ITikSentence> packageRows =
        [
            new TikFakeReSentence(new Dictionary<string, string>
            {
                ["name"] = mainPackageName,
                ["version"] = version,
                ["disabled"] = "false",
            }),
        ];

        if (includeBundled)
        {
            packageRows.Add(new TikFakeReSentence(new Dictionary<string, string>
            {
                ["name"] = "system",
                ["version"] = version,
                ["disabled"] = "false",
                ["bundle"] = mainPackageName,
            }));
            packageRows.Add(new TikFakeReSentence(new Dictionary<string, string>
            {
                ["name"] = "ipv6",
                ["version"] = version,
                ["disabled"] = "true",
                ["bundle"] = mainPackageName,
            }));
        }

        packageRows.Add(new TikFakeReSentence(new Dictionary<string, string>
        {
            ["name"] = "user-manager",
            ["version"] = version,
            ["disabled"] = "false",
        }));
        packageRows.Add(new TikFakeDoneSentence());

        return fake.WithResponse(
            static rows => rows.First() == "/system/package/print",
            packageRows);
    }
}

/// <summary>
/// Fake connection that records issued command paths for assertions via
/// <see cref="ITikConnection.CreateCommand(string, ITikCommandParameter[])"/>.
/// </summary>
/// <param name="inner">Wrapped connection that receives all forwarded calls.</param>
internal sealed class CommandRecordingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
{
    /// <summary>Backing list for <see cref="Commands"/>.</summary>
    private readonly List<string> _commands = [];

    /// <summary>Command texts passed to any <c>CreateCommand*</c> overload, in call order.</summary>
    public IReadOnlyList<string> Commands => _commands;

    /// <summary>Records <paramref name="commandText"/> and creates the command on the inner connection.</summary>
    /// <param name="commandText">Command path, for example <c>/system/identity/print</c>.</param>
    /// <param name="parameters">Command parameters forwarded unchanged.</param>
    /// <returns>The command created by the inner connection.</returns>
    public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
    {
        _commands.Add(commandText);
        return base.CreateCommand(commandText, parameters);
    }

    /// <summary>Records <paramref name="commandText"/> and creates the command with an explicit parameter format.</summary>
    /// <param name="commandText">Command path, for example <c>/system/identity/print</c>.</param>
    /// <param name="defaultParameterFormat">Default format applied to parameters.</param>
    /// <param name="parameters">Command parameters forwarded unchanged.</param>
    /// <returns>The command created by the inner connection.</returns>
    public override ITikCommand CreateCommand(
        string commandText,
        TikCommandParameterFormat defaultParameterFormat,
        params ITikCommandParameter[] parameters)
    {
        _commands.Add(commandText);
        return base.CreateCommand(commandText, defaultParameterFormat, parameters);
    }

    /// <summary>Records <paramref name="commandText"/> and creates the command from name/value parameter pairs.</summary>
    /// <param name="commandText">Command path, for example <c>/system/identity/print</c>.</param>
    /// <param name="parameterNamesAndValues">Alternating parameter names and values forwarded unchanged.</param>
    /// <returns>The command created by the inner connection.</returns>
    public override ITikCommand CreateCommandAndParameters(string commandText, params string[] parameterNamesAndValues)
    {
        _commands.Add(commandText);
        return base.CreateCommandAndParameters(commandText, parameterNamesAndValues);
    }

    /// <summary>
    /// Records <paramref name="commandText"/> and creates the command from name/value pairs with an explicit
    /// parameter format.
    /// </summary>
    /// <param name="commandText">Command path, for example <c>/system/identity/print</c>.</param>
    /// <param name="defaultParameterFormat">Default format applied to parameters.</param>
    /// <param name="parameterNamesAndValues">Alternating parameter names and values forwarded unchanged.</param>
    /// <returns>The command created by the inner connection.</returns>
    public override ITikCommand CreateCommandAndParameters(
        string commandText,
        TikCommandParameterFormat defaultParameterFormat,
        params string[] parameterNamesAndValues)
    {
        _commands.Add(commandText);
        return base.CreateCommandAndParameters(commandText, defaultParameterFormat, parameterNamesAndValues);
    }
}

/// <summary>
/// Forwards to an inner connection and records Dispose calls.
/// </summary>
/// <param name="inner">Wrapped connection that receives all forwarded calls.</param>
internal class DisposeTrackingConnection(ITikConnection inner) : ITikConnection
{
    /// <summary>Number of times <see cref="Dispose"/> has completed on this wrapper.</summary>
    public int DisposeCount { get; private set; }

    /// <summary>
    /// Debug-logging flag of the wrapped connection. Reads and writes pass straight through to the inner connection,
    /// so the decorator keeps no state of its own.
    /// </summary>
    public bool DebugEnabled
    {
        get => inner.DebugEnabled;
        set => inner.DebugEnabled = value;
    }

    /// <summary>Gets whether the wrapped connection is currently open.</summary>
    public bool IsOpened => inner.IsOpened;

    /// <summary>
    /// Text encoding of the wrapped connection. Reads and writes pass straight through to the inner connection, so
    /// the decorator keeps no state of its own.
    /// </summary>
    public Encoding Encoding
    {
        get => inner.Encoding;
        set => inner.Encoding = value;
    }

    /// <summary>
    /// Send timeout in milliseconds of the wrapped connection. Reads and writes pass straight through to the inner
    /// connection, so tests can observe any value production code assigns.
    /// </summary>
    public int SendTimeout
    {
        get => inner.SendTimeout;
        set => inner.SendTimeout = value;
    }

    /// <summary>
    /// Receive timeout in milliseconds of the wrapped connection. Reads and writes pass straight through to the inner
    /// connection, so tests can observe any value production code assigns.
    /// </summary>
    public int ReceiveTimeout
    {
        get => inner.ReceiveTimeout;
        set => inner.ReceiveTimeout = value;
    }

    /// <summary>
    /// Connect timeout in milliseconds of the wrapped connection. Reads and writes pass straight through to the inner
    /// connection, so tests can observe any value production code assigns.
    /// </summary>
    public int ConnectTimeout
    {
        get => inner.ConnectTimeout;
        set => inner.ConnectTimeout = value;
    }

    /// <summary>Raised by the wrapped connection when a row is read; subscriptions are forwarded.</summary>
    public event EventHandler<TikConnectionCommCallbackEventArgs>? OnReadRow
    {
        add => inner.OnReadRow += value;
        remove => inner.OnReadRow -= value;
    }

    /// <summary>Raised by the wrapped connection when a row is written; subscriptions are forwarded.</summary>
    public event EventHandler<TikConnectionCommCallbackEventArgs>? OnWriteRow
    {
        add => inner.OnWriteRow += value;
        remove => inner.OnWriteRow -= value;
    }

    /// <summary>Opens the wrapped connection on the default API port.</summary>
    /// <param name="host">Router host name or address.</param>
    /// <param name="user">User name.</param>
    /// <param name="password">Password.</param>
    public void Open(string host, string user, string password) => inner.Open(host, user, password);

    /// <summary>Opens the wrapped connection on an explicit port.</summary>
    /// <param name="host">Router host name or address.</param>
    /// <param name="port">API port.</param>
    /// <param name="user">User name.</param>
    /// <param name="password">Password.</param>
    public void Open(string host, int port, string user, string password) =>
        inner.Open(host, port, user, password);

    /// <summary>Asynchronously opens the wrapped connection on the default API port.</summary>
    /// <param name="host">Router host name or address.</param>
    /// <param name="user">User name.</param>
    /// <param name="password">Password.</param>
    /// <param name="cancellationToken">Token that cancels the open operation.</param>
    /// <returns>A task that completes when the connection is open.</returns>
    public Task OpenAsync(string host, string user, string password, CancellationToken cancellationToken) =>
        inner.OpenAsync(host, user, password, cancellationToken);

    /// <summary>Asynchronously opens the wrapped connection on an explicit port.</summary>
    /// <param name="host">Router host name or address.</param>
    /// <param name="port">API port.</param>
    /// <param name="user">User name.</param>
    /// <param name="password">Password.</param>
    /// <param name="cancellationToken">Token that cancels the open operation.</param>
    /// <returns>A task that completes when the connection is open.</returns>
    public Task OpenAsync(string host, int port, string user, string password, CancellationToken cancellationToken) =>
        inner.OpenAsync(host, port, user, password, cancellationToken);

    /// <summary>Closes the wrapped connection without disposing it.</summary>
    public void Close() => inner.Close();

    /// <summary>Creates an empty command on the wrapped connection.</summary>
    /// <returns>A new command with no command text.</returns>
    public virtual ITikCommand CreateCommand() => inner.CreateCommand();

    /// <summary>Creates an empty command with an explicit default parameter format.</summary>
    /// <param name="defaultParameterFormat">Default format applied to parameters.</param>
    /// <returns>A new command with no command text.</returns>
    public virtual ITikCommand CreateCommand(TikCommandParameterFormat defaultParameterFormat) =>
        inner.CreateCommand(defaultParameterFormat);

    /// <summary>Creates a command for <paramref name="commandText"/> on the wrapped connection.</summary>
    /// <param name="commandText">Command path, for example <c>/system/identity/print</c>.</param>
    /// <param name="parameters">Command parameters.</param>
    /// <returns>The created command.</returns>
    public virtual ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters) =>
        inner.CreateCommand(commandText, parameters);

    /// <summary>Creates a command for <paramref name="commandText"/> with an explicit parameter format.</summary>
    /// <param name="commandText">Command path, for example <c>/system/identity/print</c>.</param>
    /// <param name="defaultParameterFormat">Default format applied to parameters.</param>
    /// <param name="parameters">Command parameters.</param>
    /// <returns>The created command.</returns>
    public virtual ITikCommand CreateCommand(
        string commandText,
        TikCommandParameterFormat defaultParameterFormat,
        params ITikCommandParameter[] parameters) =>
        inner.CreateCommand(commandText, defaultParameterFormat, parameters);

    /// <summary>Creates a command from <paramref name="commandText"/> and name/value parameter pairs.</summary>
    /// <param name="commandText">Command path, for example <c>/system/identity/print</c>.</param>
    /// <param name="parameterNamesAndValues">Alternating parameter names and values.</param>
    /// <returns>The created command.</returns>
    public virtual ITikCommand CreateCommandAndParameters(string commandText, params string[] parameterNamesAndValues) =>
        inner.CreateCommandAndParameters(commandText, parameterNamesAndValues);

    /// <summary>Creates a command from name/value parameter pairs with an explicit parameter format.</summary>
    /// <param name="commandText">Command path, for example <c>/system/identity/print</c>.</param>
    /// <param name="defaultParameterFormat">Default format applied to parameters.</param>
    /// <param name="parameterNamesAndValues">Alternating parameter names and values.</param>
    /// <returns>The created command.</returns>
    public virtual ITikCommand CreateCommandAndParameters(
        string commandText,
        TikCommandParameterFormat defaultParameterFormat,
        params string[] parameterNamesAndValues) =>
        inner.CreateCommandAndParameters(commandText, defaultParameterFormat, parameterNamesAndValues);

    /// <summary>Creates a command parameter using the wrapped connection's default format.</summary>
    /// <param name="name">Parameter name.</param>
    /// <param name="value">Parameter value, or <see langword="null"/>.</param>
    /// <returns>The created parameter.</returns>
    public ITikCommandParameter CreateParameter(string name, string? value) =>
        inner.CreateParameter(name, value);

    /// <summary>Creates a command parameter with an explicit format.</summary>
    /// <param name="name">Parameter name.</param>
    /// <param name="value">Parameter value, or <see langword="null"/>.</param>
    /// <param name="parameterFormat">Format used to encode the parameter.</param>
    /// <returns>The created parameter.</returns>
    public ITikCommandParameter CreateParameter(
        string name,
        string? value,
        TikCommandParameterFormat parameterFormat) =>
        inner.CreateParameter(name, value, parameterFormat);

    /// <summary>
    /// When true, a second <see cref="Dispose"/> throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    public bool FailOnDoubleDispose { get; init; }

    /// <summary>Increments <see cref="DisposeCount"/> and disposes the wrapped connection.</summary>
    /// <exception cref="ObjectDisposedException">
    /// When <see cref="FailOnDoubleDispose"/> is set and the connection was already disposed.
    /// </exception>
    public virtual void Dispose()
    {
        if (FailOnDoubleDispose && DisposeCount > 0)
        {
            throw new ObjectDisposedException(nameof(DisposeTrackingConnection));
        }

        DisposeCount++;
        inner.Dispose();
    }
}
