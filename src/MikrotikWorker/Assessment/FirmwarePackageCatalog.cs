using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Assessment;

internal interface IFirmwarePackageCatalog
{
    IReadOnlyList<FirmwarePackageInfo> GetPackages();

    IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages();
}

/// <summary>
/// Scans the configured FirmwareFiles tree for parseable <c>*.npk</c> packages.
/// </summary>
internal sealed class FirmwarePackageCatalog(IOptions<FirmwareFilesOptions> options) : IFirmwarePackageCatalog
{
    public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
        GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

    public IReadOnlyList<FirmwarePackageInfo> GetPackages()
    {
        string root = options.Value.GetAbsolutePath();
        if (!Directory.Exists(root))
        {
            return [];
        }

        List<FirmwarePackageInfo> packages = [];
        foreach (string path in Directory.EnumerateFiles(root, "*.npk", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(path);
            if (!FirmwarePackageFileNameParser.TryParse(
                    fileName,
                    out string packageName,
                    out string? architecture,
                    out RouterOsVersion version))
            {
                continue;
            }

            FileInfo info = new(path);
            packages.Add(new FirmwarePackageInfo(
                packageName,
                fileName,
                path,
                architecture,
                version,
                info.Length,
                ComputeSha256Hex(path)));
        }

        return packages;
    }

    internal static string ComputeSha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string GetRelativeUrlPath(string firmwareRoot, string fullPath)
    {
        string relative = Path.GetRelativePath(firmwareRoot, fullPath).Replace('\\', '/');
        return relative.TrimStart('/');
    }
}
