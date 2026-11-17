// -----------------------------------------------------------------------------
// File: FirmwareTransportFailureClassifierTests.cs
//
// Description:
// Classification of package-fetch failures into transient transport retries vs
// permanent upgrade failures.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using MikrotikWorker.Upgrade;
using tik4net;
using tik4net.Testing;

namespace MikrotikWorker.Tests.Upgrade;

/// <summary>
/// Covers <see cref="FirmwareTransportFailureClassifier"/> for fetch/transport failures.
/// </summary>
public sealed class FirmwareTransportFailureClassifierTests
{
    /// <summary>Receive timeout is transient.</summary>
    [Fact]
    public void ReceiveTimeout_IsTransient()
    {
        Exception ex = new TikConnectionReceiveTimeoutException(30_000, new TimeoutException("recv"));
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.TransientTransport,
            FirmwareTransportFailureClassifier.ClassifyException(ex));
    }

    /// <summary>Command abort (lost connection during bounded fetch read) is transient.</summary>
    [Fact]
    public void CommandAbort_IsTransient()
    {
        ITikCommand command = FakeMikrotikConnections.CreateFullyPopulated().CreateCommand("/tool/fetch");
        Exception ex = new TikCommandAbortException(command, "connection lost while reading tag");
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.TransientTransport,
            FirmwareTransportFailureClassifier.ClassifyException(ex));
    }

    /// <summary>Socket reset is transient.</summary>
    [Fact]
    public void SocketException_IsTransient()
    {
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.TransientTransport,
            FirmwareTransportFailureClassifier.ClassifyException(new SocketException(10054)));
    }

    /// <summary>HTTP 404 fetch status is permanent.</summary>
    [Fact]
    public void Fetch404_IsPermanent()
    {
        Exception ex = new InvalidOperationException(
            "Fetch of 'advanced-tools-6.49.23-mipsbe.npk' failed: 404 Not Found");
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.Permanent,
            FirmwareTransportFailureClassifier.ClassifyException(ex));
    }

    /// <summary>Missing local catalogue style invalid operation without transport markers is permanent.</summary>
    [Fact]
    public void GenericInvalidOperation_IsPermanent()
    {
        Exception ex = new InvalidOperationException("Catalogue selection failed");
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.Permanent,
            FirmwareTransportFailureClassifier.ClassifyException(ex));
    }

    /// <summary>Incomplete staged file after fetch is transient.</summary>
    [Fact]
    public void IncompleteFetch_IsTransient()
    {
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.TransientTransport,
            FirmwareTransportFailureClassifier.ClassifyIncompleteFetch());
    }

    /// <summary>Cancellation is not a transport retry.</summary>
    [Fact]
    public void Cancellation_IsNotTransientTransport()
    {
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.Cancellation,
            FirmwareTransportFailureClassifier.ClassifyException(new OperationCanceledException()));
    }

    /// <summary>HTTP 503 is transient.</summary>
    [Fact]
    public void Http503_IsTransient()
    {
        Exception ex = new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.TransientTransport,
            FirmwareTransportFailureClassifier.ClassifyException(ex));
    }

    /// <summary>HTTP 404 request exception is permanent.</summary>
    [Fact]
    public void Http404_IsPermanent()
    {
        Exception ex = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.Permanent,
            FirmwareTransportFailureClassifier.ClassifyException(ex));
    }

    /// <summary>Request timeout without status is transient.</summary>
    [Fact]
    public void HttpTimeout_IsTransient()
    {
        Exception ex = new HttpRequestException("The request was canceled due to the configured HttpClient.Timeout");
        Assert.Equal(
            FirmwareTransportFailureClassifier.Kind.TransientTransport,
            FirmwareTransportFailureClassifier.ClassifyException(ex));
    }
}
