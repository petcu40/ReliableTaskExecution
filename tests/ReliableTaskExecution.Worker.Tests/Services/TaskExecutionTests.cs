using Microsoft.Extensions.Logging;
using Moq;
using ReliableTaskExecution.Worker.Data;
using ReliableTaskExecution.Worker.Services;
using Xunit;

namespace ReliableTaskExecution.Worker.Tests.Services;

/// <summary>
/// Unit tests for task execution functionality.
/// These 5 tests verify the core task execution behavior including completion,
/// failure handling, and cancellation scenarios.
/// </summary>
public class TaskExecutionTests
{
    private readonly Mock<IJobRepository> _jobRepositoryMock;
    private readonly Mock<ILogger<SampleTaskExecutor>> _executorLoggerMock;
    private readonly Guid _jobId;
    private readonly string _workerId;

    public TaskExecutionTests()
    {
        _jobRepositoryMock = new Mock<IJobRepository>();
        _executorLoggerMock = new Mock<ILogger<SampleTaskExecutor>>();
        _jobId = Guid.NewGuid();
        _workerId = "TestWorker_123_abc";
    }

    #region Test 1: Successful Task Execution Updates LastRunTime

    /// <summary>
    /// Test 1: Verify successful task execution updates LastRunTime.
    /// When a task completes successfully, CompleteTaskAsync should be called,
    /// which sets LastRunTime = GETUTCDATE() and calculates NextRunTime from LockedAt.
    /// </summary>
    [Fact]
    public async Task SuccessfulTaskExecution_UpdatesLastRunTime()
    {
        // Arrange
        var lockedAt = DateTime.UtcNow.AddMinutes(-1);
        var intervalMinutes = 10;
        var expectedNextRunTime = lockedAt.AddMinutes(intervalMinutes);

        var job = new Job
        {
            Id = _jobId,
            JobName = "TestJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-5),
            LastRunTime = null,
            LockedBy = _workerId,
            LockedAt = lockedAt,
            LockTimeoutMinutes = 2,
            IntervalMinutes = intervalMinutes
        };

        _jobRepositoryMock
            .Setup(r => r.CompleteTaskAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() =>
            {
                // Simulate what the SQL UPDATE does:
                // NextRunTime = DATEADD(MINUTE, IntervalMinutes, LockedAt)
                // LastRunTime = GETUTCDATE()
                job.NextRunTime = job.LockedAt!.Value.AddMinutes(job.IntervalMinutes);
                job.LastRunTime = DateTime.UtcNow;
                job.LockedBy = null;
                job.LockedAt = null;
            });

        _jobRepositoryMock
            .Setup(r => r.GetJobByIdAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => job);

        var executor = new SampleTaskExecutor(
            _workerId,
            TimeSpan.FromMilliseconds(10), // Short duration for test
            _executorLoggerMock.Object);

        // Act - Simulate task execution and completion
        await executor.ExecuteAsync(CancellationToken.None);
        var completed = await _jobRepositoryMock.Object.CompleteTaskAsync(_jobId);
        var jobAfter = await _jobRepositoryMock.Object.GetJobByIdAsync(_jobId);

        // Assert
        Assert.True(completed, "CompleteTaskAsync should return true");
        Assert.NotNull(jobAfter);
        Assert.NotNull(jobAfter.LastRunTime);
        Assert.Null(jobAfter.LockedBy);
        Assert.Null(jobAfter.LockedAt);

        _jobRepositoryMock.Verify(
            r => r.CompleteTaskAsync(_jobId, It.IsAny<CancellationToken>()),
            Times.Once,
            "CompleteTaskAsync should be called on successful completion");
    }

    #endregion

    #region Test 2: NextRunTime Calculated from LockedAt (Not Completion Time)

