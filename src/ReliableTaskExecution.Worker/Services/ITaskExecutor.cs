namespace ReliableTaskExecution.Worker.Services;

/// <summary>
/// Interface for task executors that perform the actual work of a job.
/// Implementations should be cancellable and support graceful shutdown.
/// </summary>
public interface ITaskExecutor
{
    /// <summary>
    /// Executes the task asynchronously.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token that signals cancellation, either due to graceful shutdown,
    /// task timeout, or heartbeat failure abandonment.
    /// </param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when the cancellation token is triggered during execution.
    /// </exception>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
