using Microsoft.Extensions.Logging.Abstractions;
using MikrotikWorker.MikroTik;
using Renci.SshNet;

namespace MikrotikWorker.Tests.MikroTik;

public sealed class MikrotikScpFileTransferClientTests
{
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

    private static MikrotikScpFileTransferClient CreateClient(Func<ScriptedSession> sessionFactory) =>
        new(NullLogger<MikrotikScpFileTransferClient>.Instance, _ => sessionFactory());

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

    private sealed class ScriptedSession : ISftpTransferSession
    {
        private readonly Func<Stream, CancellationToken, Task>? _onDownload;
        private readonly Action? _afterDownload;
        private readonly TimeSpan _connectDelay;
        private int _disposed;

        public ScriptedSession(
            Func<Stream, CancellationToken, Task>? onDownload = null,
            Action? afterDownload = null,
            TimeSpan connectDelay = default)
        {
            _onDownload = onDownload;
            _afterDownload = afterDownload;
            _connectDelay = connectDelay;
        }

        public int ConnectCalls { get; private set; }

        public int DownloadCalls { get; private set; }

        public int DisposeCount => _disposed;

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

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }
}
