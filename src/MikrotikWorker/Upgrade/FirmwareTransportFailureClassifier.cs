// -----------------------------------------------------------------------------
// File: FirmwareTransportFailureClassifier.cs
//
// Description:
// Distinguishes transient firmware package-fetch / transport failures (scheduler
// Connection retry) from permanent package or configuration failures. Reuses
// MikrotikConnectFailureClassifier for tik4net exception types.
//
// -----------------------------------------------------------------------------

using System.Net.Sockets;
using MikrotikWorker.MikroTik;
using tik4net;

namespace MikrotikWorker.Upgrade;

/// <summary>
/// Classifies package-fetch and related transport failures for scheduler-level retry.
/// </summary>
internal static class FirmwareTransportFailureClassifier
{
    /// <summary>Classification of a package-stage failure.</summary>
    public enum Kind
    {
        /// <summary>Caller/host cancellation — do not schedule a transport retry.</summary>
        Cancellation,

        /// <summary>Transient transport / fetch interruption — Connection retry budget.</summary>
        TransientTransport,

        /// <summary>Deterministic package, catalogue, or configuration failure — terminal.</summary>
        Permanent,
    }

    /// <summary>
    /// Classifies an exception thrown during router-side <c>/tool/fetch</c> or related transport.
    /// </summary>
    public static Kind ClassifyException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        MikrotikConnectFailureClassifier.Kind connectKind = MikrotikConnectFailureClassifier.Classify(exception);
        return connectKind switch
        {
            MikrotikConnectFailureClassifier.Kind.Cancellation => Kind.Cancellation,
            MikrotikConnectFailureClassifier.Kind.RetryableConnection => Kind.TransientTransport,
            MikrotikConnectFailureClassifier.Kind.RetryableAuthentication => Kind.TransientTransport,
            _ => ClassifyFetchSpecific(exception),
        };
    }

    /// <summary>
    /// Whether a post-fetch size/presence failure (no thrown transport exception) should be retried.
    /// Incomplete or missing staged files after a fetch attempt are treated as transient; confirmed
    /// catalogue/selection errors are not raised on this path.
    /// </summary>
    public static Kind ClassifyIncompleteFetch() => Kind.TransientTransport;

    /// <summary>
    /// Walks fetch-specific permanent markers (HTTP 404 / not found) that may surface as
    /// <see cref="InvalidOperationException"/> from RouterOS fetch status sentences.
    /// </summary>
    private static Kind ClassifyFetchSpecific(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException httpEx)
            {
                return ClassifyHttpRequestException(httpEx);
            }

            if (current is TikCommandAbortException
                || current is TikConnectionReceiveTimeoutException
                || current is TikConnectionSessionClosedException
                || current is TikConnectionResponseIncompleteException
                || current is TimeoutException
                || current is SocketException
                || current is IOException)
            {
                return Kind.TransientTransport;
            }

            if (current is InvalidOperationException)
            {
                string message = current.Message;
                if (IsPermanentFetchMessage(message))
                {
                    return Kind.Permanent;
                }

                if (IsTransientFetchMessage(message))
                {
                    return Kind.TransientTransport;
                }
            }
        }

        return Kind.Permanent;
    }

    /// <summary>
    /// Treats missing-resource HTTP statuses as permanent; 5xx / timeouts / other transport as transient.
    /// </summary>
    private static Kind ClassifyHttpRequestException(HttpRequestException httpEx)
    {
        if (httpEx.StatusCode is { } status)
        {
            int code = (int)status;
            if (code is 404 or 410 or 400 or 401 or 403 or 405 or 422)
            {
                return Kind.Permanent;
            }

            if (code >= 500 && code <= 599)
            {
                return Kind.TransientTransport;
            }
        }

        if (IsPermanentFetchMessage(httpEx.Message))
        {
            return Kind.Permanent;
        }

        // No status or ambiguous status — treat as transport unless message is clearly permanent.
        return Kind.TransientTransport;
    }

    /// <summary>Permanent missing-resource phrasing from RouterOS fetch failure text.</summary>
    private static bool IsPermanentFetchMessage(string message) =>
        Contains(message, "404")
        || Contains(message, "not found")
        || Contains(message, "no such file")
        || Contains(message, "does not exist");

    /// <summary>Transient transport phrasing from RouterOS / library fetch failure text.</summary>
    private static bool IsTransientFetchMessage(string message) =>
        Contains(message, "connection lost")
        || Contains(message, "connection reset")
        || Contains(message, "connection refused")
        || Contains(message, "timed out")
        || Contains(message, "timeout")
        || Contains(message, "network is unreachable")
        || Contains(message, "temporarily")
        || Contains(message, "503")
        || Contains(message, "502")
        || Contains(message, "504");

    /// <summary>
    /// Case-insensitive substring match used by message-marker heuristics after typed exception checks.
    /// </summary>
    /// <param name="haystack">Exception or status message text.</param>
    /// <param name="needle">Marker phrase (for example <c>timed out</c>).</param>
    /// <returns><see langword="true"/> when <paramref name="haystack"/> contains <paramref name="needle"/>.</returns>
    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
