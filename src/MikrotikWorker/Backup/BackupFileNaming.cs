using System.Globalization;
using System.Net;
using MikrotikWorker.Logging;

namespace MikrotikWorker.Backup;

internal static class BackupFileNaming
{
    private static readonly object StemAllocationGate = new();
    public const string StemAllocationMarkerExtension = ".stem.alloc";

    /// <summary>
    /// Builds a shared stem for export/binary/manifest files:
    /// <c>{Identity}_{IP}_{yyyyMMdd_HHmmss_fff}</c>.
    /// </summary>
    public static string BuildBaseName(string? routerIdentity, IPAddress routerIp, DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(routerIp);
        string deviceStem = DeviceLogNaming.BuildDeviceStem(routerIdentity, routerIp);
        string stamp = timestampUtc.UtcDateTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        return $"{deviceStem}_{stamp}";
    }

    /// <summary>
    /// Atomically reserves one free stem for <c>.rsc</c>, <c>.backup</c> and <c>.json</c> together.
    /// Claims via a sidecar marker under a process-wide lock — never creates empty content files
    /// and never falls back to identity-only naming.
    /// </summary>
    public static BackupStemReservation AllocateUniqueBaseName(
        string? routerIdentity,
        IPAddress routerIp,
        DateTimeOffset timestampUtc,
        string exportsDirectory,
        string binaryDirectory,
        string manifestsDirectory)
    {
        ArgumentNullException.ThrowIfNull(routerIp);
        string candidate = BuildBaseName(routerIdentity, routerIp, timestampUtc);
        Directory.CreateDirectory(exportsDirectory);
        Directory.CreateDirectory(binaryDirectory);
        Directory.CreateDirectory(manifestsDirectory);

        lock (StemAllocationGate)
        {
            for (int i = 0; i < 10_000; i++)
            {
                string stem = i == 0 ? candidate : $"{candidate}_{i}";
                string exportPath = Path.Combine(exportsDirectory, stem + ".rsc");
                string binaryPath = Path.Combine(binaryDirectory, stem + ".backup");
                string manifestPath = Path.Combine(manifestsDirectory, stem + ".json");
                string markerPath = Path.Combine(manifestsDirectory, stem + StemAllocationMarkerExtension);
                if (Exists(exportPath)
                    || Exists(binaryPath)
                    || Exists(manifestPath)
                    || Exists(markerPath))
                {
                    continue;
                }

                try
                {
                    ReserveNewFile(markerPath);
                }
                catch (IOException)
                {
                    // Lost the race to another claim of the same marker; try next stem.
                    continue;
                }

                return new BackupStemReservation(stem, exportPath, binaryPath, manifestPath, markerPath);
            }
        }

        throw new IOException(
            $"Unable to allocate a unique backup stem for identity '{routerIdentity}' at {routerIp}.");
    }

    public static string EnsureUniquePath(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 1; i < 10_000; i++)
        {
            string next = Path.Combine(directory, $"{stem}_{i}{ext}");
            if (!File.Exists(next) && !Directory.Exists(next))
            {
                return next;
            }
        }

        throw new IOException($"Unable to allocate a unique backup filename for '{fileName}'.");
    }

    private static void ReserveNewFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
