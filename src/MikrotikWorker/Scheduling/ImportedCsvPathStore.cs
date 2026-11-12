// -----------------------------------------------------------------------------
// File: ImportedCsvPathStore.cs
//
// Description:
// Concurrent registry of on-disk Accepted CSV paths keyed by import id.
// Separated from the scheduler so CSV result writers can resolve paths via DI
// without creating a circular dependency on the scheduler itself.
// Cleared when an import is aborted, archived, or retired.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Tracks on-disk Accepted CSV paths per import for result persistence.
/// Separate from the scheduler so CSV writer DI does not cycle.
/// </summary>
internal sealed class ImportedCsvPathStore : IImportedCsvPathResolver
{
    /// <summary>
    /// Accepted CSV path for each import ID.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _paths = new();

    /// <summary>
    /// Records or replaces the Accepted CSV path for <paramref name="importId"/>.
    /// Called once the file is known to exist under Accepted (not merely intended).
    /// </summary>
    /// <param name="importId">Import whose Accepted path is being registered.</param>
    /// <param name="acceptedPath">Full on-disk path to the Accepted CSV.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="acceptedPath"/> is null or whitespace.</exception>
    public void SetAcceptedPath(Guid importId, string acceptedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedPath);
        _paths[importId] = acceptedPath;
    }

    /// <summary>
    /// Removes any registered path for <paramref name="importId"/> (abort, archive, or retire).
    /// </summary>
    /// <param name="importId">Import whose path mapping should be forgotten.</param>
    public void Clear(Guid importId) => _paths.TryRemove(importId, out _);

    /// <inheritdoc />
    public bool TryGetAcceptedCsvPath(Guid importId, out string? path) =>
        _paths.TryGetValue(importId, out path);
}
