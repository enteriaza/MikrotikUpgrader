using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;
public sealed class RouterOsV7TargetProviderTests
{
    [Fact]
    public async Task Startup_LoadsValidVersionFile()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        RouterOsV7TargetSnapshot? snap = harness.Provider.TryGetCurrent();
        Assert.NotNull(snap);
        Assert.Equal("7.23.8", snap.VersionText);
        Assert.Equal(RouterOsVersion.Parse("7.23.8"), snap.Version);
        Assert.Equal(harness.VersionPath, snap.SourcePath);
    }

    [Fact]
    public async Task Startup_MissingFile_LeavesTargetUnavailable()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync(versionContents: null);
        Assert.Null(harness.Provider.TryGetCurrent());
        Assert.Null(harness.Provider.CaptureSnapshot());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n")]
    [InlineData("not-a-version")]
    [InlineData("6.49.19")]
    [InlineData("7.23.8\n7.23.9")]
    [InlineData("7.23.8 extra")]
    public async Task Startup_InvalidContents_LeavesTargetUnavailable(string contents)
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync(contents);
        Assert.Null(harness.Provider.TryGetCurrent());
    }

    [Fact]
    public async Task Startup_TrimsWhitespaceAroundValidVersion()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("  \n7.23.8\r\n  ");
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    [Fact]
    public async Task Reload_ValidUpdate_ChangesTargetForNewJobs()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        RouterOsV7TargetSnapshot first = harness.Provider.CaptureSnapshot()!;

        File.WriteAllText(harness.VersionPath, "7.23.9");
        Assert.True(harness.Provider.TryReloadForTests("manual-update"));

        RouterOsV7TargetSnapshot second = harness.Provider.CaptureSnapshot()!;
        Assert.Equal("7.23.8", first.VersionText);
        Assert.Equal("7.23.9", second.VersionText);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task ExistingJobSnapshot_RetainsOriginalTargetAfterUpdate()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        RouterOsV7TargetSnapshot jobSnapshot = harness.Provider.CaptureSnapshot()!;

        File.WriteAllText(harness.VersionPath, "7.24.0");
        Assert.True(harness.Provider.TryReloadForTests("during-job"));

        Assert.Equal("7.23.8", jobSnapshot.VersionText);
        Assert.Equal("7.24.0", harness.Provider.TryGetCurrent()!.VersionText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("6.49.19")]
    [InlineData("7.23.8\nnoise")]
    public async Task InvalidUpdate_PreservesLastKnownGood(string contents)
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        File.WriteAllText(harness.VersionPath, contents);
        Assert.False(harness.Provider.TryReloadForTests("reject"));
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    [Fact]
    public async Task DeletedFile_PreservesLastKnownGood()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        File.Delete(harness.VersionPath);
        Assert.False(harness.Provider.TryReloadForTests("deleted"));
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    [Fact]
    public async Task CreatedAfterStartup_BecomesAvailable()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync(versionContents: null);
        Assert.Null(harness.Provider.TryGetCurrent());

        File.WriteAllText(harness.VersionPath, "7.23.8");
        Assert.True(harness.Provider.TryReloadForTests("created"));
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    [Fact]
    public async Task PartialWrite_DoesNotReplaceValidTarget()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        // Simulate a truncated/incomplete write that is not a valid version.
        File.WriteAllText(harness.VersionPath, "7.");
        Assert.False(harness.Provider.TryReloadForTests("partial"));
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    [Fact]
    public async Task DuplicateReload_SameVersion_RemainsStable()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        RouterOsV7TargetSnapshot? before = harness.Provider.TryGetCurrent();
        Assert.True(harness.Provider.TryReloadForTests("duplicate"));
        RouterOsV7TargetSnapshot? after = harness.Provider.TryGetCurrent();
        Assert.Same(before, after);
    }

    [Fact]
    public async Task ConcurrentReaders_ReceiveConsistentValues()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        RouterOsV7TargetSnapshot?[] results = new RouterOsV7TargetSnapshot?[64];
        Parallel.For(0, results.Length, i => results[i] = harness.Provider.TryGetCurrent());
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.Equal("7.23.8", r.VersionText);
        });
    }

    [Fact]
    public async Task FileWatcher_RenameReplace_UpdatesTarget()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        string temp = harness.VersionPath + ".tmp";
        File.WriteAllText(temp, "7.23.9");
        File.Move(temp, harness.VersionPath, overwrite: true);

        await WaitForAsync(
            () => harness.Provider.TryGetCurrent()?.VersionText == "7.23.9",
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Shutdown_DisposesWatcherCleanly()
    {
        TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        await harness.Provider.StopAsync(CancellationToken.None);
        harness.Provider.Dispose();
        // Idempotent stop/dispose must not throw.
        await harness.Provider.StopAsync(CancellationToken.None);
        harness.Provider.Dispose();
        await harness.DisposeAsync();
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private sealed class TargetHarness : IAsyncDisposable
    {
        private TargetHarness(string root, RouterOsV7TargetProvider provider, string versionPath)
        {
            Root = root;
            Provider = provider;
            VersionPath = versionPath;
        }

        public string Root { get; }

        public string VersionPath { get; }

        public RouterOsV7TargetProvider Provider { get; }

        public static async Task<TargetHarness> CreateAsync(string? versionContents)
        {
            string root = Directory.CreateTempSubdirectory("v7-target-").FullName;
            string mikrotik = Path.Combine(root, "mikrotik");
            Directory.CreateDirectory(mikrotik);
            string versionPath = Path.Combine(mikrotik, "version");
            if (versionContents is not null)
            {
                File.WriteAllText(versionPath, versionContents);
            }

            RouterOsV7TargetProvider provider = new(
                Options.Create(new FirmwareFilesOptions { Path = root }),
                TimeProvider.System,
                NullLogger<RouterOsV7TargetProvider>.Instance);
            await provider.StartAsync(CancellationToken.None);
            return new TargetHarness(root, provider, versionPath);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.StopAsync(CancellationToken.None);
            Provider.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
