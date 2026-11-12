// -----------------------------------------------------------------------------
// File: DeviceIdentityClassification.cs
//
// Description:
// Device-type prefixes, identity classification result, and DeviceIdentityClassifier
// for the standard TYPEdd.POPddd.ddd.sss naming convention. All types remain
// RouterOS 7 eligible; OSPF capture/plan validation is the fail-closed gate.
//
// -----------------------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Recognised device-type prefixes from the standard identity naming convention.
/// </summary>
public enum DeviceType
{
    /// <summary>Identity missing, empty, or not matching the standard pattern.</summary>
    Unknown = 0,

    /// <summary>ACE (access) device type prefix.</summary>
    Ace = 1,

    /// <summary>AP (access point) device type prefix.</summary>
    Ap = 2,

    /// <summary>BR (border/backbone) device type prefix.</summary>
    Br = 3,

    /// <summary>DCL device type prefix.</summary>
    Dcl = 4,
}

/// <summary>
/// Result of classifying a RouterOS identity for second-stage (RouterOS 7) policy.
/// </summary>
public sealed class DeviceIdentityClassification
{
    /// <summary>Parsed device type, or <see cref="DeviceType.Unknown"/>.</summary>
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
    /// Second-stage (RouterOS 7) eligibility. All device types — including ACE and BR — are eligible;
    /// OSPF capture/plan validation remains the fail-closed gate before the v7 stage.
    /// </summary>
    public bool ShouldUpgradeToV7 => true;

    /// <summary>
    /// Human-readable eligibility reason used in logs and blocked-path detail text.
    /// </summary>
    public string PolicyReason => DeviceType switch
    {
        DeviceType.Ace => "Device type ACE is eligible for RouterOS 7",
        DeviceType.Br => "Device type BR is eligible for RouterOS 7",
        DeviceType.Ap => "Device type AP is eligible for RouterOS 7",
        DeviceType.Dcl => "Device type DCL is eligible for RouterOS 7",
        _ => "Identity is unknown or non-standard; device remains eligible for RouterOS 7",
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

    /// <summary>
    /// Classifies <paramref name="identity"/> into a <see cref="DeviceIdentityClassification"/>.
    /// Never throws into the upgrade workflow.
    /// </summary>
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

    /// <summary>
    /// Maps a matched type token to <see cref="DeviceType"/>; unknown tokens become Unknown.
    /// </summary>
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
    public static bool ShouldUpgradeToV7(DeviceType deviceType)
    {
        _ = deviceType;
        return true;
    }
}
