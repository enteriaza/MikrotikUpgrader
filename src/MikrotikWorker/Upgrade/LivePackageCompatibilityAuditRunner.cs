// -----------------------------------------------------------------------------
// File: LivePackageCompatibilityAuditRunner.cs
//
// Description:
// Explicit dual-gated CLI entry for live, read-only fleet package-compatibility
// audits. Requires both --audit-package-compatibility-live and
// LivePackageCompatibilityAudit:Enabled=true. Uses LivePackageCompatibilityAuditHost
// (not the full worker host) and does not enable firmware upgrades.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Explicit dual-gated CLI entry for live, read-only fleet package-compatibility audits.
/// Requires both <see cref="LiveAuditArgument"/> and <c>LivePackageCompatibilityAudit:Enabled=true</c>.
/// Uses <see cref="LivePackageCompatibilityAuditHost"/> — not the full worker host.
/// Does not enable firmware upgrades.
/// </summary>
internal static class LivePackageCompatibilityAuditRunner
{
    /// <summary>
    /// CLI flag that must be present to activate the live package-compatibility audit.
    /// </summary>
    public const string LiveAuditArgument = "--audit-package-compatibility-live";

    /// <summary>
    /// True when <see cref="LiveAuditArgument"/> appears in <paramref name="args"/>.
    /// </summary>
    public static bool IsLiveAuditRequested(string[] args) =>
        args.Any(static a => string.Equals(a, LiveAuditArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns a human-readable rejection reason when the dual gate is not satisfied; otherwise null.
    /// </summary>
    internal static string? DescribeActivationFailure(bool cliRequested, bool configEnabled)
    {
        if (!cliRequested)
        {
            return "Live package compatibility audit requires the explicit CLI option "
                + LiveAuditArgument
                + ".";
        }

        if (!configEnabled)
        {
            return "Live package compatibility audit is disabled. Set LivePackageCompatibilityAudit:Enabled=true "
                + "in configuration (for example appsettings.*.local.json) in addition to the CLI option "
                + LiveAuditArgument
                + ". Router credentials alone are not permission to connect.";
        }

        return null;
    }

    /// <summary>
    /// Runs the dual-gated live read-only fleet audit and returns a process exit code.
    /// </summary>
    public static Task<int> RunLiveAuditAsync(
        string[] args,
        CancellationToken cancellationToken = default) =>
        RunLiveAuditAsync(args, configureHost: null, cancellationToken);

    /// <summary>
    /// Runs the dual-gated live read-only fleet audit and returns a process exit code.
    /// </summary>
    /// <param name="args">Process arguments including <c>--audit-package-compatibility-live</c> and <c>--input</c>.</param>
    /// <param name="configureHost">
    /// Optional test/host customization applied after default appsettings (including optional
    /// <c>*.local.json</c>). Later configuration sources win — use this to force
    /// <c>LivePackageCompatibilityAudit:Enabled=false</c> without depending on ambient local files.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the audit run.</param>
    /// <returns>Process exit code (0 success; non-zero for gate/input/runtime failures).</returns>
    public static async Task<int> RunLiveAuditAsync(
        string[] args,
        Action<HostApplicationBuilder>? configureHost,
        CancellationToken cancellationToken = default)
    {
        if (!IsLiveAuditRequested(args))
        {
            Console.Error.WriteLine(DescribeActivationFailure(cliRequested: false, configEnabled: false));
            return 2;
        }

        string? inputPath = ResolveArg(args, "--input");
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            Console.Error.WriteLine(
                "Live package compatibility audit requires --input <router-list.csv>.");
            return 2;
        }

        inputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"Live audit input CSV was not found: {inputPath}");
            return 2;
        }

        string outputDir = ResolveArg(args, "--output")
            ?? Path.Combine(AppContext.BaseDirectory, "Reports", "LivePackageCompatibility");
        outputDir = Path.GetFullPath(outputDir);

        HostApplicationBuilder builder = LivePackageCompatibilityAuditHost.CreateBuilder(args);
        configureHost?.Invoke(builder);
        using IHost host = builder.Build();
        IServiceProvider services = host.Services;

        LivePackageCompatibilityAuditOptions liveOptions =
            services.GetRequiredService<IOptions<LivePackageCompatibilityAuditOptions>>().Value;
        FirmwareUpgradeOptions upgradeOptions =
            services.GetRequiredService<IOptions<FirmwareUpgradeOptions>>().Value;
        MikroTikOptions mikroTikOptions =
            services.GetRequiredService<IOptions<MikroTikOptions>>().Value;

