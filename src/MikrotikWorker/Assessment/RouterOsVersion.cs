using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MikrotikWorker.Assessment;

/// <summary>
/// Numeric RouterOS version (major.minor[.patch[.build]]) parsed from device or package strings.
/// Release-channel suffixes (stable, long-term, rc, beta) are ignored for numeric comparison.
/// </summary>
public readonly partial struct RouterOsVersion : IComparable<RouterOsVersion>, IEquatable<RouterOsVersion>
{
    private static readonly Regex VersionRegex = VersionPattern();

    public RouterOsVersion(int major, int minor, int patch = 0, int build = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(patch);
        ArgumentOutOfRangeException.ThrowIfNegative(build);
        Major = major;
        Minor = minor;
        Patch = patch;
        Build = build;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public int Build { get; }

    public bool IsV6 => Major == 6;

    public bool IsV7OrLater => Major >= 7;

    public static bool TryParse(string? text, out RouterOsVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        Match match = VersionRegex.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
        {
            return false;
        }

        int patch = 0;
        if (match.Groups["patch"].Success
            && !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        int build = 0;
        if (match.Groups["build"].Success
            && !int.TryParse(match.Groups["build"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out build))
        {
            return false;
        }

        version = new RouterOsVersion(major, minor, patch, build);
        return true;
    }

    public static RouterOsVersion Parse(string text)
    {
        if (!TryParse(text, out RouterOsVersion version))
        {
            throw new FormatException($"Unable to parse RouterOS version from '{text}'.");
        }

        return version;
    }

    public int CompareTo(RouterOsVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        return c != 0 ? c : Build.CompareTo(other.Build);
    }

    public bool Equals(RouterOsVersion other) => CompareTo(other) == 0;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is RouterOsVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Build);

    public override string ToString() =>
        Build > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}.{Build}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public static bool operator <(RouterOsVersion left, RouterOsVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(RouterOsVersion left, RouterOsVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(RouterOsVersion left, RouterOsVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(RouterOsVersion left, RouterOsVersion right) => left.CompareTo(right) >= 0;

    public static bool operator ==(RouterOsVersion left, RouterOsVersion right) => left.Equals(right);

    public static bool operator !=(RouterOsVersion left, RouterOsVersion right) => !left.Equals(right);

    /// <summary>
    /// Matches numeric versions with optional channel suffixes such as
    /// <c>6.49.19 (long-term)</c>, <c>6.49rc1</c>, <c>6.48beta40</c>, <c>6.49.10rc1</c>.
    /// </summary>
    [GeneratedRegex(
        @"^(?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+)(?:\.(?<build>\d+))?)?(?:(?:rc|beta|alpha)\d*)?(?:\s*\(.*\))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex VersionPattern();
}
