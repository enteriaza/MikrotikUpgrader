// -----------------------------------------------------------------------------
// File: FirmwarePackageInfo.cs
//
// Description:
// Parsed *.npk catalogue entry plus FirmwarePackageFileNameParser, which maps
// v6/v7 RouterOS and optional package filename shapes onto package name,
// architecture, and RouterOsVersion.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Assessment;

/// <summary>
/// Parsed RouterOS package discovered under FirmwareFiles.
/// </summary>
/// <param name="PackageName">Logical package name (e.g. <c>routeros</c>, <c>routing</c>).</param>
/// <param name="FileName">File name only (no directory).</param>
/// <param name="FullPath">Absolute path on disk.</param>
/// <param name="Architecture">Normalised architecture token when present in the file name.</param>
/// <param name="Version">Parsed numeric RouterOS package version.</param>
/// <param name="SizeBytes">File length in bytes.</param>
/// <param name="Sha256Hex">Lowercase hex SHA-256 of file contents.</param>
public sealed record FirmwarePackageInfo(
    string PackageName,
    string FileName,
    string FullPath,
    string? Architecture,
    RouterOsVersion Version,
    long SizeBytes,
    string Sha256Hex);

/// <summary>
/// Parses MikroTik <c>.npk</c> file names into package identity fields.
/// </summary>
internal static class FirmwarePackageFileNameParser
{
    /// <summary>
    /// Lower-case architecture names that can appear in firmware package file names.
    /// </summary>
    private static readonly HashSet<string> ArchitectureTokens = new(StringComparer.Ordinal)
    {
        "arm", "arm64", "mipsbe", "mmips", "smips", "tile", "x86", "powerpc", "ppc",
    };

    /// <summary>
    /// Parses package identity from FirmwareFiles filenames.
    /// Main v6: <c>routeros-{arch}-{version}.npk</c>.
    /// Optional/split v6: <c>{name}-{version}-{arch}.npk</c> or <c>{name}-{version}.npk</c>.
    /// v7 main: <c>routeros-{version}-{arch}.npk</c>.
    /// </summary>
    public static bool TryParse(
        string fileName,
        out string packageName,
        out string? architecture,
        out RouterOsVersion version)
    {
        packageName = string.Empty;
        architecture = null;
        version = default;

        if (string.IsNullOrWhiteSpace(fileName)
            || !fileName.EndsWith(".npk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string[] parts = stem.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        // Main routeros package (special arch/version ordering).
        if (parts[0].Equals("routeros", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRouterOsMain(parts, out packageName, out architecture, out version);
        }

        return TryParseNamedPackage(parts, out packageName, out architecture, out version);
    }

    /// <summary>
    /// Lowercases and maps <c>ppc</c> → <c>powerpc</c>.
    /// </summary>
    public static string NormalizeArchitecture(string architecture)
    {
        string trimmed = architecture.Trim().ToLowerInvariant();
        return trimmed switch
        {
            "ppc" => "powerpc",
            "powerpc" => "powerpc",
            _ => trimmed,
        };
    }

    /// <summary>
    /// True when both architectures normalise to the same non-empty token.
    /// </summary>
    public static bool ArchitecturesMatch(string? deviceArchitecture, string? packageArchitecture)
    {
        if (string.IsNullOrWhiteSpace(deviceArchitecture) || string.IsNullOrWhiteSpace(packageArchitecture))
        {
            return false;
        }

        return string.Equals(
            NormalizeArchitecture(deviceArchitecture),
            NormalizeArchitecture(packageArchitecture),
            StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="token"/> is a known architecture name.</summary>
    public static bool IsArchitectureToken(string token)
    {
        string n = token.Trim().ToLowerInvariant();
        return ArchitectureTokens.Contains(n);
    }

    /// <summary>
    /// Parses the main <c>routeros</c> package in either the v6 (<c>routeros-{arch}-{version}</c>) or v7 (<c>routeros-{version}[-{arch}]</c>) naming style.
    /// </summary>
    /// <param name="parts">File name without extension, split on hyphens; the first part is <c>routeros</c>.</param>
    /// <param name="packageName">Receives <c>routeros</c>.</param>
    /// <param name="architecture">Receives the normalised architecture, or null when the name carries none.</param>
    /// <param name="version">Receives the parsed version.</param>
    /// <returns><see langword="true"/> when the parts match a main package name.</returns>
    private static bool TryParseRouterOsMain(
        string[] parts,
        out string packageName,
        out string? architecture,
        out RouterOsVersion version)
    {
        packageName = "routeros";
        architecture = null;
        version = default;

        // v6: routeros-{arch}-{version}
        if (parts.Length >= 3
            && IsArchitectureToken(parts[1])
            && RouterOsVersion.TryParse(parts[^1], out version))
        {
            architecture = NormalizeArchitecture(parts[1]);
            return true;
        }

        // v7: routeros-{version}-{arch} or routeros-{version}
        if (RouterOsVersion.TryParse(parts[1], out version))
        {
            if (parts.Length >= 3 && IsArchitectureToken(parts[2]))
            {
                architecture = NormalizeArchitecture(parts[2]);
                return true;
            }

            if (parts.Length == 2)
            {
                architecture = null;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parses non-main packages by locating the right-most version token and optional trailing arch.
    /// </summary>
    private static bool TryParseNamedPackage(
        string[] parts,
        out string packageName,
        out string? architecture,
        out RouterOsVersion version)
    {
        packageName = string.Empty;
        architecture = null;
        version = default;

        // Find the right-most version token; optional arch may follow it.
        int versionIndex = -1;
        for (int i = parts.Length - 1; i >= 1; i--)
        {
            if (RouterOsVersion.TryParse(parts[i], out version))
            {
                versionIndex = i;
                break;
            }
        }

        if (versionIndex < 1)
        {
            return false;
        }

        int nameEnd = versionIndex;
        if (versionIndex + 1 < parts.Length)
        {
            if (versionIndex + 1 != parts.Length - 1 || !IsArchitectureToken(parts[^1]))
            {
                return false;
            }

            architecture = NormalizeArchitecture(parts[^1]);
        }
        else
        {
            architecture = null;
        }

        packageName = string.Join('-', parts.Take(nameEnd)).ToLowerInvariant();
        return packageName.Length > 0;
    }
}
