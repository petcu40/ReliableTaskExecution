using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ReliableTaskExecution.Worker.Configuration;
using ReliableTaskExecution.Worker.Data;
using ReliableTaskExecution.Worker.Services;
using Xunit;

namespace ReliableTaskExecution.Worker.Tests.Services;

/// <summary>
/// Unit tests for HeartbeatService functionality.
/// These 6 tests verify heartbeat behavior for distributed task coordination.
///
/// The heartbeat mechanism extends lock duration while a task is executing,
/// preventing the lock from expiring and being reclaimed by another worker.
/// </summary>
public class HeartbeatServiceTests
{
    private readonly Mock<IJobRepository> _jobRepositoryMock;
    private readonly Mock<ILogger<HeartbeatService>> _loggerMock;
    private readonly Guid _jobId;
    private readonly string _workerId;

    public HeartbeatServiceTests()
    {
        _jobRepositoryMock = new Mock<IJobRepository>();
        _loggerMock = new Mock<ILogger<HeartbeatService>>();
        _jobId = Guid.NewGuid();
        _workerId = "TestWorker_123_abc";
    }

    #region Test 1: Heartbeat Updates LockedAt Timestamp

    /// <summary>
    /// Test 1: Verify heartbeat updates LockedAt timestamp.
    /// When the heartbeat fires, it should call UpdateHeartbeatAsync on the repository.
    /// </summary>
    [Fact]
    public async Task Heartbeat_UpdatesLockedAtTimestamp_WhenFired()
    {
        // Arrange
        _jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(_jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var heartbeatInterval = TimeSpan.FromMilliseconds(50);
        var maxFailures = 3;

        await using var heartbeatService = new HeartbeatService(
            _jobId,
            _workerId,
            _jobRepositoryMock.Object,
            heartbeatInterval,
            maxFailures,
            _loggerMock.Object);

        var taskCancellationSource = new CancellationTokenSource();

        // Act
        heartbeatService.Start(taskCancellationSource);

        // Wait for at least one heartbeat to fire
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        await heartbeatService.StopAsync();

        // Assert
        _jobRepositoryMock.Verify(
            r => r.UpdateHeartbeatAsync(_jobId, _workerId, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(),
            "Heartbeat should call UpdateHeartbeatAsync to update LockedAt timestamp");
    }

    #endregion

    #region Test 2: Heartbeat Runs at Configured Interval

    /// <summary>
    /// Test 2: Verify heartbeat runs at configured interval.
    /// The heartbeat should fire approximately at the configured interval.
    /// </summary>
    [Fact]
    public async Task Heartbeat_RunsAtConfiguredInterval()
    {
        // Arrange
        var callCount = 0;
        _jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(_jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => callCount++);

        var heartbeatInterval = TimeSpan.FromMilliseconds(30);
        var maxFailures = 3;

        await using var heartbeatService = new HeartbeatService(
            _jobId,
            _workerId,
            _jobRepositoryMock.Object,
            heartbeatInterval,
            maxFailures,
            _loggerMock.Object);

        var taskCancellationSource = new CancellationTokenSource();

        // Act
        heartbeatService.Start(taskCancellationSource);

        // Wait for multiple heartbeats (3 intervals + some buffer)
        await Task.Delay(TimeSpan.FromMilliseconds(120));

        await heartbeatService.StopAsync();

        // Assert - Should have fired 2-4 times in 120ms with 30ms interval
        // First heartbeat at ~30ms, second at ~60ms, third at ~90ms
        Assert.True(
            callCount >= 2 && callCount <= 5,
            $"Expected 2-5 heartbeat calls, but got {callCount}");
    }

    #endregion

    #region Test 3: Heartbeat Stops When Task Completes

    /// <summary>
    /// Test 3: Verify heartbeat stops when task completes.
    /// When StopAsync is called, the heartbeat loop should exit gracefully.
    /// </summary>
    [Fact]
    public async Task Heartbeat_StopsWhenTaskCompletes()
    {
        // Arrange
        var callCount = 0;
        _jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(_jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => callCount++);

        var heartbeatInterval = TimeSpan.FromMilliseconds(30);
        var maxFailures = 3;

        await using var heartbeatService = new HeartbeatService(
            _jobId,
            _workerId,
            _jobRepositoryMock.Object,
            heartbeatInterval,
            maxFailures,
            _loggerMock.Object);

        var taskCancellationSource = new CancellationTokenSource();

        // Act
        heartbeatService.Start(taskCancellationSource);

        // Wait for one heartbeat
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        var callCountBeforeStop = callCount;

        // Stop the heartbeat
        await heartbeatService.StopAsync();

        // Wait to ensure no more heartbeats fire after stop
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        var callCountAfterStop = callCount;

        // Assert - No additional heartbeats should fire after StopAsync
        Assert.Equal(callCountBeforeStop, callCountAfterStop);
    }

    #endregion

    #region Test 4: Heartbeat Failure Increments Consecutive Failure Counter

    /// <summary>
    /// Test 4: Verify heartbeat failure increments consecutive failure counter.
    /// When UpdateHeartbeatAsync returns false, the failure count should increase.
    /// </summary>
    [Fact]
    public async Task HeartbeatFailure_IncrementsConsecutiveFailureCounter()
    {
        // Arrange - First heartbeat fails, then stop
        _jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(_jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var heartbeatInterval = TimeSpan.FromMilliseconds(30);
        var maxFailures = 5; // High enough to not trigger abandonment

        await using var heartbeatService = new HeartbeatService(
            _jobId,
            _workerId,
            _jobRepositoryMock.Object,
            heartbeatInterval,
            maxFailures,
            _loggerMock.Object);

        var taskCancellationSource = new CancellationTokenSource();

        // Act
        heartbeatService.Start(taskCancellationSource);

        // Wait for a couple of heartbeats to fail
        await Task.Delay(TimeSpan.FromMilliseconds(80));

        await heartbeatService.StopAsync();

        // Assert
        Assert.True(
            heartbeatService.ConsecutiveFailureCount >= 1,
            "Consecutive failure count should be at least 1 after failed heartbeat");
    }

    #endregion

    #region Test 5: Three Consecutive Heartbeat Failures Triggers Job Abandonment

    /// <summary>
    /// Test 5: Verify 3 consecutive heartbeat failures triggers job abandonment.
    /// After 3 consecutive failures, the job should be abandoned and task execution cancelled.
    /// </summary>
    [Fact]
    public async Task ThreeConsecutiveFailures_TriggersJobAbandonment()
    {
        // Arrange - All heartbeats fail
        _jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(_jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _jobRepositoryMock
            .Setup(r => r.ReleaseLockOnFailureAsync(_jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var heartbeatInterval = TimeSpan.FromMilliseconds(20);
        var maxFailures = 3;

        await using var heartbeatService = new HeartbeatService(
            _jobId,
            _workerId,
            _jobRepositoryMock.Object,
            heartbeatInterval,
            maxFailures,
            _loggerMock.Object);

        var taskCancellationSource = new CancellationTokenSource();

        // Act
        heartbeatService.Start(taskCancellationSource);

        // Wait for 3 consecutive failures to occur
        // 3 failures * 20ms interval = 60ms + buffer
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        // Assert
        Assert.True(
            heartbeatService.WasAbandoned,
            "Job should be marked as abandoned after 3 consecutive failures");

        Assert.True(
            taskCancellationSource.IsCancellationRequested,
            "Task cancellation should be triggered on abandonment");

        _jobRepositoryMock.Verify(
            r => r.ReleaseLockOnFailureAsync(_jobId, It.IsAny<CancellationToken>()),
            Times.Once,
            "Lock should be released on abandonment");
    }

    #endregion

    #region Test 6: Successful Heartbeat Resets Consecutive Failure Counter

    /// <summary>
    /// Test 6: Verify successful heartbeat resets consecutive failure counter.
    /// If a heartbeat succeeds after failures, the failure count should reset to 0.
    /// </summary>
    [Fact]
    public async Task SuccessfulHeartbeat_ResetsConsecutiveFailureCounter()
    {
        // Arrange - First heartbeat fails, second succeeds
        var callCount = 0;
        _jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(_jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                // First call fails, subsequent calls succeed
                return callCount > 1;
            });

        var heartbeatInterval = TimeSpan.FromMilliseconds(30);
        var maxFailures = 3;

        await using var heartbeatService = new HeartbeatService(
            _jobId,
            _workerId,
            _jobRepositoryMock.Object,
            heartbeatInterval,
            maxFailures,
            _loggerMock.Object);

        var taskCancellationSource = new CancellationTokenSource();

        // Act
        heartbeatService.Start(taskCancellationSource);

        // Wait for multiple heartbeats (first fails, subsequent succeed)
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        await heartbeatService.StopAsync();

        // Assert - After a successful heartbeat, counter should be reset to 0
        Assert.Equal(
            0,
            heartbeatService.ConsecutiveFailureCount);

        Assert.False(
            heartbeatService.WasAbandoned,
            "Job should not be abandoned when successful heartbeat resets counter");
    }

    #endregion

    #region Additional Validation Tests

    /// <summary>
    /// Verify HeartbeatService validates constructor parameters.
    /// </summary>
    [Fact]
    public void Constructor_ThrowsOnInvalidParameters()
    {
        // Null repository
        Assert.Throws<ArgumentNullException>(() =>
            new HeartbeatService(
                _jobId,
                _workerId,
                null!,
                TimeSpan.FromSeconds(30),
                3,
                _loggerMock.Object));

        // Null logger
        Assert.Throws<ArgumentNullException>(() =>
            new HeartbeatService(
                _jobId,
                _workerId,
                _jobRepositoryMock.Object,
                TimeSpan.FromSeconds(30),
                3,
                null!));

        // Empty worker ID
        Assert.Throws<ArgumentException>(() =>
            new HeartbeatService(
                _jobId,
                "",
                _jobRepositoryMock.Object,
                TimeSpan.FromSeconds(30),
                3,
                _loggerMock.Object));

        // Zero heartbeat interval
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HeartbeatService(
                _jobId,
                _workerId,
                _jobRepositoryMock.Object,
                TimeSpan.Zero,
                3,
                _loggerMock.Object));

        // Zero max failures
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HeartbeatService(
                _jobId,
                _workerId,
                _jobRepositoryMock.Object,
                TimeSpan.FromSeconds(30),
                0,
                _loggerMock.Object));
    }

    /// <summary>
    /// Verify HeartbeatService cannot be started twice.
    /// </summary>
    [Fact]
    public async Task Start_ThrowsIfAlreadyRunning()
    {
        // Arrange
        _jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(_jobId, _workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await using var heartbeatService = new HeartbeatService(
            _jobId,
            _workerId,
            _jobRepositoryMock.Object,
            TimeSpan.FromSeconds(30),
            3,
            _loggerMock.Object);

        var taskCancellationSource1 = new CancellationTokenSource();
        var taskCancellationSource2 = new CancellationTokenSource();

        // Act
        heartbeatService.Start(taskCancellationSource1);

        // Assert
        Assert.Throws<InvalidOperationException>(() =>
            heartbeatService.Start(taskCancellationSource2));

        await heartbeatService.StopAsync();
    }

    /// <summary>
    /// Verify IHeartbeatService interface has all required members.
    /// </summary>
    [Fact]
    public void IHeartbeatService_HasAllRequiredMembers()
    {
        // Arrange
        var mock = new Mock<IHeartbeatService>();

        // Assert - All members exist and can be setup
        mock.SetupGet(h => h.ConsecutiveFailureCount).Returns(0);
        mock.SetupGet(h => h.WasAbandoned).Returns(false);
        mock.Setup(h => h.Start(It.IsAny<CancellationTokenSource>()));
        mock.Setup(h => h.StopAsync()).Returns(Task.CompletedTask);
        mock.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        Assert.NotNull(mock.Object);
    }

    #endregion
}
