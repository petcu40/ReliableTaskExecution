using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReliableTaskExecution.Worker.Configuration;
using ReliableTaskExecution.Worker.Data;

namespace ReliableTaskExecution.Worker.Services;

/// <summary>
/// BackgroundService that implements the main polling loop for distributed task execution.
/// Coordinates job discovery, lock acquisition, heartbeat management, and task execution.
///
/// Polling loop sequence:
/// 1. Reclaim stale locks (call ReclaimStaleLocksAsync)
/// 2. Check for available job (call GetAvailableJobAsync)
/// 3. Try to acquire lock (call TryAcquireLockAsync)
/// 4. If lock acquired: start heartbeat, execute task, complete/fail
/// 5. Wait for polling interval before next iteration
/// </summary>
public sealed class TaskExecutionWorker : BackgroundService
{
    private readonly IJobRepository _jobRepository;
    private readonly ITaskExecutor _taskExecutor;
    private readonly ILogger<TaskExecutionWorker> _logger;
    private readonly ILogger<HeartbeatService> _heartbeatLogger;
    private readonly TimeSpan _pollingInterval;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _maxConsecutiveHeartbeatFailures;
    private readonly string _workerId;

    /// <summary>
    /// Initializes a new instance of the TaskExecutionWorker.
    /// </summary>
    /// <param name="jobRepository">Repository for job operations.</param>
    /// <param name="taskExecutor">Executor for running tasks.</param>
    /// <param name="options">Task execution configuration options.</param>
    /// <param name="logger">Logger for worker diagnostics.</param>
    /// <param name="heartbeatLogger">Logger for heartbeat service.</param>
    public TaskExecutionWorker(
        IJobRepository jobRepository,
        ITaskExecutor taskExecutor,
        IOptions<TaskExecutionOptions> options,
        ILogger<TaskExecutionWorker> logger,
        ILogger<HeartbeatService> heartbeatLogger)
    {
        ArgumentNullException.ThrowIfNull(jobRepository);
        ArgumentNullException.ThrowIfNull(taskExecutor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(heartbeatLogger);

        _jobRepository = jobRepository;
        _taskExecutor = taskExecutor;
        _pollingInterval = options.Value.PollingInterval;
        _heartbeatInterval = options.Value.HeartbeatInterval;
        _maxConsecutiveHeartbeatFailures = options.Value.MaxConsecutiveHeartbeatFailures;
        _logger = logger;
        _heartbeatLogger = heartbeatLogger;
        _workerId = WorkerIdGenerator.GenerateWorkerId();

        _logger.LogInformation("TaskExecutionWorker initialized with worker ID: {WorkerId}", _workerId);
    }

    /// <summary>
    /// Constructor for testing with explicit configuration values.
    /// </summary>
    /// <param name="jobRepository">Repository for job operations.</param>
    /// <param name="taskExecutor">Executor for running tasks.</param>
    /// <param name="pollingInterval">Interval between polling cycles.</param>
    /// <param name="heartbeatInterval">Interval between heartbeats.</param>
    /// <param name="maxConsecutiveHeartbeatFailures">Max heartbeat failures before abandonment.</param>
    /// <param name="workerId">Explicit worker ID for testing.</param>
    /// <param name="logger">Logger for worker diagnostics.</param>
    /// <param name="heartbeatLogger">Logger for heartbeat service.</param>
    internal TaskExecutionWorker(
        IJobRepository jobRepository,
        ITaskExecutor taskExecutor,
        TimeSpan pollingInterval,
        TimeSpan heartbeatInterval,
        int maxConsecutiveHeartbeatFailures,
        string workerId,
        ILogger<TaskExecutionWorker> logger,
        ILogger<HeartbeatService> heartbeatLogger)
    {
        ArgumentNullException.ThrowIfNull(jobRepository);
        ArgumentNullException.ThrowIfNull(taskExecutor);
        ArgumentException.ThrowIfNullOrEmpty(workerId);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(heartbeatLogger);

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollingInterval), "Polling interval must be positive.");
        }

        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "Heartbeat interval must be positive.");
        }

        if (maxConsecutiveHeartbeatFailures <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveHeartbeatFailures), "Max consecutive failures must be positive.");
        }

        _jobRepository = jobRepository;
        _taskExecutor = taskExecutor;
        _pollingInterval = pollingInterval;
        _heartbeatInterval = heartbeatInterval;
        _maxConsecutiveHeartbeatFailures = maxConsecutiveHeartbeatFailures;
        _workerId = workerId;
        _logger = logger;
        _heartbeatLogger = heartbeatLogger;

        _logger.LogInformation("TaskExecutionWorker initialized with worker ID: {WorkerId}", _workerId);
    }

    /// <summary>
    /// Gets the worker ID for this instance.
    /// </summary>
    public string WorkerId => _workerId;

    /// <summary>
    /// Main polling loop for the background service.
    /// Runs until cancellation is requested via graceful shutdown.
    /// </summary>
    /// <param name="stoppingToken">Token that signals when the host is stopping.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TaskExecutionWorker started. Polling interval: {PollingInterval}ms",
            _pollingInterval.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecutePollingIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown requested - exit the loop
                _logger.LogInformation("TaskExecutionWorker received shutdown signal, stopping polling loop");
                break;
            }
            catch (Exception ex)
            {
                // Log error but continue polling - the system should be resilient
                _logger.LogError(ex, "Error during polling iteration, will retry after interval");
            }

            // Wait for the polling interval before next iteration
            try
            {
                await Task.Delay(_pollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown during delay - exit the loop
                _logger.LogInformation("TaskExecutionWorker shutdown requested during polling delay");
                break;
            }
        }

        _logger.LogInformation("TaskExecutionWorker stopped");
    }

    /// <summary>
    /// Executes a single polling iteration.
    /// </summary>
    private async Task ExecutePollingIterationAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("Starting polling iteration");

        // Step 1: Reclaim stale locks from crashed workers
        var reclaimedCount = await ReclaimStaleLocksAsync(stoppingToken);
        if (reclaimedCount > 0)
        {
            _logger.LogInformation("Reclaimed {Count} stale lock(s)", reclaimedCount);
        }

        // Step 2: Check for available job
        var job = await _jobRepository.GetAvailableJobAsync(stoppingToken);
        if (job == null)
        {
            _logger.LogDebug("No available jobs found");
            return;
        }

        _logger.LogDebug("Found available job: {JobId} ({JobName})", job.Id, job.JobName);

        // Step 3: Try to acquire lock
        var lockAcquired = await _jobRepository.TryAcquireLockAsync(job.Id, _workerId, stoppingToken);
        if (!lockAcquired)
        {
            _logger.LogDebug("Failed to acquire lock for job {JobId}, another worker claimed it", job.Id);
            return;
        }

        _logger.LogInformation("Acquired lock for job {JobId} ({JobName})", job.Id, job.JobName);

        // Step 4: Execute task with heartbeat
        await ExecuteJobWithHeartbeatAsync(job, stoppingToken);
    }

    /// <summary>
    /// Reclaims stale locks from crashed workers.
    /// </summary>
    private async Task<int> ReclaimStaleLocksAsync(CancellationToken stoppingToken)
    {
        try
        {
            return await _jobRepository.ReclaimStaleLocksAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reclaim stale locks");
            return 0;
        }
    }

    /// <summary>
    /// Executes a job with heartbeat monitoring.
    /// Handles task completion, failure, and graceful shutdown scenarios.
    /// </summary>
    private async Task ExecuteJobWithHeartbeatAsync(Job job, CancellationToken stoppingToken)
    {
        // Create cancellation token source for task execution
        // This can be cancelled by: heartbeat failures, graceful shutdown, or task timeout
        using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        // Create heartbeat service
        await using var heartbeatService = new HeartbeatService(
            job.Id,
            _workerId,
            _jobRepository,
            _heartbeatInterval,
            _maxConsecutiveHeartbeatFailures,
            _heartbeatLogger);

        try
        {
            // Start heartbeat
            heartbeatService.Start(taskCts);

            _logger.LogInformation("Starting task execution for job {JobId} ({JobName})", job.Id, job.JobName);

            // Execute the task
            await _taskExecutor.ExecuteAsync(taskCts.Token);

            // Check if heartbeat abandoned the job
            if (heartbeatService.WasAbandoned)
            {
                _logger.LogWarning(
                    "Job {JobId} was abandoned during execution due to heartbeat failures",
                    job.Id);
                // Lock was already released by heartbeat service, no need to do anything
                return;
            }

            // Task completed successfully - update the job
            var completed = await _jobRepository.CompleteTaskAsync(job.Id, stoppingToken);
            if (completed)
            {
                _logger.LogInformation(
                    "Task completed successfully for job {JobId} ({JobName})",
                    job.Id,
                    job.JobName);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to mark job {JobId} as completed, lock may have been lost",
                    job.Id);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown requested
            _logger.LogInformation(
                "Shutdown requested during task execution for job {JobId}, releasing lock",
                job.Id);

            // Release lock so another worker can pick it up
            await ReleaseLockOnShutdownAsync(job.Id);
        }
        catch (OperationCanceledException) when (heartbeatService.WasAbandoned)
        {
            // Task was cancelled due to heartbeat failure
            _logger.LogWarning(
                "Task for job {JobId} was cancelled due to heartbeat failures",
                job.Id);
            // Lock was already released by heartbeat service
        }
        catch (Exception ex)
        {
            // Task failed with exception
            _logger.LogError(
                ex,
                "Task execution failed for job {JobId} ({JobName})",
                job.Id,
                job.JobName);

            // Release lock on failure (preserves NextRunTime for retry)
            await FailJobAsync(job.Id);
        }
        finally
        {
            // Ensure heartbeat is stopped
            await heartbeatService.StopAsync();
        }
    }

    /// <summary>
    /// Releases lock on graceful shutdown.
    /// </summary>
    private async Task ReleaseLockOnShutdownAsync(Guid jobId)
    {
        try
        {
            // Use FailTaskAsync to release lock while preserving NextRunTime
            await _jobRepository.FailTaskAsync(jobId);
            _logger.LogInformation("Lock released for job {JobId} on shutdown", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release lock for job {JobId} on shutdown", jobId);
        }
    }

    /// <summary>
    /// Marks a job as failed and releases the lock.
    /// </summary>
    private async Task FailJobAsync(Guid jobId)
    {
        try
        {
            await _jobRepository.FailTaskAsync(jobId);
            _logger.LogInformation(
                "Lock released for failed job {JobId}, available for retry by another worker",
                jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release lock for failed job {JobId}", jobId);
        }
    }
}
