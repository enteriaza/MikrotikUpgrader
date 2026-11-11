using System.Reflection;
using MikrotikWorker;
using MikrotikWorker.Logging;
using MikrotikWorker.Upgrade;
using Serilog;

Log.Logger = SerilogConfigurator.CreateBootstrapLogger();

try
{
    string? version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    Log.Information(
        "MikrotikWorker starting. Version={ApplicationVersion}, BaseDirectory={BaseDirectory}",
        version,
        AppContext.BaseDirectory);

    if (LivePackageCompatibilityAuditRunner.IsLiveAuditRequested(args))
    {
        Log.Information("Running live read-only package compatibility fleet audit");
        int exitCode = await LivePackageCompatibilityAuditRunner
            .RunLiveAuditAsync(args)
            .ConfigureAwait(false);
        Environment.ExitCode = exitCode;
        return;
    }

    if (PackageCompatibilityAuditRunner.IsFixtureAuditRequested(args))
    {
        Log.Information("Running non-live package compatibility fixture audit");
        int exitCode = await PackageCompatibilityAuditRunner
            .RunFixtureAuditAsync(args)
            .ConfigureAwait(false);
        Environment.ExitCode = exitCode;
        return;
    }

    if (MikrotikWorker.Upgrade.Ospf.OspfOnlyLabRecoveryRunner.IsRequested(args))
    {
        Log.Information("Running OSPF-only lab recovery (no firmware upgrade stages)");
        int exitCode = await MikrotikWorker.Upgrade.Ospf.OspfOnlyLabRecoveryRunner
            .RunAsync(args)
            .ConfigureAwait(false);
        Environment.ExitCode = exitCode;
        return;
    }

    WebApplicationBuilder builder = MikrotikWorkerHost.CreateApplicationBuilder(args);
    MikrotikWorkerHost.ConfigureSerilog(builder);

    WebApplication app = MikrotikWorkerHost.BuildApplication(builder);

    Log.Information(
        "MikrotikWorker host built. Environment={EnvironmentName}",
        app.Environment.EnvironmentName);

    await app.RunAsync().ConfigureAwait(false);

    Log.Information("MikrotikWorker shut down gracefully");
}
catch (Exception ex)
{
    Log.Fatal(ex, "MikrotikWorker terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
