using System.Net.Sockets;
using MikrotikWorker.MikroTik;
using tik4net;

namespace MikrotikWorker.Tests.MikroTik;

public sealed class MikrotikConnectFailureClassifierTests
{
    [Fact]
    public void LoginRefused_IsRetryableAuthentication()
    {
        TikConnectionLoginRefusedException ex = new("invalid user name or password", "api");
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableAuthentication,
            MikrotikConnectFailureClassifier.Classify(ex));
    }

    [Fact]
    public void LoginException_IsRetryableAuthentication()
    {
        TikConnectionLoginException ex = new(new Exception("login failed"));
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableAuthentication,
            MikrotikConnectFailureClassifier.Classify(ex));
    }

    [Fact]
    public void LoginNoAnswer_IsRetryableConnection_NotAuthentication()
    {
        TikConnectionLoginNoAnswerException ex = new("api", "login", new TimeoutException("timed out"));
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableConnection,
            MikrotikConnectFailureClassifier.Classify(ex));
    }

    [Fact]
    public void ReceiveTimeout_IsRetryableConnection()
    {
        TikConnectionReceiveTimeoutException ex = new(10_000, new TimeoutException("recv"));
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableConnection,
            MikrotikConnectFailureClassifier.Classify(ex));
    }

    [Theory]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    public void CommonTransportFailures_AreRetryableConnection(Type exceptionType)
    {
        Exception ex = exceptionType == typeof(SocketException)
            ? new SocketException(10060)
            : (Exception)Activator.CreateInstance(exceptionType, "transient")!;

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableConnection,
            MikrotikConnectFailureClassifier.Classify(ex));
    }

    [Fact]
    public void TimeoutWrappedInOuterException_IsRetryableConnection()
    {
        Exception wrapped = new Exception("wrapper", new TimeoutException("connect timed out"));
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableConnection,
            MikrotikConnectFailureClassifier.Classify(wrapped));

        AggregateException aggregate = new(new TimeoutException("connect timed out"));
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableConnection,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void ProtocolMismatch_IsPermanent()
    {
        TikConnectionProtocolMismatchException ex = new(8728, false, 0, 0, "mismatch", new IOException("x"));
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Permanent,
            MikrotikConnectFailureClassifier.Classify(ex));
    }

    [Fact]
    public void ArgumentAndInvalidOperation_ArePermanent()
    {
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Permanent,
            MikrotikConnectFailureClassifier.Classify(new ArgumentException("host required")));
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Permanent,
            MikrotikConnectFailureClassifier.Classify(new InvalidOperationException("bug")));
    }

    [Fact]
    public void Cancellation_IsNotCountedAsRetry()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Cancellation,
            MikrotikConnectFailureClassifier.Classify(new OperationCanceledException(cts.Token)));
    }

    [Fact]
    public void Aggregate_TimeoutPlusCancellation_IsCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        AggregateException aggregate = new(
            new TimeoutException("connect timed out"),
            new OperationCanceledException(cts.Token));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Cancellation,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void Aggregate_IoPlusNestedCancellation_IsCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        AggregateException aggregate = new(
            new IOException("reset"),
            new AggregateException(new OperationCanceledException(cts.Token)));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Cancellation,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void Aggregate_NestedAggregateWithCancellation_IsCancellation()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        AggregateException nested = new(
            new SocketException(10060),
            new AggregateException(new TimeoutException("t"), new OperationCanceledException(cts.Token)));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Cancellation,
            MikrotikConnectFailureClassifier.Classify(nested));
    }

    [Fact]
    public void Aggregate_TimeoutPlusInvalidOperation_IsPermanent()
    {
        AggregateException aggregate = new(
            new TimeoutException("connect timed out"),
            new InvalidOperationException("programming defect"));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Permanent,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void Aggregate_SocketPlusProtocolFailure_IsPermanent()
    {
        AggregateException aggregate = new(
            new SocketException(10060),
            new TikConnectionProtocolMismatchException(8728, false, 0, 0, "mismatch", new IOException("x")));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Permanent,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void Aggregate_TimeoutPlusLoginRefusal_IsRetryableAuthentication()
    {
        AggregateException aggregate = new(
            new TimeoutException("connect timed out"),
            new TikConnectionLoginRefusedException("invalid user name or password", "api"));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableAuthentication,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void Aggregate_PermanentPlusLoginRefusal_IsRetryableAuthentication()
    {
        AggregateException aggregate = new(
            new InvalidOperationException("unexpected state"),
            new TikConnectionLoginRefusedException("denied", "api"));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableAuthentication,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void Aggregate_TimeoutPlusSocket_IsRetryableConnection()
    {
        AggregateException aggregate = new(
            new TimeoutException("connect timed out"),
            new SocketException(10060));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.RetryableConnection,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }

    [Fact]
    public void Aggregate_UnknownPlusUnknown_IsPermanent()
    {
        AggregateException aggregate = new(
            new Exception("unknown-a"),
            new Exception("unknown-b"));

        Assert.Equal(
            MikrotikConnectFailureClassifier.Kind.Permanent,
            MikrotikConnectFailureClassifier.Classify(aggregate));
    }
}
