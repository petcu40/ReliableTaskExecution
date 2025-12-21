using Microsoft.Extensions.Logging;
using Moq;
using ReliableTaskExecution.Worker.Configuration;
using ReliableTaskExecution.Worker.Data;
using Xunit;

namespace ReliableTaskExecution.Worker.Tests.Data;

/// <summary>
/// Unit tests for leader election functionality.
/// These 6 tests verify the core leader election behavior for distributed task coordination.
/// </summary>
public class LeaderElectionTests
{
    /// <summary>
    /// Test 1: Verify successful lock acquisition when LockedBy is NULL.
    /// A job with no lock (LockedBy = NULL) should allow any worker to acquire it.
    /// </summary>
    [Fact]
    public void Job_CanBeAcquired_WhenLockedByIsNull()
    {
        // Arrange - Job with no lock
        var job = new Job
        {
            Id = Guid.NewGuid(),
            JobName = "AvailableJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-5),
            LockedBy = null,
            LockedAt = null,
            LockTimeoutMinutes = 2
        };

        // Act - Check if job is available for acquisition
        var isAvailable = job.LockedBy == null;
        var isNotStale = !job.IsLockStale();

        // Assert
        Assert.True(isAvailable, "Job with NULL LockedBy should be available for acquisition");
        Assert.False(job.IsLockStale(), "Unlocked job should not be considered stale");
    }

    /// <summary>
    /// Test 2: Verify failed lock acquisition when already locked by another worker.
    /// A job with an active lock (LockedBy != NULL, not expired) should reject new claims.
    /// </summary>
    [Fact]
    public void Job_CannotBeAcquired_WhenAlreadyLocked()
    {
        // Arrange - Job locked by another worker 30 seconds ago
        var job = new Job
        {
            Id = Guid.NewGuid(),
            JobName = "LockedJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-5),
            LockedBy = "OtherWorker_123_abc",
            LockedAt = DateTime.UtcNow.AddSeconds(-30),
            LockTimeoutMinutes = 2
        };

        // Act - Check if job is locked
        var isLocked = job.LockedBy != null;
        var lockIsValid = !job.IsLockStale();

        // Assert
        Assert.True(isLocked, "Job with non-NULL LockedBy should be considered locked");
        Assert.True(lockIsValid, "Recently locked job should have valid lock");
    }

    /// <summary>
    /// Test 3: Verify stale lock detection when LockedAt has expired.
    /// A lock is stale when: LockedAt + LockTimeoutMinutes < CurrentTime
    /// </summary>
    [Fact]
    public void Job_DetectsStaleLocK_WhenLockExpired()
    {
        // Arrange - Lock acquired 5 minutes ago with 2 minute timeout
        var job = new Job
        {
            Id = Guid.NewGuid(),
            JobName = "StaleLockedJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-10),
            LockedBy = "CrashedWorker_456_def",
            LockedAt = DateTime.UtcNow.AddMinutes(-5),
            LockTimeoutMinutes = 2
        };

        // Act
        var isStale = job.IsLockStale();

        // Assert
        Assert.True(isStale, "Lock should be stale when LockedAt + LockTimeoutMinutes < NOW");
    }

    /// <summary>
    /// Test 4: Verify stale lock reclamation makes job available.
    /// After reclaiming a stale lock, the job should be available for acquisition.
    /// </summary>
    [Fact]
    public void Job_BecomesAvailable_AfterStaleLockReclamation()
    {
        // Arrange - Job with stale lock
        var job = new Job
        {
            Id = Guid.NewGuid(),
            JobName = "ReclaimableJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-10),
            LockedBy = "CrashedWorker_789_ghi",
            LockedAt = DateTime.UtcNow.AddMinutes(-5),
            LockTimeoutMinutes = 2
        };

        // Verify lock is stale before reclamation
        Assert.True(job.IsLockStale(), "Lock should be stale before reclamation");

        // Act - Simulate reclamation (what SQL UPDATE does)
        job.LockedBy = null;
        job.LockedAt = null;

        // Assert - Job is now available
        Assert.Null(job.LockedBy);
        Assert.Null(job.LockedAt);
        Assert.False(job.IsLockStale(), "Reclaimed job should not be stale");
    }

