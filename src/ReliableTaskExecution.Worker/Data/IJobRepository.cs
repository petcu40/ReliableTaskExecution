namespace ReliableTaskExecution.Worker.Data;

/// <summary>
/// Repository interface for job-related database operations.
/// Provides atomic lock operations for leader election in distributed task execution.
/// </summary>
public interface IJobRepository
{
    /// <summary>
    /// Gets an available job that is due to run and not currently locked.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An available job, or null if no jobs are available.</returns>
    Task<Job?> GetAvailableJobAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to acquire a lock on a job using atomic UPDATE WHERE pattern.
    /// Only one worker succeeds when multiple attempt simultaneous claims.
    /// </summary>
    /// <param name="jobId">The ID of the job to lock.</param>
    /// <param name="workerId">The unique worker identifier.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the lock was successfully acquired; false if another worker claimed it first.</returns>
    Task<bool> TryAcquireLockAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a lock on a job, updating completion timestamps.
    /// Called when a task completes successfully.
    /// NextRunTime is calculated from LockedAt to prevent timing drift.
    /// </summary>
    /// <param name="jobId">The ID of the job to release.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the lock was released; false if the job was not found.</returns>
    Task<bool> ReleaseLockAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a lock on a job after a failure, preserving NextRunTime for retry.
    /// Called when a task fails or is abandoned.
    /// </summary>
    /// <param name="jobId">The ID of the job to release.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the lock was released; false if the job was not found.</returns>
    Task<bool> ReleaseLockOnFailureAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a task successfully, updating LastRunTime and scheduling next run.
    /// NextRunTime = LockedAt + IntervalMinutes (prevents timing drift).
    /// This is an alias for ReleaseLockAsync with semantic naming for task completion.
    /// </summary>
    /// <param name="jobId">The ID of the job to complete.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the task was completed; false if the job was not found.</returns>
    Task<bool> CompleteTaskAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fails a task, releasing the lock immediately and preserving NextRunTime for retry.
    /// Another worker can pick up the task for immediate retry.
    /// This is an alias for ReleaseLockOnFailureAsync with semantic naming for task failure.
    /// </summary>
    /// <param name="jobId">The ID of the job that failed.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the failure was recorded; false if the job was not found.</returns>
    Task<bool> FailTaskAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclaims stale locks where the lock has expired.
    /// Stale locks occur when a worker crashes without releasing its lock.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of stale locks that were reclaimed.</returns>
    Task<int> ReclaimStaleLocksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the heartbeat timestamp for a locked job.
    /// Extends the lock window while a task is executing.
    /// </summary>
    /// <param name="jobId">The ID of the job.</param>
    /// <param name="workerId">The worker ID that holds the lock.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the heartbeat was updated; false if the lock was lost.</returns>
    Task<bool> UpdateHeartbeatAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a job by its ID, including lock information.
    /// Used for testing and verification purposes.
    /// </summary>
    /// <param name="jobId">The ID of the job to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The job if found; null otherwise.</returns>
    Task<Job?> GetJobByIdAsync(Guid jobId, CancellationToken cancellationToken = default);
}
