using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

internal sealed class ScheduledJobsPaths(IOptions<ScheduledJobsOptions> options)
{
    public string Root { get; private set; } = string.Empty;

    public string Processing { get; private set; } = string.Empty;

    public string Accepted { get; private set; } = string.Empty;

    public string Completed { get; private set; } = string.Empty;

    public string Rejected { get; private set; } = string.Empty;

    public void EnsureCreated(string? baseDirectory = null)
    {
        string root = ResolveRoot(baseDirectory);
        Root = root;
        Processing = Path.Combine(root, "Processing");
        Accepted = Path.Combine(root, "Accepted");
        Completed = Path.Combine(root, "Completed");
        Rejected = Path.Combine(root, "Rejected");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Processing);
        Directory.CreateDirectory(Accepted);
        Directory.CreateDirectory(Completed);
        Directory.CreateDirectory(Rejected);
    }

    public string ResolveRoot(string? baseDirectory = null)
    {
        ScheduledJobsOptions configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.Path))
        {
            throw new InvalidOperationException("ScheduledJobs:Path must be configured.");
        }

        string rootBase = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        return Path.IsPathRooted(configured.Path)
            ? Path.GetFullPath(configured.Path)
            : Path.GetFullPath(configured.Path, rootBase);
    }
}
