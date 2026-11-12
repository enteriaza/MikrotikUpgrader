// -----------------------------------------------------------------------------
// File: BackupStemReservation.cs
//
// Description:
// Process-local claim of a backup stem via .stem.alloc marker. Complete() drops
// the marker after real artefacts exist; Release()/Dispose() never deletes
// content files and retains the marker if any content already exists.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Backup;

/// <summary>
/// Process-local reservation of a shared backup stem for <c>.rsc</c>, <c>.backup</c> and <c>.json</c>.
/// The stem is claimed via a sidecar marker; content files are created later with CreateNew.
/// </summary>
internal sealed class BackupStemReservation : IDisposable
{
    /// <summary>
    /// Lock guarding the completed and released state.
    /// </summary>
    private readonly object _gate = new();
    /// <summary>
    /// Set once the reservation was consumed by real artefact files.
    /// </summary>
    private bool _completed;
    /// <summary>
    /// Set once the reservation has been completed or released, making further calls no-ops.
    /// </summary>
    private bool _released;

    /// <summary>
    /// Creates a reservation for the given stem and absolute artefact paths.
    /// </summary>
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

    /// <summary>Shared stem without extension.</summary>
    public string Stem { get; }

    /// <summary>Absolute intended path for the <c>.rsc</c> export.</summary>
    public string ExportLocalPath { get; }

    /// <summary>Absolute intended path for the <c>.backup</c> binary.</summary>
    public string BinaryLocalPath { get; }

    /// <summary>Absolute intended path for the <c>.json</c> manifest.</summary>
    public string ManifestLocalPath { get; }

    /// <summary>Absolute path of the <c>.stem.alloc</c> claim marker.</summary>
    public string MarkerPath { get; }

    /// <summary><see cref="Stem"/> plus <c>.rsc</c>.</summary>
    public string ExportFileName => Stem + ".rsc";

    /// <summary><see cref="Stem"/> plus <c>.backup</c>.</summary>
    public string BinaryFileName => Stem + ".backup";

    /// <summary><see cref="Stem"/> plus <c>.json</c>.</summary>
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

    /// <inheritdoc cref="Release" />
    public void Dispose() => Release();

    /// <summary>
    /// Deletes the claim marker on a best-effort basis; if deletion fails the stem stays blocked.
    /// </summary>
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
