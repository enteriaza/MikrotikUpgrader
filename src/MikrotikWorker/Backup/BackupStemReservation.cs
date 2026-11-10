namespace MikrotikWorker.Backup;

/// <summary>
/// Process-local reservation of a shared backup stem for <c>.rsc</c>, <c>.backup</c> and <c>.json</c>.
/// The stem is claimed via a sidecar marker; content files are created later with CreateNew.
/// </summary>
internal sealed class BackupStemReservation : IDisposable
{
    private readonly object _gate = new();
    private bool _completed;
    private bool _released;

    public BackupStemReservation(
        string stem,
        string exportLocalPath,
        string binaryLocalPath,
        string manifestLocalPath,
        string markerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);
        Stem = stem;
        ExportLocalPath = exportLocalPath;
        BinaryLocalPath = binaryLocalPath;
        ManifestLocalPath = manifestLocalPath;
        MarkerPath = markerPath;
    }

    public string Stem { get; }

    public string ExportLocalPath { get; }

    public string BinaryLocalPath { get; }

    public string ManifestLocalPath { get; }

    public string MarkerPath { get; }

    public string ExportFileName => Stem + ".rsc";

    public string BinaryFileName => Stem + ".backup";

    public string ManifestFileName => Stem + ".json";

    /// <summary>
    /// Marks the reservation as successfully consumed. The marker may be removed because the
    /// real artefact files now occupy the stem.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            _completed = true;
            TryDeleteMarker();
            _released = true;
        }
    }

    /// <summary>
    /// Releases this reservation only. Never deletes export/binary/manifest content.
    /// If any content file already exists, the marker is retained so the stem cannot be reused.
    /// </summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_released || _completed)
            {
                return;
            }

            bool contentExists =
                File.Exists(ExportLocalPath)
                || File.Exists(BinaryLocalPath)
                || File.Exists(ManifestLocalPath);

            if (!contentExists)
            {
                TryDeleteMarker();
            }

            _released = true;
        }
    }

    public void Dispose() => Release();

    private void TryDeleteMarker()
    {
        try
        {
            if (File.Exists(MarkerPath))
            {
                File.Delete(MarkerPath);
            }
        }
        catch (IOException)
        {
            // Best-effort; stem remains blocked if marker cannot be removed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
