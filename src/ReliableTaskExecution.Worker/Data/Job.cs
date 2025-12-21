namespace ReliableTaskExecution.Worker.Data;

/// <summary>
/// Represents a scheduled job in the distributed task execution system.
/// Maps to the Jobs table in SQL Azure.
/// </summary>
public class Job
{
    /// <summary>
    /// Unique identifier for the job.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name for the job.
    /// </summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// The next scheduled time for this job to run.
    /// Based on task START time, not completion time, to prevent timing drift.
    /// </summary>
    public DateTime NextRunTime { get; set; }

    /// <summary>
    /// The last time this job was successfully executed.
    /// Null if the job has never run.
    /// </summary>
    public DateTime? LastRunTime { get; set; }

    /// <summary>
    /// Worker ID that currently holds the lock on this job.
    /// Null if the job is not locked.
    /// </summary>
    public string? LockedBy { get; set; }

    /// <summary>
    /// Timestamp when the lock was acquired.
    /// Used for stale lock detection.
    /// </summary>
    public DateTime? LockedAt { get; set; }

    /// <summary>
    /// Lock timeout duration in minutes.
    /// If LockedAt + LockTimeoutMinutes < NOW, the lock is considered stale.
    /// Default: 2 minutes.
    /// </summary>
    public int LockTimeoutMinutes { get; set; } = 2;

    /// <summary>
    /// Interval in minutes between task executions.
    /// NextRunTime = LockedAt + IntervalMinutes after completion.
    /// Default: 10 minutes.
    /// </summary>
    public int IntervalMinutes { get; set; } = 10;

    /// <summary>
    /// Determines if the job lock has expired based on current UTC time.
    /// </summary>
    /// <returns>True if the lock is stale and can be reclaimed.</returns>
    public bool IsLockStale()
    {
        if (LockedBy == null || LockedAt == null)
        {
            return false;
        }

        var lockExpiry = LockedAt.Value.AddMinutes(LockTimeoutMinutes);
        return lockExpiry < DateTime.UtcNow;
    }
}
