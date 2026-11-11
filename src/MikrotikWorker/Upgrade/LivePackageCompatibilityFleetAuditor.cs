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
    None = 0,
    UserCancellation = 1,
    DeadlineExceeded = 2,
}

/// <summary>
/// Test seam invoked after all router work and unscheduled-row fill, before summary flags are fixed.
/// </summary>
internal sealed class LiveFleetAuditCompletionBoundary
{
    public required CancellationTokenSource DeadlineCts { get; init; }

    public required CancellationToken CallerToken { get; init; }

    public required IReadOnlyList<LiveFleetRouterAuditRecord> Records { get; init; }

    public required bool AllTargetsReachedNaturalTerminal { get; init; }
}

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

    private static bool IsNaturalTerminal(LiveInventoryCollectionStatus status) =>
        status is LiveInventoryCollectionStatus.Succeeded
            or LiveInventoryCollectionStatus.Partial
            or LiveInventoryCollectionStatus.ConnectionFailed
            or LiveInventoryCollectionStatus.AuthenticationFailed
            or LiveInventoryCollectionStatus.TimedOut
            or LiveInventoryCollectionStatus.Failed;

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
        int aceBrExcluded = 0;
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
            if (report.Classification.DeviceType is DeviceType.Ace or DeviceType.Br)
            {
                aceBrExcluded++;
            }

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
            AceOrBrExcludedFromV7 = aceBrExcluded,
            UnknownOrUnavailableIdentity = unknownIdentity,
            Completed = completed,
            Cancelled = cancelled,
            Notes = notes,
        };
    }
}