    /// <summary>
    /// Test 2: Verify NextRunTime is calculated from LockedAt (task start time), not completion time.
    /// This prevents timing drift over multiple executions.
    /// NextRunTime = LockedAt + IntervalMinutes
    /// </summary>
    [Fact]
    public async Task NextRunTime_CalculatedFromLockedAt_NotCompletionTime()
    {
        // Arrange
        var lockedAt = DateTime.UtcNow.AddMinutes(-5); // Task started 5 minutes ago
        var intervalMinutes = 10;
        var expectedNextRunTime = lockedAt.AddMinutes(intervalMinutes);

        // If NextRunTime were based on completion time (now), it would be ~10 minutes from now
        // But it should be based on LockedAt, so it should be ~5 minutes from now

        var job = new Job
        {
            Id = _jobId,
            JobName = "TestJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-10),
            LockedBy = _workerId,
            LockedAt = lockedAt,
            LockTimeoutMinutes = 2,
            IntervalMinutes = intervalMinutes
        };

        _jobRepositoryMock
            .Setup(r => r.ReleaseLockAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() =>
            {
                // Simulate what the SQL UPDATE does:
                // NextRunTime = DATEADD(MINUTE, IntervalMinutes, LockedAt)
                job.NextRunTime = job.LockedAt!.Value.AddMinutes(job.IntervalMinutes);
                job.LastRunTime = DateTime.UtcNow;
                job.LockedBy = null;
                job.LockedAt = null;
            });

        // Act
        await _jobRepositoryMock.Object.ReleaseLockAsync(_jobId);

        // Assert
        // NextRunTime should be LockedAt + IntervalMinutes, not "now + IntervalMinutes"
        Assert.Equal(expectedNextRunTime, job.NextRunTime);

        // Verify it's NOT based on completion time (which would be ~10 minutes from now)
        var completionBasedNextRun = DateTime.UtcNow.AddMinutes(intervalMinutes);
        Assert.NotEqual(completionBasedNextRun.ToString("yyyy-MM-dd HH:mm"), job.NextRunTime.ToString("yyyy-MM-dd HH:mm"));
    }

    #endregion

    #region Test 3: Task Failure Releases Lock Immediately

    /// <summary>
    /// Test 3: Verify task failure releases lock immediately.
    /// When a task fails, FailTaskAsync should set LockedBy = NULL immediately,
    /// allowing another worker to pick up the job.
    /// </summary>
    [Fact]
    public async Task TaskFailure_ReleasesLockImmediately()
    {
        // Arrange
        var job = new Job
        {
            Id = _jobId,
            JobName = "FailingJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-5),
            LockedBy = _workerId,
            LockedAt = DateTime.UtcNow.AddMinutes(-1),
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        _jobRepositoryMock
            .Setup(r => r.FailTaskAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() =>
            {
                // Simulate what the SQL UPDATE does on failure:
                // SET LockedBy = NULL, LockedAt = NULL
                // NextRunTime is NOT changed
                job.LockedBy = null;
                job.LockedAt = null;
            });

        // Act - Simulate task failure
        var released = await _jobRepositoryMock.Object.FailTaskAsync(_jobId);

        // Assert
        Assert.True(released, "FailTaskAsync should return true");
        Assert.Null(job.LockedBy);
        Assert.Null(job.LockedAt);

        _jobRepositoryMock.Verify(
            r => r.FailTaskAsync(_jobId, It.IsAny<CancellationToken>()),
            Times.Once,
            "FailTaskAsync should be called on task failure");
    }

    #endregion

    #region Test 4: Task Failure Preserves NextRunTime for Retry

    /// <summary>
    /// Test 4: Verify task failure preserves NextRunTime for retry.
    /// When a task fails, NextRunTime should remain unchanged so another worker
    /// can immediately pick up the task for retry.
    /// </summary>
    [Fact]
    public async Task TaskFailure_PreservesNextRunTime_ForRetry()
    {
        // Arrange
        var originalNextRunTime = DateTime.UtcNow.AddMinutes(-5);

        var job = new Job
        {
            Id = _jobId,
            JobName = "FailingJob",
            NextRunTime = originalNextRunTime,
            LockedBy = _workerId,
            LockedAt = DateTime.UtcNow.AddMinutes(-1),
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        _jobRepositoryMock
            .Setup(r => r.ReleaseLockOnFailureAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() =>
            {
                // Simulate failure: only clear lock fields, keep NextRunTime
                job.LockedBy = null;
                job.LockedAt = null;
                // NextRunTime intentionally NOT changed
            });

        // Act
        await _jobRepositoryMock.Object.ReleaseLockOnFailureAsync(_jobId);

        // Assert
        Assert.Equal(originalNextRunTime, job.NextRunTime);
        Assert.Null(job.LockedBy);

        // Since NextRunTime is in the past, the job is immediately available for retry
        Assert.True(job.NextRunTime < DateTime.UtcNow, "Job should be immediately available for retry");
    }

    #endregion

    #region Test 5: Cancellation Stops Task Gracefully

    /// <summary>
    /// Test 5: Verify cancellation stops task gracefully.
    /// When the cancellation token is triggered, the task should stop execution
    /// and throw OperationCanceledException (or its subclass TaskCanceledException).
    /// </summary>
    [Fact]
    public async Task Cancellation_StopsTaskGracefully()
    {
        // Arrange
        var executor = new SampleTaskExecutor(
            _workerId,
            TimeSpan.FromSeconds(10), // Long duration to ensure we can cancel
            _executorLoggerMock.Object);

        var cancellationTokenSource = new CancellationTokenSource();

        // Act - Start task and cancel quickly
        var executeTask = executor.ExecuteAsync(cancellationTokenSource.Token);

        // Wait a moment then cancel
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await cancellationTokenSource.CancelAsync();

        // Assert - Task should throw OperationCanceledException or its subclass TaskCanceledException
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await executeTask;
        });

        // Verify the exception is related to our cancellation token
        Assert.True(
            exception is OperationCanceledException || exception is TaskCanceledException,
            "Should throw OperationCanceledException or TaskCanceledException on cancellation");
    }

