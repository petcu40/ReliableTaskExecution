using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace ReliableTaskExecution.Worker.Data;

/// <summary>
/// Repository for job-related database operations using raw ADO.NET.
/// Implements atomic lock operations for leader election using UPDATE WHERE pattern.
/// No ORM is used to ensure transparent, predictable SQL execution.
/// </summary>
public sealed class JobRepository : IJobRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ILogger<JobRepository> _logger;

    /// <summary>
    /// Initializes a new instance of the JobRepository.
    /// </summary>
    /// <param name="connectionFactory">Factory for creating SQL connections.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public JobRepository(
        ISqlConnectionFactory connectionFactory,
        ILogger<JobRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets an available job that is due to run and not currently locked.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An available job, or null if no jobs are available.</returns>
    public async Task<Job?> GetAvailableJobAsync(CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT TOP 1
                Id,
                JobName,
                NextRunTime,
                LastRunTime,
                LockedBy,
                LockedAt,
                LockTimeoutMinutes,
                IntervalMinutes
            FROM Jobs
            WHERE NextRunTime <= GETUTCDATE()
              AND LockedBy IS NULL
            ORDER BY NextRunTime";

        _logger.LogDebug("Querying for available jobs...");

        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            _logger.LogDebug("No available jobs found");
            return null;
        }

        var job = MapToJob(reader);
        _logger.LogDebug("Found available job: {JobId} - {JobName}", job.Id, job.JobName);

        return job;
    }

    /// <summary>
    /// Gets a job by its ID, including lock information.
    /// Used for testing and verification purposes.
    /// </summary>
    /// <param name="jobId">The ID of the job to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The job if found; null otherwise.</returns>
    public async Task<Job?> GetJobByIdAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        const string sql = @"
            SELECT
                Id,
                JobName,
                NextRunTime,
                LastRunTime,
                LockedBy,
                LockedAt,
                LockTimeoutMinutes,
                IntervalMinutes
            FROM Jobs
            WHERE Id = @jobId";

        _logger.LogDebug("Getting job by ID: {JobId}", jobId);

        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@jobId", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            _logger.LogDebug("Job not found: {JobId}", jobId);
            return null;
        }

        var job = MapToJob(reader);
        _logger.LogDebug("Found job: {JobId} - {JobName}", job.Id, job.JobName);

        return job;
    }

    /// <summary>
    /// Attempts to acquire a lock on a job using atomic UPDATE WHERE pattern.
    /// Only one worker succeeds when multiple attempt simultaneous claims.
    /// </summary>
    /// <param name="jobId">The ID of the job to lock.</param>
    /// <param name="workerId">The unique worker identifier.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the lock was successfully acquired; false if another worker claimed it first.</returns>
    public async Task<bool> TryAcquireLockAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default)
    {
        // Atomic UPDATE WHERE pattern ensures only one worker can acquire the lock
        const string sql = @"
            UPDATE Jobs
            SET LockedBy = @workerId,
                LockedAt = GETUTCDATE()
            WHERE Id = @jobId
              AND LockedBy IS NULL";

        _logger.LogDebug("Attempting to acquire lock on job {JobId} for worker {WorkerId}", jobId, workerId);

        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@jobId", jobId);
        command.Parameters.AddWithValue("@workerId", workerId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected == 1)
        {
            _logger.LogInformation("Lock acquired on job {JobId} by worker {WorkerId}", jobId, workerId);
            return true;
        }

        _logger.LogDebug("Failed to acquire lock on job {JobId} - already locked by another worker", jobId);
        return false;
    }

    /// <summary>
    /// Releases a lock on a job, updating completion timestamps.
    /// Called when a task completes successfully.
    /// NextRunTime is calculated from LockedAt to prevent timing drift.
    /// </summary>
    /// <param name="jobId">The ID of the job to release.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the lock was released; false if the job was not found.</returns>
    public async Task<bool> ReleaseLockAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        // NextRunTime = LockedAt + IntervalMinutes to prevent timing drift
        const string sql = @"
            UPDATE Jobs
            SET LockedBy = NULL,
                LastRunTime = GETUTCDATE(),
                NextRunTime = DATEADD(MINUTE, IntervalMinutes, LockedAt)
            WHERE Id = @jobId";

        _logger.LogDebug("Releasing lock on job {JobId} after successful completion", jobId);

        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@jobId", jobId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected == 1)
        {
            _logger.LogInformation("Lock released on job {JobId} - task completed successfully", jobId);
            return true;
        }

        _logger.LogWarning("Failed to release lock on job {JobId} - job not found", jobId);
        return false;
    }

    /// <summary>
    /// Releases a lock on a job after a failure, preserving NextRunTime for retry.
    /// Called when a task fails or is abandoned.
    /// </summary>
    /// <param name="jobId">The ID of the job to release.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the lock was released; false if the job was not found.</returns>
    public async Task<bool> ReleaseLockOnFailureAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        // Leave NextRunTime unchanged so another worker can retry quickly
        const string sql = @"
            UPDATE Jobs
            SET LockedBy = NULL,
                LockedAt = NULL
            WHERE Id = @jobId";

        _logger.LogDebug("Releasing lock on job {JobId} after failure", jobId);

        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@jobId", jobId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected == 1)
        {
            _logger.LogWarning("Lock released on job {JobId} after failure - available for retry", jobId);
            return true;
        }

        _logger.LogWarning("Failed to release lock on job {JobId} after failure - job not found", jobId);
        return false;
    }

    /// <summary>
    /// Completes a task successfully, updating LastRunTime and scheduling next run.
    /// NextRunTime = LockedAt + IntervalMinutes (prevents timing drift).
    /// This is an alias for ReleaseLockAsync with semantic naming for task completion.
    /// </summary>
    /// <param name="jobId">The ID of the job to complete.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the task was completed; false if the job was not found.</returns>
    public Task<bool> CompleteTaskAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Completing task for job {JobId}", jobId);
        return ReleaseLockAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Fails a task, releasing the lock immediately and preserving NextRunTime for retry.
    /// Another worker can pick up the task for immediate retry.
    /// This is an alias for ReleaseLockOnFailureAsync with semantic naming for task failure.
    /// </summary>
    /// <param name="jobId">The ID of the job that failed.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the failure was recorded; false if the job was not found.</returns>
    public Task<bool> FailTaskAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Failing task for job {JobId}", jobId);
        return ReleaseLockOnFailureAsync(jobId, cancellationToken);
    }

    /// <summary>
    /// Reclaims stale locks where the lock has expired.
    /// Stale locks occur when a worker crashes without releasing its lock.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The number of stale locks that were reclaimed.</returns>
    public async Task<int> ReclaimStaleLocksAsync(CancellationToken cancellationToken = default)
    {
        // Reclaim locks where LockedAt + LockTimeoutMinutes < NOW
        const string sql = @"
            UPDATE Jobs
            SET LockedBy = NULL,
                LockedAt = NULL
            WHERE LockedBy IS NOT NULL
              AND DATEADD(MINUTE, LockTimeoutMinutes, LockedAt) < GETUTCDATE()";

        _logger.LogDebug("Checking for stale locks to reclaim...");

        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected > 0)
        {
            _logger.LogWarning("Reclaimed {Count} stale lock(s) from crashed workers", rowsAffected);
        }
        else
        {
            _logger.LogDebug("No stale locks found");
        }

        return rowsAffected;
    }

    /// <summary>
    /// Updates the heartbeat timestamp for a locked job.
    /// Extends the lock window while a task is executing.
    /// </summary>
    /// <param name="jobId">The ID of the job.</param>
    /// <param name="workerId">The worker ID that holds the lock.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True if the heartbeat was updated; false if the lock was lost.</returns>
    public async Task<bool> UpdateHeartbeatAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default)
    {
        // Only update if the worker still holds the lock
        const string sql = @"
            UPDATE Jobs
            SET LockedAt = GETUTCDATE()
            WHERE Id = @jobId
              AND LockedBy = @workerId";

        _logger.LogDebug("Sending heartbeat for job {JobId} from worker {WorkerId}", jobId, workerId);

        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);

        command.Parameters.AddWithValue("@jobId", jobId);
        command.Parameters.AddWithValue("@workerId", workerId);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

        if (rowsAffected == 1)
        {
            _logger.LogDebug("Heartbeat updated for job {JobId}", jobId);
            return true;
        }

        _logger.LogWarning("Heartbeat failed for job {JobId} - lock may have been lost", jobId);
        return false;
    }

    /// <summary>
    /// Maps a SqlDataReader row to a Job object.
    /// </summary>
    private static Job MapToJob(SqlDataReader reader)
    {
        return new Job
        {
            Id = reader.GetGuid(reader.GetOrdinal("Id")),
            JobName = reader.GetString(reader.GetOrdinal("JobName")),
            NextRunTime = reader.GetDateTime(reader.GetOrdinal("NextRunTime")),
            LastRunTime = reader.IsDBNull(reader.GetOrdinal("LastRunTime"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("LastRunTime")),
            LockedBy = reader.IsDBNull(reader.GetOrdinal("LockedBy"))
                ? null
                : reader.GetString(reader.GetOrdinal("LockedBy")),
            LockedAt = reader.IsDBNull(reader.GetOrdinal("LockedAt"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("LockedAt")),
            LockTimeoutMinutes = reader.GetInt32(reader.GetOrdinal("LockTimeoutMinutes")),
            IntervalMinutes = reader.GetInt32(reader.GetOrdinal("IntervalMinutes"))
        };
    }
}
