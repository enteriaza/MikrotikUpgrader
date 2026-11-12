using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Tests.Upgrade.Live;
/// <summary>
/// Live RouterOS pre-flight against a real device.
/// Credentials come from untracked <c>src/MikrotikWorker/appsettings.Development.local.json</c>.
/// Run explicitly: <c>dotnet test --filter Category=Live</c>
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveMikrotikPreflightTests
{
    private const string LiveHost = "198.18.0.83";
    private static readonly string SummaryPath =
        Path.Combine(Path.GetTempPath(), "mikrotik-live-preflight-result.json");

    [Fact]
    public async Task LiveRouter_Preflight_ConnectReadDisconnect()
    {
        LiveMikrotikTestGate.RequireArmed(LiveMikrotikTestGate.AllowLiveMikrotikEnv, "Live");

        string? localConfigPath = FindLocalConfigPath();
        Assert.True(
            localConfigPath is not null,
            "Missing appsettings.Development.local.json (gitignored). Create it with Mikrotik Enabled/Username/Password.");

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(localConfigPath, optional: false, reloadOnChange: false)
            .Build();

        MikroTikOptions options = new();
        configuration.GetSection(MikroTikOptions.SectionName).Bind(options);

        Assert.True(options.Enabled, "Mikrotik:Enabled must be true in the local config for live pre-flight.");
        Assert.False(string.IsNullOrWhiteSpace(options.Username), "Mikrotik:Username is required.");
        Assert.False(string.IsNullOrEmpty(options.Password), "Mikrotik:Password is required.");

        Assert.True(
            await IsTcpReachableAsync(LiveHost, options.Port, TimeSpan.FromSeconds(3)),
            $"TCP connectivity failure: {LiveHost}:{options.Port} is not reachable.");

        string password = options.Password;
        RecordingLoggerProvider logs = new();
        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(logs);
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        Tik4netConnectionFactory factory = new(Options.Create(options));
        MikrotikApiClient client = new(
            factory,
            Options.Create(options),
            loggerFactory.CreateLogger<MikrotikApiClient>());

        MikrotikConnectionOptions connection = new()
        {
            Host = LiveHost,
            Username = options.Username,
            Password = password,
        };

        MikrotikDeviceInfo info;
        try
        {
            using CancellationTokenSource opCts = new(TimeSpan.FromSeconds(20));
            info = await client.GetDeviceInfoAsync(connection, opCts.Token)
                .WaitAsync(TimeSpan.FromSeconds(20), opCts.Token);
        }
        catch (Exception ex)
        {
            string category = ClassifyFailure(ex);
            await WriteSummaryAsync(new
            {
                RouterIp = LiveHost,
                Status = "Failed",
                FailureCategory = category,
                FailureType = ex.GetType().FullName,
                FailureReason = Sanitize(ex.Message, password),
            });
            Assert.Fail($"Live pre-flight failed ({category}): {ex.GetType().Name}: {Sanitize(ex.Message, password)}");
            return;
        }

        MikrotikPreflightResult result = MikrotikPreflightResult.FromDeviceInfo(
            LiveHost,
            info,
            DateTimeOffset.UtcNow,
            TimeSpan.Zero);

        await WriteSummaryAsync(new
        {
            result.RouterIp,
            result.RouterIdentity,
            result.RouterOsVersion,
            result.Architecture,
            result.BoardName,
            result.Platform,
            result.Uptime,
            result.RouterboardModel,
            result.RouterboardFirmwareType,
            result.RouterboardCurrentFirmware,
            result.RouterboardUpgradeFirmware,
            result.RouterboardFactoryFirmware,
            Status = result.Status.ToString(),
            result.FailureReason,
            DisconnectedCleanly = true,
        });

        Assert.Equal(MikrotikPreflightStatus.Succeeded, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.RouterIdentity));
        // Deliberate pre-upgrade baseline for the next authorised live run.
        Assert.Contains("6.49.19", result.RouterOsVersion, StringComparison.Ordinal);
        Assert.Equal("mipsbe", result.Architecture, ignoreCase: true);
        Assert.Equal("hEX PoE", result.BoardName, ignoreCase: true);
        Assert.Equal("960PGS", result.RouterboardModel, ignoreCase: true);
        Assert.Equal("7.23.8", result.RouterboardCurrentFirmware);
        Assert.Equal("6.49.19", result.RouterboardUpgradeFirmware);
        Assert.Equal("3.41", result.RouterboardFactoryFirmware);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(password, StringComparison.Ordinal));
        Assert.DoesNotContain(
            logs.Messages,
            static m => m.Contains("Password=", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WriteSummaryAsync(object summary)
    {
        await File.WriteAllTextAsync(
            SummaryPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? FindLocalConfigPath()
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MikrotikWorker", "appsettings.Development.local.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.local.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src", "MikrotikWorker", "appsettings.Development.local.json")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.Development.local.json")),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Sanitize(string message, string password) =>
        string.IsNullOrEmpty(password)
            ? message
            : message.Replace(password, "***", StringComparison.Ordinal);

    private static string ClassifyFailure(Exception ex) =>
        ex switch
        {
            OperationCanceledException => "Cancellation",
            TimeoutException => "Timeout",
            SocketException => "TCP connectivity failure",
            IOException => "TCP connectivity failure",
            _ when ex.GetType().Name.Contains("Login", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("login", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("invalid user", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("cannot log", StringComparison.OrdinalIgnoreCase)
                => "API authentication failure",
            _ when ex.Message.Contains("no such command", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("trap", StringComparison.OrdinalIgnoreCase)
                => "API command failure",
            _ => "Unknown / see exception",
        };

    private static async Task<bool> IsTcpReachableAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            using TcpClient client = new();
            using CancellationTokenSource cts = new(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
