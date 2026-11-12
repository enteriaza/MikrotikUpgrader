using System.Collections.Concurrent;

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Tracks on-disk Accepted CSV paths per import for result persistence.
/// Separate from the scheduler so CSV writer DI does not cycle.
/// </summary>
internal sealed class ImportedCsvPathStore : IImportedCsvPathResolver
{
    private readonly ConcurrentDictionary<Guid, string> _paths = new();

    public void SetAcceptedPath(Guid importId, string acceptedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedPath);
        _paths[importId] = acceptedPath;
    }

    public void Clear(Guid importId) => _paths.TryRemove(importId, out _);

    public bool TryGetAcceptedCsvPath(Guid importId, out string? path) =>
        _paths.TryGetValue(importId, out path);
}
