using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReliableTaskExecution.Worker.Configuration;
using ReliableTaskExecution.Worker.Data;

namespace ReliableTaskExecution.Worker.Services;

/// <summary>
/// Service that sends heartbeat updates to extend lock duration during task execution.
/// Heartbeats update the LockedAt timestamp every 30 seconds (configurable) to prevent
/// the lock from expiring while a long-running task is executing.
///
/// If heartbeat fails 3 times consecutively, the job is abandoned to prevent
/// multi-master situations where multiple workers execute the same job.
/// </summary>
public sealed class HeartbeatService : IHeartbeatService
{
    private readonly Guid _jobId;
    private readonly string _workerId;
    private readonly IJobRepository _jobRepository;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly int _maxConsecutiveFailures;

    private CancellationTokenSource? _heartbeatCancellationSource;
    private CancellationTokenSource? _taskCancellationSource;
    private Task? _heartbeatTask;
    private int _consecutiveFailures;
    private bool _wasAbandoned;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new instance of the HeartbeatService.
    /// </summary>
    /// <param name="jobId">The ID of the job to send heartbeats for.</param>
    /// <param name="workerId">The worker ID that holds the lock.</param>
    /// <param name="jobRepository">Repository for job operations.</param>
    /// <param name="options">Task execution configuration options.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public HeartbeatService(
        Guid jobId,
        string workerId,
        IJobRepository jobRepository,
        IOptions<TaskExecutionOptions> options,
        ILogger<HeartbeatService> logger)
    {
        ArgumentNullException.ThrowIfNull(jobRepository);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(workerId);

        _jobId = jobId;
        _workerId = workerId;
        _jobRepository = jobRepository;
        _logger = logger;
        _heartbeatInterval = options.Value.HeartbeatInterval;
        _maxConsecutiveFailures = options.Value.MaxConsecutiveHeartbeatFailures;
        _consecutiveFailures = 0;
        _wasAbandoned = false;
    }

    /// <summary>
    /// Creates a HeartbeatService with explicit configuration values.
    /// Useful for testing without IOptions.
    /// </summary>
    /// <param name="jobId">The ID of the job to send heartbeats for.</param>
    /// <param name="workerId">The worker ID that holds the lock.</param>
    /// <param name="jobRepository">Repository for job operations.</param>
    /// <param name="heartbeatInterval">Interval between heartbeats.</param>
    /// <param name="maxConsecutiveFailures">Maximum consecutive failures before abandonment.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public HeartbeatService(
        Guid jobId,
        string workerId,
        IJobRepository jobRepository,
        TimeSpan heartbeatInterval,
        int maxConsecutiveFailures,
        ILogger<HeartbeatService> logger)
    {
        ArgumentNullException.ThrowIfNull(jobRepository);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(workerId);

        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval), "Heartbeat interval must be positive.");
        }

        if (maxConsecutiveFailures <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveFailures), "Max consecutive failures must be positive.");
        }

        _jobId = jobId;
        _workerId = workerId;
        _jobRepository = jobRepository;
        _logger = logger;
        _heartbeatInterval = heartbeatInterval;
        _maxConsecutiveFailures = maxConsecutiveFailures;
        _consecutiveFailures = 0;
        _wasAbandoned = false;
    }

    /// <inheritdoc/>
    public int ConsecutiveFailureCount => _consecutiveFailures;

    /// <inheritdoc/>
    public bool WasAbandoned => _wasAbandoned;

    /// <inheritdoc/>
    public void Start(CancellationTokenSource taskCancellationSource)
    {
        ArgumentNullException.ThrowIfNull(taskCancellationSource);

        if (_heartbeatTask != null)
        {
            throw new InvalidOperationException("Heartbeat service is already running.");
        }

        _taskCancellationSource = taskCancellationSource;
        _heartbeatCancellationSource = new CancellationTokenSource();
        _heartbeatTask = HeartbeatLoopAsync(_heartbeatCancellationSource.Token);

        _logger.LogDebug(
            "Heartbeat service started for job {JobId} with interval {Interval}s",
            _jobId,
            _heartbeatInterval.TotalSeconds);
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        if (_heartbeatCancellationSource == null || _heartbeatTask == null)
        {
            return;
        }

        _logger.LogDebug("Stopping heartbeat service for job {JobId}", _jobId);

        await _heartbeatCancellationSource.CancelAsync();

        try
        {
            // Wait for the heartbeat loop to complete, but with a timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _heartbeatTask.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected when the heartbeat loop is cancelled
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Heartbeat service stop timed out for job {JobId}", _jobId);
        }

        _logger.LogDebug("Heartbeat service stopped for job {JobId}", _jobId);
    }

    /// <summary>
    /// Main heartbeat loop that runs until cancellation or abandonment.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Heartbeat loop started for job {JobId}, worker {WorkerId}",
            _jobId,
            _workerId);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the heartbeat interval
                await Task.Delay(_heartbeatInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation requested during delay - exit gracefully
                _logger.LogDebug("Heartbeat delay cancelled for job {JobId}", _jobId);
                break;
            }

            // Check if already cancelled before attempting heartbeat
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var success = await _jobRepository.UpdateHeartbeatAsync(_jobId, _workerId, cancellationToken);

                if (success)
                {
                    _consecutiveFailures = 0; // Reset on success
                    _logger.LogDebug(
                        "Heartbeat successful for job {JobId}, lock extended",
                        _jobId);
                }
                else
                {
                    _consecutiveFailures++;
                    _logger.LogWarning(
                        "Heartbeat failed for job {JobId} ({Count}/{Max}) - lock may have been lost",
                        _jobId,
                        _consecutiveFailures,
                        _maxConsecutiveFailures);

                    if (_consecutiveFailures >= _maxConsecutiveFailures)
                    {
                        await AbandonJobAsync();
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation requested during heartbeat - exit gracefully
                _logger.LogDebug("Heartbeat operation cancelled for job {JobId}", _jobId);
                break;
            }
            catch (Exception ex)
            {
                // Treat exceptions as heartbeat failures
                _consecutiveFailures++;
                _logger.LogWarning(
                    ex,
                    "Heartbeat failed with exception for job {JobId} ({Count}/{Max})",
                    _jobId,
                    _consecutiveFailures,
                    _maxConsecutiveFailures);

                if (_consecutiveFailures >= _maxConsecutiveFailures)
                {
                    await AbandonJobAsync();
                    break;
                }
            }
        }

        _logger.LogInformation(
            "Heartbeat loop ended for job {JobId}, abandoned: {WasAbandoned}",
            _jobId,
            _wasAbandoned);
    }

    /// <summary>
    /// Abandons the job after consecutive heartbeat failures.
    /// Cancels task execution and releases the lock.
    /// </summary>
    private async Task AbandonJobAsync()
    {
        _wasAbandoned = true;

        _logger.LogError(
            "Abandoning job {JobId} after {Max} consecutive heartbeat failures to prevent multi-master",
            _jobId,
            _maxConsecutiveFailures);

        // Cancel task execution first
        if (_taskCancellationSource != null && !_taskCancellationSource.IsCancellationRequested)
        {
            await _taskCancellationSource.CancelAsync();
        }

        // Release the lock (leave NextRunTime unchanged for retry by another worker)
        try
        {
            await _jobRepository.ReleaseLockOnFailureAsync(_jobId);
            _logger.LogInformation(
                "Lock released for abandoned job {JobId}, available for retry by another worker",
                _jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to release lock for abandoned job {JobId}",
                _jobId);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await StopAsync();

        _heartbeatCancellationSource?.Dispose();
        _heartbeatCancellationSource = null;

        _isDisposed = true;
    }
}
