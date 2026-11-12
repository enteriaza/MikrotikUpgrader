// -----------------------------------------------------------------------------
// File: MikrotikConnectFailureClassifier.cs
//
// Description:
// Maps tik4net/connect exceptions into Cancellation, RetryableConnection,
// RetryableAuthentication, or Permanent for JobRetry counters. Documents the
// LoginNoAnswer-as-connection ambiguity and AggregateException precedence.
//
// -----------------------------------------------------------------------------
using System.Net.Sockets;
using tik4net;

namespace MikrotikWorker.MikroTik;

/// <summary>
/// Classifies exceptions thrown by <see cref="IMikrotikConnectionFactory.CreateAsync"/> into
/// retryable connection failures, retryable authentication failures, or non-retryable outcomes.
/// </summary>
/// <remarks>
/// Ambiguity: <see cref="TikConnectionLoginNoAnswerException"/> is treated as a connection
/// timeout during the login handshake (no explicit RADIUS/credential refusal). Explicit
/// refusals use <see cref="TikConnectionLoginRefusedException"/> / <see cref="TikConnectionLoginException"/>.
/// <para>
/// AggregateException precedence (after flatten): Cancellation, then Authentication, then Permanent,
/// then Connection. A transient timeout in one branch never overrides cancellation or permanent
/// failure in another branch.
/// </para>
/// </remarks>
internal static class MikrotikConnectFailureClassifier
{
    /// <summary>
    /// Classification outcome for a connect/login exception.
    /// </summary>
    public enum Kind
    {
        /// <summary>Caller cancellation or shutdown — do not count as a retry attempt.</summary>
        Cancellation,

        /// <summary>Transient transport / reachability failure — connection retry counter.</summary>
        RetryableConnection,

        /// <summary>Explicit authentication rejection — authentication retry counter.</summary>
        RetryableAuthentication,

        /// <summary>Configuration, protocol, or programming failure — fail permanently.</summary>
        Permanent,
    }

    /// <summary>
    /// Classifies a connect or login exception. Cancellation takes precedence, then authentication, then permanent failures, then retryable connection failures.
    /// </summary>
    /// <param name="exception">Exception thrown while connecting; an <see cref="AggregateException"/> is flattened and its branches are merged.</param>
    /// <returns>The classification outcome.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exception"/> is null.</exception>
    public static Kind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is AggregateException aggregate)
        {
            return ClassifyAggregate(aggregate);
        }

        // Cancellation wins over any wrapped transport failure on a single chain.
        if (ContainsCancellation(exception))
        {
            return Kind.Cancellation;
        }

        return ClassifyCore(exception);
    }

    /// <summary>
    /// Classifies an aggregate after flattening: cancellation in any branch wins, then authentication, permanent, and connection failures in that order.
    /// </summary>
    /// <param name="aggregate">Aggregate exception to classify.</param>
    /// <returns>The merged classification.</returns>
    private static Kind ClassifyAggregate(AggregateException aggregate)
    {
        IList<Exception> inners = aggregate.Flatten().InnerExceptions;

        // 1. Cancellation anywhere in any flattened branch (including nested InnerException chains).
        foreach (Exception inner in inners)
        {
            if (ContainsCancellation(inner))
            {
                return Kind.Cancellation;
            }
        }

        // 2. Classify each branch with the same single-chain rules; merge with Auth > Permanent > Connection.
        bool sawAuthentication = false;
        bool sawPermanent = false;
        bool sawConnection = false;

        foreach (Exception inner in inners)
        {
            Kind kind = ClassifyCore(inner);
            switch (kind)
            {
                case Kind.RetryableAuthentication:
                    sawAuthentication = true;
                    break;
                case Kind.Permanent:
                    sawPermanent = true;
                    break;
                case Kind.RetryableConnection:
                    sawConnection = true;
                    break;
                case Kind.Cancellation:
                    // ClassifyCore does not emit Cancellation; keep defensive.
                    return Kind.Cancellation;
            }
        }

        if (sawAuthentication)
        {
            return Kind.RetryableAuthentication;
        }

        if (sawPermanent)
        {
            return Kind.Permanent;
        }

        if (sawConnection)
        {
            return Kind.RetryableConnection;
        }

        // All unknown / unclassified branches → permanent (do not invent retries).
        return Kind.Permanent;
    }

    /// <summary>
    /// Determines whether the exception or any inner exception is a cancellation.
    /// </summary>
    /// <param name="exception">Exception whose chain is walked.</param>
    /// <returns><see langword="true"/> when an <see cref="OperationCanceledException"/> is found.</returns>
    private static bool ContainsCancellation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies a single exception chain: authentication wins, then hard permanent types, then retryable connection failures.
    /// </summary>
    /// <param name="exception">Exception whose chain is walked.</param>
    /// <returns>The classification for the chain.</returns>
    private static Kind ClassifyCore(Exception exception)
    {
        // Walk the chain so timeouts wrapped by library glue are still recognised.
        // Hard permanent types (protocol/config/programming) win over deeper IOException inners.
        bool sawHardPermanent = false;
        bool sawConnection = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (IsAuthentication(current))
            {
                return Kind.RetryableAuthentication;
            }

            if (IsHardPermanent(current))
            {
                sawHardPermanent = true;
                continue;
            }

            if (IsRetryableConnection(current))
            {
                sawConnection = true;
            }
        }

        if (sawHardPermanent)
        {
            return Kind.Permanent;
        }

        return sawConnection ? Kind.RetryableConnection : Kind.Permanent;
    }

    /// <summary>
    /// Determines whether the exception is an explicit login refusal, excluding the no-answer case treated as a connection timeout.
    /// </summary>
    /// <param name="exception">Exception to test.</param>
    /// <returns><see langword="true"/> for an authentication failure.</returns>
    private static bool IsAuthentication(Exception exception) =>
        exception is TikConnectionLoginRefusedException
        || (exception is TikConnectionLoginException and not TikConnectionLoginNoAnswerException);

    /// <summary>
    /// Determines whether the exception is a protocol, configuration, or programming error that retrying cannot fix.
    /// </summary>
    /// <param name="exception">Exception to test.</param>
    /// <returns><see langword="true"/> for a permanent failure type.</returns>
    private static bool IsHardPermanent(Exception exception) =>
        exception is TikConnectionProtocolMismatchException
            or TikConnectionSSLErrorException
            or TikConnectionCapabilityNotSupportedException
            or TikConnectionNotOpenException
            or ArgumentException
            or ArgumentOutOfRangeException
            or InvalidOperationException
            or NotSupportedException
            or NotImplementedException
            or NullReferenceException
            or ObjectDisposedException;

    /// <summary>
    /// Determines whether the exception is a transient transport or session failure worth retrying.
    /// </summary>
    /// <param name="exception">Exception to test.</param>
    /// <returns><see langword="true"/> for a retryable connection failure type.</returns>
    private static bool IsRetryableConnection(Exception exception) =>
        exception is TikConnectionLoginNoAnswerException
            or TikConnectionReceiveTimeoutException
            or TimeoutException
            or SocketException
            or TikConnectionSessionClosedException
            or TikConnectionResponseIncompleteException
            or IOException
            or TikConnectionException;
}
