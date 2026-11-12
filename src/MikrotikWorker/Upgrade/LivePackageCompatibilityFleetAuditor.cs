// -----------------------------------------------------------------------------
// File: LivePackageCompatibilityFleetAuditor.cs
//
// Description:
// Orchestrates concurrent read-only inventory collection and catalogue
// compatibility evaluation. Independent of the upgrade scheduler and
// mutation-capable services.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MikrotikWorker.Configuration;
using MikrotikWorker.MikroTik;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Orchestrates concurrent read-only inventory collection and catalogue compatibility evaluation.
/// Independent of the upgrade scheduler and mutation-capable services.
/// </summary>
internal interface ILivePackageCompatibilityFleetAuditor
{
    /// <summary>
    /// Audits the parsed CSV input concurrently and returns the fleet report.
    /// </summary>
    Task<LiveFleetAuditReport> AuditAsync(
        LiveAuditRouterCsvParser.ParseResult input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Why a fleet audit ended incomplete. Captured at the completion boundary — not by reading
/// cancellation tokens after successful work has already finished.
/// </summary>
internal enum LiveFleetIncompleteReason
{
    /// <summary>Run completed; not incomplete.</summary>
    None = 0,

    /// <summary>Caller cancellation stopped the run before natural completion.</summary>
    UserCancellation = 1,

    /// <summary>Configured total audit deadline elapsed before natural completion.</summary>
    DeadlineExceeded = 2,
}

/// <summary>
/// Test seam invoked after all router work and unscheduled-row fill, before summary flags are fixed.
/// </summary>
internal sealed class LiveFleetAuditCompletionBoundary
{
    /// <summary>Deadline CTS used for the fleet run (distinct from the caller token).</summary>
    public required CancellationTokenSource DeadlineCts { get; init; }

    /// <summary>Original caller cancellation token.</summary>
    public required CancellationToken CallerToken { get; init; }

    /// <summary>Router audit records collected so far.</summary>
    public required IReadOnlyList<LiveFleetRouterAuditRecord> Records { get; init; }

    /// <summary>True when every valid unique target reached a natural terminal collection status.</summary>
    public required bool AllTargetsReachedNaturalTerminal { get; init; }
}

/// <summary>
/// Default fleet auditor: concurrent read-only collection plus catalogue compatibility evaluation.
/// </summary>
internal sealed class LivePackageCompatibilityFleetAuditor(
    IReadOnlyMikrotikInventoryCollector collector,
    IPackageCompatibilityAuditor compatibilityAuditor,
    IOptions<LivePackageCompatibilityAuditOptions> liveOptions,
    IOptions<MikroTikOptions> mikroTikOptions,
    IRouterOsV7TargetProvider v7TargetProvider,
    ILogger<LivePackageCompatibilityFleetAuditor> logger) : ILivePackageCompatibilityFleetAuditor
{
    /// <summary>
    /// Optional test seam: runs after the work-completion boundary is established and before
    /// summary <c>Completed</c> / <c>Cancelled</c> flags are finalized.
    /// </summary>
    internal Action<LiveFleetAuditCompletionBoundary>? OnBeforeFinalizeSummary { get; set; }

    /// <summary>
    /// Audits all valid unique CSV routers concurrently and returns the fleet report.
    /// </summary>
    public async Task<LiveFleetAuditReport> AuditAsync(
        LiveAuditRouterCsvParser.ParseResult input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        DateTimeOffset started = DateTimeOffset.UtcNow;
        LivePackageCompatibilityAuditOptions live = liveOptions.Value;
        MikroTikOptions mikrotik = mikroTikOptions.Value;

        if (string.IsNullOrWhiteSpace(mikrotik.Username) || string.IsNullOrEmpty(mikrotik.Password))
        {
            throw new InvalidOperationException(
                "Live package compatibility audit requires shared Mikrotik:Username and Mikrotik:Password. Credentials are never taken from the CSV.");
        }

        TimeSpan connectionTimeout = live.ConnectionTimeout ?? mikrotik.ConnectionTimeout;
        if (connectionTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Live audit connection timeout must be greater than zero.");
        }

        // Separate deadline CTS so we can distinguish deadline vs caller cancel, and so a
        // CancelAfter that fires after natural completion cannot rewrite Completed/Cancelled.
        using CancellationTokenSource deadlineCts = new();
        using CancellationTokenSource fleetCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCts.Token);
        if (live.TotalAuditTimeout is { } totalTimeout)
        {
            deadlineCts.CancelAfter(totalTimeout);
        }

        ConcurrentBag<LiveFleetRouterAuditRecord> records = [];
        IReadOnlyList<LiveAuditRouterInput> targets = input.ValidUnique;

        // Invalid / duplicate rows still appear in the report without connecting.
        foreach (LiveAuditRouterInput row in input.Rows.Where(static r => !r.IsValid || r.IsDuplicate))
        {
            records.Add(CreateSkippedInputRecord(row));
        }

        // Empty / entirely invalid / duplicate-only input: no router work; complete successfully.
        if (targets.Count == 0)
        {
            return Finalize(
                input,
                records,
                started,
                allNaturalTerminal: true,
                incompleteReason: LiveFleetIncompleteReason.None,
                deadlineCts,
                cancellationToken);
        }

        using SemaphoreSlim gate = new(live.MaxConcurrentConnections, live.MaxConcurrentConnections);
        List<Task> tasks = new(targets.Count);
        LiveFleetIncompleteReason scheduleStopReason = LiveFleetIncompleteReason.None;

        foreach (LiveAuditRouterInput target in targets)
        {
            if (fleetCts.IsCancellationRequested)
            {
                scheduleStopReason = ResolveIncompleteReason(cancellationToken, deadlineCts);
                break;
            }

            tasks.Add(AuditOneAsync(
                target,
                mikrotik.Username,
                mikrotik.Password,
                mikrotik.Port,
                connectionTimeout,
                gate,
                records,
                fleetCts.Token,
                deadlineCts,
                cancellationToken));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        LiveFleetIncompleteReason incompleteReason = scheduleStopReason;
        FillUnscheduledTargets(
            targets,
            records,
            ref incompleteReason,
            cancellationToken,
            deadlineCts);

        bool allNaturalTerminal = AllValidUniqueReachedNaturalTerminal(targets, records);

        // Completion boundary: record state is final. Token checks after this cannot flip Completed.
        LiveFleetAuditCompletionBoundary boundary = new()
        {
            DeadlineCts = deadlineCts,
            CallerToken = cancellationToken,
            Records = records.ToList(),
            AllTargetsReachedNaturalTerminal = allNaturalTerminal,
        };
        OnBeforeFinalizeSummary?.Invoke(boundary);

        if (!allNaturalTerminal && incompleteReason == LiveFleetIncompleteReason.None)
        {
            incompleteReason = ResolveIncompleteReason(cancellationToken, deadlineCts);
        }

        if (allNaturalTerminal)
        {
            incompleteReason = LiveFleetIncompleteReason.None;
        }

        return Finalize(
            input,
            records,
            started,
            allNaturalTerminal,
            incompleteReason,
            deadlineCts,
            cancellationToken);
    }

    /// <summary>
    /// Builds the final report from the collected records, ordered by CSV row.
    /// Completion flags come from the work boundary, never from token state observed afterwards.
    /// </summary>
    /// <param name="input">Parsed CSV input.</param>
    /// <param name="records">Collected per-router records.</param>
    /// <param name="started">UTC time the audit started.</param>
    /// <param name="allNaturalTerminal">Whether every valid unique target reached a natural terminal status.</param>
    /// <param name="incompleteReason">Why the run ended incomplete, if it did.</param>
    /// <param name="deadlineCts">Deadline source (not consulted for the flags).</param>
    /// <param name="callerToken">Caller token (not consulted for the flags).</param>
    /// <returns>The fleet report with its summary.</returns>
    private LiveFleetAuditReport Finalize(
        LiveAuditRouterCsvParser.ParseResult input,
        ConcurrentBag<LiveFleetRouterAuditRecord> records,
        DateTimeOffset started,
        bool allNaturalTerminal,
        LiveFleetIncompleteReason incompleteReason,
        CancellationTokenSource deadlineCts,
        CancellationToken callerToken)
    {
        // Completion flags come from the work boundary, never from token state after natural completion.
        _ = deadlineCts;
        _ = callerToken;

        bool completed = allNaturalTerminal;
        bool cancelled = !completed;
        List<LiveFleetRouterAuditRecord> ordered = records
            .OrderBy(static r => r.Input.RowNumber)
            .ToList();

        LiveFleetAuditSummary summary = BuildSummary(
            input,
            ordered,
            completed,
            cancelled,
            incompleteReason);
        RouterOsV7TargetSnapshot? v7 = v7TargetProvider.TryGetCurrent();

        logger.LogInformation(
            "Live fleet package compatibility audit finished. Completed={Completed}, Cancelled={Cancelled}, Reason={Reason}, Routers={Count}, Compatible={Compatible}",
            summary.Completed,
            summary.Cancelled,
            incompleteReason,
            ordered.Count,
            summary.FullyCompatibleDevices);

        return new LiveFleetAuditReport
        {
            Summary = summary,
            Routers = ordered,
            StartedAtUtc = started,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            V6SteppingTarget = RouterOsUpgradeVersions.V6SteppingVersionText,
            V7Target = v7?.VersionText,
        };
    }

    /// <summary>
    /// Collects and assesses one router while holding a concurrency slot, and adds exactly one record for it.
    /// Cancellation, deadline expiry, and unexpected failures are recorded rather than thrown.
    /// </summary>
    /// <param name="target">Valid, unique router to audit.</param>
    /// <param name="username">Shared RouterOS user name.</param>
    /// <param name="password">Shared RouterOS password.</param>
    /// <param name="port">RouterOS API port.</param>
    /// <param name="connectionTimeout">Per-router connection timeout.</param>
    /// <param name="gate">Semaphore bounding concurrent connections.</param>
    /// <param name="records">Collection that receives the router record.</param>
    /// <param name="fleetToken">Token cancelled by caller cancellation or the fleet deadline.</param>
    /// <param name="deadlineCts">Fleet deadline source, used to classify cancellation.</param>
    /// <param name="callerToken">Caller cancellation token, used to classify cancellation.</param>
    private async Task AuditOneAsync(
        LiveAuditRouterInput target,
        string username,
        string password,
        int port,
        TimeSpan connectionTimeout,
        SemaphoreSlim gate,
        ConcurrentBag<LiveFleetRouterAuditRecord> records,
        CancellationToken fleetToken,
        CancellationTokenSource deadlineCts,
        CancellationToken callerToken)
    {
        string ip = target.RouterIp?.ToString()
            ?? throw new InvalidOperationException("Valid audit target missing parsed IP.");

        try
        {
            await gate.WaitAsync(fleetToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LiveFleetIncompleteReason reason = ResolveIncompleteReason(callerToken, deadlineCts);
            records.Add(CreateIncompleteRecord(
                target,
                ip,
                reason,
                waitingForSlot: true));
            return;
        }

        try
        {
            ReadOnlyInventoryCollectionResult collection = await collector
                .CollectAsync(
                    ip,
                    username,
                    password,
                    port,
                    connectionTimeout,
                    target.RouterName,
                    fleetToken)
                .ConfigureAwait(false);

            // Fleet-level cancel/deadline surfaced through the collector as Cancelled/TimedOut.
            // Remap so deadline vs user-cancel is preserved; leave per-router connection timeouts alone.
            if (fleetToken.IsCancellationRequested
                && collection.CollectionStatus is LiveInventoryCollectionStatus.Cancelled
                    or LiveInventoryCollectionStatus.TimedOut)
            {
                LiveFleetIncompleteReason reason = ResolveIncompleteReason(callerToken, deadlineCts);
                records.Add(CreateIncompleteRecord(target, ip, reason, waitingForSlot: false, collection));
                return;
            }

            PackageCompatibilityDeviceReport? compatibility = null;
            if (collection.CollectionStatus == LiveInventoryCollectionStatus.Succeeded
                && collection.HasUsableInventory)
            {
                compatibility = compatibilityAuditor.AssessDevice(new PackageCompatibilityAuditDevice
                {
                    RouterIdentity = collection.Identity,
                    RouterIp = ip,
                    RouterOsVersionRaw = collection.RouterOsVersion,
                    Architecture = collection.Architecture,
                    Packages = collection.Packages,
                    InventorySource = "LiveReadOnly",
                    FixtureName = target.RouterName,
                });
            }

            records.Add(new LiveFleetRouterAuditRecord
            {
                Input = target,
                Collection = collection,
                Compatibility = compatibility,
                AuditedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        catch (OperationCanceledException) when (fleetToken.IsCancellationRequested)
        {
            LiveFleetIncompleteReason reason = ResolveIncompleteReason(callerToken, deadlineCts);
            records.Add(CreateIncompleteRecord(target, ip, reason, waitingForSlot: false));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Unexpected live audit failure for {RouterIp} ({ExceptionType})",
                ip,
                ex.GetType().Name);
            records.Add(new LiveFleetRouterAuditRecord
            {
                Input = target,
                Collection = new ReadOnlyInventoryCollectionResult
                {
                    RouterIp = ip,
                    RouterName = target.RouterName,
                    CollectionStatus = LiveInventoryCollectionStatus.Failed,
                    IdentityStatus = LiveIdentityCollectionStatus.NotAttempted,
                    FailureDetail = "Unexpected audit failure.",
                },
                Compatibility = null,
                AuditedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Adds an incomplete record for every valid target that never produced one, such as targets skipped after cancellation.
    /// </summary>
    /// <param name="targets">Valid unique targets.</param>
    /// <param name="records">Records collected so far; receives the new ones.</param>
    /// <param name="incompleteReason">Updated from the tokens when still unset and a target was missed.</param>
    /// <param name="callerToken">Caller cancellation token.</param>
    /// <param name="deadlineCts">Fleet deadline source.</param>
    private static void FillUnscheduledTargets(
        IReadOnlyList<LiveAuditRouterInput> targets,
        ConcurrentBag<LiveFleetRouterAuditRecord> records,
        ref LiveFleetIncompleteReason incompleteReason,
        CancellationToken callerToken,
        CancellationTokenSource deadlineCts)
    {
        HashSet<int> auditedRows = new(
            records
                .Where(static r => r.Input.IsValid && !r.Input.IsDuplicate)
                .Select(static r => r.Input.RowNumber));

        foreach (LiveAuditRouterInput target in targets)
        {
            if (auditedRows.Contains(target.RowNumber))
            {
                continue;
            }

            if (incompleteReason == LiveFleetIncompleteReason.None)
            {
                incompleteReason = ResolveIncompleteReason(callerToken, deadlineCts);
            }

            string ip = target.RouterIp?.ToString() ?? target.RouterIpText;
            records.Add(CreateIncompleteRecord(
                target,
                ip,
                incompleteReason,
                waitingForSlot: false,
                prior: null,
                notScheduled: true));
        }
    }

    /// <summary>
    /// Determines whether every valid unique target has a record whose status is a natural terminal one.
    /// </summary>
    /// <param name="targets">Valid unique targets.</param>
    /// <param name="records">Collected records.</param>
    /// <returns><see langword="true"/> when all targets reached a natural terminal status.</returns>
    private static bool AllValidUniqueReachedNaturalTerminal(
        IReadOnlyList<LiveAuditRouterInput> targets,
        ConcurrentBag<LiveFleetRouterAuditRecord> records)
    {
        Dictionary<int, LiveFleetRouterAuditRecord> byRow = records
            .Where(static r => r.Input.IsValid && !r.Input.IsDuplicate)
            .GroupBy(static r => r.Input.RowNumber)
            .ToDictionary(static g => g.Key, static g => g.Last());

        foreach (LiveAuditRouterInput target in targets)
        {
            if (!byRow.TryGetValue(target.RowNumber, out LiveFleetRouterAuditRecord? record))
            {
                return false;
            }

            if (!IsNaturalTerminal(record.Collection.CollectionStatus))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether a collection status ends a router audit naturally. Cancelled and not-attempted are not natural terminals, while a per-router timeout is.
    /// </summary>
    /// <param name="status">Collection status.</param>
    /// <returns><see langword="true"/> for a natural terminal status.</returns>
    private static bool IsNaturalTerminal(LiveInventoryCollectionStatus status) =>
        status is LiveInventoryCollectionStatus.Succeeded
            or LiveInventoryCollectionStatus.Partial
            or LiveInventoryCollectionStatus.ConnectionFailed
            or LiveInventoryCollectionStatus.AuthenticationFailed
            or LiveInventoryCollectionStatus.TimedOut
            or LiveInventoryCollectionStatus.Failed;

    /// <summary>
    /// Classifies why work stopped, preferring caller cancellation when both sources are set.
    /// </summary>
    /// <param name="callerToken">Caller cancellation token.</param>
    /// <param name="deadlineCts">Fleet deadline source.</param>
    /// <returns>The incomplete reason; defaults to user cancellation when neither source is set.</returns>
    private static LiveFleetIncompleteReason ResolveIncompleteReason(
        CancellationToken callerToken,
        CancellationTokenSource deadlineCts)
    {
        // Prefer caller cancellation when both are set.
        if (callerToken.IsCancellationRequested)
        {
            return LiveFleetIncompleteReason.UserCancellation;
        }

        if (deadlineCts.IsCancellationRequested)
        {
            return LiveFleetIncompleteReason.DeadlineExceeded;
        }

        return LiveFleetIncompleteReason.UserCancellation;
    }

    /// <summary>
    /// Creates a not-attempted record for an invalid or duplicate CSV row, which is reported without connecting.
    /// </summary>
    /// <param name="row">Invalid or duplicate input row.</param>
    /// <returns>The skipped record.</returns>
    private static LiveFleetRouterAuditRecord CreateSkippedInputRecord(LiveAuditRouterInput row) =>
        new()
        {
            Input = row,
            Collection = new ReadOnlyInventoryCollectionResult
            {
                RouterIp = row.RouterIpText,
                RouterName = row.RouterName,
                CollectionStatus = LiveInventoryCollectionStatus.NotAttempted,
                IdentityStatus = LiveIdentityCollectionStatus.NotAttempted,
                FailureDetail = row.ValidationError ?? "Skipped.",
            },
            Compatibility = null,
            AuditedAtUtc = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Creates a cancelled record for work stopped by caller cancellation or the fleet deadline, keeping any partial data collected earlier.
    /// </summary>
    /// <param name="target">Router input.</param>
    /// <param name="ip">Router IP text.</param>
    /// <param name="reason">Why the work stopped.</param>
    /// <param name="waitingForSlot">Whether it stopped while waiting for a connection slot.</param>
    /// <param name="prior">Partial collection result to carry over, if any.</param>
    /// <param name="notScheduled">Whether the router was never scheduled.</param>
    /// <returns>The incomplete record.</returns>
    private static LiveFleetRouterAuditRecord CreateIncompleteRecord(
        LiveAuditRouterInput target,
        string ip,
        LiveFleetIncompleteReason reason,
        bool waitingForSlot,
        ReadOnlyInventoryCollectionResult? prior = null,
        bool notScheduled = false)
    {
        // Fleet-level incomplete work uses Cancelled (not TimedOut). TimedOut remains a natural
        // per-router connection-timeout terminal that can still yield Completed=true for the fleet.
        const LiveInventoryCollectionStatus status = LiveInventoryCollectionStatus.Cancelled;

        string detail = reason switch
        {
            LiveFleetIncompleteReason.DeadlineExceeded when notScheduled =>
                "Fleet audit deadline exceeded before connection was scheduled.",
            LiveFleetIncompleteReason.DeadlineExceeded when waitingForSlot =>
                "Fleet audit deadline exceeded while waiting for a connection slot.",
            LiveFleetIncompleteReason.DeadlineExceeded =>
                "Fleet audit deadline exceeded.",
            LiveFleetIncompleteReason.UserCancellation when notScheduled =>
                "Audit cancelled before connection was scheduled.",
            LiveFleetIncompleteReason.UserCancellation when waitingForSlot =>
                "Audit cancelled while waiting for a connection slot.",
            _ => "Audit cancelled.",
        };

        return new LiveFleetRouterAuditRecord
        {
            Input = target,
            Collection = new ReadOnlyInventoryCollectionResult
            {
                RouterIp = ip,
                RouterName = target.RouterName,
                CollectionStatus = status,
                IdentityStatus = prior?.IdentityStatus ?? LiveIdentityCollectionStatus.NotAttempted,
                Identity = prior?.Identity,
                RouterOsVersion = prior?.RouterOsVersion,
                Architecture = prior?.Architecture,
                Packages = prior?.Packages ?? [],
                FailureDetail = detail,
                Duration = prior?.Duration ?? TimeSpan.Zero,
                CommandsIssued = prior?.CommandsIssued ?? [],
            },
            Compatibility = null,
            AuditedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Computes fleet-level counts of collection outcomes and package compatibility from the router records.
    /// </summary>
    /// <param name="input">Parsed CSV input, for row totals.</param>
    /// <param name="routers">Ordered router records.</param>
    /// <param name="completed">Whether the audit completed.</param>
    /// <param name="cancelled">Whether the audit was incomplete.</param>
    /// <param name="incompleteReason">Why the audit was incomplete, if it was.</param>
    /// <returns>The summary.</returns>
    private static LiveFleetAuditSummary BuildSummary(
        LiveAuditRouterCsvParser.ParseResult input,
        IReadOnlyList<LiveFleetRouterAuditRecord> routers,
        bool completed,
        bool cancelled,
        LiveFleetIncompleteReason incompleteReason)
    {
        IReadOnlyList<LiveFleetRouterAuditRecord> attempted = routers
            .Where(static r => r.Input.IsValid && !r.Input.IsDuplicate)
            .ToList();

        int successfullyCollected = attempted.Count(static r =>
            r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Succeeded);
        int connectionFailures = attempted.Count(static r =>
            r.Collection.CollectionStatus is LiveInventoryCollectionStatus.ConnectionFailed
                or LiveInventoryCollectionStatus.AuthenticationFailed
                or LiveInventoryCollectionStatus.TimedOut
                or LiveInventoryCollectionStatus.Failed);
        int partial = attempted.Count(static r =>
            r.Collection.CollectionStatus == LiveInventoryCollectionStatus.Partial);

        int fullyCompatible = 0;
        int blockedMissing = 0;
        int ambiguous = 0;
        int unknownIdentity = 0;

        foreach (LiveFleetRouterAuditRecord record in attempted)
        {
            if (record.Collection.IdentityStatus is LiveIdentityCollectionStatus.Unavailable
                    or LiveIdentityCollectionStatus.Failed
                || string.IsNullOrWhiteSpace(record.Collection.Identity)
                || record.Compatibility?.Classification.DeviceType == DeviceType.Unknown)
            {
                unknownIdentity++;
            }

            if (record.Compatibility is null)
            {
                continue;
            }

            PackageCompatibilityDeviceReport report = record.Compatibility;

            bool anyMissing = report.Stages.Any(static s =>
                s.Status is CatalogueCompatibilityStatus.MissingMainPackage
                    or CatalogueCompatibilityStatus.MissingOptionalPackage);
            bool anyAmbiguous = report.Stages.Any(static s =>
                s.Status == CatalogueCompatibilityStatus.AmbiguousPackage);
            bool allOkOrNa = report.Stages.All(static s =>
                s.Status is CatalogueCompatibilityStatus.Compatible
                    or CatalogueCompatibilityStatus.NotApplicable);

            if (anyMissing)
            {
                blockedMissing++;
            }

            if (anyAmbiguous)
            {
                ambiguous++;
            }

            if (record.Collection.CollectionStatus == LiveInventoryCollectionStatus.Succeeded
                && allOkOrNa
                && !anyMissing
                && !anyAmbiguous)
            {
                fullyCompatible++;
            }
        }

        string? notes = incompleteReason switch
        {
            LiveFleetIncompleteReason.DeadlineExceeded =>
                "Fleet audit timed out; results are incomplete.",
            LiveFleetIncompleteReason.UserCancellation =>
                "Fleet audit cancelled; results are incomplete.",
            _ when cancelled =>
                "Fleet audit cancelled or timed out; results are incomplete.",
            _ => null,
        };

        return new LiveFleetAuditSummary
        {
            TotalInputRows = input.Rows.Count,
            ValidUniqueAddresses = input.ValidUnique.Count,
            DuplicateAddresses = input.DuplicateCount,
            InvalidInputRows = input.InvalidCount,
            SuccessfullyCollected = successfullyCollected,
            ConnectionFailures = connectionFailures,
            PartialInventories = partial,
            FullyCompatibleDevices = fullyCompatible,
            BlockedByMissingPackages = blockedMissing,
            AmbiguousPackages = ambiguous,
            // Obsolete counter retained for report schema compatibility; ACE/BR are no longer excluded.
            AceOrBrExcludedFromV7 = 0,
            UnknownOrUnavailableIdentity = unknownIdentity,
            Completed = completed,
            Cancelled = cancelled,
            Notes = notes,
        };
    }
}
