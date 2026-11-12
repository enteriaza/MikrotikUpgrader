namespace MikrotikWorker.Scheduling;

/// <summary>
/// Failure category for transient connect/login retry counters.
/// Counters are independent — a connection failure never consumes an authentication attempt.
/// </summary>
public enum JobRetryFailureCategory
{
    Connection = 1,
    Authentication = 2,
}