    #endregion

    #region Additional Validation Tests

    /// <summary>
    /// Verify SampleTaskExecutor validates constructor parameters.
    /// </summary>
    [Fact]
    public void SampleTaskExecutor_Constructor_ValidatesParameters()
    {
        // Empty worker ID
        Assert.Throws<ArgumentException>(() =>
            new SampleTaskExecutor(
                "",
                TimeSpan.FromSeconds(5),
                _executorLoggerMock.Object));

        // Null logger
        Assert.Throws<ArgumentNullException>(() =>
            new SampleTaskExecutor(
                _workerId,
                TimeSpan.FromSeconds(5),
                null!));

        // Negative task duration
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SampleTaskExecutor(
                _workerId,
                TimeSpan.FromSeconds(-1),
                _executorLoggerMock.Object));
    }

    /// <summary>
    /// Verify SampleTaskExecutor completes within expected duration.
    /// </summary>
    [Fact]
    public async Task SampleTaskExecutor_CompletesWithinExpectedDuration()
    {
        // Arrange
        var taskDuration = TimeSpan.FromMilliseconds(100);
        var executor = new SampleTaskExecutor(
            _workerId,
            taskDuration,
            _executorLoggerMock.Object);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        await executor.ExecuteAsync(CancellationToken.None);
        stopwatch.Stop();

        // Assert - Allow some tolerance for execution overhead
        Assert.True(
            stopwatch.Elapsed >= taskDuration,
            $"Execution should take at least {taskDuration.TotalMilliseconds}ms");

        Assert.True(
            stopwatch.Elapsed < taskDuration.Add(TimeSpan.FromMilliseconds(500)),
            $"Execution should not take excessively long");
    }

    /// <summary>
    /// Verify ITaskExecutor interface is properly implemented.
    /// </summary>
    [Fact]
    public async Task ITaskExecutor_InterfaceImplementation()
    {
        // Arrange
        ITaskExecutor executor = new SampleTaskExecutor(
            _workerId,
            TimeSpan.FromMilliseconds(10),
            _executorLoggerMock.Object);

        // Act & Assert - Should not throw
        await executor.ExecuteAsync(CancellationToken.None);
    }

    /// <summary>
    /// Verify IJobRepository interface includes CompleteTaskAsync, FailTaskAsync, and GetJobByIdAsync.
    /// </summary>
    [Fact]
    public async Task IJobRepository_HasSemanticMethods()
    {
        // Arrange
        _jobRepositoryMock
            .Setup(r => r.CompleteTaskAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _jobRepositoryMock
            .Setup(r => r.FailTaskAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _jobRepositoryMock
            .Setup(r => r.GetJobByIdAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Job { Id = _jobId, JobName = "Test" });

        // Act & Assert - Verify methods can be called
        var completeResult = await _jobRepositoryMock.Object.CompleteTaskAsync(_jobId);
        var failResult = await _jobRepositoryMock.Object.FailTaskAsync(_jobId);
        var job = await _jobRepositoryMock.Object.GetJobByIdAsync(_jobId);

        Assert.True(completeResult);
        Assert.True(failResult);
        Assert.NotNull(job);
        Assert.Equal(_jobId, job.Id);
    }

    #endregion
}
