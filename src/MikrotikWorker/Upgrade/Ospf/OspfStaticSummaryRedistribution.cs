using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using tik4net;
using TikSpecial = tik4net.TikSpecialProperties;

namespace MikrotikWorker.Upgrade.Ospf;

/// <summary>
/// Optional stage: advertise OSPF area-range prefixes as redistributed static blackhole routes
/// through the managed <c>ospf-v2-out</c> filter. Does not claim Type-3 LSA summarisation;
/// redistributed statics produce Type-5 external LSAs when originated.
/// </summary>
/// <remarks>
/// In-process compensation restores the pre-stage OSPF instance <c>redistribute</c> /
/// <c>out-filter-chain</c> after a post-enable failure. Abrupt process termination cannot be
/// compensated by catch blocks; retry after restart re-runs this stage idempotently.
/// </remarks>
internal interface IOspfStaticSummaryRedistributionService
{
    /// <summary>
    /// When disabled, returns <see cref="OspfStaticSummaryRedistributionResult.SkippedDisabled"/> without
    /// mutating RouterOS. When enabled, reconciles routes/filter/instance and verifies.
    /// </summary>
    Task<OspfStaticSummaryRedistributionResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        OspfMigrationPlan? plan,
        CancellationToken cancellationToken = default);
}

internal sealed class OspfStaticSummaryRedistributionResult
{
    public bool Succeeded { get; private init; }
    public bool Skipped { get; private init; }
    public UpgradeOutcome FailureOutcome { get; private init; }
    public string? Detail { get; private init; }
    public bool CompensationAttempted { get; private init; }
    public bool? CompensationSucceeded { get; private init; }
    public string? CompensationDetail { get; private init; }

    public static OspfStaticSummaryRedistributionResult Ok() => new() { Succeeded = true };

    public static OspfStaticSummaryRedistributionResult SkippedDisabled() => new()
    {
        Succeeded = true,
        Skipped = true,
        Detail = "OSPF static summary redistribution skipped (RedistributeSummaryviaStatics=false).",
    };

    public static OspfStaticSummaryRedistributionResult Failed(
        string detail,
        bool compensationAttempted = false,
        bool? compensationSucceeded = null,
        string? compensationDetail = null)
    {
        string combined = detail;
        if (compensationAttempted)
        {
            combined += compensationSucceeded == true
                ? " Compensation restored the pre-stage OSPF instance redistribute/out-filter-chain."
                : " Compensation failed; router may require manual inspection of OSPF redistribute/out-filter-chain. "
                  + (compensationDetail ?? "unspecified compensation failure.");
        }

        return new OspfStaticSummaryRedistributionResult
        {
            Succeeded = false,
            FailureOutcome = UpgradeOutcome.OspfStaticSummaryRedistributionFailed,
            Detail = combined,
            CompensationAttempted = compensationAttempted,
            CompensationSucceeded = compensationSucceeded,
            CompensationDetail = compensationDetail,
        };
    }
}

/// <summary>Fleet-managed constants for the optional static-summary redistribution stage.</summary>
internal static class OspfStaticSummaryRedistributionConstants
{
    public const string OutFilterChainName = "ospf-v2-out";
    public const int BlackholeDistance = 250;
    public const string FinalRejectRule = "reject";

    public static string BuildAcceptRule(string cidr, int prefixLength) =>
        "if (protocol static && distance == "
        + BlackholeDistance.ToString(CultureInfo.InvariantCulture)
        + " && dst == "
        + cidr
        + " && dst-len == "
        + prefixLength.ToString(CultureInfo.InvariantCulture)
        + ") { accept }";
}

