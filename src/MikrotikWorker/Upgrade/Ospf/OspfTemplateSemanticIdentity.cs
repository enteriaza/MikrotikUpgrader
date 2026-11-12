// -----------------------------------------------------------------------------
// File: OspfTemplateSemanticIdentity.cs
//
// Description:
// Canonical semantic identity helpers for OSPF interface-template provenance
// matching (interfaces, networks, auth fingerprints, timers, comments).
//
// -----------------------------------------------------------------------------

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
    /// <summary>Canonical token for an unset or broadcast-equivalent <c>type</c>.</summary>
    public const string DefaultTypeToken = "default";
    /// <summary>Canonical token for an unset cost or the RouterOS default cost of 1.</summary>
    public const string DefaultCostToken = "default";
    /// <summary>Canonical token for an unset priority or an equivalent default (1 on ROS7, 128 in legacy captures).</summary>
    public const string DefaultPriorityToken = "default";
    /// <summary>Canonical token for no authentication.</summary>
    public const string DefaultAuthToken = "none";
    /// <summary>Canonical token for an unset or default (<see cref="DefaultHelloSeconds"/>) hello-interval.</summary>
    public const string DefaultHelloToken = "default";
    /// <summary>Canonical token for an unset or default (<see cref="DefaultDeadSeconds"/>) dead-interval.</summary>
    public const string DefaultDeadToken = "default";
    /// <summary>Canonical token for an unset or default (<see cref="DefaultRetransmitSeconds"/>) retransmit-interval.</summary>
    public const string DefaultRetransmitToken = "default";
    /// <summary>Canonical token for an unset or default (<see cref="DefaultTransmitDelaySeconds"/>) transmit-delay.</summary>
    public const string DefaultTransmitDelayToken = "default";
    /// <summary>Canonical token for an unset or default (<see cref="DefaultInstanceId"/>) protocol instance-id.</summary>
    public const string DefaultInstanceIdToken = "default";
    /// <summary>Canonical token for <c>use-bfd</c> when absent or explicitly false.</summary>
    public const string DefaultUseBfdToken = "false";
    /// <summary>Canonical token for point-to-point networks (<c>ptp</c> / <c>point-to-point</c>).</summary>
    public const string PtpTypeToken = "ptp";
    /// <summary>Canonical token for non-broadcast multi-access networks.</summary>
    public const string NbmaTypeToken = "nbma";
    /// <summary>Canonical token for point-to-multipoint networks (<c>ptmp</c> / <c>point-to-multipoint</c>).</summary>
    public const string PtmpTypeToken = "ptmp";
    /// <summary>Canonical token for unnumbered point-to-point networks.</summary>
    public const string PtpUnnumberedTypeToken = "ptp-unnumbered";
    /// <summary>Canonical token for virtual links.</summary>
    public const string VirtualLinkTypeToken = "virtual-link";
    /// <summary>Canonical token for point-to-multipoint broadcast networks.</summary>
    public const string PtmpBroadcastTypeToken = "ptmp-broadcast";

    /// <summary>RouterOS default OSPF hello-interval, in seconds.</summary>
    public const int DefaultHelloSeconds = 10;
    /// <summary>RouterOS default OSPF dead-interval, in seconds.</summary>
    public const int DefaultDeadSeconds = 40;
    /// <summary>RouterOS default OSPF retransmit-interval, in seconds.</summary>
    public const int DefaultRetransmitSeconds = 5;
    /// <summary>RouterOS default OSPF transmit-delay, in seconds.</summary>
    public const int DefaultTransmitDelaySeconds = 1;
    /// <summary>RouterOS default OSPF protocol instance-id.</summary>
    public const int DefaultInstanceId = 0;

    /// <summary>Matches RouterOS durations such as <c>1w2d3h4m5s</c> or <c>10.5s</c>; groups 1-5 are weeks, days, hours, minutes and whole seconds (group 6 is the ignored fractional part).</summary>
    private static readonly Regex RosTimeRegex = new(
        @"^\s*(?:(\d+)w)?(?:(\d+)d)?(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)(?:\.(\d+))?s)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Result of resolving auth-key / authentication-key into a present/fingerprint/ambiguous triple.
    /// Plaintext secrets are never retained here.
    /// </summary>
    /// <param name="Present">True when an auth key value was present.</param>
    /// <param name="Fingerprint">Stable fingerprint of the key material when present and unambiguous.</param>
    /// <param name="Ambiguous">True when conflicting key aliases prevent a single fingerprint.</param>
    public readonly record struct AuthKeyResolution(bool Present, string? Fingerprint, bool Ambiguous);

    /// <summary>
    /// Canonicalises an <c>interfaces</c> matcher so equivalent lists compare equal.
    /// </summary>
    /// <param name="interfaces">Raw comma-separated interface list, <c>all</c>, or null/blank.</param>
    /// <returns>Empty for null/blank, the canonical <c>all</c> token when it denotes all interfaces, otherwise the distinct names sorted ordinally and comma-joined.</returns>
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

    /// <summary>
    /// Canonicalises a <c>networks</c> matcher so equivalent prefix sets compare equal.
    /// </summary>
    /// <param name="networks">Raw comma-separated prefix list; may be null.</param>
    /// <returns>The prefixes normalised and joined by <see cref="OspfNetworkList.JoinCanonical"/>.</returns>
    public static string CanonicalNetworks(string? networks) =>
        OspfNetworkList.JoinCanonical(OspfNetworkList.Split(networks));

    /// <summary>
    /// Exact comment identity. Null/empty (RouterOS unset) are equivalent absent annotations.
    /// Non-empty comments are compared byte-for-byte (Ordinal); content is not trimmed.
    /// </summary>
    /// <param name="comment">Raw comment text; may be null.</param>
    /// <returns>Null for null/empty, otherwise the comment unchanged.</returns>
    public static string? NormalizeComment(string? comment) =>
        string.IsNullOrEmpty(comment) ? null : comment;

    /// <summary>
    /// Maps a RouterOS network <c>type</c> (and its long-form aliases) to a canonical token.
    /// </summary>
    /// <param name="type">Raw type value; may be null/blank.</param>
    /// <returns>
    /// <see cref="DefaultTypeToken"/> for blank, <c>broadcast</c> or <c>default</c>; a dedicated token for ptp, nbma, ptmp, ptp-unnumbered, virtual-link and ptmp-broadcast;
    /// otherwise the lower-cased value so unknown types stay distinct and are never collapsed into the default.
    /// </returns>
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

    /// <summary>
    /// Normalises an OSPF <c>cost</c> value.
    /// </summary>
    /// <param name="cost">Raw cost text; may be null/blank.</param>
    /// <returns>
    /// <see cref="DefaultCostToken"/> for blank or 1 (the RouterOS default), the decimal value otherwise,
    /// or <c>invalid:&lt;raw&gt;</c> when the text is not a non-negative integer (so callers can fail closed).
    /// </returns>
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

    /// <summary>
    /// Normalises an OSPF router <c>priority</c> value.
    /// </summary>
    /// <param name="priority">Raw priority text; may be null/blank.</param>
    /// <returns>
    /// <see cref="DefaultPriorityToken"/> for blank, 1 or 128 (ROS7 default and legacy broadcast default), the decimal value otherwise,
    /// or <c>invalid:&lt;raw&gt;</c> when the text is not a non-negative integer.
    /// </returns>
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

    /// <summary>
    /// Normalises the OSPF authentication method name.
    /// </summary>
    /// <param name="auth">Raw <c>auth</c> value; may be null/blank.</param>
    /// <returns><see cref="DefaultAuthToken"/> for blank, otherwise the trimmed lower-case method name.</returns>
    public static string NormalizeAuth(string? auth)
    {
        if (string.IsNullOrWhiteSpace(auth))
        {
            return DefaultAuthToken;
        }

        return auth.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Normalises the authentication key id.
    /// </summary>
    /// <param name="authId">Raw <c>auth-id</c> value; may be null/blank.</param>
    /// <returns>The trimmed id, or null when blank.</returns>
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
    /// <param name="raw">Raw <c>use-bfd</c> text; null/empty means absent.</param>
    /// <returns><c>false</c>, <c>true</c>, or <c>invalid:&lt;raw&gt;</c> for unrecognised values.</returns>
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
    /// <param name="useBfd">Already-parsed value; null means absent.</param>
    /// <param name="raw">Raw text that takes precedence over <paramref name="useBfd"/> when supplied.</param>
    /// <returns>The canonical <c>use-bfd</c> token.</returns>
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

    /// <summary>
    /// Normalises a RouterOS timer (hello, dead, retransmit, transmit-delay) so default values compare equal regardless of how they were written.
    /// </summary>
    /// <param name="raw">Raw time text (plain seconds, clock form or RouterOS duration); may be null/blank.</param>
    /// <param name="defaultSeconds">Protocol default for this timer, in seconds.</param>
    /// <param name="defaultToken">Token returned when the timer is unset or equals <paramref name="defaultSeconds"/>.</param>
    /// <returns>The default token, the value in whole seconds, or <c>invalid:&lt;raw&gt;</c> when the text cannot be parsed or is negative.</returns>
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

    /// <summary>
    /// Normalises the OSPF protocol <c>instance-id</c>.
    /// </summary>
    /// <param name="instanceId">Raw instance-id text; may be null/blank.</param>
    /// <returns><see cref="DefaultInstanceIdToken"/> for blank or 0, the decimal value for 1-255, or <c>invalid:&lt;raw&gt;</c> for anything else.</returns>
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

    /// <summary>
    /// Normalises an optional name-like attribute (area instance, prefix-list, vlink neighbour or transit area).
    /// </summary>
    /// <param name="value">Raw name; may be null/blank.</param>
    /// <returns>The trimmed name, or null when blank.</returns>
    public static string? NormalizeOptionalName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>
    /// Produces a non-reversible fingerprint of an authentication key so keys can be compared without retaining plaintext.
    /// </summary>
    /// <param name="authKey">Plaintext key as read from RouterOS; may be null/empty.</param>
    /// <returns>Upper-case hex SHA-256 of the UTF-8 key bytes, or null when no key is present.</returns>
    public static string? FingerprintAuthKey(string? authKey)
    {
        if (string.IsNullOrEmpty(authKey))
        {
            return null;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(authKey));
        return Convert.ToHexString(hash);
    }

    /// <summary>Returns true when a non-empty authentication key value is present.</summary>
    public static bool AuthKeyPresent(string? authKey) => !string.IsNullOrEmpty(authKey);

    /// <summary>
    /// Resolve ROS7 <c>auth-key</c> and legacy/docs <c>authentication-key</c> into one semantic key.
    /// Conflicting non-equal values are ambiguous and fail closed.
    /// </summary>
    /// <param name="authKey">Value of the ROS7 <c>auth-key</c> property; may be null/empty.</param>
    /// <param name="authenticationKey">Value of the legacy <c>authentication-key</c> property; may be null/empty.</param>
    /// <returns>Present with a fingerprint, absent, or ambiguous (no fingerprint) when both aliases are set to different values.</returns>
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

    /// <summary>
    /// Parses a RouterOS time value into whole seconds.
    /// </summary>
    /// <remarks>
    /// Accepts plain integers (seconds), clock forms (<c>c</c>, <c>g</c>, <c>h:mm:ss</c>) and RouterOS duration
    /// strings (<c>1w2d3h4m5s</c>, optionally with fractional seconds, which are ignored). Fails closed on empty, negative, malformed or overflowing input.
    /// </remarks>
    /// <param name="raw">Time text to parse.</param>
    /// <param name="seconds">Receives the duration in whole seconds; 0 when parsing fails.</param>
    /// <returns>True when <paramref name="raw"/> was parsed successfully.</returns>
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

    /// <summary>
    /// Builds the canonical superseded-template identity from planner-captured data.
    /// </summary>
    /// <param name="areaName">Area name of the source template.</param>
    /// <param name="interfaces">Source <c>interfaces</c> matcher; null/blank for network-only templates.</param>
    /// <param name="networks">Source <c>networks</c> list before consolidation.</param>
    /// <param name="passive">Whether the source template was passive.</param>
    /// <param name="semantics">Captured remaining semantics; null is treated as all defaults.</param>
    /// <returns>A fully normalised <see cref="OspfSupersededTemplateIdentity"/>.</returns>
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

    /// <summary>Builds the canonical identity for a live inventory row so it can be compared with a recorded <see cref="OspfSupersededTemplateIdentity"/>. The auth-key fingerprint is dropped when no key is present.</summary>
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

    /// <summary>
    /// Determines whether a live row is semantically identical to a recorded superseded identity.
    /// </summary>
    /// <remarks>
    /// Every identity attribute is compared after normalisation. Rows without area or networks, rows or identities with
    /// invalid tokens, and rows or identities with an ambiguous auth key never match (fail closed).
    /// </remarks>
    /// <param name="row">Live inventory row.</param>
    /// <param name="identity">Recorded superseded identity from the migration plan.</param>
    /// <returns>True only on a complete, unambiguous match.</returns>
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

    /// <summary>
    /// Serialises an identity into a stable pipe-delimited string for equality checks and audit output.
    /// </summary>
    /// <param name="identity">Identity to serialise.</param>
    /// <returns>A string covering every identity attribute; the auth key contributes only its fingerprint.</returns>
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

    /// <summary>
    /// Captures the non-matcher semantics of a live RouterOS template row for later provenance matching.
    /// </summary>
    /// <param name="template">Live interface-template row.</param>
    /// <param name="areaInstanceName">Name of the OSPF instance owning the row's area, if resolved.</param>
    /// <returns>Captured semantics with the auth key reduced to presence, fingerprint and ambiguity flags.</returns>
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

    /// <summary>Shared factory that normalises every raw attribute into an <see cref="OspfSupersededTemplateIdentity"/>. An ambiguous or absent auth key yields no fingerprint, and ambiguity forces <c>AuthKeyPresent</c> to false.</summary>
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

    /// <summary>Returns true when any numeric/timer/BFD token of the identity is an <c>invalid:</c> marker, meaning the source value could not be normalised.</summary>
    public static bool HasInvalidSemanticTokens(OspfSupersededTemplateIdentity identity) =>
        HasInvalidToken(identity);

    /// <summary>Returns true when any normalised token of the identity (cost, priority, timers, instance-id, use-bfd) is an <c>invalid:</c> marker.</summary>
    private static bool HasInvalidToken(OspfSupersededTemplateIdentity identity) =>
        StartsInvalid(identity.Cost)
        || StartsInvalid(identity.Priority)
        || StartsInvalid(identity.HelloInterval)
        || StartsInvalid(identity.DeadInterval)
        || StartsInvalid(identity.RetransmitInterval)
        || StartsInvalid(identity.TransmitDelay)
        || StartsInvalid(identity.InstanceId)
        || StartsInvalid(identity.UseBfd);

    /// <summary>Returns true when a normalised token carries the <c>invalid:</c> prefix produced by the Normalize* helpers.</summary>
    private static bool StartsInvalid(string value) =>
        value.StartsWith("invalid:", StringComparison.Ordinal);
}
