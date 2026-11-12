// -----------------------------------------------------------------------------
// File: MikrotikScpFileTransferClientTests.cs
//
// Description:
// Tests SFTP download partial-file promotion, cancellation boundaries, and destination
// collision behaviour of MikrotikScpFileTransferClient via a fake session factory.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.MikroTik;
using Renci.SshNet;

namespace MikrotikWorker.Tests.MikroTik;

/// <summary>
/// Covers SFTP partial download and cancellation behaviour.
/// </summary>
public sealed class MikrotikScpFileTransferClientTests
{
    /// <summary>
    /// Scenario: A download starts against a session whose connect blocks until cancelled, and the token is
    /// cancelled once connect has been called.
    /// Expected: The download throws <c>OperationCanceledException</c>; no destination or <c>.partial</c> file
    /// exists, connect ran once, download never ran, and the session was disposed once.
    /// Invariant: Cancelling during connect leaves nothing on disk and releases the session.
    /// </summary>
    [Fact]
    public async Task CancellationBeforeDownloadStarts_DuringConnect_DoesNotCreateDestinationOrPartial()
    {
        string dir = Directory.CreateTempSubdirectory("scp-cancel-before-").FullName;
        string dest = Path.Combine(dir, "export.rsc");
        using CancellationTokenSource cts = new();

        ScriptedSession session = new(connectDelay: Timeout.InfiniteTimeSpan);
        MikrotikScpFileTransferClient client = CreateClient(() => session);

        Task download = client.DownloadFileAsync(
            "10.0.0.1",
            22,
            "admin",
            "x",
            "a.rsc",
            dest,
            TimeSpan.FromSeconds(5),
            cts.Token);
        await WaitForAsync(() => session.ConnectCalls > 0, TimeSpan.FromSeconds(2));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, session.ConnectCalls);
        Assert.Equal(0, session.DownloadCalls);
    }

    /// <summary>
    /// Scenario: The download script writes <c>partial-content</c> to the stream (a <c>.partial</c> file exists at
    /// that point), then cancels the token and throws.
    /// Expected: The download throws <c>OperationCanceledException</c>; the partial file is deleted, the destination
    /// does not exist, and the session was disposed once after one download call.
    /// Invariant: Cancelled transfers never promote or orphan partial data.
    /// </summary>
    [Fact]
    public async Task CancellationDuringDownload_DeletesPartial_AndDoesNotPromoteDestination()
    {
        string dir = Directory.CreateTempSubdirectory("scp-cancel-during-").FullName;
        string dest = Path.Combine(dir, "export.rsc");
        using CancellationTokenSource cts = new();

        ScriptedSession session = new(
            onDownload: async (stream, token) =>
            {
                byte[] chunk = "partial-content"u8.ToArray();
                await stream.WriteAsync(chunk, token);
                await stream.FlushAsync(token);
                Assert.NotEmpty(Directory.EnumerateFiles(dir, "*.partial"));
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
            });
        MikrotikScpFileTransferClient client = CreateClient(() => session);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DownloadFileAsync("10.0.0.1", 22, "admin", "x", "a.rsc", dest, TimeSpan.FromSeconds(5), cts.Token));

        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(1, session.DownloadCalls);
    }

    /// <summary>
    /// Scenario: The download writes the complete content, then the after-download hook cancels the token before the
    /// client promotes the file.
    /// Expected: The download throws <c>OperationCanceledException</c>; there is no destination or partial file and
    /// the session was disposed once.
    /// Invariant: Even a fully downloaded file is discarded if cancellation lands before promotion.
    /// </summary>
    [Fact]
    public async Task CancellationAfterDownloadBeforePromote_DoesNotLeaveDestination()
    {
        string dir = Directory.CreateTempSubdirectory("scp-cancel-after-").FullName;
        string dest = Path.Combine(dir, "export.rsc");
        using CancellationTokenSource cts = new();

        ScriptedSession session = new(
            onDownload: async (stream, token) =>
            {
                await stream.WriteAsync("complete"u8.ToArray(), token);
                await stream.FlushAsync(token);
            },
            afterDownload: () => cts.Cancel());
        MikrotikScpFileTransferClient client = CreateClient(() => session);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DownloadFileAsync("10.0.0.1", 22, "admin", "x", "a.rsc", dest, TimeSpan.FromSeconds(5), cts.Token));

        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>
    /// Scenario: The download script writes <c>finished-package</c> and completes without error.
    /// Expected: The destination exists with exactly that content, no <c>.partial</c> file remains, and the session
    /// was disposed once.
    /// Invariant: A successful transfer promotes the complete file and releases the session exactly once.
    /// </summary>
    [Fact]
    public async Task SuccessfulDownload_PromotesCompleteFile_AndDisposesSessionOnce()
    {
        string dir = Directory.CreateTempSubdirectory("scp-success-").FullName;
        string dest = Path.Combine(dir, "export.rsc");

        ScriptedSession session = new(
            onDownload: async (stream, token) =>
            {
                await stream.WriteAsync("finished-package"u8.ToArray(), token);
                await stream.FlushAsync(token);
            });
        MikrotikScpFileTransferClient client = CreateClient(() => session);

        await client.DownloadFileAsync(
            "10.0.0.1",
            22,
            "admin",
            "x",
            "a.rsc",
            dest,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
        Assert.Equal("finished-package", await File.ReadAllTextAsync(dest));
        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>
    /// Scenario: The destination already holds <c>original-valid</c> when a download to that path is requested.
    /// Expected: An <c>IOException</c> mentioning "already exists" is thrown; the file content is unchanged, no
    /// partial file is created, and the session is never connected or disposed.
    /// Invariant: An existing file is never overwritten, and the refusal happens before any connection is made.
    /// </summary>
    [Fact]
    public async Task ExistingDestination_IsLeftIntact_WhenDownloadRefused()
    {
        string dir = Directory.CreateTempSubdirectory("scp-existing-").FullName;
        string dest = Path.Combine(dir, "export.rsc");
        await File.WriteAllTextAsync(dest, "original-valid");

        ScriptedSession session = new();
        MikrotikScpFileTransferClient client = CreateClient(() => session);

        IOException ex = await Assert.ThrowsAsync<IOException>(() =>
            client.DownloadFileAsync("10.0.0.1", 22, "admin", "x", "a.rsc", dest, TimeSpan.FromSeconds(5)));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original-valid", await File.ReadAllTextAsync(dest));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
        Assert.Equal(0, session.ConnectCalls);
        Assert.Equal(0, session.DisposeCount);
    }

    /// <summary>
    /// Scenario: A <c>.npk</c> download writes 1024 bytes, then the token is cancelled while the transfer is blocked.
    /// Expected: The download throws <c>OperationCanceledException</c>; the destination does not exist and the
    /// directory contains no <c>.npk</c> file.
    /// Invariant: A truncated firmware package never appears under its final name where it could be mistaken for a
    /// complete file.
    /// </summary>
    [Fact]
    public async Task InterruptedDownload_NeverAppearsAsCompletedPackage()
    {
        string dir = Directory.CreateTempSubdirectory("scp-incomplete-").FullName;
        string dest = Path.Combine(dir, "routeros.npk");
        using CancellationTokenSource cts = new();

        ScriptedSession session = new(
            onDownload: async (stream, token) =>
            {
                await stream.WriteAsync(new byte[1024], token);
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            });
        MikrotikScpFileTransferClient client = CreateClient(() => session);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DownloadFileAsync("10.0.0.1", 22, "admin", "x", "a.npk", dest, TimeSpan.FromSeconds(5), cts.Token));

        Assert.False(File.Exists(dest));
        Assert.False(Directory.EnumerateFiles(dir, "*.npk").Any());
    }

    /// <summary>
    /// Scenario: The download script throws <c>IOException("remote missing")</c> with no cancellation requested.
    /// Expected: An <c>IOException</c> with message <c>remote missing</c> propagates; no destination or partial file
    /// remains, and the session was disposed once.
    /// Invariant: Genuine transfer errors surface as errors, not as cancellations, and are cleaned up the same way.
    /// </summary>
    [Fact]
    public async Task OrdinaryFailure_IsNotReportedAsCancellation()
    {
        string dir = Directory.CreateTempSubdirectory("scp-fail-").FullName;
        string dest = Path.Combine(dir, "export.rsc");

        ScriptedSession session = new(
            onDownload: (_, _) => throw new IOException("remote missing"));
        MikrotikScpFileTransferClient client = CreateClient(() => session);

        IOException ex = await Assert.ThrowsAsync<IOException>(() =>
            client.DownloadFileAsync("10.0.0.1", 22, "admin", "x", "a.rsc", dest, TimeSpan.FromSeconds(5)));

        Assert.Equal("remote missing", ex.Message);
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
        Assert.Equal(1, session.DisposeCount);
    }

    /// <summary>
    /// Scenario: Five downloads each block inside the download script and are cancelled once their session has
    /// started downloading.
    /// Expected: Each download throws <c>OperationCanceledException</c> and leaves no destination file; exactly five
    /// sessions were created and each was disposed once.
    /// Invariant: Repeated cancellations do not leak sessions.
    /// </summary>
    [Fact]
    public async Task RepeatedCancellation_DoesNotAccumulateSessions()
    {
        List<ScriptedSession> sessions = [];
        object gate = new();
        MikrotikScpFileTransferClient client = new(
            NullLogger<MikrotikScpFileTransferClient>.Instance,
            _ =>
            {
                ScriptedSession session = new(
                    onDownload: async (_, token) =>
                    {
                        await Task.Delay(Timeout.Infinite, token);
                    });
                lock (gate)
                {
                    sessions.Add(session);
                }

                return session;
            });

        for (int i = 0; i < 5; i++)
        {
            string dest = Path.Combine(
                Directory.CreateTempSubdirectory($"scp-repeat-{i}-").FullName,
                "a.rsc");
            using CancellationTokenSource cts = new();
            Task download = client.DownloadFileAsync(
                "10.0.0.1",
                22,
                "admin",
                "x",
                "a.rsc",
                dest,
                TimeSpan.FromSeconds(5),
                cts.Token);
            await WaitForAsync(
                () =>
                {
                    lock (gate)
                    {
                        return sessions.Count > i && sessions[i].DownloadCalls > 0;
                    }
                },
                TimeSpan.FromSeconds(2));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.False(File.Exists(dest));
        }

        Assert.Equal(5, sessions.Count);
        Assert.All(sessions, s => Assert.Equal(1, s.DisposeCount));
    }

    /// <summary>
    /// Scenario: The download script cancels the token and throws via <c>ThrowIfCancellationRequested</c>.
    /// Expected: An <c>OperationCanceledException</c> is observed with cancellation requested; no destination or
    /// partial file remains.
    /// Invariant: Cancellation surfaces to the caller as <c>OperationCanceledException</c> and leaves no files.
    /// </summary>
    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceledException()
    {
        string dir = Directory.CreateTempSubdirectory("scp-cancel-propagate-").FullName;
        string dest = Path.Combine(dir, "job.rsc");
        using CancellationTokenSource cts = new();

        ScriptedSession session = new(
            onDownload: async (_, token) =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
            });
        MikrotikScpFileTransferClient client = CreateClient(() => session);

        OperationCanceledException ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DownloadFileAsync("10.0.0.1", 22, "admin", "x", "a.rsc", dest, TimeSpan.FromSeconds(5), cts.Token));

        Assert.True(ex.CancellationToken.IsCancellationRequested || cts.IsCancellationRequested);
        Assert.False(File.Exists(dest));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
    }

    /// <summary>
    /// Hold the second download open until cancelled so it cannot promote first.
    /// Expects: ThrowsAnyAsync&lt;OperationCanceledException&gt;(() =&gt; second);; True(File.Exists(dest));; Equal("session-1", File.ReadAllTextAsync(dest));; Empty(Directory.EnumerateFiles(dir, "*.partial"));.
    /// </summary>
    [Fact]
    public async Task ConcurrentDownloads_SameDestination_DoNotSharePartialArtefacts()
    {
        string dir = Directory.CreateTempSubdirectory("scp-concurrent-").FullName;
        string dest = Path.Combine(dir, "export.rsc");
        TaskCompletionSource firstPartialReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource allowFirstToFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        List<string> observedPartials = [];
        object gate = new();
        int sessionIndex = 0;

        MikrotikScpFileTransferClient client = new(
            NullLogger<MikrotikScpFileTransferClient>.Instance,
            _ =>
            {
                int index = Interlocked.Increment(ref sessionIndex);
                return new ScriptedSession(
                    onDownload: async (stream, token) =>
                    {
                        await stream.WriteAsync(
                            System.Text.Encoding.UTF8.GetBytes($"session-{index}"),
                            token);
                        await stream.FlushAsync(token);
                        lock (gate)
                        {
                            observedPartials.AddRange(Directory.EnumerateFiles(dir, "*.partial"));
                        }

                        if (index == 1)
                        {
                            firstPartialReady.TrySetResult();
                            await allowFirstToFinish.Task.WaitAsync(token);
                        }
                        else
                        {
                            // Hold the second download open until cancelled so it cannot promote first.
                            await Task.Delay(Timeout.Infinite, token);
                        }
                    });
            });

        using CancellationTokenSource secondCts = new();
        Task first = client.DownloadFileAsync(
            "10.0.0.1", 22, "admin", "x", "a.rsc", dest, TimeSpan.FromSeconds(5));
        await firstPartialReady.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task second = client.DownloadFileAsync(
            "10.0.0.1", 22, "admin", "x", "a.rsc", dest, TimeSpan.FromSeconds(5), secondCts.Token);
        await WaitForAsync(
            () =>
            {
                lock (gate)
                {
                    return observedPartials.Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
                }
            },
            TimeSpan.FromSeconds(2));

        await secondCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        allowFirstToFinish.TrySetResult();
        await first;

        Assert.True(File.Exists(dest));
        Assert.Equal("session-1", await File.ReadAllTextAsync(dest));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
        Assert.True(
            observedPartials.Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2,
            "Concurrent downloads must use distinct partial paths.");
    }

    /// <summary>
    /// Creates a <c>MikrotikScpFileTransferClient</c> whose session factory ignores connection options and returns
    /// the scripted session produced by the supplied factory.
    /// </summary>
    private static MikrotikScpFileTransferClient CreateClient(Func<ScriptedSession> sessionFactory) =>
        new(NullLogger<MikrotikScpFileTransferClient>.Instance, _ => sessionFactory());

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

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    /// <summary>
    /// SFTP transfer session fake with a configurable connect delay (including an infinite block until cancelled)
    /// and an optional download script, counting connect and download calls and recording disposal.
    /// </summary>
    private sealed class ScriptedSession : ISftpTransferSession
    {
        /// <summary>Optional script run while the download stream is open; may write to or cancel it.</summary>
        private readonly Func<Stream, CancellationToken, Task>? _onDownload;

        /// <summary>Optional hook invoked after the download script completes, before cancellation is rechecked.</summary>
        private readonly Action? _afterDownload;

        /// <summary>Delay applied in <see cref="ConnectAsync"/>; <see cref="Timeout.InfiniteTimeSpan"/> blocks until cancelled.</summary>
        private readonly TimeSpan _connectDelay;

        /// <summary>Non-zero once <see cref="DisposeAsync"/> has been called (set atomically).</summary>
        private int _disposed;

        /// <summary>Creates a scripted session with optional download behaviour and connect delay.</summary>
        /// <param name="onDownload">Script that receives the destination stream and cancellation token.</param>
        /// <param name="afterDownload">Hook run after <paramref name="onDownload"/> completes.</param>
        /// <param name="connectDelay">Connect delay; <see cref="Timeout.InfiniteTimeSpan"/> blocks until cancelled.</param>
        public ScriptedSession(
            Func<Stream, CancellationToken, Task>? onDownload = null,
            Action? afterDownload = null,
            TimeSpan connectDelay = default)
        {
            _onDownload = onDownload;
            _afterDownload = afterDownload;
            _connectDelay = connectDelay;
        }

        /// <summary>Number of times <see cref="ConnectAsync"/> was invoked.</summary>
        public int ConnectCalls { get; private set; }

        /// <summary>Number of times <see cref="DownloadToStreamAsync"/> was invoked.</summary>
        public int DownloadCalls { get; private set; }

        /// <summary>Non-zero once the session has been disposed.</summary>
        public int DisposeCount => _disposed;

        /// <summary>Simulates connecting, optionally blocking for the configured delay.</summary>
        /// <param name="cancellationToken">Token that aborts the simulated connect.</param>
        /// <returns>A task that completes when the simulated connect finishes.</returns>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCalls++;
            if (_connectDelay == Timeout.InfiniteTimeSpan)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else if (_connectDelay > TimeSpan.Zero)
            {
                await Task.Delay(_connectDelay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>Counts the call and runs the configured download script against <paramref name="destination"/>.</summary>
        /// <param name="remotePath">Remote file path (ignored by the fake).</param>
        /// <param name="destination">Stream that receives the simulated file content.</param>
        /// <param name="cancellationToken">Token observed by the script and rechecked afterwards.</param>
        /// <returns>A task that completes when the simulated download finishes.</returns>
        public async Task DownloadToStreamAsync(
            string remotePath,
            Stream destination,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            if (_onDownload is not null)
            {
                await _onDownload(destination, cancellationToken);
            }

            _afterDownload?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
        }

        /// <summary>Marks the session disposed so tests can assert it was released exactly once.</summary>
        /// <returns>A completed task.</returns>
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
