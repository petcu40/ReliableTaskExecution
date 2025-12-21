using Microsoft.Extensions.Logging;

namespace ReliableTaskExecution.Worker.Services;

/// <summary>
/// Interface for the heartbeat service that extends lock duration during task execution.
/// Heartbeats update the LockedAt timestamp to prevent lock expiration while a task is running.
/// </summary>
public interface IHeartbeatService : IAsyncDisposable
{
    /// <summary>
    /// Starts the heartbeat loop for the specified job.
    /// The heartbeat will run at the configured interval until stopped or cancelled.
    /// </summary>
    /// <param name="taskCancellationSource">
    /// CancellationTokenSource that will be cancelled if heartbeat failures trigger job abandonment.
    /// This allows the heartbeat service to stop task execution when the lock is lost.
    /// </param>
    void Start(CancellationTokenSource taskCancellationSource);

    /// <summary>
    /// Stops the heartbeat loop gracefully.
    /// Should be called when the task completes successfully or fails.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Gets the current count of consecutive heartbeat failures.
    /// Resets to 0 on successful heartbeat.
    /// </summary>
    int ConsecutiveFailureCount { get; }

    /// <summary>
    /// Gets whether the job was abandoned due to consecutive heartbeat failures.
    /// </summary>
    bool WasAbandoned { get; }
}
