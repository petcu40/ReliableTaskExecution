using Microsoft.Extensions.Logging;
using Moq;
using ReliableTaskExecution.Worker.Data;
using ReliableTaskExecution.Worker.Services;
using Xunit;

namespace ReliableTaskExecution.Worker.Tests.Services;

/// <summary>
/// Unit tests for TaskExecutionWorker (BackgroundService).
/// These 4 tests verify the core polling loop behavior including interval timing,
/// graceful shutdown, continuation after task completion, and handling of empty job queues.
/// </summary>
public class TaskExecutionWorkerTests
{
    private readonly Mock<IJobRepository> _jobRepositoryMock;
    private readonly Mock<ITaskExecutor> _taskExecutorMock;
    private readonly Mock<ILogger<TaskExecutionWorker>> _workerLoggerMock;
    private readonly Mock<ILogger<HeartbeatService>> _heartbeatLoggerMock;
    private readonly string _workerId;

    public TaskExecutionWorkerTests()
    {
        _jobRepositoryMock = new Mock<IJobRepository>();
        _taskExecutorMock = new Mock<ITaskExecutor>();
        _workerLoggerMock = new Mock<ILogger<TaskExecutionWorker>>();
        _heartbeatLoggerMock = new Mock<ILogger<HeartbeatService>>();
        _workerId = "TestWorker_123_abc";
    }

    private TaskExecutionWorker CreateWorker(
        TimeSpan? pollingInterval = null,
        TimeSpan? heartbeatInterval = null,
        int maxConsecutiveHeartbeatFailures = 3)
    {
        return new TaskExecutionWorker(
            _jobRepositoryMock.Object,
            _taskExecutorMock.Object,
            pollingInterval ?? TimeSpan.FromMilliseconds(100),
            heartbeatInterval ?? TimeSpan.FromSeconds(30),
            maxConsecutiveHeartbeatFailures,
            _workerId,
            _workerLoggerMock.Object,
            _heartbeatLoggerMock.Object);
    }

    #region Test 1: Polling Occurs at Configured Interval

    /// <summary>
    /// Test 1: Verify polling occurs at the configured interval.
    /// The worker should poll for jobs at least twice within 2x the polling interval,
    /// demonstrating that the polling loop respects the configured timing.
    /// </summary>
    [Fact]
    public async Task PollingOccursAtConfiguredInterval()
    {
        // Arrange
        var pollingInterval = TimeSpan.FromMilliseconds(100);
        var pollCount = 0;

        _jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0)
            .Callback(() => pollCount++);

        _jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job?)null);

        var worker = CreateWorker(pollingInterval: pollingInterval);

        using var cts = new CancellationTokenSource();

        // Act - Run for enough time to complete at least 2 polling iterations
        var workerTask = worker.StartAsync(cts.Token);

        // Wait for at least 3 polling cycles (100ms interval + buffer)
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert - Should have polled at least twice
        Assert.True(pollCount >= 2, $"Expected at least 2 polls, but got {pollCount}");
    }

    #endregion

    #region Test 2: Graceful Shutdown on CancellationToken

    /// <summary>
    /// Test 2: Verify graceful shutdown on CancellationToken.
    /// When the cancellation token is triggered, the worker should stop polling
    /// and exit cleanly without throwing exceptions.
    /// </summary>
    [Fact]
    public async Task GracefulShutdownOnCancellationToken()
    {
        // Arrange
        _jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job?)null);

        var worker = CreateWorker(pollingInterval: TimeSpan.FromMilliseconds(100));

        using var cts = new CancellationTokenSource();

        // Act
        var workerTask = worker.StartAsync(cts.Token);

        // Let it poll once
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // Request shutdown
        await cts.CancelAsync();

        // Wait for graceful stop - should not throw
        var stopTask = worker.StopAsync(CancellationToken.None);

        // Assert - Should complete within a reasonable time without throwing
        var completedInTime = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(2))) == stopTask;
        Assert.True(completedInTime, "Worker should stop gracefully within 2 seconds");
    }

    #endregion

    #region Test 3: Continues Polling After Task Completion

    /// <summary>
    /// Test 3: Verify the worker continues polling after task completion.
    /// After successfully completing a task, the worker should return to polling
    /// for more jobs, not exit or stop.
    /// </summary>
    [Fact]
    public async Task ContinuesPollingAfterTaskCompletion()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var pollsAfterCompletion = 0;
        var taskExecuted = false;

        var job = new Job
        {
            Id = jobId,
            JobName = "TestJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-1),
            LockedBy = null,
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        // First poll returns a job, subsequent polls return null
        var pollNumber = 0;
        _jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollNumber++;
                if (pollNumber == 1 && !taskExecuted)
                {
                    return job;
                }

                // After task execution, track subsequent polls
                if (taskExecuted)
                {
                    pollsAfterCompletion++;
                }

                return null;
            });

        _jobRepositoryMock
            .Setup(r => r.TryAcquireLockAsync(jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _jobRepositoryMock
            .Setup(r => r.CompleteTaskAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _taskExecutorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(10, ct); // Quick task
                taskExecuted = true;
            });

        var worker = CreateWorker(pollingInterval: TimeSpan.FromMilliseconds(100));

        using var cts = new CancellationTokenSource();

        // Act
        var workerTask = worker.StartAsync(cts.Token);

        // Wait for task execution and at least one more poll
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(taskExecuted, "Task should have been executed");
        Assert.True(pollsAfterCompletion >= 1, $"Should have polled at least once after task completion, but got {pollsAfterCompletion}");
    }

    #endregion

    #region Test 4: Handles No Available Jobs Gracefully

    /// <summary>
    /// Test 4: Verify the worker handles no available jobs gracefully.
    /// When GetAvailableJobAsync returns null, the worker should continue polling
    /// without errors or exceptions.
    /// </summary>
    [Fact]
    public async Task HandlesNoAvailableJobsGracefully()
    {
        // Arrange
        var pollCount = 0;

        _jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCount++;
                return null; // No jobs available
            });

        var worker = CreateWorker(pollingInterval: TimeSpan.FromMilliseconds(100));

        using var cts = new CancellationTokenSource();

        // Act
        var workerTask = worker.StartAsync(cts.Token);

        // Wait for multiple polling iterations
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(pollCount >= 2, $"Worker should continue polling even with no jobs, but polled {pollCount} times");

        // Verify no task execution was attempted
        _taskExecutorMock.Verify(
            e => e.ExecuteAsync(It.IsAny<CancellationToken>()),
            Times.Never,
            "Task executor should not be called when no jobs are available");

        // Verify no lock acquisition was attempted
        _jobRepositoryMock.Verify(
            r => r.TryAcquireLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Lock acquisition should not be attempted when no jobs are available");
    }

    #endregion
}
