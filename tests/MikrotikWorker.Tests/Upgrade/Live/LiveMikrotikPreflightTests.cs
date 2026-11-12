// -----------------------------------------------------------------------------
// File: LiveMikrotikPreflightTests.cs
//
// Description:
// Live category preflight checks against lab MikroTik devices.
//
// -----------------------------------------------------------------------------

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
    /// <summary>Lab router address targeted by this opt-in live test.</summary>
    private const string LiveHost = "198.18.0.83";
    /// <summary>Temp file where the preflight result JSON is written for review.</summary>
    private static readonly string SummaryPath =
        Path.Combine(Path.GetTempPath(), "mikrotik-live-preflight-result.json");

    /// <summary>
    /// Deliberate pre-upgrade baseline for the next authorised live run.
    /// Arms the live gate, loads local credentials, checks TCP reachability,
    /// then reads device info and asserts pre-flight succeeded.
    /// Invariant: secrets must never appear in log or validation failure text.
    /// </summary>
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

    /// <summary>
    /// Serialises the summary object as indented JSON and writes it to the preflight summary file.
    /// </summary>
    private static async Task WriteSummaryAsync(object summary)
    {
        await File.WriteAllTextAsync(
            SummaryPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Looks for <c>appsettings.Development.local.json</c> in the worker project folder, the test output folder and
    /// the current directory, returning the first existing path or null.
    /// </summary>
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

    /// <summary>
    /// Replaces every occurrence of the password in the message with <c>***</c>, leaving the message unchanged when
    /// the password is empty.
    /// </summary>
    private static string Sanitize(string message, string password) =>
        string.IsNullOrEmpty(password)
            ? message
            : message.Replace(password, "***", StringComparison.Ordinal);

    /// <summary>
    /// Maps an exception to a coarse failure label (cancellation, timeout, TCP connectivity, API authentication,
    /// API command, or unknown) for the preflight summary.
    /// </summary>
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

    /// <summary>
    /// Attempts a TCP connect to the host and port within the timeout and returns true on success, false on any
    /// failure.
    /// </summary>
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