internal sealed class OspfStaticSummaryRedistributionService(
    IOspfMikrotikOperations ops,
    IOptions<OspfSummaryRedistributionOptions> options,
    ILogger<OspfStaticSummaryRedistributionService> logger) : IOspfStaticSummaryRedistributionService
{
    public async Task<OspfStaticSummaryRedistributionResult> ApplyAndVerifyAsync(
        ITikConnection connection,
        OspfMigrationPlan? plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        bool enabled = options.Value.RedistributeSummaryviaStatics;
        logger.LogInformation(
            "OSPF static summary redistribution setting RedistributeSummaryviaStatics={Enabled}",
            enabled);

        if (!enabled)
        {
            logger.LogInformation(
                "OSPF static summary redistribution skipped because RedistributeSummaryviaStatics is false");
            return OspfStaticSummaryRedistributionResult.SkippedDisabled();
        }

        if (plan is null)
        {
            return OspfStaticSummaryRedistributionResult.Failed(
                "OSPF static summary redistribution cannot run: migration plan is missing.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<OspfIpPrefix> prefixes;
        try
        {
            prefixes = CollectUniqueAreaRangePrefixes(plan);
        }
        catch (InvalidOperationException ex)
        {
            return OspfStaticSummaryRedistributionResult.Failed(ex.Message);
        }

        logger.LogInformation(
            "OSPF static summary redistribution processing {Count} unique area-range prefix(es): {Prefixes}",
            prefixes.Count,
            string.Join(", ", prefixes.Select(static p => p.ToCidrString())));

        OspfV7Inventory inventory = await ops.ReadV7InventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<OspfIpAddressRow> addresses = await ops
            .ReadIpAddressInventoryAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        OspfLiveIdentityResolutionResult identity = OspfLiveIdentityResolver.Resolve(plan, inventory, addresses);
        if (!identity.Succeeded || identity.Binding is null)
        {
            return OspfStaticSummaryRedistributionResult.Failed(
                "OSPF static summary redistribution failed: live OSPFv2 identity resolution failed: "
                + (identity.Detail ?? "unspecified."));
        }

        string liveInstance = identity.Binding.LiveInstanceName;
        logger.LogInformation(
            "OSPF static summary redistribution selected live OSPFv2 instance {Instance}",
            liveInstance);

        InstanceSnapshot original;
        try
        {
            original = await ReadInstanceSnapshotAsync(connection, liveInstance, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OspfStaticSummaryRedistributionException ex)
        {
            return OspfStaticSummaryRedistributionResult.Failed(ex.Message);
        }

        logger.LogInformation(
            "Captured pre-stage OSPFv2 instance snapshot Instance={Instance} Redistribute='{Redistribute}' OutFilterChain='{OutFilter}'",
            liveInstance,
            original.Redistribute ?? "",
            original.OutFilterChain ?? "");

        bool redistributeEnableAttempted = false;
        try
        {
            await ReconcileBlackholeRoutesAsync(connection, prefixes, cancellationToken)
                .ConfigureAwait(false);
            await VerifyBlackholeRoutesAsync(connection, prefixes, cancellationToken)
                .ConfigureAwait(false);

            await ReconcileOutFilterChainAsync(connection, prefixes, cancellationToken)
                .ConfigureAwait(false);
            await VerifyOutFilterChainAsync(connection, prefixes, cancellationToken)
                .ConfigureAwait(false);

            // Attach filter before enabling redistribution — never open an unfiltered window.
            await AttachOutFilterChainAsync(connection, liveInstance, cancellationToken)
                .ConfigureAwait(false);
            await VerifyOutFilterAttachedAsync(connection, liveInstance, cancellationToken)
                .ConfigureAwait(false);

            redistributeEnableAttempted = true;
            await EnableStaticRedistributionAsync(connection, liveInstance, cancellationToken)
                .ConfigureAwait(false);
            await VerifyInstanceRedistributionAsync(connection, liveInstance, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OspfStaticSummaryRedistributionException ex)
        {
            logger.LogError(ex, "OSPF static summary redistribution failed");
            if (!redistributeEnableAttempted)
            {
                return OspfStaticSummaryRedistributionResult.Failed(ex.Message);
            }

            return await CompensateAfterEnableFailureAsync(
                    connection,
                    liveInstance,
                    original,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OSPF static summary redistribution failed unexpectedly");
            string detail = "OSPF static summary redistribution failed: " + ex.Message;
            if (!redistributeEnableAttempted)
            {
                return OspfStaticSummaryRedistributionResult.Failed(detail);
            }

            return await CompensateAfterEnableFailureAsync(
                    connection,
                    liveInstance,
                    original,
                    detail,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "OSPF static summary redistribution verified. Instance={Instance}, Chain={Chain}, Prefixes={Count}. "
            + "Limitation: Type-5 external LSA advertisement content is not independently inspected; "
            + "this stage verifies configuration only (not Type-3 area-range summaries). "
            + "Process-crash mid-stage is not compensated in-process; retry re-runs this stage idempotently.",
            liveInstance,
            OspfStaticSummaryRedistributionConstants.OutFilterChainName,
            prefixes.Count);

        return OspfStaticSummaryRedistributionResult.Ok();
    }

    internal static IReadOnlyList<OspfIpPrefix> CollectUniqueAreaRangePrefixes(OspfMigrationPlan plan)
    {
        if (plan.AreaRanges.Count == 0)
        {
            throw new InvalidOperationException(
                "OSPF static summary redistribution requires at least one area-range prefix; "
                + "the migration plan has none. Refusing to enable unrestricted static redistribution.");
        }

        Dictionary<string, OspfIpPrefix> unique = new(StringComparer.Ordinal);
        foreach (OspfPlanAreaRange range in plan.AreaRanges)
        {
            if (!OspfIpPrefix.TryParse(range.RangeCidr, out OspfIpPrefix prefix))
            {
                throw new InvalidOperationException(
                    $"OSPF static summary redistribution cannot parse area-range '{range.RangeCidr}' "
                    + "as an IPv4 CIDR (IPv6 area ranges are not supported by this stage).");
            }

            unique[prefix.ToCidrString()] = prefix;
        }

        if (unique.Count == 0)
        {
            throw new InvalidOperationException(
                "OSPF static summary redistribution found no usable unique area-range prefixes.");
        }

        return unique.Values
            .OrderBy(static p => p.ToCidrString(), StringComparer.Ordinal)
            .ToList();
    }

    private async Task<OspfStaticSummaryRedistributionResult> CompensateAfterEnableFailureAsync(
        ITikConnection connection,
        string liveInstanceName,
        InstanceSnapshot original,
        string stageFailureDetail,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Attempting compensation of OSPFv2 instance {Instance} after post-enable failure",
            liveInstanceName);

        try
        {
            await RestoreInstanceSnapshotAsync(connection, liveInstanceName, original, cancellationToken)
                .ConfigureAwait(false);
            InstanceSnapshot restored = await ReadInstanceSnapshotAsync(
                    connection,
                    liveInstanceName,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!SnapshotEquals(original, restored))
            {
                string mismatch =
                    $"Read-back after compensation mismatch. Expected redistribute='{original.Redistribute}', "
                    + $"out-filter-chain='{original.OutFilterChain}'; observed redistribute='{restored.Redistribute}', "
                    + $"out-filter-chain='{restored.OutFilterChain}'.";
                logger.LogError("{Detail}", mismatch);
                return OspfStaticSummaryRedistributionResult.Failed(
                    stageFailureDetail,
                    compensationAttempted: true,
                    compensationSucceeded: false,
                    compensationDetail: mismatch);
            }

            logger.LogInformation(
                "Compensation restored OSPFv2 instance {Instance} to pre-stage redistribute/out-filter-chain",
                liveInstanceName);
            return OspfStaticSummaryRedistributionResult.Failed(
                stageFailureDetail,
                compensationAttempted: true,
                compensationSucceeded: true,
                compensationDetail: "pre-stage redistribute/out-filter-chain restored");
        }
        catch (Exception compensationEx)
        {
            logger.LogError(
                compensationEx,
                "Compensation failed for OSPFv2 instance {Instance}",
                liveInstanceName);
            return OspfStaticSummaryRedistributionResult.Failed(
                stageFailureDetail,
                compensationAttempted: true,
                compensationSucceeded: false,
                compensationDetail: compensationEx.Message);
        }
    }

    private async Task ReconcileBlackholeRoutesAsync(
        ITikConnection connection,
        IReadOnlyList<OspfIpPrefix> prefixes,
        CancellationToken cancellationToken)
    {
        List<ITikReSentence> rows = (await connection
                .CreateCommand("/ip/route/print")
                .ExecuteListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToList();

        foreach (OspfIpPrefix prefix in prefixes)
        {
            string cidr = prefix.ToCidrString();
            List<ITikReSentence> matches = rows
                .Where(r => string.Equals(Field(r, "dst-address"), cidr, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                logger.LogInformation(
                    "Adding blackhole static route dst={Dst} distance={Distance}",
                    cidr,
                    OspfStaticSummaryRedistributionConstants.BlackholeDistance);
                ITikCommand add = connection.CreateCommand("/ip/route/add");
                add.AddParameter("dst-address", cidr);
                add.AddParameter("blackhole", "yes");
                add.AddParameter(
                    "distance",
                    OspfStaticSummaryRedistributionConstants.BlackholeDistance.ToString(CultureInfo.InvariantCulture));
                await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (ITikReSentence match in matches)
            {
                if (!IsManagedBlackhole(match))
                {
                    throw new OspfStaticSummaryRedistributionException(
                        $"Conflicting /ip/route for dst-address={cidr}: expected blackhole distance "
                        + $"{OspfStaticSummaryRedistributionConstants.BlackholeDistance}, "
                        + $"observed blackhole='{Field(match, "blackhole")}', distance='{Field(match, "distance")}', "
                        + $".id='{Field(match, ".id")}'. Refusing to overwrite unrelated or incorrect route.");
                }
            }

            if (matches.Count > 1)
            {
                throw new OspfStaticSummaryRedistributionException(
                    $"Duplicate managed blackhole routes for dst-address={cidr} (count={matches.Count}); fail-closed.");
            }

            logger.LogInformation(
                "Blackhole static route already correct dst={Dst} .id={Id}",
                cidr,
                Field(matches[0], ".id"));
        }
    }

    private async Task VerifyBlackholeRoutesAsync(
        ITikConnection connection,
        IReadOnlyList<OspfIpPrefix> prefixes,
        CancellationToken cancellationToken)
    {
        List<ITikReSentence> rows = (await connection
                .CreateCommand("/ip/route/print")
                .ExecuteListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToList();

        foreach (OspfIpPrefix prefix in prefixes)
        {
            string cidr = prefix.ToCidrString();
            List<ITikReSentence> matches = rows
                .Where(r =>
                    string.Equals(Field(r, "dst-address"), cidr, StringComparison.OrdinalIgnoreCase)
                    && IsManagedBlackhole(r))
                .ToList();

            if (matches.Count != 1)
            {
                throw new OspfStaticSummaryRedistributionException(
                    $"Blackhole route verification failed for {cidr}: expected exactly one managed route, found {matches.Count}.");
            }
        }
    }

    private async Task ReconcileOutFilterChainAsync(
        ITikConnection connection,
        IReadOnlyList<OspfIpPrefix> prefixes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> expected = BuildExpectedRuleTexts(prefixes);
        List<(string Id, string Rule)> live = await ReadManagedChainRulesAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        bool matches = live.Count == expected.Count
                       && live.Select(static x => NormalizeRule(x.Rule))
                           .SequenceEqual(expected.Select(NormalizeRule));

        if (matches)
        {
            logger.LogInformation(
                "Routing filter chain {Chain} already matches required rules in order (count={Count})",
                OspfStaticSummaryRedistributionConstants.OutFilterChainName,
                expected.Count);
            return;
        }

        logger.LogInformation(
            "Reconciling routing filter chain {Chain}: removing {Live} existing rule(s), installing {Expected} with place-before ordering",
            OspfStaticSummaryRedistributionConstants.OutFilterChainName,
            live.Count,
            expected.Count);

        foreach ((string id, _) in live)
        {
            ITikCommand remove = connection.CreateCommand(OspfRoutingFilterMenus.V7FilterRuleRemove);
            remove.AddParameter(".id", id);
            await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Deterministic evaluation order: install unconditional reject first, then insert each
        // accept immediately before the reject via place-before (RouterOS chain evaluation order).
        string rejectRule = OspfStaticSummaryRedistributionConstants.FinalRejectRule;
        logger.LogInformation(
            "Installing filter rule chain={Chain} rule={Rule} (anchor)",
            OspfStaticSummaryRedistributionConstants.OutFilterChainName,
            rejectRule);
        ITikCommand addReject = connection.CreateCommand("/routing/filter/rule/add");
        addReject.AddParameter("chain", OspfStaticSummaryRedistributionConstants.OutFilterChainName);
        addReject.AddParameter("rule", rejectRule);
        await addReject.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        string rejectId = await FindRejectRuleIdAsync(connection, cancellationToken).ConfigureAwait(false);

        foreach (string acceptRule in expected.Where(static r =>
                     !string.Equals(
                         NormalizeRule(r),
                         NormalizeRule(OspfStaticSummaryRedistributionConstants.FinalRejectRule),
                         StringComparison.Ordinal)))
        {
            logger.LogInformation(
                "Installing filter rule chain={Chain} rule={Rule} place-before={PlaceBefore}",
                OspfStaticSummaryRedistributionConstants.OutFilterChainName,
                acceptRule,
                rejectId);
            ITikCommand add = connection.CreateCommand("/routing/filter/rule/add");
            add.AddParameter("chain", OspfStaticSummaryRedistributionConstants.OutFilterChainName);
            add.AddParameter("rule", acceptRule);
            add.AddParameter("place-before", rejectId);
            await add.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task VerifyOutFilterChainAsync(
        ITikConnection connection,
        IReadOnlyList<OspfIpPrefix> prefixes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> expected = BuildExpectedRuleTexts(prefixes);
        List<(string Id, string Rule)> live = await ReadManagedChainRulesAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (live.Count != expected.Count)
        {
            throw new OspfStaticSummaryRedistributionException(
                $"Filter chain {OspfStaticSummaryRedistributionConstants.OutFilterChainName} rule count is {live.Count}, expected {expected.Count}.");
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(NormalizeRule(live[i].Rule), NormalizeRule(expected[i]), StringComparison.Ordinal))
            {
                throw new OspfStaticSummaryRedistributionException(
                    $"Filter chain {OspfStaticSummaryRedistributionConstants.OutFilterChainName} evaluation-order mismatch at index {i}. "
                    + $"Expected '{expected[i]}', observed '{live[i].Rule}'. "
                    + "Order is established with place-before relative to the final reject and verified via chain print order.");
            }

            if (expected[i] != OspfStaticSummaryRedistributionConstants.FinalRejectRule
                && expected[i].Contains("protocol == static", StringComparison.Ordinal))
            {
                throw new OspfStaticSummaryRedistributionException(
                    "Generated filter rule incorrectly contains 'protocol == static'.");
            }

            if (expected[i] != OspfStaticSummaryRedistributionConstants.FinalRejectRule
                && !expected[i].Contains("protocol static", StringComparison.Ordinal))
            {
                throw new OspfStaticSummaryRedistributionException(
                    "Generated filter accept rule must contain 'protocol static'.");
            }
        }

        if (!string.Equals(
                NormalizeRule(live[^1].Rule),
                NormalizeRule(OspfStaticSummaryRedistributionConstants.FinalRejectRule),
                StringComparison.Ordinal))
        {
            throw new OspfStaticSummaryRedistributionException(
                $"Filter chain {OspfStaticSummaryRedistributionConstants.OutFilterChainName} must end with unconditional reject.");
        }

        // Confirm the reject remains the place-before anchor (last rule) and accepts precede it.
        string rejectId = live[^1].Id;
        for (int i = 0; i < live.Count - 1; i++)
        {
            if (string.Equals(live[i].Id, rejectId, StringComparison.Ordinal))
            {
                throw new OspfStaticSummaryRedistributionException(
                    "Filter ordering verification failed: reject rule is not uniquely last.");
            }
        }

        logger.LogInformation(
            "Filter chain {Chain} verified ({Count} rules, final reject last, place-before ordering confirmed)",
            OspfStaticSummaryRedistributionConstants.OutFilterChainName,
            live.Count);
    }

    private async Task AttachOutFilterChainAsync(
        ITikConnection connection,
        string liveInstanceName,
        CancellationToken cancellationToken)
    {
        ITikReSentence row = await FindV2InstanceAsync(connection, liveInstanceName, cancellationToken)
            .ConfigureAwait(false);
        string id = Field(row, ".id")
                    ?? throw new OspfStaticSummaryRedistributionException(
                        $"OSPFv2 instance '{liveInstanceName}' has no .id.");

        string? current = Field(row, "out-filter-chain");
        if (string.Equals(
                current,
                OspfStaticSummaryRedistributionConstants.OutFilterChainName,
                StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "OSPFv2 instance {Instance} already has out-filter-chain={Chain}",
                liveInstanceName,
                OspfStaticSummaryRedistributionConstants.OutFilterChainName);
            return;
        }

        logger.LogInformation(
            "Attaching out-filter-chain={Chain} to OSPFv2 instance {Instance} (before enabling redistribute)",
            OspfStaticSummaryRedistributionConstants.OutFilterChainName,
            liveInstanceName);
        ITikCommand set = connection.CreateCommand("/routing/ospf/instance/set");
        set.AddParameter(".id", id);
        set.AddParameter("out-filter-chain", OspfStaticSummaryRedistributionConstants.OutFilterChainName);
        await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyOutFilterAttachedAsync(
        ITikConnection connection,
        string liveInstanceName,
        CancellationToken cancellationToken)
    {
        ITikReSentence row = await FindV2InstanceAsync(connection, liveInstanceName, cancellationToken)
            .ConfigureAwait(false);
        string? chain = Field(row, "out-filter-chain");
        if (!string.Equals(
                chain,
                OspfStaticSummaryRedistributionConstants.OutFilterChainName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new OspfStaticSummaryRedistributionException(
                $"OSPFv2 instance '{liveInstanceName}' out-filter-chain is '{chain}', "
                + $"expected '{OspfStaticSummaryRedistributionConstants.OutFilterChainName}'.");
        }
    }

    private async Task EnableStaticRedistributionAsync(
        ITikConnection connection,
        string liveInstanceName,
        CancellationToken cancellationToken)
    {
        ITikReSentence row = await FindV2InstanceAsync(connection, liveInstanceName, cancellationToken)
            .ConfigureAwait(false);
        string id = Field(row, ".id")
                    ?? throw new OspfStaticSummaryRedistributionException(
                        $"OSPFv2 instance '{liveInstanceName}' has no .id.");

        string? current = Field(row, "redistribute");
        string merged = MergeRedistributeWithStatic(current);

        if (RedistributeIncludesStatic(current)
            && string.Equals(
                NormalizeRedistribute(current),
                NormalizeRedistribute(merged),
                StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "OSPFv2 instance {Instance} already redistributes static ({Redistribute}); leaving unchanged",
                liveInstanceName,
                current);
            return;
        }

        logger.LogInformation(
            "Enabling redistribute={Redistribute} on OSPFv2 instance {Instance} after filter attach/verify",
            merged,
            liveInstanceName);
        ITikCommand set = connection.CreateCommand("/routing/ospf/instance/set");
        set.AddParameter(".id", id);
        set.AddParameter("redistribute", merged);
        await set.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyInstanceRedistributionAsync(
        ITikConnection connection,
        string liveInstanceName,
        CancellationToken cancellationToken)
    {
        ITikReSentence row = await FindV2InstanceAsync(connection, liveInstanceName, cancellationToken)
            .ConfigureAwait(false);

        if (!RedistributeIncludesStatic(Field(row, "redistribute")))
        {
            throw new OspfStaticSummaryRedistributionException(
                $"OSPFv2 instance '{liveInstanceName}' redistribute='{Field(row, "redistribute")}' "
                + "does not include static after enable.");
        }

        if (!string.Equals(
                Field(row, "out-filter-chain"),
                OspfStaticSummaryRedistributionConstants.OutFilterChainName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new OspfStaticSummaryRedistributionException(
                $"OSPFv2 instance '{liveInstanceName}' lost out-filter-chain "
                + $"'{OspfStaticSummaryRedistributionConstants.OutFilterChainName}' after redistribute enable.");
        }

        logger.LogInformation(
            "OSPFv2 instance {Instance} verified: redistribute={Redistribute}, out-filter-chain={Chain}",
            liveInstanceName,
            Field(row, "redistribute"),
            Field(row, "out-filter-chain"));
    }

    private async Task RestoreInstanceSnapshotAsync(
        ITikConnection connection,
        string liveInstanceName,
        InstanceSnapshot original,
        CancellationToken cancellationToken)
    {
        ITikReSentence row = await FindV2InstanceAsync(connection, liveInstanceName, cancellationToken)
            .ConfigureAwait(false);
        string id = Field(row, ".id")
                    ?? throw new OspfStaticSummaryRedistributionException(
                        $"OSPFv2 instance '{liveInstanceName}' has no .id for compensation.");

        if (string.IsNullOrWhiteSpace(original.Redistribute))
        {
            ITikCommand clearRedistribute = connection.CreateCommand("/routing/ospf/instance/set");
            clearRedistribute.AddParameter(".id", id);
            clearRedistribute.AddParameter("redistribute", "");
            await clearRedistribute.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            ITikCommand setRedistribute = connection.CreateCommand("/routing/ospf/instance/set");
            setRedistribute.AddParameter(".id", id);
            setRedistribute.AddParameter("redistribute", original.Redistribute);
            await setRedistribute.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Re-read after redistribute restore so filter compensation uses current live fields.
        row = await FindV2InstanceAsync(connection, liveInstanceName, cancellationToken)
            .ConfigureAwait(false);
        id = Field(row, ".id")
             ?? throw new OspfStaticSummaryRedistributionException(
                 $"OSPFv2 instance '{liveInstanceName}' has no .id for filter compensation.");

        string? currentFilter = Field(row, "out-filter-chain");
        if (string.IsNullOrWhiteSpace(original.OutFilterChain))
        {
            if (!string.IsNullOrWhiteSpace(currentFilter))
            {
                ITikCommand unset = connection.CreateCommand("/routing/ospf/instance/unset");
                unset.AddParameter(".id", id);
                unset.AddParameter(TikSpecial.UnsetValueName, "out-filter-chain");
                await unset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else if (!string.Equals(
                     currentFilter,
                     original.OutFilterChain,
                     StringComparison.OrdinalIgnoreCase))
        {
            ITikCommand setFilter = connection.CreateCommand("/routing/ospf/instance/set");
            setFilter.AddParameter(".id", id);
            setFilter.AddParameter("out-filter-chain", original.OutFilterChain);
            await setFilter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<InstanceSnapshot> ReadInstanceSnapshotAsync(
        ITikConnection connection,
        string liveInstanceName,
        CancellationToken cancellationToken)
    {
        ITikReSentence row = await FindV2InstanceAsync(connection, liveInstanceName, cancellationToken)
            .ConfigureAwait(false);
        return new InstanceSnapshot(
            NormalizeOptional(Field(row, "redistribute")),
            NormalizeOptional(Field(row, "out-filter-chain")));
    }

    private async Task<string> FindRejectRuleIdAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        List<(string Id, string Rule)> live = await ReadManagedChainRulesAsync(connection, cancellationToken)
            .ConfigureAwait(false);
        for (int i = live.Count - 1; i >= 0; i--)
        {
            if (string.Equals(
                    NormalizeRule(live[i].Rule),
                    NormalizeRule(OspfStaticSummaryRedistributionConstants.FinalRejectRule),
                    StringComparison.Ordinal))
            {
                return live[i].Id;
            }
        }

        throw new OspfStaticSummaryRedistributionException(
            $"Cannot locate reject rule in chain {OspfStaticSummaryRedistributionConstants.OutFilterChainName} for place-before ordering.");
    }

    private async Task<List<(string Id, string Rule)>> ReadManagedChainRulesAsync(
        ITikConnection connection,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand(OspfRoutingFilterMenus.V7FilterRulePrint)
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Chain print order is treated as evaluation order after place-before reconciliation.
        List<(string Id, string Rule)> result = [];
        foreach (ITikReSentence row in rows)
        {
            if (!string.Equals(
                    Field(row, "chain"),
                    OspfStaticSummaryRedistributionConstants.OutFilterChainName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? id = Field(row, ".id");
            string? rule = Field(row, "rule");
            if (string.IsNullOrWhiteSpace(id) || rule is null)
            {
                throw new OspfStaticSummaryRedistributionException(
                    $"Filter rule in chain {OspfStaticSummaryRedistributionConstants.OutFilterChainName} is missing .id or rule text.");
            }

            result.Add((id, rule));
        }

        return result;
    }

    private async Task<ITikReSentence> FindV2InstanceAsync(
        ITikConnection connection,
        string liveInstanceName,
        CancellationToken cancellationToken)
    {
        IEnumerable<ITikReSentence> rows = await connection
            .CreateCommand("/routing/ospf/instance/print")
            .ExecuteListAsync(cancellationToken)
            .ConfigureAwait(false);

        ITikReSentence? match = rows.FirstOrDefault(r =>
            !string.Equals(Field(r, "version"), "3", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Field(r, "name"), liveInstanceName, StringComparison.OrdinalIgnoreCase));

        return match
               ?? throw new OspfStaticSummaryRedistributionException(
                   $"Live OSPFv2 instance '{liveInstanceName}' not found.");
    }

    internal static IReadOnlyList<string> BuildExpectedRuleTexts(IReadOnlyList<OspfIpPrefix> prefixes)
    {
        List<string> rules = prefixes
            .Select(p => OspfStaticSummaryRedistributionConstants.BuildAcceptRule(
                p.ToCidrString(),
                p.PrefixLength))
            .ToList();
        rules.Add(OspfStaticSummaryRedistributionConstants.FinalRejectRule);
        return rules;
    }

    internal static string MergeRedistributeWithStatic(string? existing)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(existing))
        {
            foreach (string part in existing.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!parts.Contains(part, StringComparer.OrdinalIgnoreCase)
                    && !string.Equals(part, "static", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(part);
                }
            }
        }

        parts.Add("static");
        return string.Join(",", parts);
    }

    internal static bool RedistributeIncludesStatic(string? redistribute)
    {
        if (string.IsNullOrWhiteSpace(redistribute))
        {
            return false;
        }

        return redistribute
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(static p => string.Equals(p, "static", StringComparison.OrdinalIgnoreCase));
    }

    internal static string NormalizeRedistribute(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                ",",
                value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .OrderBy(static s => s, StringComparer.OrdinalIgnoreCase));

    private static bool SnapshotEquals(InstanceSnapshot expected, InstanceSnapshot observed) =>
        string.Equals(
            NormalizeRedistribute(expected.Redistribute),
            NormalizeRedistribute(observed.Redistribute),
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            expected.OutFilterChain ?? string.Empty,
            observed.OutFilterChain ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsManagedBlackhole(ITikReSentence row)
    {
        if (!IsTruthyFlag(Field(row, "blackhole")))
        {
            return false;
        }

        string? distance = Field(row, "distance");
        return string.Equals(
            distance,
            OspfStaticSummaryRedistributionConstants.BlackholeDistance.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static bool IsTruthyFlag(string? value) =>
        value is not null
        && (value.Length == 0
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value == "1");

    private static string NormalizeRule(string rule)
    {
        StringBuilder sb = new(rule.Length);
        bool pendingSpace = false;
        foreach (char c in rule.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string? Field(ITikReSentence? row, string name) =>
        row?.GetResponseFieldOrDefault(name, null);

    private readonly record struct InstanceSnapshot(string? Redistribute, string? OutFilterChain);
}

internal sealed class OspfStaticSummaryRedistributionException : InvalidOperationException
{
    public OspfStaticSummaryRedistributionException(string message)
        : base(message)
    {
    }
}
