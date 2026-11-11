using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Backup;
using MikrotikWorker.Configuration;
using MikrotikWorker.Logging;
using MikrotikWorker.MikroTik;
using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Hosting;

namespace MikrotikWorker;

/// <summary>
/// Shared host composition for the Mikrotik worker application and firmware HTTP server.
/// </summary>
public static class MikrotikWorkerHost
{
    /// <summary>
    /// Creates the web application builder and applies production service registrations.
    /// </summary>
    public static WebApplicationBuilder CreateApplicationBuilder(string[]? args = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args ?? []);
        builder.Configuration.AddJsonFile(
            $"appsettings.{builder.Environment.EnvironmentName}.local.json",
            optional: true,
            reloadOnChange: true);
        ConfigureServices(builder);
        ConfigureKestrel(builder);
        return builder;
    }

    /// <summary>
    /// Configures Serilog as the sole Microsoft.Extensions.Logging provider.
    /// </summary>
    public static WebApplicationBuilder ConfigureSerilog(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();
        // Replace Log.Logger so UseSerilogRequestLogging (static Log) shares the same sinks.
        builder.Host.UseSerilog(
            (context, services, loggerConfiguration) =>
                SerilogConfigurator.Configure(
                    loggerConfiguration,
                    context.Configuration,
                    context.HostingEnvironment,
                    deviceLogWriter: services.GetService<DeviceLogWriter>()),
            preserveStaticLogger: false,
            writeToProviders: false);

        return builder;
    }

    /// <summary>
    /// Registers options, validation, and the background worker.
    /// </summary>
    public static WebApplicationBuilder ConfigureServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services
            .AddOptions<WorkerOptions>()
            .BindConfiguration(WorkerOptions.SectionName)
            .Validate(
                static options => options.HeartbeatInterval > TimeSpan.Zero,
                "Worker:HeartbeatInterval must be greater than zero.")
            .ValidateOnStart();

        builder.Services
            .AddOptions<ListeningOptions>()
            .BindConfiguration(ListeningOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ListeningOptions>, ListeningOptionsValidator>();

        builder.Services
            .AddOptions<FirmwareFilesOptions>()
            .BindConfiguration(FirmwareFilesOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<FirmwareFilesOptions>, FirmwareFilesOptionsValidator>();

        builder.Services
            .AddOptions<MikroTikOptions>()
            .BindConfiguration(MikroTikOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<MikroTikOptions>, MikroTikOptionsValidator>();

        builder.Services
            .AddOptions<CsvOptions>()
            .BindConfiguration(CsvOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<CsvOptions>, CsvOptionsValidator>();

        builder.Services
            .AddOptions<ScheduledJobsOptions>()
            .BindConfiguration(ScheduledJobsOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ScheduledJobsOptions>, ScheduledJobsOptionsValidator>();

        builder.Services
            .AddOptions<SerilogOptions>()
            .BindConfiguration(SerilogOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<SerilogOptions>, SerilogOptionsValidator>();

        builder.Services
            .AddOptions<FirmwareUpgradeOptions>()
            .BindConfiguration(FirmwareUpgradeOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<FirmwareUpgradeOptions>, FirmwareUpgradeOptionsValidator>();

        builder.Services
            .AddOptions<LivePackageCompatibilityAuditOptions>()
            .BindConfiguration(LivePackageCompatibilityAuditOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<
            IValidateOptions<LivePackageCompatibilityAuditOptions>,
            LivePackageCompatibilityAuditOptionsValidator>();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DeviceLogIdentityRegistry>();
        builder.Services.AddSingleton<DeviceLogWriter>();
        builder.Services.AddHostedService(static sp => sp.GetRequiredService<DeviceLogWriter>());
        builder.Services.AddSingleton<RouterOsV7TargetProvider>();
        builder.Services.AddSingleton<IRouterOsV7TargetProvider>(static sp => sp.GetRequiredService<RouterOsV7TargetProvider>());
        builder.Services.AddHostedService(static sp => sp.GetRequiredService<RouterOsV7TargetProvider>());
        builder.Services.AddHostedService<ServiceLifecycleLoggingHostedService>();
        builder.Services.AddSingleton<IFirmwarePackageCatalog, FirmwarePackageCatalog>();
        builder.Services.AddSingleton<IDeviceCompatibilityAssessor, DeviceCompatibilityAssessor>();
        builder.Services.AddSingleton<IUpgradePackageResolver, UpgradePackageResolver>();
        builder.Services.AddSingleton<IPackageCompatibilityAuditor, PackageCompatibilityAuditor>();
        builder.Services.AddSingleton<ICatalogueCoverageReporter, CatalogueCoverageReporter>();
        builder.Services.AddSingleton<IReadOnlyMikrotikInventoryCollector, ReadOnlyMikrotikInventoryCollector>();
        builder.Services.AddSingleton<ILivePackageCompatibilityFleetAuditor, LivePackageCompatibilityFleetAuditor>();
        builder.Services.AddSingleton<IMikrotikFileTransferClient, MikrotikScpFileTransferClient>();
        builder.Services.AddSingleton<IDeviceBackupService, DeviceBackupService>();
        builder.Services.AddSingleton<IRouterOsV6UpgradeExecutor, RouterOsV6UpgradeExecutor>();
        builder.Services.AddSingleton<ScheduledJobsPaths>();
        builder.Services.AddSingleton<IScheduledJobFileMover, FileSystemScheduledJobFileMover>();
        builder.Services.AddSingleton<CsvDateTimeParser>();
        builder.Services.AddSingleton<ScheduledJobCsvParser>();
        builder.Services.AddSingleton<CsvFileReadinessChecker>();
        builder.Services.AddSingleton<RouterJobExecutionQueue>();
        builder.Services.AddSingleton<InMemoryJobScheduler>();
        builder.Services.AddSingleton<IInMemoryJobScheduler>(static sp => sp.GetRequiredService<InMemoryJobScheduler>());
        builder.Services.AddSingleton<IRouterJobExecutionCompleter>(static sp => sp.GetRequiredService<InMemoryJobScheduler>());
        builder.Services.AddSingleton<ScheduledJobFileProcessor>();
        builder.Services.AddSingleton<RouterJobExecutor>();

        builder.Services.AddSingleton<IRouterCredentialResolver, AppSettingsRouterCredentialResolver>();
        builder.Services.AddSingleton<IMikrotikConnectionFactory, Tik4netConnectionFactory>();
        builder.Services.AddSingleton<MikrotikApiClient>();
        builder.Services.AddSingleton<IMikrotikApiClient>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        builder.Services.AddSingleton<IMikrotikDeviceInfoReader>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        builder.Services.AddSingleton<IMikrotikRouterOperations>(static sp => sp.GetRequiredService<MikrotikApiClient>());
        builder.Services.AddHostedService<Worker>();
        builder.Services.AddHostedService<ScheduledJobsWatcherService>();
        builder.Services.AddHostedService<InMemoryJobSchedulerService>();
        builder.Services.AddHostedService<RouterJobExecutionService>();

        return builder;
    }

    /// <summary>
    /// Binds Kestrel to the configured listening IP address and port (HTTP only).
    /// </summary>
    public static WebApplicationBuilder ConfigureKestrel(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WebHost.PreferHostingUrls(false);
        builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>, KestrelListeningConfigureOptions>();

        return builder;
    }

    /// <summary>
    /// Builds the application and configures static firmware file middleware.
    /// </summary>
    public static WebApplication BuildApplication(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        WebApplication app = builder.Build();
        app.Services.GetRequiredService<ScheduledJobsPaths>().EnsureCreated();
        ConfigureMiddleware(app);
        return app;
    }

    /// <summary>
    /// Maps <c>/firmware_files</c> to the configured firmware directory.
    /// </summary>
    public static WebApplication ConfigureMiddleware(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Request logging requires UseSerilog (registers DiagnosticContext). Skip when tests
        // replace the logging pipeline with a recording provider only.
        if (app.Services.GetService<DiagnosticContext>() is not null)
        {
            app.UseSerilogRequestLogging(options =>
            {
                options.IncludeQueryInRequestPath = false;
                // Method, path, status, duration, remote IP — never query strings, bodies, or auth headers.
                options.MessageTemplate =
                    "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms RemoteIp={ClientIp}";
                options.GetLevel = static (httpContext, elapsed, ex) =>
                {
                    if (ex is not null || httpContext.Response.StatusCode >= 500)
                    {
                        return LogEventLevel.Error;
                    }

                    if (httpContext.Response.StatusCode >= 400)
                    {
                        return LogEventLevel.Warning;
                    }

                    return LogEventLevel.Information;
                };
                options.EnrichDiagnosticContext = static (diagnosticContext, httpContext) =>
                {
                    diagnosticContext.Set(
                        "ClientIp",
                        httpContext.Connection.RemoteIpAddress?.ToString() ?? "-");
                    diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                    diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
                };
            });
        }

        FirmwareFilesOptions firmwareOptions = app.Services.GetRequiredService<IOptions<FirmwareFilesOptions>>().Value;
        string absolutePath = firmwareOptions.GetAbsolutePath();

        FileExtensionContentTypeProvider contentTypes = new();
        contentTypes.Mappings[".npk"] = "application/octet-stream";

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(absolutePath),
            RequestPath = FirmwareFilesOptions.RequestPath,
            ContentTypeProvider = contentTypes,
        });

        return app;
    }
}
