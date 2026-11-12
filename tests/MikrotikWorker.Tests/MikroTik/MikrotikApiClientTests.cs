// -----------------------------------------------------------------------------
// File: MikrotikApiClientTests.cs
//
// Description:
// Tests MikrotikApiClient device-info and router operations against scripted
// tik4net connections / fakes.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.MikroTik;
/// <summary>
/// Covers MikrotikApiClient API operations against fakes.
/// </summary>
public sealed class MikrotikApiClientTests
{
    /// <summary>Upper bound for each asynchronous API client call in these tests.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Scenario: The shape of <c>MikroTikOptions</c> is inspected by reflection.
    /// Expected: It exposes <c>Username</c> and <c>Password</c> but has no <c>Host</c> or <c>Address</c> property.
    /// Invariant: Global options hold only shared credentials; each router's address comes from its own connection options.
    /// </summary>
    [Fact]
    public void GlobalMikroTikOptions_CarrySharedCredentials_WithoutPerDeviceHost()
    {
        Type optionsType = typeof(MikroTikOptions);

        Assert.Null(optionsType.GetProperty("Host"));
        Assert.Null(optionsType.GetProperty("Address"));
        Assert.NotNull(optionsType.GetProperty("Username"));
        Assert.NotNull(optionsType.GetProperty("Password"));
    }