        string? gateFailure = DescribeActivationFailure(
            cliRequested: true,
            configEnabled: liveOptions.Enabled);
        if (gateFailure is not null)
        {
            Console.Error.WriteLine(gateFailure);
            return 2;
        }

        // Live audit must not depend on or mutate firmware upgrade enablement.
        _ = upgradeOptions.Enabled;

        if (string.IsNullOrWhiteSpace(mikroTikOptions.Username)
            || string.IsNullOrEmpty(mikroTikOptions.Password))
        {
            Console.Error.WriteLine(
                "Live package compatibility audit requires shared Mikrotik:Username and Mikrotik:Password "
                + "before any router connection is attempted. Credentials are never taken from the CSV.");
            return 2;
        }

        FirmwareFilesOptions firmwareFiles =
            services.GetRequiredService<IOptions<FirmwareFilesOptions>>().Value;
        string firmwareRoot = Path.GetFullPath(firmwareFiles.GetAbsolutePath());
        if (IsPathEqualOrUnder(outputDir, firmwareRoot))
        {
            Console.Error.WriteLine(
                "Live audit --output must not point at or inside FirmwareFiles "
                + $"(resolved '{outputDir}' under '{firmwareRoot}').");
            return 2;
        }

        await host.StartAsync(cancellationToken).ConfigureAwait(false);

        ILogger logger = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("LivePackageCompatibilityAudit");

        try
        {
            LiveAuditRouterCsvParser.ParseResult parsed;
            try
            {
                parsed = LiveAuditRouterCsvParser.ParseFile(inputPath);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Failed to parse live audit CSV {InputPath} ({ExceptionType})",
                    inputPath,
                    ex.GetType().Name);
                Console.Error.WriteLine($"Failed to parse live audit CSV: {ex.Message}");
                return 2;
            }

            DateTime beforeWrite = File.GetLastWriteTimeUtc(inputPath);
            long beforeLength = new FileInfo(inputPath).Length;

            logger.LogInformation(
                "Live read-only package compatibility audit starting. Input={Input}, Output={Output}, Rows={Rows}, Unique={Unique}, FirmwareUpgradeEnabled={FirmwareUpgradeEnabled}",
                inputPath,
                outputDir,
                parsed.Rows.Count,
                parsed.ValidUnique.Count,
                upgradeOptions.Enabled);

            ILivePackageCompatibilityFleetAuditor auditor =
                services.GetRequiredService<ILivePackageCompatibilityFleetAuditor>();

            LiveFleetAuditReport report = await auditor
                .AuditAsync(parsed, cancellationToken)
                .ConfigureAwait(false);

            (string csvPath, string jsonPath) = await LivePackageCompatibilityAuditReportWriter
                .WriteAsync(outputDir, report, cancellationToken)
                .ConfigureAwait(false);

            DateTime afterWrite = File.GetLastWriteTimeUtc(inputPath);
            long afterLength = new FileInfo(inputPath).Length;
            if (beforeWrite != afterWrite || beforeLength != afterLength)
            {
                logger.LogError(
                    "Live audit input CSV appears to have been modified. Path={InputPath}",
                    inputPath);
                return 3;
            }

            logger.LogInformation(
                "Live audit reports written. Csv={Csv}, Json={Json}, Completed={Completed}, Cancelled={Cancelled}",
                csvPath,
                jsonPath,
                report.Summary.Completed,
                report.Summary.Cancelled);

            Console.WriteLine($"CSV: {csvPath}");
            Console.WriteLine($"JSON: {jsonPath}");
            Console.WriteLine(
                $"Summary: collected={report.Summary.SuccessfullyCollected}, "
                + $"failures={report.Summary.ConnectionFailures}, "
                + $"compatible={report.Summary.FullyCompatibleDevices}, "
                + $"completed={report.Summary.Completed}, cancelled={report.Summary.Cancelled}");

            return report.Summary.Cancelled ? 1 : 0;
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// True when candidate is the same as or under root after full path normalization.
    /// </summary>
    internal static bool IsPathEqualOrUnder(string candidate, string root)
    {
        string normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the value following a named CLI argument, or null when absent.
    /// </summary>
    private static string? ResolveArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
