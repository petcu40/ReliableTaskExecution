using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReliableTaskExecution.Worker.Configuration;

namespace ReliableTaskExecution.Worker.Services;

/// <summary>
/// Sample task executor that demonstrates task execution with logging.
/// Simulates work by waiting for a configurable duration (default 5 seconds).
///
/// This executor logs:
/// - Task start with timestamp and worker ID
/// - Progress updates during execution
/// - Task completion with total elapsed time
/// </summary>
public sealed class SampleTaskExecutor : ITaskExecutor
{
    private readonly string _workerId;
    private readonly TimeSpan _taskDuration;
    private readonly ILogger<SampleTaskExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the SampleTaskExecutor.
    /// </summary>
    /// <param name="workerId">The worker ID for logging purposes.</param>
    /// <param name="options">Task execution configuration options.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public SampleTaskExecutor(
        string workerId,
        IOptions<TaskExecutionOptions> options,
        ILogger<SampleTaskExecutor> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(workerId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _workerId = workerId;
        _taskDuration = options.Value.TaskDuration;
        _logger = logger;
    }

    /// <summary>
    /// Initializes a new instance of the SampleTaskExecutor with explicit configuration.
    /// Useful for testing without IOptions.
    /// </summary>
    /// <param name="workerId">The worker ID for logging purposes.</param>
    /// <param name="taskDuration">Duration to simulate work.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public SampleTaskExecutor(
        string workerId,
        TimeSpan taskDuration,
        ILogger<SampleTaskExecutor> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(workerId);
        ArgumentNullException.ThrowIfNull(logger);

        if (taskDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(taskDuration), "Task duration cannot be negative.");
        }

        _workerId = workerId;
        _taskDuration = taskDuration;
        _logger = logger;
    }

    /// <summary>
    /// Gets the configured task duration.
    /// </summary>
    public TimeSpan TaskDuration => _taskDuration;

    /// <summary>
    /// Gets the worker ID.
    /// </summary>
    public string WorkerId => _workerId;

    /// <summary>
    /// Executes the sample task, simulating work with logging.
    /// </summary>
    /// <param name="cancellationToken">Token to signal cancellation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var startTime = DateTime.UtcNow;

        // Log task start
        _logger.LogInformation(
            "[{Timestamp:O}] Task started by worker {WorkerId}",
            startTime,
            _workerId);

        Console.WriteLine($"[{startTime:O}] Task started by worker {_workerId}");

        // Check for cancellation before starting work
        cancellationToken.ThrowIfCancellationRequested();

        // Simulate work with progress updates
        var progressInterval = TimeSpan.FromSeconds(1);
        var elapsed = TimeSpan.Zero;

        while (elapsed < _taskDuration)
        {
            // Calculate the remaining time to wait
            var remainingDuration = _taskDuration - elapsed;
            var waitTime = remainingDuration < progressInterval ? remainingDuration : progressInterval;

            try
            {
                await Task.Delay(waitTime, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Log cancellation
                var cancelTime = DateTime.UtcNow;
                _logger.LogWarning(
                    "[{Timestamp:O}] Task cancelled by worker {WorkerId} after {ElapsedSeconds:F1}s",
                    cancelTime,
                    _workerId,
                    elapsed.TotalSeconds);

                Console.WriteLine($"[{cancelTime:O}] Task cancelled by worker {_workerId} after {elapsed.TotalSeconds:F1}s");

                throw;
            }

            elapsed += waitTime;

            // Log progress (only if not at the end)
            if (elapsed < _taskDuration)
            {
                var progressTime = DateTime.UtcNow;
                var progressPercentage = (elapsed.TotalSeconds / _taskDuration.TotalSeconds) * 100;

                _logger.LogDebug(
                    "[{Timestamp:O}] Task progress: {Progress:F0}% (worker {WorkerId})",
                    progressTime,
                    progressPercentage,
                    _workerId);

                Console.WriteLine($"[{progressTime:O}] Task progress: {progressPercentage:F0}% (worker {_workerId})");
            }
        }

        // Log task completion
        var endTime = DateTime.UtcNow;
        var totalElapsed = endTime - startTime;

        _logger.LogInformation(
            "[{Timestamp:O}] Task completed by worker {WorkerId} in {ElapsedSeconds:F1}s",
            endTime,
            _workerId,
            totalElapsed.TotalSeconds);

        Console.WriteLine($"[{endTime:O}] Task completed by worker {_workerId} in {totalElapsed.TotalSeconds:F1}s");
    }
}
