namespace ReliableTaskExecution.Worker.Configuration;

/// <summary>
/// Configuration options for task execution behavior.
/// Bound from the "TaskExecution" section in appsettings.json.
/// </summary>
public class TaskExecutionOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "TaskExecution";

    /// <summary>
    /// Interval in seconds between polling cycles for available jobs.
    /// Default: 60 seconds.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Duration in seconds for the sample task execution.
    /// Used to simulate work being performed.
    /// Default: 5 seconds.
    /// </summary>
    public int TaskDurationSeconds { get; set; } = 5;

    /// <summary>
    /// Interval in seconds between heartbeat updates while a task is executing.
    /// Should be less than LockTimeoutMinutes to prevent lock expiration.
    /// Default: 30 seconds.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Lock timeout in minutes. If a worker fails to send a heartbeat
    /// within this duration, the lock is considered stale.
    /// Default: 2 minutes.
    /// </summary>
    public int LockTimeoutMinutes { get; set; } = 2;

    /// <summary>
    /// Maximum number of consecutive heartbeat failures before abandoning a job.
    /// This prevents multi-master situations where multiple workers execute the same job.
    /// Default: 3 failures.
    /// </summary>
    public int MaxConsecutiveHeartbeatFailures { get; set; } = 3;

    /// <summary>
    /// Gets the polling interval as a TimeSpan.
    /// </summary>
    public TimeSpan PollingInterval => TimeSpan.FromSeconds(PollingIntervalSeconds);

    /// <summary>
    /// Gets the task duration as a TimeSpan.
    /// </summary>
    public TimeSpan TaskDuration => TimeSpan.FromSeconds(TaskDurationSeconds);

    /// <summary>
    /// Gets the heartbeat interval as a TimeSpan.
    /// </summary>
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(HeartbeatIntervalSeconds);

    /// <summary>
    /// Gets the lock timeout as a TimeSpan.
    /// </summary>
    public TimeSpan LockTimeout => TimeSpan.FromMinutes(LockTimeoutMinutes);
}
