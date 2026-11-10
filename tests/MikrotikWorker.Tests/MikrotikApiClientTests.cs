using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests;

public sealed class MikrotikApiClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void GlobalMikroTikOptions_CarrySharedCredentials_WithoutPerDeviceHost()
    {
        Type optionsType = typeof(MikroTikOptions);

        Assert.Null(optionsType.GetProperty("Host"));
        Assert.Null(optionsType.GetProperty("Address"));
        Assert.NotNull(optionsType.GetProperty("Username"));
        Assert.NotNull(optionsType.GetProperty("Password"));
    }

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

    private static MikrotikApiClient CreateClient(IMikrotikConnectionFactory factory) =>
        new(factory, Options.Create(CreateGlobalOptions()), NullLogger<MikrotikApiClient>.Instance);

    private static MikroTikOptions CreateGlobalOptions() => new()
    {
        Enabled = false,
        Port = 8728,
        ConnectionTimeout = TimeSpan.FromSeconds(10),
        ReconnectInterval = TimeSpan.FromSeconds(30),
    };

    private static MikrotikConnectionOptions CreateConnection(string host, int? port = null) =>
        new()
        {
            Host = host,
            Port = port,
            Username = "admin",
            Password = "test-password",
        };

    private static TikFakeConnection CreateFullyPopulatedFakeConnection() =>
        FakeMikrotikConnections.CreateFullyPopulated();
}
