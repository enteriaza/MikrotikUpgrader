// -----------------------------------------------------------------------------
// File: EnumNumericCompatibilityTests.cs
//
// Description:
// Locks explicit numeric identities for assessment/upgrade enums after dead-member
// removals. Retained members must keep historical values; removed slots remain unused.
//
// -----------------------------------------------------------------------------

using MikrotikWorker.Assessment;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Assessment;

/// <summary>
/// Compatibility: explicit enum numeric values must not shift when unused members are removed.
/// </summary>
public sealed class EnumNumericCompatibilityTests
{
    /// <summary>
    /// Retained <see cref="UpgradeOutcome"/> members keep their historical integers, including
    /// gaps left by removed values 9, 13 and 16.
    /// </summary>
    [Fact]
    public void UpgradeOutcome_RetainedMembers_KeepHistoricalNumericValues()
    {
        Assert.Equal(0, (int)UpgradeOutcome.NotAttempted);
        Assert.Equal(1, (int)UpgradeOutcome.SkippedAlreadyAtTarget);
        Assert.Equal(2, (int)UpgradeOutcome.SkippedUpgradeDisabled);
        Assert.Equal(3, (int)UpgradeOutcome.UpgradeSucceeded);
        Assert.Equal(4, (int)UpgradeOutcome.UpgradeFailed);
        Assert.Equal(5, (int)UpgradeOutcome.UpgradeTimedOut);
        Assert.Equal(6, (int)UpgradeOutcome.UpgradeVersionMismatch);
        Assert.Equal(7, (int)UpgradeOutcome.UpgradePackageMismatch);
        Assert.Equal(8, (int)UpgradeOutcome.UpgradeInterrupted);
        Assert.Equal(10, (int)UpgradeOutcome.BackupFailed);
        Assert.Equal(11, (int)UpgradeOutcome.PackageResolutionFailed);
        Assert.Equal(12, (int)UpgradeOutcome.PolicyBlocked);
        Assert.Equal(14, (int)UpgradeOutcome.SkippedNewerThanTarget);
        Assert.Equal(15, (int)UpgradeOutcome.TargetUnavailable);
        Assert.Equal(17, (int)UpgradeOutcome.LegacyFirmwareAutomationCleanupFailed);
        Assert.Equal(18, (int)UpgradeOutcome.RouterboardUpgradeFailed);
        Assert.Equal(19, (int)UpgradeOutcome.RouterboardUpgradeTimedOut);
        Assert.Equal(20, (int)UpgradeOutcome.RouterboardVerificationFailed);
        Assert.Equal(21, (int)UpgradeOutcome.OspfCaptureFailed);
        Assert.Equal(22, (int)UpgradeOutcome.OspfPlanValidationFailed);
        Assert.Equal(23, (int)UpgradeOutcome.OspfMigrationApplyFailed);
        Assert.Equal(24, (int)UpgradeOutcome.OspfMigrationVerificationFailed);
        Assert.Equal(25, (int)UpgradeOutcome.OspfStaticSummaryRedistributionFailed);
        Assert.Equal(26, (int)UpgradeOutcome.TransientTransportFailure);

        Assert.DoesNotContain(9, DefinedValues<UpgradeOutcome>());
        Assert.DoesNotContain(13, DefinedValues<UpgradeOutcome>());
        Assert.DoesNotContain(16, DefinedValues<UpgradeOutcome>());
    }

    /// <summary>
    /// Retained <see cref="UpgradePathStatus"/> members keep values 0/4/5/7–10; removed 1/2/3/6 stay unused.
    /// </summary>
    [Fact]
    public void UpgradePathStatus_RetainedMembers_KeepHistoricalNumericValues()
    {
        Assert.Equal(0, (int)UpgradePathStatus.EligibleSubjectToManualPathVerification);
        Assert.Equal(4, (int)UpgradePathStatus.UnknownRequiresManualReview);
        Assert.Equal(5, (int)UpgradePathStatus.Blocked);
        Assert.Equal(7, (int)UpgradePathStatus.SkipV6SteppingProceedToV7);
        Assert.Equal(8, (int)UpgradePathStatus.EligibleDirectV7Upgrade);
        Assert.Equal(9, (int)UpgradePathStatus.AlreadyAtV7Target);
        Assert.Equal(10, (int)UpgradePathStatus.NewerThanV7Target);

        Assert.DoesNotContain(1, DefinedValues<UpgradePathStatus>());
        Assert.DoesNotContain(2, DefinedValues<UpgradePathStatus>());
        Assert.DoesNotContain(3, DefinedValues<UpgradePathStatus>());
        Assert.DoesNotContain(6, DefinedValues<UpgradePathStatus>());
    }

    /// <summary>
    /// <see cref="DeviceAssessmentStatus.Blocked"/> remains 3 after ManualReviewRequired/Unsupported removal.
    /// </summary>
    [Fact]
    public void DeviceAssessmentStatus_RetainedMembers_KeepHistoricalNumericValues()
    {
        Assert.Equal(0, (int)DeviceAssessmentStatus.ReadyForUpgrade);
        Assert.Equal(1, (int)DeviceAssessmentStatus.AlreadyAtTarget);
        Assert.Equal(3, (int)DeviceAssessmentStatus.Blocked);

        Assert.DoesNotContain(2, DefinedValues<DeviceAssessmentStatus>());
        Assert.DoesNotContain(4, DefinedValues<DeviceAssessmentStatus>());
    }

    /// <summary>
    /// <see cref="PackageCompatibilityStatus"/> values 0–5 are unchanged; former NotEvaluated=6 is unused.
    /// </summary>
    [Fact]
    public void PackageCompatibilityStatus_RetainedMembers_KeepHistoricalNumericValues()
    {
        Assert.Equal(0, (int)PackageCompatibilityStatus.Compatible);
        Assert.Equal(1, (int)PackageCompatibilityStatus.Missing);
        Assert.Equal(2, (int)PackageCompatibilityStatus.ArchitectureMismatch);
        Assert.Equal(3, (int)PackageCompatibilityStatus.VersionMismatch);
        Assert.Equal(4, (int)PackageCompatibilityStatus.Ambiguous);
        Assert.Equal(5, (int)PackageCompatibilityStatus.Unidentifiable);

        Assert.DoesNotContain(6, DefinedValues<PackageCompatibilityStatus>());
    }

    /// <summary>
    /// <see cref="RouterboardFirmwareStatus"/> values 0–3 are unchanged; former NotEvaluated=4 is unused.
    /// </summary>
    [Fact]
    public void RouterboardFirmwareStatus_RetainedMembers_KeepHistoricalNumericValues()
    {
        Assert.Equal(0, (int)RouterboardFirmwareStatus.Current);
        Assert.Equal(1, (int)RouterboardFirmwareStatus.UpgradeAvailable);
        Assert.Equal(2, (int)RouterboardFirmwareStatus.Unavailable);
        Assert.Equal(3, (int)RouterboardFirmwareStatus.Inconclusive);

        Assert.DoesNotContain(4, DefinedValues<RouterboardFirmwareStatus>());
    }

    /// <summary>Defined underlying integers for an enum (excludes undefined gaps).</summary>
    private static HashSet<int> DefinedValues<TEnum>()
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(static v => Convert.ToInt32(v)).ToHashSet();
}
