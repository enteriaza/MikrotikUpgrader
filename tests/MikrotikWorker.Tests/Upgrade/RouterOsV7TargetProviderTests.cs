// -----------------------------------------------------------------------------
// File: RouterOsV7TargetProviderTests.cs
//
// Description:
// Unit tests for RouterOsV7TargetProvider version-file load, watch, and
// snapshot capture.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MikrotikWorker.Assessment;
using MikrotikWorker.Upgrade;

namespace MikrotikWorker.Tests.Upgrade;
/// <summary>
/// Unit tests for RouterOsV7TargetProvider load/watch/snapshot behaviour.
/// </summary>
public sealed class RouterOsV7TargetProviderTests
{
    /// <summary>
    /// A version file containing 7.23.8 gives a non-null snapshot with <c>VersionText</c> 7.23.8, a parsed
    /// <c>Version</c> equal to <c>RouterOsVersion.Parse("7.23.8")</c>, and <c>SourcePath</c> set to the file's
    /// path.
    /// </summary>
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

    /// <summary>
    /// With no version file at startup, both <c>TryGetCurrent</c> and <c>CaptureSnapshot</c> return null.
    /// </summary>
    [Fact]
    public async Task Startup_MissingFile_LeavesTargetUnavailable()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync(versionContents: null);
        Assert.Null(harness.Provider.TryGetCurrent());
        Assert.Null(harness.Provider.CaptureSnapshot());
    }

    /// <summary>
    /// Each InlineData content (empty, whitespace only, <c>not-a-version</c>, the v6 version 6.49.19, two versions
    /// on separate lines, and 7.23.8 followed by extra text) must leave <c>TryGetCurrent</c> null.
    /// </summary>
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

    /// <summary>
    /// A file with surrounding blank space and CRLF around 7.23.8 loads as <c>VersionText</c> 7.23.8.
    /// </summary>
    [Fact]
    public async Task Startup_TrimsWhitespaceAroundValidVersion()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("  \n7.23.8\r\n  ");
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    /// <summary>
    /// After the file changes from 7.23.8 to 7.23.9 and a reload succeeds, a snapshot captured earlier still reads
    /// 7.23.8 and a newly captured one reads 7.23.9. The two are different instances.
    /// </summary>
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

    /// <summary>
    /// A snapshot captured before the file changes to 7.24.0 keeps 7.23.8 after a successful reload, while
    /// <c>TryGetCurrent</c> reports 7.24.0.
    /// </summary>
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

    /// <summary>
    /// Overwriting a loaded 7.23.8 target with empty text, <c>bogus</c>, 6.49.19 or 7.23.8 followed by noise makes
    /// the reload return false. <c>TryGetCurrent</c> still reports 7.23.8.
    /// </summary>
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

    /// <summary>
    /// Deleting the version file makes the reload return false, and the current target remains 7.23.8.
    /// </summary>
    [Fact]
    public async Task DeletedFile_PreservesLastKnownGood()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        File.Delete(harness.VersionPath);
        Assert.False(harness.Provider.TryReloadForTests("deleted"));
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    /// <summary>
    /// A provider that started with no file reports no target. Once the file is written with 7.23.8, the reload
    /// returns true and <c>TryGetCurrent</c> reports 7.23.8.
    /// </summary>
    [Fact]
    public async Task CreatedAfterStartup_BecomesAvailable()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync(versionContents: null);
        Assert.Null(harness.Provider.TryGetCurrent());

        File.WriteAllText(harness.VersionPath, "7.23.8");
        Assert.True(harness.Provider.TryReloadForTests("created"));
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    /// <summary>
    /// Simulate a truncated/incomplete write that is not a valid version.
    /// A truncated version file containing only '7.' makes the reload return false, and the current target stays
    /// 7.23.8.
    /// </summary>
    [Fact]
    public async Task PartialWrite_DoesNotReplaceValidTarget()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        // Simulate a truncated/incomplete write that is not a valid version.
        File.WriteAllText(harness.VersionPath, "7.");
        Assert.False(harness.Provider.TryReloadForTests("partial"));
        Assert.Equal("7.23.8", harness.Provider.TryGetCurrent()!.VersionText);
    }

    /// <summary>
    /// Reloading an unchanged 7.23.8 file returns true and <c>TryGetCurrent</c> returns the same snapshot instance
    /// as before, not a replacement.
    /// </summary>
    [Fact]
    public async Task DuplicateReload_SameVersion_RemainsStable()
    {
        await using TargetHarness harness = await TargetHarness.CreateAsync("7.23.8");
        RouterOsV7TargetSnapshot? before = harness.Provider.TryGetCurrent();
        Assert.True(harness.Provider.TryReloadForTests("duplicate"));
        RouterOsV7TargetSnapshot? after = harness.Provider.TryGetCurrent();
        Assert.Same(before, after);
    }

    /// <summary>
    /// Sixty-four parallel <c>TryGetCurrent</c> calls all return a non-null snapshot with <c>VersionText</c>
    /// 7.23.8.
    /// </summary>
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

    /// <summary>
    /// Moving a temporary file containing 7.23.9 over the version file (overwrite) is picked up by the file watcher
    /// without an explicit reload: <c>TryGetCurrent</c> reports 7.23.9 within 5 seconds.
    /// </summary>
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

    /// <summary>
    /// Idempotent stop/dispose must not throw.
    /// Calling <c>StopAsync</c> and <c>Dispose</c> a second time must not throw. The test asserts nothing beyond
    /// completing without an exception.
    /// </summary>
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

    /// <summary>
    /// Polls a condition until timeout or success.
    /// </summary>
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

    /// <summary>
    /// Fixture that creates a temp firmware root with an optional <c>mikrotik/version</c> file, starts a
    /// <c>RouterOsV7TargetProvider</c> over it, and stops it and deletes the root on dispose.
    /// </summary>
    private sealed class TargetHarness : IAsyncDisposable
    {
        /// <summary>Creates a harness over an already started provider.</summary>
        /// <param name="root">Temp firmware root to delete on dispose.</param>
        /// <param name="provider">Started provider under test.</param>
        /// <param name="versionPath">Path of the <c>mikrotik/version</c> file.</param>
        private TargetHarness(string root, RouterOsV7TargetProvider provider, string versionPath)
        {
            Root = root;
            Provider = provider;
            VersionPath = versionPath;
        }

        /// <summary>Temp firmware root containing the <c>mikrotik</c> folder.</summary>
        public string Root { get; }

        /// <summary>Path of the <c>mikrotik/version</c> file read by the provider.</summary>
        public string VersionPath { get; }

        /// <summary>Started provider under test.</summary>
        public RouterOsV7TargetProvider Provider { get; }

        /// <summary>Creates a temp firmware root, optionally writes the version file, and starts a provider over it.</summary>
        /// <param name="versionContents">Version file text; null leaves the file absent.</param>
        /// <returns>A started harness; dispose it to stop the provider and delete the temp root.</returns>
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

        /// <summary>Stops and disposes the provider and best-effort deletes the temp root.</summary>
        /// <returns>A task that completes when cleanup is done.</returns>
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
