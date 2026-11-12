// -----------------------------------------------------------------------------
// File: JobOutcomeMappingTests.cs
//
// Description:
// Maps UpgradeOutcome values to ScheduledJobTerminalState via
// RouterJobExecutor.MapUpgradeOutcome, asserting success-like vs blocked/
// failed/cancelled semantics.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Scheduling;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Scheduling;

/// <summary>
/// Job outcome semantics: only a verified RouterOS 7 target (or an already-verified target) is a success.
/// </summary>
public sealed class JobOutcomeMappingTests
{
    /// <summary>
    /// Theory over representative outcomes.
    /// Expects each outcome's mapped terminal state and success bit match the table,
    /// so CSV/result reporting cannot claim success for blocked, failed, or cancelled work.
    /// </summary>
    [Theory]
    [InlineData(UpgradeOutcome.UpgradeSucceeded, ScheduledJobTerminalState.Succeeded, true)]
    [InlineData(UpgradeOutcome.SkippedAlreadyAtTarget, ScheduledJobTerminalState.AlreadyUpToDate, true)]
    [InlineData(UpgradeOutcome.SkippedUpgradeDisabled, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.NotAttempted, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.SkippedNewerThanTarget, ScheduledJobTerminalState.Blocked, false)]
    [InlineData(UpgradeOutcome.PolicyBlocked, ScheduledJobTerminalState.Blocked, false)]
    [InlineData(UpgradeOutcome.TargetUnavailable, ScheduledJobTerminalState.Blocked, false)]
    [InlineData(UpgradeOutcome.UpgradeInterrupted, ScheduledJobTerminalState.Cancelled, false)]
    [InlineData(UpgradeOutcome.UpgradeFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.UpgradeTimedOut, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.UpgradeVersionMismatch, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.UpgradePackageMismatch, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.ManualReviewRequired, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.BackupFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.PackageResolutionFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.LegacyFirmwareAutomationCleanupFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.RouterboardUpgradeFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.RouterboardUpgradeTimedOut, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.RouterboardVerificationFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.OspfCaptureFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.OspfPlanValidationFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.OspfMigrationApplyFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.OspfMigrationVerificationFailed, ScheduledJobTerminalState.Failed, false)]
    [InlineData(UpgradeOutcome.OspfStaticSummaryRedistributionFailed, ScheduledJobTerminalState.Failed, false)]
    public void MapUpgradeOutcome_IsTruthful(
        UpgradeOutcome outcome,
        ScheduledJobTerminalState expectedTerminal,
        bool expectedSuccess)
    {
        (ScheduledJobTerminalState terminal, bool success) = RouterJobExecutor.MapUpgradeOutcome(outcome);

        Assert.Equal(expectedTerminal, terminal);
        Assert.Equal(expectedSuccess, success);
    }

    /// <summary>
    /// Exhaustive enum sweep of every <c>UpgradeOutcome</c>.
    /// Expects success (and Succeeded/AlreadyUpToDate terminals) only when the device
    /// is verified at the RouterOS 7 target — regression against optimistic success mapping.
    /// </summary>
    [Fact]
    public void OnlyVerifiedTargetOutcomes_AreSuccess()
    {
        foreach (UpgradeOutcome outcome in Enum.GetValues<UpgradeOutcome>())
        {
            (ScheduledJobTerminalState terminal, bool success) = RouterJobExecutor.MapUpgradeOutcome(outcome);
            bool isTargetVerified = outcome is UpgradeOutcome.UpgradeSucceeded or UpgradeOutcome.SkippedAlreadyAtTarget;

            Assert.Equal(isTargetVerified, success);
            Assert.Equal(
                isTargetVerified,
                terminal is ScheduledJobTerminalState.Succeeded or ScheduledJobTerminalState.AlreadyUpToDate);
        }
    }

    /// <summary>
    /// Exhaustive enum sweep.
    /// Expects no upgrade outcome maps to <c>ScheduledJobTerminalState.Skipped</c>,
    /// which is reserved for non-upgrade skip paths.
    /// </summary>
    [Fact]
    public void SkippedTerminalState_IsNeverProducedByUpgradeOutcomes()
    {
        foreach (UpgradeOutcome outcome in Enum.GetValues<UpgradeOutcome>())
        {
            Assert.NotEqual(ScheduledJobTerminalState.Skipped, RouterJobExecutor.MapUpgradeOutcome(outcome).Terminal);
        }
    }
}
