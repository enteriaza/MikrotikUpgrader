// -----------------------------------------------------------------------------
// File: ScheduledJobsPaths.cs
//
// Description:
// Resolves and materializes the ScheduledJobs folder tree (Root, Processing,
// Accepted, Completed, Rejected) from configuration. Call <see cref="EnsureCreated"/>
// before watching or moving files so relative Path values bind to a concrete base.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Holds absolute ScheduledJobs directory paths and ensures the lifecycle folders exist.
/// </summary>
/// <param name="options">Provides <see cref="ScheduledJobsOptions.Path"/> (absolute or relative).</param>
internal sealed class ScheduledJobsPaths(IOptions<ScheduledJobsOptions> options)
{
    /// <summary>Absolute Incoming/root folder (parent of lifecycle subfolders).</summary>
    public string Root { get; private set; } = string.Empty;

    /// <summary>Folder for CSVs currently being validated/activated.</summary>
    public string Processing { get; private set; } = string.Empty;

    /// <summary>Folder for CSVs accepted into the in-memory scheduler.</summary>
    public string Accepted { get; private set; } = string.Empty;

    /// <summary>Folder for Accepted CSVs archived after all scheduled jobs are terminal.</summary>
    public string Completed { get; private set; } = string.Empty;

    /// <summary>Folder for rejected CSVs and companion <c>.error.txt</c> reports.</summary>
    public string Rejected { get; private set; } = string.Empty;

    /// <summary>
    /// Resolves root from configuration, assigns subfolder properties, and creates all directories.
    /// </summary>
    /// <param name="baseDirectory">
    /// Optional base for relative <see cref="ScheduledJobsOptions.Path"/>; defaults to <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">Thrown when <c>ScheduledJobs:Path</c> is missing.</exception>
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

    /// <summary>
    /// Computes the absolute ScheduledJobs root without mutating properties or creating directories.
    /// </summary>
    /// <param name="baseDirectory">Optional base for relative configured paths.</param>
    /// <returns>Absolute root path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <c>ScheduledJobs:Path</c> is missing.</exception>
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
