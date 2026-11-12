using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Canonical semantic identity for OSPF interface-template provenance and cleanup matching.
/// </summary>
/// <remarks>
/// <para><b>Included:</b> area, area→instance, interfaces, networks, passive, disabled, type, cost,
/// auth, auth-id, auth-key fingerprint (canonical across auth-key/authentication-key aliases),
/// priority, use-bfd, timers, protocol instance-id, prefix-list, vlink fields, exact comment.</para>
/// <para><b>Excluded:</b> <c>.id</c>, inactive/dynamic/.tag, plaintext secrets.</para>
/// </remarks>
internal static class OspfTemplateSemanticIdentity
{
    public const string DefaultTypeToken = "default";
    public const string DefaultCostToken = "default";
    public const string DefaultPriorityToken = "default";
    public const string DefaultAuthToken = "none";
    public const string DefaultHelloToken = "default";
    public const string DefaultDeadToken = "default";
    public const string DefaultRetransmitToken = "default";
    public const string DefaultTransmitDelayToken = "default";
    public const string DefaultInstanceIdToken = "default";
    public const string DefaultUseBfdToken = "false";
    public const string PtpTypeToken = "ptp";
    public const string NbmaTypeToken = "nbma";
    public const string PtmpTypeToken = "ptmp";
    public const string PtpUnnumberedTypeToken = "ptp-unnumbered";
    public const string VirtualLinkTypeToken = "virtual-link";
    public const string PtmpBroadcastTypeToken = "ptmp-broadcast";

    public const int DefaultHelloSeconds = 10;
    public const int DefaultDeadSeconds = 40;
    public const int DefaultRetransmitSeconds = 5;
    public const int DefaultTransmitDelaySeconds = 1;
    public const int DefaultInstanceId = 0;

    private static readonly Regex RosTimeRegex = new(
        @"^\s*(?:(\d+)w)?(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)(?:\.(\d+))?s)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public readonly record struct AuthKeyResolution(bool Present, string? Fingerprint, bool Ambiguous);

    public static string CanonicalInterfaces(string? interfaces)
    {
        if (string.IsNullOrWhiteSpace(interfaces))
        {
            return string.Empty;
        }

        if (OspfSiteMatcherConsolidation.IsAllInterfaces(interfaces))
        {
            return OspfSiteMatcherConsolidation.AllInterfaces;
        }

        return string.Join(
            ",",
            interfaces
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static s => s, StringComparer.Ordinal));
    }

    public static string CanonicalNetworks(string? networks) =>
        OspfNetworkList.JoinCanonical(OspfNetworkList.Split(networks));

    /// <summary>
    /// Exact comment identity. Null/empty (RouterOS unset) are equivalent absent annotations.
    /// Non-empty comments are compared byte-for-byte (Ordinal); content is not trimmed.
    /// </summary>
    public static string? NormalizeComment(string? comment) =>
        string.IsNullOrEmpty(comment) ? null : comment;

