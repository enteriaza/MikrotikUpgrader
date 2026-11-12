// -----------------------------------------------------------------------------
// File: JobRetryFailureCategory.cs
//
// Description:
// Distinguishes transient connect/login failure kinds used by the scheduler's
// per-job retry counters. Connection and authentication budgets are independent
// so one failure mode never consumes the other's attempts.
//
// -----------------------------------------------------------------------------

namespace MikrotikWorker.Scheduling;

/// <summary>
/// Failure category for transient connect/login retry counters.
/// Counters are independent — a connection failure never consumes an authentication attempt.
/// </summary>
public enum JobRetryFailureCategory
{
    /// <summary>
    /// Transient transport/connectivity failure during <c>CreateAsync</c> (before a usable session).
    /// </summary>
    Connection = 1,

    /// <summary>
    /// Transient authentication/login failure during <c>CreateAsync</c>.
    /// </summary>
    Authentication = 2,
}
