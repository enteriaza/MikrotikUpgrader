using System.Text;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Infrastructure.Fakes;
internal static class FakeMikrotikConnections
{
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

    public static TikFakeConnection CreateMissingIdentity() =>
        CreateFullyPopulated(identity: " ");

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

    public static TikFakeConnection CreateInfoRetrievalFailure() =>
        new TikFakeConnection()
            .WithTrap(
                static rows => rows.First() == "/system/identity/print",
                "cannot read identity");

    /// <summary>
    /// Fully populated device info plus installed package inventory for live read-only audits.
    /// </summary>
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
/// Records RouterOS command texts issued through <see cref="ITikConnection.CreateCommand(string, ITikCommandParameter[])"/>.
/// </summary>
internal sealed class CommandRecordingConnection(ITikConnection inner) : DisposeTrackingConnection(inner)
{
    private readonly List<string> _commands = [];

    public IReadOnlyList<string> Commands => _commands;

    public override ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters)
    {
        _commands.Add(commandText);
        return base.CreateCommand(commandText, parameters);
    }

    public override ITikCommand CreateCommand(
        string commandText,
        TikCommandParameterFormat defaultParameterFormat,
        params ITikCommandParameter[] parameters)
    {
        _commands.Add(commandText);
        return base.CreateCommand(commandText, defaultParameterFormat, parameters);
    }

    public override ITikCommand CreateCommandAndParameters(string commandText, params string[] parameterNamesAndValues)
    {
        _commands.Add(commandText);
        return base.CreateCommandAndParameters(commandText, parameterNamesAndValues);
    }

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
internal class DisposeTrackingConnection(ITikConnection inner) : ITikConnection
{
    public int DisposeCount { get; private set; }

    public bool DebugEnabled
    {
        get => inner.DebugEnabled;
        set => inner.DebugEnabled = value;
    }

    public bool IsOpened => inner.IsOpened;

    public Encoding Encoding
    {
        get => inner.Encoding;
        set => inner.Encoding = value;
    }

    public int SendTimeout
    {
        get => inner.SendTimeout;
        set => inner.SendTimeout = value;
    }

    public int ReceiveTimeout
    {
        get => inner.ReceiveTimeout;
        set => inner.ReceiveTimeout = value;
    }

    public int ConnectTimeout
    {
        get => inner.ConnectTimeout;
        set => inner.ConnectTimeout = value;
    }

    public event EventHandler<TikConnectionCommCallbackEventArgs>? OnReadRow
    {
        add => inner.OnReadRow += value;
        remove => inner.OnReadRow -= value;
    }

    public event EventHandler<TikConnectionCommCallbackEventArgs>? OnWriteRow
    {
        add => inner.OnWriteRow += value;
        remove => inner.OnWriteRow -= value;
    }

    public void Open(string host, string user, string password) => inner.Open(host, user, password);

    public void Open(string host, int port, string user, string password) =>
        inner.Open(host, port, user, password);

    public Task OpenAsync(string host, string user, string password, CancellationToken cancellationToken) =>
        inner.OpenAsync(host, user, password, cancellationToken);

    public Task OpenAsync(string host, int port, string user, string password, CancellationToken cancellationToken) =>
        inner.OpenAsync(host, port, user, password, cancellationToken);

    public void Close() => inner.Close();

    public virtual ITikCommand CreateCommand() => inner.CreateCommand();

    public virtual ITikCommand CreateCommand(TikCommandParameterFormat defaultParameterFormat) =>
        inner.CreateCommand(defaultParameterFormat);

    public virtual ITikCommand CreateCommand(string commandText, params ITikCommandParameter[] parameters) =>
        inner.CreateCommand(commandText, parameters);

    public virtual ITikCommand CreateCommand(
        string commandText,
        TikCommandParameterFormat defaultParameterFormat,
        params ITikCommandParameter[] parameters) =>
        inner.CreateCommand(commandText, defaultParameterFormat, parameters);

    public virtual ITikCommand CreateCommandAndParameters(string commandText, params string[] parameterNamesAndValues) =>
        inner.CreateCommandAndParameters(commandText, parameterNamesAndValues);

    public virtual ITikCommand CreateCommandAndParameters(
        string commandText,
        TikCommandParameterFormat defaultParameterFormat,
        params string[] parameterNamesAndValues) =>
        inner.CreateCommandAndParameters(commandText, defaultParameterFormat, parameterNamesAndValues);

    public ITikCommandParameter CreateParameter(string name, string? value) =>
        inner.CreateParameter(name, value);

    public ITikCommandParameter CreateParameter(
        string name,
        string? value,
        TikCommandParameterFormat parameterFormat) =>
        inner.CreateParameter(name, value, parameterFormat);

    /// <summary>
    /// When true, a second <see cref="Dispose"/> throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    public bool FailOnDoubleDispose { get; init; }

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