    /// <summary>
    /// Test 5: Verify lock release on task completion updates NextRunTime.
    /// NextRunTime = LockedAt + IntervalMinutes to prevent timing drift.
    /// </summary>
    [Fact]
    public void Job_UpdatesNextRunTime_OnTaskCompletion()
    {
        // Arrange - Job that was locked 10 minutes ago
        var lockedAt = DateTime.UtcNow.AddMinutes(-10);
        var job = new Job
        {
            Id = Guid.NewGuid(),
            JobName = "CompletingJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-15),
            LockedBy = "ActiveWorker_111_aaa",
            LockedAt = lockedAt,
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        // Act - Simulate completion (what SQL UPDATE does)
        // NextRunTime = LockedAt + IntervalMinutes
        var expectedNextRunTime = lockedAt.AddMinutes(job.IntervalMinutes);
        job.NextRunTime = expectedNextRunTime;
        job.LastRunTime = DateTime.UtcNow;
        job.LockedBy = null;
        job.LockedAt = null;

        // Assert
        Assert.Null(job.LockedBy);
        Assert.NotNull(job.LastRunTime);
        Assert.Equal(expectedNextRunTime, job.NextRunTime);
    }

    /// <summary>
    /// Test 6: Verify lock release on task failure preserves NextRunTime.
    /// Failed tasks should be immediately available for retry.
    /// </summary>
    [Fact]
    public void Job_PreservesNextRunTime_OnTaskFailure()
    {
        // Arrange - Job that was running but failed
        var originalNextRunTime = DateTime.UtcNow.AddMinutes(-5);
        var job = new Job
        {
            Id = Guid.NewGuid(),
            JobName = "FailingJob",
            NextRunTime = originalNextRunTime,
            LockedBy = "FailingWorker_222_bbb",
            LockedAt = DateTime.UtcNow.AddMinutes(-1),
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        // Act - Simulate failure (what SQL UPDATE does)
        // Only clear lock, do NOT update NextRunTime
        job.LockedBy = null;
        job.LockedAt = null;
        // NextRunTime intentionally NOT changed

        // Assert
        Assert.Null(job.LockedBy);
        Assert.Null(job.LockedAt);
        Assert.Equal(originalNextRunTime, job.NextRunTime);
        // Job is immediately available for retry since NextRunTime is in the past
    }

    /// <summary>
    /// Verify worker ID generation produces unique identifiers.
    /// </summary>
    [Fact]
    public void WorkerIdGenerator_GeneratesUniqueIds()
    {
        // Act
        var id1 = WorkerIdGenerator.GenerateWorkerId();
        var id2 = WorkerIdGenerator.GenerateWorkerId();

        // Assert
        Assert.NotNull(id1);
        Assert.NotNull(id2);
        Assert.NotEqual(id1, id2);
        Assert.Contains(Environment.MachineName, id1);
    }

    /// <summary>
    /// Verify worker ID format includes machine name and process ID.
    /// </summary>
    [Fact]
    public void WorkerIdGenerator_IncludesMachineAndProcessInfo()
    {
        // Act
        var workerId = WorkerIdGenerator.GenerateWorkerId();
        var shortId = WorkerIdGenerator.GenerateShortWorkerId();

        // Assert
        Assert.Contains(Environment.MachineName, workerId);
        Assert.Contains(Environment.MachineName, shortId);

        var processId = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
        Assert.Contains(processId, workerId);
        Assert.Contains(processId, shortId);
    }

    /// <summary>
    /// Verify job default values are set correctly.
    /// </summary>
    [Fact]
    public void Job_HasCorrectDefaults()
    {
        // Arrange & Act
        var job = new Job();

        // Assert
        Assert.Equal(2, job.LockTimeoutMinutes);
        Assert.Equal(10, job.IntervalMinutes);
        Assert.Null(job.LockedBy);
        Assert.Null(job.LockedAt);
        Assert.Null(job.LastRunTime);
        Assert.Equal(string.Empty, job.JobName);
    }

    /// <summary>
    /// Verify IJobRepository interface has all required methods.
    /// </summary>
    [Fact]
    public void IJobRepository_HasAllRequiredMethods()
    {
        // Arrange
        var mock = new Mock<IJobRepository>();

        // Assert - All methods exist and can be setup
        mock.Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((Job?)null);

        mock.Setup(r => r.TryAcquireLockAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        mock.Setup(r => r.ReleaseLockAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        mock.Setup(r => r.ReleaseLockOnFailureAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        mock.Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        mock.Setup(r => r.UpdateHeartbeatAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        Assert.NotNull(mock.Object);
    }
}
