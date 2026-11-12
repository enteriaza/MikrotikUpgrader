// -----------------------------------------------------------------------------
// File: DeviceArtefactNamingTests.cs
//
// Description:
// Unit tests for BackupFileNaming stems, EnsureUniquePath, and identity/IP sanitisation
// alignment with device log naming.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using MikrotikWorker.Backup;
using MikrotikWorker.Logging;

namespace MikrotikWorker.Tests.Backup;
/// <summary>
/// Covers backup artefact stem and unique-path naming.
/// </summary>
public sealed class DeviceArtefactNamingTests
{
    /// <summary>
    /// Scenario: Device stems are built for identity <c>MikroTik</c> at 198.18.0.83 and 198.18.0.84.
    /// Expected: The stems are <c>MikroTik_198.18.0.83</c> and <c>MikroTik_198.18.0.84</c>, they differ, and the log
    /// file name for the first is <c>MikroTik_198.18.0.83.log</c>.
    /// Invariant: Routers sharing an identity never share an artefact stem.
    /// </summary>
    [Fact]
    public void SameIdentity_DifferentIpv4_ProducesDistinctStems()
    {
        string a = DeviceLogNaming.BuildDeviceStem("MikroTik", IPAddress.Parse("198.18.0.83"));
        string b = DeviceLogNaming.BuildDeviceStem("MikroTik", IPAddress.Parse("198.18.0.84"));
        Assert.Equal("MikroTik_198.18.0.83", a);
        Assert.Equal("MikroTik_198.18.0.84", b);
        Assert.Equal("MikroTik_198.18.0.83.log", DeviceLogNaming.BuildDeviceLogFileName("MikroTik", IPAddress.Parse("198.18.0.83")));
        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Scenario: Device stems are built for identity <c>MikroTik</c> at the IPv6 addresses 2001:db8::1 and
    /// 2001:db8::2.
    /// Expected: Both stems start with <c>MikroTik_</c>, contain no colon or other invalid file-name character, and
    /// differ from each other.
    /// Invariant: IPv6 addresses are sanitised for file names without losing uniqueness.
    /// </summary>
    [Fact]
    public void SameIdentity_DifferentIpv6_ProducesDistinctSanitisedStems()
    {
        IPAddress a = IPAddress.Parse("2001:db8::1");
        IPAddress b = IPAddress.Parse("2001:db8::2");
        Assert.Equal(AddressFamily.InterNetworkV6, a.AddressFamily);
        string stemA = DeviceLogNaming.BuildDeviceStem("MikroTik", a);
        string stemB = DeviceLogNaming.BuildDeviceStem("MikroTik", b);
        Assert.StartsWith("MikroTik_", stemA, StringComparison.Ordinal);
        Assert.StartsWith("MikroTik_", stemB, StringComparison.Ordinal);
        Assert.DoesNotContain(":", stemA, StringComparison.Ordinal);
        Assert.DoesNotContain(":", stemB, StringComparison.Ordinal);
        Assert.NotEqual(stemA, stemB);
        Assert.Equal(-1, stemA.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    /// <summary>
    /// Scenario: Device stems are built for identities <c>CORE-A</c> and <c>CORE-B</c> at the same IP, 10.0.0.1.
    /// Expected: The stems are <c>CORE-A_10.0.0.1</c> and <c>CORE-B_10.0.0.1</c>.
    /// Invariant: The identity is part of the stem, so a re-addressed or swapped router does not reuse another
    /// device's name.
    /// </summary>
    [Fact]
    public void DifferentIdentities_SameIp_ProducesDistinctStems()
    {
        IPAddress ip = IPAddress.Parse("10.0.0.1");
        string a = DeviceLogNaming.BuildDeviceStem("CORE-A", ip);
        string b = DeviceLogNaming.BuildDeviceStem("CORE-B", ip);
        Assert.Equal("CORE-A_10.0.0.1", a);
        Assert.Equal("CORE-B_10.0.0.1", b);
    }

    /// <summary>
    /// Scenario: The hostile identity <c>CORE:01/..\evil</c> at 10.0.0.7 is used to build both a device log file
    /// name and a backup base name stamped 2026-11-10T12:00:00.123Z.
    /// Expected: The log name is <c>CORE_01_.._evil_10.0.0.7.log</c>, the backup base name is
    /// <c>CORE_01_.._evil_10.0.0.7_20261110_120000_123</c>, and the backup name begins with the log name's stem.
    /// Invariant: Log and backup naming sanitise identities identically, so unsafe characters never reach the file system.
    /// </summary>
    [Fact]
    public void UnsafeIdentityCharacters_AreSanitisedConsistentlyForLogAndBackup()
    {
        IPAddress ip = IPAddress.Parse("10.0.0.7");
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-11-10T12:00:00.123Z");
        string log = DeviceLogNaming.BuildDeviceLogFileName(@"CORE:01/..\evil", ip);
        string backup = BackupFileNaming.BuildBaseName(@"CORE:01/..\evil", ip, stamp);
        Assert.Equal("CORE_01_.._evil_10.0.0.7.log", log);
        Assert.Equal("CORE_01_.._evil_10.0.0.7_20261110_120000_123", backup);
        Assert.StartsWith(Path.GetFileNameWithoutExtension(log) + "_", backup, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scenario: A backup stem is allocated, its export, binary and manifest files are written and the reservation
    /// is completed; a second allocation then uses the same router and timestamp (same millisecond).
    /// Expected: The first stem is <c>MikroTik_198.18.0.83_20261110_220000_123</c> and the second adds the suffix
    /// <c>_1</c>; neither uses the old identity-only form.
    /// Invariant: Same-millisecond backups of one router get distinct stems that include the IP.
    /// </summary>
    [Fact]
    public void RepeatedBackup_SameRouter_SameMillisecond_AllocatesDistinctSharedStem()
    {
        string root = Directory.CreateTempSubdirectory("name-repeat-").FullName;
        string exports = Path.Combine(root, "Exports");
        string binary = Path.Combine(root, "Binary");
        string manifests = Path.Combine(root, "Manifests");
        IPAddress ip = IPAddress.Parse("198.18.0.83");
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-11-10T22:00:00.123Z");

        using BackupStemReservation first = BackupFileNaming.AllocateUniqueBaseName("MikroTik", ip, stamp, exports, binary, manifests);
        File.WriteAllText(Path.Combine(exports, first.Stem + ".rsc"), "1");
        File.WriteAllText(Path.Combine(binary, first.Stem + ".backup"), "1");
        File.WriteAllText(Path.Combine(manifests, first.Stem + ".json"), "{}");
        first.Complete();

        using BackupStemReservation second = BackupFileNaming.AllocateUniqueBaseName("MikroTik", ip, stamp, exports, binary, manifests);
        Assert.Equal("MikroTik_198.18.0.83_20261110_220000_123", first.Stem);
        Assert.Equal("MikroTik_198.18.0.83_20261110_220000_123_1", second.Stem);
        Assert.False(string.Equals(second.Stem, "MikroTik_20261110_220000_123", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: 20 tasks concurrently allocate a backup stem for the same router and the same millisecond, each
    /// writing its export, binary and manifest files before completing.
    /// Expected: All 20 stems are distinct and each starts with <c>MikroTik_10.1.2.3_20261110_220000_500</c>.
    /// Invariant: Concurrent allocations never receive the same stem, so artefacts cannot overwrite each other.
    /// </summary>
    [Fact]
    public async Task ConcurrentBackupAllocation_NeverOverlaps()
    {
        string root = Directory.CreateTempSubdirectory("name-conc-").FullName;
        string exports = Path.Combine(root, "Exports");
        string binary = Path.Combine(root, "Binary");
        string manifests = Path.Combine(root, "Manifests");
        IPAddress ip = IPAddress.Parse("10.1.2.3");
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-11-10T22:00:00.500Z");

        string[] stems = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            BackupStemReservation reservation = BackupFileNaming.AllocateUniqueBaseName("MikroTik", ip, stamp, exports, binary, manifests);
            File.WriteAllText(reservation.ExportLocalPath, "x");
            File.WriteAllText(reservation.BinaryLocalPath, "x");
            File.WriteAllText(reservation.ManifestLocalPath, "{}");
            reservation.Complete();
            return reservation.Stem;
        })));

        Assert.Equal(20, stems.Distinct(StringComparer.Ordinal).Count());
        Assert.All(stems, s => Assert.StartsWith("MikroTik_10.1.2.3_20261110_220000_500", s, StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: A stem is allocated for <c>MikroTik</c> at 198.18.0.83 and the reservation's file names are read.
    /// Expected: The export, binary and manifest names equal the stem plus <c>.rsc</c>, <c>.backup</c> and
    /// <c>.json</c> respectively.
    /// Invariant: The three artefacts of one backup always share the same stem.
    /// </summary>
    [Fact]
    public void AllocateUniqueBaseName_KeepsRscBackupJsonOnIdenticalStem()
    {
        string root = Directory.CreateTempSubdirectory("name-triple-").FullName;
        string exports = Path.Combine(root, "Exports");
        string binary = Path.Combine(root, "Binary");
        string manifests = Path.Combine(root, "Manifests");
        IPAddress ip = IPAddress.Parse("198.18.0.83");
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-11-10T22:00:00.001Z");
        using BackupStemReservation reservation = BackupFileNaming.AllocateUniqueBaseName("MikroTik", ip, stamp, exports, binary, manifests);
        string stem = reservation.Stem;
        Assert.Equal(reservation.ExportFileName, stem + ".rsc");
        Assert.Equal(reservation.BinaryFileName, stem + ".backup");
        Assert.Equal(reservation.ManifestFileName, stem + ".json");
    }

    /// <summary>
    /// Scenario: A first reservation's export file is written with <c>original</c> and completed; a second
    /// allocation then uses the same router and timestamp.
    /// Expected: The second stem differs from the first, the original export content is unchanged, the second export
    /// file does not exist yet, and the second reservation's marker file exists.
    /// Invariant: Allocation never overwrites existing backup files and claims its stem with a marker file.
    /// </summary>
    [Fact]
    public void ExistingFiles_AreNeverOverwritten_ByAllocation()
    {
        string root = Directory.CreateTempSubdirectory("name-nover-").FullName;
        string exports = Path.Combine(root, "Exports");
        string binary = Path.Combine(root, "Binary");
        string manifests = Path.Combine(root, "Manifests");
        IPAddress ip = IPAddress.Parse("198.18.0.83");
        DateTimeOffset stamp = DateTimeOffset.Parse("2026-11-10T22:00:00.777Z");
        using BackupStemReservation first = BackupFileNaming.AllocateUniqueBaseName("MikroTik", ip, stamp, exports, binary, manifests);
        string exportPath = first.ExportLocalPath;
        File.WriteAllText(exportPath, "original");
        first.Complete();
        using BackupStemReservation second = BackupFileNaming.AllocateUniqueBaseName("MikroTik", ip, stamp, exports, binary, manifests);
        Assert.NotEqual(first.Stem, second.Stem);
        Assert.Equal("original", File.ReadAllText(exportPath));
        Assert.False(File.Exists(second.ExportLocalPath));
        Assert.True(File.Exists(second.MarkerPath));
    }

    /// <summary>
    /// Scenario: A device log file name and a backup base name are built for <c>MikroTik</c> at 198.18.0.83.
    /// Expected: The log stem is <c>MikroTik_198.18.0.83</c> and the backup name starts with that stem followed by
    /// an underscore.
    /// Invariant: Log and backup artefacts for one router can be correlated by the shared identity_ip prefix.
    /// </summary>
    [Fact]
    public void DeviceLogAndBackup_ShareIdentityIpPrefix()
    {
        IPAddress ip = IPAddress.Parse("198.18.0.83");
        string logStem = Path.GetFileNameWithoutExtension(DeviceLogNaming.BuildDeviceLogFileName("MikroTik", ip));
        string backup = BackupFileNaming.BuildBaseName("MikroTik", ip, DateTimeOffset.Parse("2026-11-10T22:00:00.123Z"));
        Assert.Equal("MikroTik_198.18.0.83", logStem);
        Assert.StartsWith(logStem + "_", backup, StringComparison.Ordinal);
    }
}