    public static string NormalizeType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return DefaultTypeToken;
        }

        string t = type.Trim();
        if (t.Equals("ptp", StringComparison.OrdinalIgnoreCase)
            || t.Equals("point-to-point", StringComparison.OrdinalIgnoreCase))
        {
            return PtpTypeToken;
        }

        if (t.Equals("broadcast", StringComparison.OrdinalIgnoreCase)
            || t.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultTypeToken;
        }

        if (t.Equals("nbma", StringComparison.OrdinalIgnoreCase))
        {
            return NbmaTypeToken;
        }

        if (t.Equals("ptmp", StringComparison.OrdinalIgnoreCase)
            || t.Equals("point-to-multipoint", StringComparison.OrdinalIgnoreCase))
        {
            return PtmpTypeToken;
        }

        if (t.Equals("ptp-unnumbered", StringComparison.OrdinalIgnoreCase)
            || t.Equals("point-to-point-unnumbered", StringComparison.OrdinalIgnoreCase))
        {
            return PtpUnnumberedTypeToken;
        }

        if (t.Equals("virtual-link", StringComparison.OrdinalIgnoreCase)
            || t.Equals("virtuallink", StringComparison.OrdinalIgnoreCase))
        {
            return VirtualLinkTypeToken;
        }

        if (t.Equals("ptmp-broadcast", StringComparison.OrdinalIgnoreCase))
        {
            return PtmpBroadcastTypeToken;
        }

        // Unknown explicit types remain distinct — never collapse to broadcast/default.
        return t.ToLowerInvariant();
    }

    public static string NormalizeCost(string? cost)
    {
        if (string.IsNullOrWhiteSpace(cost))
        {
            return DefaultCostToken;
        }

        if (!int.TryParse(cost.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value < 0)
        {
            return $"invalid:{cost.Trim()}";
        }

        return value == 1 ? DefaultCostToken : value.ToString(CultureInfo.InvariantCulture);
    }

    public static string NormalizePriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority))
        {
            return DefaultPriorityToken;
        }

        if (!int.TryParse(priority.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value < 0)
        {
            return $"invalid:{priority.Trim()}";
        }

        // RouterOS 7 interface-template default priority is 1. Some captures/fixtures still emit 128
        // (legacy broadcast default). Treat both as the unset/default token so ROS6↔ROS7 default
        // equivalence does not create false semantic mismatches for otherwise identical templates.
        return value is 1 or 128
            ? DefaultPriorityToken
            : value.ToString(CultureInfo.InvariantCulture);
    }

    public static string NormalizeAuth(string? auth)
    {
        if (string.IsNullOrWhiteSpace(auth))
        {
            return DefaultAuthToken;
        }

        return auth.Trim().ToLowerInvariant();
    }

    public static string? NormalizeAuthId(string? authId)
    {
        if (string.IsNullOrWhiteSpace(authId))
        {
            return null;
        }

        return authId.Trim();
    }

    /// <summary>
    /// Canonicalise <c>use-bfd</c>. Verified configurable on ROS7 interface-templates
    /// (MikroTik BFD docs + ROS 7.13 property list). Absent/false/no/0 → false; true/yes/1 → true;
    /// any other value → invalid (fail closed).
    /// </summary>
    public static string NormalizeUseBfd(string? raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return DefaultUseBfdToken;
        }

        string v = raw.Trim();
        if (v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("no", StringComparison.OrdinalIgnoreCase)
            || v == "0")
        {
            return DefaultUseBfdToken;
        }

        if (v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v == "1")
        {
            return "true";
        }

        return $"invalid:{raw}";
    }

    /// <summary>Legacy bool? helper for call sites that already parsed a bool.</summary>
    public static string NormalizeUseBfd(bool? useBfd, string? raw = null)
    {
        if (raw is not null)
        {
            return NormalizeUseBfd(raw);
        }

        if (useBfd is null)
        {
            return DefaultUseBfdToken;
        }

        return useBfd.Value ? "true" : DefaultUseBfdToken;
    }

    public static string NormalizeInterval(string? raw, int defaultSeconds, string defaultToken)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultToken;
        }

        if (!TryParseRosTimeSeconds(raw.Trim(), out int seconds) || seconds < 0)
        {
            return $"invalid:{raw.Trim()}";
        }

        return seconds == defaultSeconds
            ? defaultToken
            : seconds.ToString(CultureInfo.InvariantCulture);
    }

    public static string NormalizeInstanceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return DefaultInstanceIdToken;
        }

        if (!int.TryParse(instanceId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value < 0
            || value > 255)
        {
            return $"invalid:{instanceId.Trim()}";
        }

        return value == DefaultInstanceId
            ? DefaultInstanceIdToken
            : value.ToString(CultureInfo.InvariantCulture);
    }

    public static string? NormalizeOptionalName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    public static string? FingerprintAuthKey(string? authKey)
    {
        if (string.IsNullOrEmpty(authKey))
        {
            return null;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(authKey));
        return Convert.ToHexString(hash);
    }

    public static bool AuthKeyPresent(string? authKey) => !string.IsNullOrEmpty(authKey);

    /// <summary>
    /// Resolve ROS7 <c>auth-key</c> and legacy/docs <c>authentication-key</c> into one semantic key.
    /// Conflicting non-equal values are ambiguous and fail closed.
    /// </summary>
    public static AuthKeyResolution ResolveAuthKey(string? authKey, string? authenticationKey)
    {
        bool hasAuthKey = !string.IsNullOrEmpty(authKey);
        bool hasAuthenticationKey = !string.IsNullOrEmpty(authenticationKey);

        if (hasAuthKey && hasAuthenticationKey
            && !string.Equals(authKey, authenticationKey, StringComparison.Ordinal))
        {
            return new AuthKeyResolution(Present: false, Fingerprint: null, Ambiguous: true);
        }

        string? key = hasAuthKey ? authKey : authenticationKey;
        if (string.IsNullOrEmpty(key))
        {
            return new AuthKeyResolution(Present: false, Fingerprint: null, Ambiguous: false);
        }

        return new AuthKeyResolution(Present: true, Fingerprint: FingerprintAuthKey(key), Ambiguous: false);
    }

    public static bool TryParseRosTimeSeconds(string raw, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string value = raw.Trim();

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plain)
            && plain >= 0)
        {
            seconds = plain;
            return true;
        }

        if (TimeSpan.TryParseExact(
                value,
                ["c", "g", @"h\:mm\:ss", @"hh\:mm\:ss"],
                CultureInfo.InvariantCulture,
                out TimeSpan clock)
            && clock.TotalSeconds is >= 0 and <= int.MaxValue)
        {
            seconds = (int)Math.Round(clock.TotalSeconds, MidpointRounding.AwayFromZero);
            return true;
        }

        Match m = RosTimeRegex.Match(value);
        if (!m.Success)
        {
            return false;
        }

        if (!m.Groups[1].Success
            && !m.Groups[2].Success
            && !m.Groups[3].Success
            && !m.Groups[4].Success
            && !m.Groups[5].Success)
        {
            return false;
        }

        long total = 0;
        if (m.Groups[1].Success)
        {
            total += long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 7L * 24 * 3600;
        }

        if (m.Groups[2].Success)
        {
            total += long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 24L * 3600;
        }

        if (m.Groups[3].Success)
        {
            total += long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * 3600L;
        }

        if (m.Groups[4].Success)
        {
            total += long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * 60L;
        }

        if (m.Groups[5].Success)
        {
            total += long.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
        }

        if (total < 0 || total > int.MaxValue)
        {
            return false;
        }

        seconds = (int)total;
        return true;
    }

    public static OspfSupersededTemplateIdentity FromCaptured(
        string areaName,
        string? interfaces,
        string networks,
        bool passive,
        OspfCapturedTemplateSemantics? semantics)
    {
        OspfCapturedTemplateSemantics s = semantics ?? new OspfCapturedTemplateSemantics();
        return BuildIdentity(
            areaName: areaName,
            interfaces: interfaces,
            networks: networks,
            passive: passive,
            disabled: s.Disabled,
            type: s.Type,
            cost: s.Cost,
            auth: s.Auth,
            authId: s.AuthId,
            authKeyPresent: s.AuthKeyPresent,
            authKeyFingerprint: s.AuthKeyFingerprint,
            authKeyAmbiguous: s.AuthKeyAmbiguous,
            priority: s.Priority,
            useBfd: s.UseBfd,
            helloInterval: s.HelloInterval,
            deadInterval: s.DeadInterval,
            retransmitInterval: s.RetransmitInterval,
            transmitDelay: s.TransmitDelay,
            instanceId: s.InstanceId,
            areaInstanceName: s.AreaInstanceName,
            prefixList: s.PrefixList,
            vlinkNeighborId: s.VlinkNeighborId,
            vlinkTransitArea: s.VlinkTransitArea,
            comment: s.Comment);
    }

    public static OspfSupersededTemplateIdentity FromInventoryRow(OspfTemplateInventoryRow row) =>
        BuildIdentity(
            areaName: row.Area ?? string.Empty,
            interfaces: row.Interfaces,
            networks: row.Networks ?? string.Empty,
            passive: row.Passive,
            disabled: row.Disabled,
            type: row.Type,
            cost: row.Cost,
            auth: row.Auth,
            authId: row.AuthId,
            authKeyPresent: row.AuthKeyPresent,
            authKeyFingerprint: row.AuthKeyPresent ? row.AuthKeyFingerprint : null,
            authKeyAmbiguous: row.AuthKeyAmbiguous,
            priority: row.Priority,
            useBfd: row.UseBfd,
            helloInterval: row.HelloInterval,
            deadInterval: row.DeadInterval,
            retransmitInterval: row.RetransmitInterval,
            transmitDelay: row.TransmitDelay,
            instanceId: row.InstanceId,
            areaInstanceName: row.AreaInstanceName,
            prefixList: row.PrefixList,
            vlinkNeighborId: row.VlinkNeighborId,
            vlinkTransitArea: row.VlinkTransitArea,
            comment: row.Comment);

    public static bool Matches(OspfTemplateInventoryRow row, OspfSupersededTemplateIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(row.Area) || string.IsNullOrWhiteSpace(row.Networks))
        {
            return false;
        }

        OspfSupersededTemplateIdentity live = FromInventoryRow(row);
        if (HasInvalidToken(live) || HasInvalidToken(identity)
            || live.AuthKeyAmbiguous || identity.AuthKeyAmbiguous)
        {
            return false;
        }

        return string.Equals(live.AreaName, identity.AreaName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(live.Interfaces ?? string.Empty, identity.Interfaces ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(live.NetworkCidr, identity.NetworkCidr, StringComparison.Ordinal)
            && live.Passive == identity.Passive
            && live.Disabled == identity.Disabled
            && string.Equals(live.Type, identity.Type, StringComparison.Ordinal)
            && string.Equals(live.Cost, identity.Cost, StringComparison.Ordinal)
            && string.Equals(live.Auth, identity.Auth, StringComparison.Ordinal)
            && string.Equals(live.AuthId ?? string.Empty, identity.AuthId ?? string.Empty, StringComparison.Ordinal)
            && live.AuthKeyPresent == identity.AuthKeyPresent
            && string.Equals(
                live.AuthKeyFingerprint ?? string.Empty,
                identity.AuthKeyFingerprint ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(live.Priority, identity.Priority, StringComparison.Ordinal)
            && string.Equals(live.UseBfd, identity.UseBfd, StringComparison.Ordinal)
            && string.Equals(live.HelloInterval, identity.HelloInterval, StringComparison.Ordinal)
            && string.Equals(live.DeadInterval, identity.DeadInterval, StringComparison.Ordinal)
            && string.Equals(live.RetransmitInterval, identity.RetransmitInterval, StringComparison.Ordinal)
            && string.Equals(live.TransmitDelay, identity.TransmitDelay, StringComparison.Ordinal)
            && string.Equals(live.InstanceId, identity.InstanceId, StringComparison.Ordinal)
            && string.Equals(
                live.AreaInstanceName ?? string.Empty,
                identity.AreaInstanceName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(live.PrefixList ?? string.Empty, identity.PrefixList ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(
                live.VlinkNeighborId ?? string.Empty,
                identity.VlinkNeighborId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                live.VlinkTransitArea ?? string.Empty,
                identity.VlinkTransitArea ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(live.Comment ?? string.Empty, identity.Comment ?? string.Empty, StringComparison.Ordinal);
    }

    public static string Fingerprint(OspfSupersededTemplateIdentity identity) =>
        string.Join(
            '|',
            identity.AreaName,
            identity.Interfaces ?? "",
            identity.NetworkCidr,
            identity.Passive ? "1" : "0",
            identity.Disabled ? "1" : "0",
            identity.Type,
            identity.Cost,
            identity.Auth,
            identity.AuthId ?? "",
            identity.AuthKeyPresent ? "1" : "0",
            identity.AuthKeyAmbiguous ? "A" : "0",
            identity.AuthKeyFingerprint ?? "",
            identity.Priority,
            identity.UseBfd,
            identity.HelloInterval,
            identity.DeadInterval,
            identity.RetransmitInterval,
            identity.TransmitDelay,
            identity.InstanceId,
            identity.AreaInstanceName ?? "",
            identity.PrefixList ?? "",
            identity.VlinkNeighborId ?? "",
            identity.VlinkTransitArea ?? "",
            identity.Comment ?? "");

    public static OspfCapturedTemplateSemantics CaptureFromTemplateRow(
        OspfInterfaceTemplateRow template,
        string? areaInstanceName = null)
    {
        AuthKeyResolution key = ResolveAuthKey(template.AuthKey, template.AuthenticationKey);
        return new OspfCapturedTemplateSemantics
        {
            Disabled = template.Disabled,
            Type = template.Type,
            Cost = template.Cost,
            Auth = template.Auth,
            AuthId = template.AuthId,
            AuthKeyPresent = key.Present,
            AuthKeyFingerprint = key.Fingerprint,
            AuthKeyAmbiguous = key.Ambiguous,
            Priority = template.Priority,
            UseBfd = template.UseBfd,
            HelloInterval = template.HelloInterval,
            DeadInterval = template.DeadInterval,
            RetransmitInterval = template.RetransmitInterval,
            TransmitDelay = template.TransmitDelay,
            InstanceId = template.InstanceId,
            AreaInstanceName = NormalizeOptionalName(areaInstanceName),
            PrefixList = template.PrefixList,
            VlinkNeighborId = template.VlinkNeighborId,
            VlinkTransitArea = template.VlinkTransitArea,
            Comment = NormalizeComment(template.Comment),
        };
    }

    private static OspfSupersededTemplateIdentity BuildIdentity(
        string areaName,
        string? interfaces,
        string networks,
        bool passive,
        bool disabled,
        string? type,
        string? cost,
        string? auth,
        string? authId,
        bool authKeyPresent,
        string? authKeyFingerprint,
        bool authKeyAmbiguous,
        string? priority,
        string? useBfd,
        string? helloInterval,
        string? deadInterval,
        string? retransmitInterval,
        string? transmitDelay,
        string? instanceId,
        string? areaInstanceName,
        string? prefixList,
        string? vlinkNeighborId,
        string? vlinkTransitArea,
        string? comment) =>
        new()
        {
            AreaName = areaName.Trim(),
            Interfaces = string.IsNullOrWhiteSpace(interfaces) ? null : CanonicalInterfaces(interfaces),
            NetworkCidr = CanonicalNetworks(networks),
            Passive = passive,
            Disabled = disabled,
            Type = NormalizeType(type),
            Cost = NormalizeCost(cost),
            Auth = NormalizeAuth(auth),
            AuthId = NormalizeAuthId(authId),
            AuthKeyFingerprint = authKeyAmbiguous || !authKeyPresent ? null : authKeyFingerprint,
            AuthKeyPresent = !authKeyAmbiguous && authKeyPresent,
            AuthKeyAmbiguous = authKeyAmbiguous,
            Priority = NormalizePriority(priority),
            UseBfd = NormalizeUseBfd(useBfd),
            HelloInterval = NormalizeInterval(helloInterval, DefaultHelloSeconds, DefaultHelloToken),
            DeadInterval = NormalizeInterval(deadInterval, DefaultDeadSeconds, DefaultDeadToken),
            RetransmitInterval = NormalizeInterval(
                retransmitInterval,
                DefaultRetransmitSeconds,
                DefaultRetransmitToken),
            TransmitDelay = NormalizeInterval(
                transmitDelay,
                DefaultTransmitDelaySeconds,
                DefaultTransmitDelayToken),
            InstanceId = NormalizeInstanceId(instanceId),
            AreaInstanceName = NormalizeOptionalName(areaInstanceName),
            PrefixList = NormalizeOptionalName(prefixList),
            VlinkNeighborId = NormalizeOptionalName(vlinkNeighborId),
            VlinkTransitArea = NormalizeOptionalName(vlinkTransitArea),
            Comment = NormalizeComment(comment),
        };

    public static bool HasInvalidSemanticTokens(OspfSupersededTemplateIdentity identity) =>
        HasInvalidToken(identity);

    private static bool HasInvalidToken(OspfSupersededTemplateIdentity identity) =>
        StartsInvalid(identity.Cost)
        || StartsInvalid(identity.Priority)
        || StartsInvalid(identity.HelloInterval)
        || StartsInvalid(identity.DeadInterval)
        || StartsInvalid(identity.RetransmitInterval)
        || StartsInvalid(identity.TransmitDelay)
        || StartsInvalid(identity.InstanceId)
        || StartsInvalid(identity.UseBfd);

    private static bool StartsInvalid(string value) =>
        value.StartsWith("invalid:", StringComparison.Ordinal);
}
