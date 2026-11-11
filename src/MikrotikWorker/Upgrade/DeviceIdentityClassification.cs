using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Recognised device-type prefixes from the standard identity naming convention.
/// </summary>
public enum DeviceType
{
    Unknown = 0,
    Ace = 1,
    Ap = 2,
    Br = 3,
    Dcl = 4,
}

/// <summary>
/// Result of classifying a RouterOS identity for second-stage (RouterOS 7) policy.
/// </summary>
public sealed class DeviceIdentityClassification
{
    public required DeviceType DeviceType { get; init; }

    /// <summary>
    /// Raw identity that was classified (may be null/empty when unavailable).
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// True when the identity matched the standard <c>TYPEdd.POPddd.ddd.sss</c> pattern.
    /// </summary>
    public bool MatchedStandardFormat { get; init; }

    /// <summary>
    /// Exclusion-based second-stage decision: every type except ACE and BR proceeds.
    /// </summary>
    public bool ShouldUpgradeToV7 =>
        DeviceType is not DeviceType.Ace and not DeviceType.Br;

    public string PolicyReason => DeviceType switch
    {
        DeviceType.Ace => "RouterOS 7 upgrade deferred for device type ACE",
        DeviceType.Br => "RouterOS 7 upgrade deferred for device type BR",
        DeviceType.Ap => "Device type AP is eligible for RouterOS 7 under the exclusion-based policy",
        DeviceType.Dcl => "Device type DCL is eligible for RouterOS 7 under the exclusion-based policy",
        _ => "Identity is not positively identified as ACE or BR; device remains eligible for RouterOS 7",
    };
}

/// <summary>
/// Classifies RouterOS identities using the standard
/// <c>[ACE|AP|BR|DCL]dd.POPddd.ddd.sss</c> convention.
/// Classification never throws; null/empty/unparseable identities become <see cref="DeviceType.Unknown"/>.
/// </summary>
public static partial class DeviceIdentityClassifier
{
    /// <summary>
    /// Standard format: type + two digits, POP + three digits, three digits, unrestricted suffix
    /// (suffix may contain additional dots). Matching is case-insensitive and anchored.
    /// </summary>
    [GeneratedRegex(
        @"^(?<type>ACE|AP|BR|DCL)(?<site>\d{2})\.POP(?<pop>\d{3})\.(?<seq>\d{3})\.(?<rest>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex StandardIdentityPattern();

    public static DeviceIdentityClassification Classify(string? identity)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(identity))
            {
                return new DeviceIdentityClassification
                {
                    DeviceType = DeviceType.Unknown,
                    Identity = identity,
                    MatchedStandardFormat = false,
                };
            }

            string trimmed = identity.Trim();
            Match match = StandardIdentityPattern().Match(trimmed);
            if (!match.Success)
            {
                return new DeviceIdentityClassification
                {
                    DeviceType = DeviceType.Unknown,
                    Identity = trimmed,
                    MatchedStandardFormat = false,
                };
            }

            DeviceType type = ParseType(match.Groups["type"].Value);
            return new DeviceIdentityClassification
            {
                DeviceType = type,
                Identity = trimmed,
                MatchedStandardFormat = true,
            };
        }
        catch
        {
            // Identity is a classification aid only — never abort the upgrade workflow.
            return new DeviceIdentityClassification
            {
                DeviceType = DeviceType.Unknown,
                Identity = identity,
                MatchedStandardFormat = false,
            };
        }
    }

    private static DeviceType ParseType(string token) =>
        token.ToUpperInvariant() switch
        {
            "ACE" => DeviceType.Ace,
            "AP" => DeviceType.Ap,
            "BR" => DeviceType.Br,
            "DCL" => DeviceType.Dcl,
            _ => DeviceType.Unknown,
        };

    /// <summary>
    /// Test/helper seam mirroring the governing policy expression.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public static bool ShouldUpgradeToV7(DeviceType deviceType) =>
        deviceType is not DeviceType.Ace and not DeviceType.Br;
}
