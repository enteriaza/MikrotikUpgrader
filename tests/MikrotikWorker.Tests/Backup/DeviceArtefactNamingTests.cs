using System.Net;
using System.Net.Sockets;
using MikrotikWorker.Backup;
using MikrotikWorker.Logging;

namespace MikrotikWorker.Tests.Backup;
public sealed class DeviceArtefactNamingTests
{
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

    [Fact]
    public void DifferentIdentities_SameIp_ProducesDistinctStems()
    {
        IPAddress ip = IPAddress.Parse("10.0.0.1");
        string a = DeviceLogNaming.BuildDeviceStem("CORE-A", ip);
        string b = DeviceLogNaming.BuildDeviceStem("CORE-B", ip);
        Assert.Equal("CORE-A_10.0.0.1", a);
        Assert.Equal("CORE-B_10.0.0.1", b);
    }

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
