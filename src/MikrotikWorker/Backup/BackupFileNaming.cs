using System.Globalization;
using System.Net;
using MikrotikWorker.Logging;

namespace MikrotikWorker.Backup;

internal static class BackupFileNaming
{
    public static string BuildBaseName(string? routerName, IPAddress routerIp, DateTimeOffset timestampUtc)
    {
        string label = DeviceLogNaming.Sanitize(
            string.IsNullOrWhiteSpace(routerName) ? "router" : routerName);
        string ip = DeviceLogNaming.Sanitize(routerIp.ToString());
        string stamp = timestampUtc.UtcDateTime.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        return $"{label}_{ip}_{stamp}";
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
}
