// -----------------------------------------------------------------------------
// File: FirmwarePackageCatalog.cs
//
// Description:
// Scans FirmwareFiles for parseable *.npk packages, computes SHA-256, and exposes
// IFirmwarePackageCatalog for assessors and upgrade package resolvers.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;

namespace MikrotikWorker.Assessment;

/// <summary>
/// Provides discovered firmware packages from the configured FirmwareFiles tree.
/// </summary>
internal interface IFirmwarePackageCatalog
{
    /// <summary>All parseable <c>*.npk</c> packages under the firmware root.</summary>
    IReadOnlyList<FirmwarePackageInfo> GetPackages();

    /// <summary>Subset where <see cref="FirmwarePackageInfo.PackageName"/> is <c>routeros</c>.</summary>
    IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages();
}

/// <summary>
/// Scans the configured FirmwareFiles tree for parseable <c>*.npk</c> packages.
/// </summary>
internal sealed class FirmwarePackageCatalog(IOptions<FirmwareFilesOptions> options) : IFirmwarePackageCatalog
{
    /// <inheritdoc />
    public IReadOnlyList<FirmwarePackageInfo> GetRouterOsPackages() =>
        GetPackages().Where(static p => p.PackageName.Equals("routeros", StringComparison.Ordinal)).ToList();

    /// <inheritdoc />
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

    /// <summary>Computes lowercase hex SHA-256 for <paramref name="path"/>.</summary>
    internal static string ComputeSha256Hex(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Returns a forward-slash relative URL path under the firmware root.</summary>
    internal static string GetRelativeUrlPath(string firmwareRoot, string fullPath)
    {
        string relative = Path.GetRelativePath(firmwareRoot, fullPath).Replace('\\', '/');
        return relative.TrimStart('/');
    }
}