    /// <summary>
    /// Scenario: <c>MikroTikOptionsValidator</c> validates one options instance that is disabled with blank
    /// credentials, and another that is enabled with a blank username and the password <c>secret</c>.
    /// Expected: The disabled options pass; the enabled options fail, and no failure message contains the password.
    /// Invariant: Credentials are only required when the integration is enabled, and secrets never appear in
    /// validation failure text.
    /// </summary>
    [Fact]
    public void GlobalOptionsValidation_RequiresCredentialsOnlyWhenEnabled()
    {
        MikroTikOptionsValidator validator = new();

        ValidateOptionsResult disabled = validator.Validate(
            name: null,
            new MikroTikOptions
            {
                Enabled = false,
                Username = string.Empty,
                Password = string.Empty,
            });
        Assert.False(disabled.Failed);

        ValidateOptionsResult enabledMissingUser = validator.Validate(
            name: null,
            new MikroTikOptions
            {
                Enabled = true,
                Username = string.Empty,
                Password = "secret",
            });
        Assert.True(enabledMissingUser.Failed);
        Assert.DoesNotContain(
            enabledMissingUser.Failures,
            static failure => failure.Contains("secret", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: <c>MikroTikOptionsValidator</c> validates options with port 70000, a zero connection timeout and a
    /// negative reconnect interval.
    /// Expected: Validation fails with separate failures that mention <c>Port</c>, <c>ConnectionTimeout</c> and
    /// <c>ReconnectInterval</c>.
    /// Invariant: Each invalid setting is reported by name so the operator can correct it.
    /// </summary>
    [Fact]
    public void GlobalOptionsValidation_RejectsNonPositiveTimeoutsAndInvalidPort()
    {
        MikroTikOptionsValidator validator = new();

        ValidateOptionsResult result = validator.Validate(
            name: null,
            new MikroTikOptions
            {
                Port = 70000,
                ConnectionTimeout = TimeSpan.Zero,
                ReconnectInterval = TimeSpan.FromSeconds(-1),
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, static failure => failure.Contains("Port", StringComparison.Ordinal));
        Assert.Contains(result.Failures, static failure => failure.Contains("ConnectionTimeout", StringComparison.Ordinal));
        Assert.Contains(result.Failures, static failure => failure.Contains("ReconnectInterval", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: <c>MikrotikConnectionOptionsValidator.Validate</c> is called once with a whitespace-only host and
    /// once with an empty username.
    /// Expected: Both calls throw <c>ArgumentException</c>.
    /// Invariant: A device connection cannot be attempted without a host and a username.
    /// </summary>
    [Fact]
    public void DeviceConnectionOptionsValidation_RejectsMissingHostAndUsername()
    {
        Assert.Throws<ArgumentException>(() => MikrotikConnectionOptionsValidator.Validate(
            new MikrotikConnectionOptions
            {
                Host = " ",
                Username = "admin",
                Password = "secret",
            }));

        Assert.Throws<ArgumentException>(() => MikrotikConnectionOptionsValidator.Validate(
            new MikrotikConnectionOptions
            {
                Host = "192.168.88.1",
                Username = "",
                Password = "secret",
            }));
    }

    /// <summary>
    /// Scenario: One client requests device info for 192.168.88.1 (default port) and then for 10.0.0.2 on port 8729,
    /// using a scripted connection factory.
    /// Expected: The factory receives two connections in order, with hosts 192.168.88.1 and 10.0.0.2 and the second
    /// using port 8729.
    /// Invariant: Host and port come from each call's connection options, not from shared client state.
    /// </summary>
    [Fact]
    public async Task DifferentRouters_CanBeSuppliedIndependently()
    {
        ScriptedMikrotikConnectionFactory factory = new(_ => CreateFullyPopulatedFakeConnection());
        MikrotikApiClient client = CreateClient(factory);

        await client.GetDeviceInfoAsync(CreateConnection("192.168.88.1"), CancellationToken.None);
        await client.GetDeviceInfoAsync(CreateConnection("10.0.0.2", port: 8729), CancellationToken.None);

        Assert.Equal(2, factory.Connections.Count);
        Assert.Equal("192.168.88.1", factory.Connections[0].Host);
        Assert.Equal("10.0.0.2", factory.Connections[1].Host);
        Assert.Equal(8729, factory.Connections[1].Port);
    }

    /// <summary>
    /// Scenario: <c>GetDeviceInfoAsync</c> reads a fully populated fake router (identity, resource and routerboard
    /// replies).
    /// Expected: The result maps identity Office-AP, version 6.49.10 (long-term), arm, RB4011iGS+, MikroTik, uptime
    /// 1d2h3m4s, and the routerboard model plus current (7.15.3), upgrade (7.16) and factory (7.12) firmware.
    /// Invariant: Every RouterOS reply field is mapped to the matching <c>MikrotikDeviceInfo</c> property.
    /// </summary>
    [Fact]
    public async Task GetDeviceInfoAsync_MapsRouterOsFields()
    {
        ScriptedMikrotikConnectionFactory factory = new(_ => CreateFullyPopulatedFakeConnection());
        MikrotikApiClient client = CreateClient(factory);

        MikrotikDeviceInfo info = await client.GetDeviceInfoAsync(CreateConnection("192.168.88.1"));

        Assert.Equal("Office-AP", info.Identity);
        Assert.Equal("6.49.10 (long-term)", info.Version);
        Assert.Equal("arm", info.ArchitectureName);
        Assert.Equal("RB4011iGS+", info.BoardName);
        Assert.Equal("MikroTik", info.Platform);
        Assert.Equal("1d2h3m4s", info.Uptime);
        Assert.Equal("RB4011iGS+", info.RouterboardModel);
        Assert.Equal("7.15.3", info.RouterboardCurrentFirmware);
        Assert.Equal("7.16", info.RouterboardUpgradeFirmware);
        Assert.Equal("7.12", info.RouterboardFactoryFirmware);
    }

    /// <summary>
    /// Scenario: A fake CHR-style router answers identity and resource but traps <c>/system/routerboard/print</c> with
    /// "no such command".
    /// Expected: Device info is still returned (identity NoBoard, version 7.14) with null current and upgrade
    /// RouterBOARD firmware.
    /// Invariant: Routers without a RouterBOARD do not fail device discovery.
    /// </summary>
    [Fact]
    public async Task GetDeviceInfoAsync_HandlesMissingRouterboardInformation()
    {
        TikFakeConnection fake = new TikFakeConnection()
            .WithResponse(
                static rows => rows.First() == "/system/identity/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string> { ["name"] = "NoBoard" }),
                    new TikFakeDoneSentence(),
                ])
            .WithResponse(
                static rows => rows.First() == "/system/resource/print",
                [
                    new TikFakeReSentence(new Dictionary<string, string>
                    {
                        ["version"] = "7.14",
                        ["architecture-name"] = "x86",
                        ["board-name"] = "CHR",
                        ["platform"] = "MikroTik",
                        ["uptime"] = "10m",
                    }),
                    new TikFakeDoneSentence(),
                ])
            .WithTrap(
                static rows => rows.First() == "/system/routerboard/print",
                "no such command");

        MikrotikApiClient client = CreateClient(new ScriptedMikrotikConnectionFactory(_ => fake));

        MikrotikDeviceInfo info = await client.GetDeviceInfoAsync(CreateConnection("10.0.0.5"));

        Assert.Equal("NoBoard", info.Identity);
        Assert.Equal("7.14", info.Version);
        Assert.Null(info.RouterboardCurrentFirmware);
        Assert.Null(info.RouterboardUpgradeFirmware);
    }

    /// <summary>
    /// Scenario: The connection factory throws <c>InvalidOperationException("connection refused")</c> while a
    /// recording logger captures output.
    /// Expected: The same exception, with message <c>connection refused</c>, reaches the caller, and a log entry
    /// contains <c>Failed to communicate with MikroTik device</c>.
    /// Invariant: Connection failures are logged once and rethrown unwrapped.
    /// </summary>
    [Fact]
    public async Task GetDeviceInfoAsync_PropagatesConnectionFailuresCleanly()
    {
        RecordingLoggerProvider loggerProvider = new();
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(loggerProvider);
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        MikrotikApiClient client = new(
            new ThrowingMikrotikConnectionFactory(new InvalidOperationException("connection refused")),
            Options.Create(CreateGlobalOptions()),
            loggerFactory.CreateLogger<MikrotikApiClient>());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetDeviceInfoAsync(CreateConnection("192.168.1.50")));

        Assert.Equal("connection refused", exception.Message);
        Assert.Contains(
            loggerProvider.Messages,
            static message => message.Contains("Failed to communicate with MikroTik device", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: <c>GetDeviceInfoAsync</c> is called with a token that is already cancelled, and the connection
    /// factory throws if it is ever invoked.
    /// Expected: An <c>OperationCanceledException</c> is thrown and the factory is not called.
    /// Invariant: A cancelled request never opens a connection to the router.
    /// </summary>
    [Fact]
    public async Task GetDeviceInfoAsync_RespectsCancellation()
    {
        ScriptedMikrotikConnectionFactory factory = new(_ =>
        {
            throw new InvalidOperationException("Factory should not be called after cancellation.");
        });
        MikrotikApiClient client = CreateClient(factory);

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetDeviceInfoAsync(CreateConnection("192.168.88.1"), cts.Token));
    }

    /// <summary>
    /// Scenario: Device info is fetched with the password <c>SuperSecretPassword!42</c> while a recording logger
    /// captures everything at Trace level.
    /// Expected: No log message contains the password or the text <c>Password=</c>.
    /// Invariant: Secrets never appear in log output, even at the most verbose level.
    /// </summary>
    [Fact]
    public async Task GetDeviceInfoAsync_NeverLogsCredentials()
    {
        const string password = "SuperSecretPassword!42";
        RecordingLoggerProvider loggerProvider = new();
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(loggerProvider);
            builder.SetMinimumLevel(LogLevel.Trace);
        });

        MikrotikApiClient client = new(
            new ScriptedMikrotikConnectionFactory(_ => CreateFullyPopulatedFakeConnection()),
            Options.Create(CreateGlobalOptions()),
            loggerFactory.CreateLogger<MikrotikApiClient>());

        await client.GetDeviceInfoAsync(
            new MikrotikConnectionOptions
            {
                Host = "192.168.88.1",
                Username = "admin",
                Password = password,
            }).WaitAsync(TestTimeout);

        Assert.DoesNotContain(
            loggerProvider.Messages,
            message => message.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(
            loggerProvider.Messages,
            static message => message.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Creates a <c>MikrotikApiClient</c> over the supplied connection factory using default global options and a
    /// null logger.
    /// </summary>
    private static MikrotikApiClient CreateClient(IMikrotikConnectionFactory factory) =>
        new(factory, Options.Create(CreateGlobalOptions()), NullLogger<MikrotikApiClient>.Instance);

    /// <summary>
    /// Helper used by this suite.
    /// Key steps: Enabled = false,; Port = 8728,; ConnectionTimeout = TimeSpan.FromSeconds(10),.
    /// </summary>
    private static MikroTikOptions CreateGlobalOptions() => new()
    {
        Enabled = false,
        Port = 8728,
        ConnectionTimeout = TimeSpan.FromSeconds(10),
        ReconnectInterval = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Private helper <c>CreateConnection</c> supporting the scenarios in this file.
    /// </summary>
    private static MikrotikConnectionOptions CreateConnection(string host, int? port = null) =>
        new()
        {
            Host = host,
            Port = port,
            Username = "admin",
            Password = "test-password",
        };

    /// <summary>
    /// Private helper <c>CreateFullyPopulatedFakeConnection</c> supporting the scenarios in this file.
    /// </summary>
    private static TikFakeConnection CreateFullyPopulatedFakeConnection() =>
        FakeMikrotikConnections.CreateFullyPopulated();
}
