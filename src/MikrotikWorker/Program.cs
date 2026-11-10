using System.Reflection;
using MikrotikWorker;
using MikrotikWorker.Logging;
using Serilog;

Log.Logger = SerilogConfigurator.CreateBootstrapLogger();

try
{
    string? version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
    Log.Information(
        "MikrotikWorker starting. Version={ApplicationVersion}, BaseDirectory={BaseDirectory}",
        version,
        AppContext.BaseDirectory);

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
