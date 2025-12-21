using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Polly.Registry;
using ReliableTaskExecution.Worker.Configuration;
using ReliableTaskExecution.Worker.Data;
using ReliableTaskExecution.Worker.Resilience;
using ReliableTaskExecution.Worker.Services;
using Xunit;

namespace ReliableTaskExecution.Worker.Tests.Integration;

/// <summary>
/// Integration tests for the distributed task execution system.
/// These 8 tests cover critical integration paths, multi-worker coordination scenarios,
/// and end-to-end workflows that were not covered by the existing unit tests.
/// </summary>
public class IntegrationTests
{
    #region Test 1: Full Polling Cycle with Mocked Database

    /// <summary>
    /// Integration test 1: Full polling cycle with mocked database.
    /// Verifies that a complete polling cycle works end-to-end:
    /// reclaim stale locks -> get job -> acquire lock -> heartbeat -> execute -> complete.
    /// </summary>
    [Fact]
    public async Task FullPollingCycle_WithMockedDatabase_ExecutesCompleteCycle()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var workerId = "IntegrationTest_Worker_001";
        var job = new Job
        {
            Id = jobId,
            JobName = "IntegrationTestJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-1),
            LockedBy = null,
            LockedAt = null,
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        var jobRepositoryMock = new Mock<IJobRepository>();
        var taskExecutorMock = new Mock<ITaskExecutor>();
        var workerLoggerMock = new Mock<ILogger<TaskExecutionWorker>>();
        var heartbeatLoggerMock = new Mock<ILogger<HeartbeatService>>();

        var executionSequence = new List<string>();

        // Setup the complete execution flow
        jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0)
            .Callback(() => executionSequence.Add("ReclaimStaleLocks"));

        var getJobCallCount = 0;
        jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                getJobCallCount++;
                executionSequence.Add("GetAvailableJob");
                // Return job on first call, null after that (job will be locked/completed)
                return getJobCallCount == 1 ? job : null;
            });

        jobRepositoryMock
            .Setup(r => r.TryAcquireLockAsync(jobId, workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => executionSequence.Add("TryAcquireLock"));

        jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(jobId, workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => executionSequence.Add("UpdateHeartbeat"));

        jobRepositoryMock
            .Setup(r => r.CompleteTaskAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => executionSequence.Add("CompleteTask"));

        var taskExecuted = false;
        taskExecutorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                executionSequence.Add("TaskExecute");
                await Task.Delay(10, ct); // Quick execution
                taskExecuted = true;
            });

        var worker = new TaskExecutionWorker(
            jobRepositoryMock.Object,
            taskExecutorMock.Object,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(50),
            3,
            workerId,
            workerLoggerMock.Object,
            heartbeatLoggerMock.Object);

        using var cts = new CancellationTokenSource();

        // Act - Run for enough time to complete one full cycle
        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(400));
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert - Verify the complete execution sequence
        Assert.True(taskExecuted, "Task should have been executed");
        Assert.Contains("ReclaimStaleLocks", executionSequence);
        Assert.Contains("GetAvailableJob", executionSequence);
        Assert.Contains("TryAcquireLock", executionSequence);
        Assert.Contains("TaskExecute", executionSequence);
        Assert.Contains("CompleteTask", executionSequence);

        // Verify correct order: ReclaimStaleLocks -> GetAvailableJob -> TryAcquireLock -> TaskExecute -> CompleteTask
        var reclaimIndex = executionSequence.IndexOf("ReclaimStaleLocks");
        var getJobIndex = executionSequence.IndexOf("GetAvailableJob");
        var lockIndex = executionSequence.IndexOf("TryAcquireLock");
        var executeIndex = executionSequence.IndexOf("TaskExecute");
        var completeIndex = executionSequence.IndexOf("CompleteTask");

        Assert.True(reclaimIndex < getJobIndex, "ReclaimStaleLocks should happen before GetAvailableJob");
        Assert.True(getJobIndex < lockIndex, "GetAvailableJob should happen before TryAcquireLock");
        Assert.True(lockIndex < executeIndex, "TryAcquireLock should happen before TaskExecute");
        Assert.True(executeIndex < completeIndex, "TaskExecute should happen before CompleteTask");
    }

    #endregion

    #region Test 2: Lock Contention with Simulated Concurrent Workers

    /// <summary>
    /// Integration test 2: Lock contention with simulated concurrent workers.
    /// Verifies that only one worker can acquire a lock when multiple workers
    /// attempt to claim the same job simultaneously.
    /// </summary>
    [Fact]
    public async Task LockContention_WithSimulatedConcurrentWorkers_OnlyOneAcquiresLock()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var job = new Job
        {
            Id = jobId,
            JobName = "ContendedJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-1),
            LockedBy = null,
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        var lockAcquisitionCount = 0;
        var lockHoldingWorkerId = string.Empty;
        var lockAcquisitionLock = new object();

        // Create a shared mock repository that simulates atomic lock behavior
        var sharedJobRepositoryMock = new Mock<IJobRepository>();

        sharedJobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        sharedJobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                lock (lockAcquisitionLock)
                {
                    return string.IsNullOrEmpty(lockHoldingWorkerId) ? job : null;
                }
            });

        sharedJobRepositoryMock
            .Setup(r => r.TryAcquireLockAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string workerId, CancellationToken ct) =>
            {
                lock (lockAcquisitionLock)
                {
                    // Simulate atomic lock acquisition - only one can succeed
                    if (string.IsNullOrEmpty(lockHoldingWorkerId))
                    {
                        lockHoldingWorkerId = workerId;
                        Interlocked.Increment(ref lockAcquisitionCount);
                        return true;
                    }
                    return false;
                }
            });

        sharedJobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        sharedJobRepositoryMock
            .Setup(r => r.CompleteTaskAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var taskExecutorMock = new Mock<ITaskExecutor>();
        var executionCount = 0;
        taskExecutorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                Interlocked.Increment(ref executionCount);
                await Task.Delay(50, ct);
            });

        var workerLoggerMock = new Mock<ILogger<TaskExecutionWorker>>();
        var heartbeatLoggerMock = new Mock<ILogger<HeartbeatService>>();

        // Create 3 simulated workers
        var workers = new List<TaskExecutionWorker>();
        for (int i = 0; i < 3; i++)
        {
            workers.Add(new TaskExecutionWorker(
                sharedJobRepositoryMock.Object,
                taskExecutorMock.Object,
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromSeconds(30),
                3,
                $"Worker_{i}",
                workerLoggerMock.Object,
                heartbeatLoggerMock.Object));
        }

        using var cts = new CancellationTokenSource();

        // Act - Start all workers simultaneously
        var workerTasks = workers.Select(w => w.StartAsync(cts.Token)).ToList();

        // Let them compete for the lock
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await cts.CancelAsync();
        await Task.WhenAll(workers.Select(w => w.StopAsync(CancellationToken.None)));

        // Assert - Only one worker should have acquired the lock
        Assert.Equal(1, lockAcquisitionCount);
        Assert.False(string.IsNullOrEmpty(lockHoldingWorkerId), "One worker should hold the lock");
        Assert.Equal(1, executionCount);
    }

    #endregion

    #region Test 3: Stale Lock Reclamation and Re-execution

    /// <summary>
    /// Integration test 3: Stale lock reclamation and re-execution.
    /// Verifies that when a lock becomes stale (worker crashed), another worker
    /// can reclaim it and execute the job.
    /// </summary>
    [Fact]
    public async Task StaleLockReclamation_AndReexecution_WorksCorrectly()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var staleLockClearedCount = 0;
        var lockAcquiredByNewWorker = false;
        var taskExecutedAfterReclamation = false;

        // Simulate a job with a stale lock (locked 5 minutes ago, 2 minute timeout)
        var job = new Job
        {
            Id = jobId,
            JobName = "StaleLockedJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-10),
            LockedBy = "CrashedWorker_123",
            LockedAt = DateTime.UtcNow.AddMinutes(-5), // Lock expired 3 minutes ago
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        var jobRepositoryMock = new Mock<IJobRepository>();

        // First call: reclaim stale locks (returns 1)
        // Subsequent calls: no stale locks
        var reclaimCallCount = 0;
        jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                reclaimCallCount++;
                if (reclaimCallCount == 1 && job.LockedBy != null && job.IsLockStale())
                {
                    // Simulate reclamation
                    job.LockedBy = null;
                    job.LockedAt = null;
                    staleLockClearedCount++;
                    return 1;
                }
                return 0;
            });

        jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => job.LockedBy == null && !lockAcquiredByNewWorker ? job : null);

        jobRepositoryMock
            .Setup(r => r.TryAcquireLockAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string workerId, CancellationToken ct) =>
            {
                if (job.LockedBy == null)
                {
                    job.LockedBy = workerId;
                    job.LockedAt = DateTime.UtcNow;
                    lockAcquiredByNewWorker = true;
                    return true;
                }
                return false;
            });

        jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(jobId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        jobRepositoryMock
            .Setup(r => r.CompleteTaskAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var taskExecutorMock = new Mock<ITaskExecutor>();
        taskExecutorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(10, ct);
                taskExecutedAfterReclamation = true;
            });

        var workerLoggerMock = new Mock<ILogger<TaskExecutionWorker>>();
        var heartbeatLoggerMock = new Mock<ILogger<HeartbeatService>>();

        var worker = new TaskExecutionWorker(
            jobRepositoryMock.Object,
            taskExecutorMock.Object,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(30),
            3,
            "RecoveryWorker_456",
            workerLoggerMock.Object,
            heartbeatLoggerMock.Object);

        using var cts = new CancellationTokenSource();

        // Act
        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, staleLockClearedCount);
        Assert.True(lockAcquiredByNewWorker, "New worker should have acquired the lock after reclamation");
        Assert.True(taskExecutedAfterReclamation, "Task should have been executed after lock reclamation");
    }

    #endregion

    #region Test 4: Graceful Shutdown Mid-Task

    /// <summary>
    /// Integration test 4: Graceful shutdown mid-task.
    /// Verifies that when a shutdown signal is received during task execution,
    /// the worker stops gracefully and releases the lock.
    /// </summary>
    [Fact]
    public async Task GracefulShutdown_MidTask_ReleasesLockAndStops()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var workerId = "ShutdownTest_Worker";
        var lockReleased = false;
        var taskStarted = false;
        var taskCompleted = false;

        var job = new Job
        {
            Id = jobId,
            JobName = "LongRunningJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-1),
            LockedBy = null,
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        var jobRepositoryMock = new Mock<IJobRepository>();

        jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => !taskStarted ? job : null);

        jobRepositoryMock
            .Setup(r => r.TryAcquireLockAsync(jobId, workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(jobId, workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Track lock release on failure (graceful shutdown uses FailTaskAsync)
        jobRepositoryMock
            .Setup(r => r.FailTaskAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                lockReleased = true;
                return true;
            });

        var taskExecutorMock = new Mock<ITaskExecutor>();
        var taskCancellationRequested = false;
        taskExecutorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                taskStarted = true;
                try
                {
                    // Long-running task that respects cancellation
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    taskCompleted = true;
                }
                catch (OperationCanceledException)
                {
                    taskCancellationRequested = true;
                    throw;
                }
            });

        var workerLoggerMock = new Mock<ILogger<TaskExecutionWorker>>();
        var heartbeatLoggerMock = new Mock<ILogger<HeartbeatService>>();

        var worker = new TaskExecutionWorker(
            jobRepositoryMock.Object,
            taskExecutorMock.Object,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(30),
            3,
            workerId,
            workerLoggerMock.Object,
            heartbeatLoggerMock.Object);

        using var cts = new CancellationTokenSource();

        // Act - Start worker and let task begin, then request shutdown
        var workerTask = worker.StartAsync(cts.Token);

        // Wait for task to start
        while (!taskStarted)
        {
            await Task.Delay(10);
        }

        // Request graceful shutdown
        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(taskStarted, "Task should have started");
        Assert.False(taskCompleted, "Task should not have completed (was cancelled)");
        Assert.True(taskCancellationRequested, "Task cancellation should have been requested");
        Assert.True(lockReleased, "Lock should have been released on shutdown");
    }

    #endregion

    #region Test 5: Polly Policy Integration

    /// <summary>
    /// Integration test 5: Polly policy integration with service registration.
    /// Verifies that Polly policies are created correctly and can be used
    /// in a dependency injection context similar to how they are used in Program.cs.
    /// </summary>
    [Fact]
    public void PollyPolicyIntegration_WithServiceRegistration_WorksCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Register the policy registry similar to how Program.cs does it
        services.AddSingleton<IPolicyRegistry<string>>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var policyLogger = loggerFactory.CreateLogger("SqlResiliencePolicies");

            var localRegistry = new PolicyRegistry
            {
                [Program.SqlRetryPolicyKey] = SqlResiliencePolicies.CreateRetryPolicy(policyLogger),
                [Program.SqlCircuitBreakerPolicyKey] = SqlResiliencePolicies.CreateCircuitBreakerPolicy(policyLogger),
                [Program.SqlCombinedPolicyKey] = SqlResiliencePolicies.CreateCombinedPolicy(policyLogger)
            };

            return localRegistry;
        });

        services.AddSingleton<IReadOnlyPolicyRegistry<string>>(sp =>
            sp.GetRequiredService<IPolicyRegistry<string>>());

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var policyRegistry = serviceProvider.GetRequiredService<IPolicyRegistry<string>>();
        var readOnlyRegistry = serviceProvider.GetRequiredService<IReadOnlyPolicyRegistry<string>>();

        // Assert
        Assert.NotNull(policyRegistry);
        Assert.NotNull(readOnlyRegistry);

        // Verify all policies are registered
        Assert.True(policyRegistry.ContainsKey(Program.SqlRetryPolicyKey), "Retry policy should be registered");
        Assert.True(policyRegistry.ContainsKey(Program.SqlCircuitBreakerPolicyKey), "Circuit breaker policy should be registered");
        Assert.True(policyRegistry.ContainsKey(Program.SqlCombinedPolicyKey), "Combined policy should be registered");

        // Verify policies can be retrieved
        var retryPolicy = policyRegistry[Program.SqlRetryPolicyKey];
        var circuitBreakerPolicy = policyRegistry[Program.SqlCircuitBreakerPolicyKey];
        var combinedPolicy = policyRegistry[Program.SqlCombinedPolicyKey];

        Assert.NotNull(retryPolicy);
        Assert.NotNull(circuitBreakerPolicy);
        Assert.NotNull(combinedPolicy);
    }

    /// <summary>
    /// Additional test: Verify Polly policy creation with proper logger.
    /// </summary>
    [Fact]
    public void PollyPolicyCreation_WithLogger_CreatesValidPolicies()
    {
        // Arrange
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        // Act
        var retryPolicy = SqlResiliencePolicies.CreateRetryPolicy(loggerMock.Object);
        var circuitBreakerPolicy = SqlResiliencePolicies.CreateCircuitBreakerPolicy(loggerMock.Object);
        var combinedPolicy = SqlResiliencePolicies.CreateCombinedPolicy(loggerMock.Object);

        // Assert
        Assert.NotNull(retryPolicy);
        Assert.NotNull(circuitBreakerPolicy);
        Assert.NotNull(combinedPolicy);
    }

    #endregion

    #region Test 6: Configuration Loading from appsettings.json

    /// <summary>
    /// Integration test 6: Configuration loading from appsettings.json.
    /// Verifies that TaskExecutionOptions are correctly bound from configuration.
    /// </summary>
    [Fact]
    public void ConfigurationLoading_FromAppsettingsJson_BindsCorrectly()
    {
        // Arrange
        var configurationData = new Dictionary<string, string?>
        {
            { "TaskExecution:PollingIntervalSeconds", "120" },
            { "TaskExecution:TaskDurationSeconds", "10" },
            { "TaskExecution:HeartbeatIntervalSeconds", "45" },
            { "TaskExecution:LockTimeoutMinutes", "5" },
            { "TaskExecution:MaxConsecutiveHeartbeatFailures", "5" }
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationData)
            .Build();

        var services = new ServiceCollection();
        services.Configure<TaskExecutionOptions>(configuration.GetSection(TaskExecutionOptions.SectionName));

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetRequiredService<IOptions<TaskExecutionOptions>>();
        var taskExecutionOptions = options.Value;

        // Assert
        Assert.Equal(120, taskExecutionOptions.PollingIntervalSeconds);
        Assert.Equal(10, taskExecutionOptions.TaskDurationSeconds);
        Assert.Equal(45, taskExecutionOptions.HeartbeatIntervalSeconds);
        Assert.Equal(5, taskExecutionOptions.LockTimeoutMinutes);
        Assert.Equal(5, taskExecutionOptions.MaxConsecutiveHeartbeatFailures);

        // Verify TimeSpan conversions
        Assert.Equal(TimeSpan.FromSeconds(120), taskExecutionOptions.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), taskExecutionOptions.TaskDuration);
        Assert.Equal(TimeSpan.FromSeconds(45), taskExecutionOptions.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), taskExecutionOptions.LockTimeout);
    }

    /// <summary>
    /// Additional test: Verify default values when configuration is not provided.
    /// </summary>
    [Fact]
    public void ConfigurationLoading_WithoutValues_UsesDefaults()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.Configure<TaskExecutionOptions>(configuration.GetSection(TaskExecutionOptions.SectionName));

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetRequiredService<IOptions<TaskExecutionOptions>>();
        var taskExecutionOptions = options.Value;

        // Assert - Verify default values from TaskExecutionOptions class
        Assert.Equal(60, taskExecutionOptions.PollingIntervalSeconds);
        Assert.Equal(5, taskExecutionOptions.TaskDurationSeconds);
        Assert.Equal(30, taskExecutionOptions.HeartbeatIntervalSeconds);
        Assert.Equal(2, taskExecutionOptions.LockTimeoutMinutes);
        Assert.Equal(3, taskExecutionOptions.MaxConsecutiveHeartbeatFailures);
    }

    #endregion

    #region Test 7: Heartbeat Abandonment Prevents Multi-Master

    /// <summary>
    /// Integration test 7: Heartbeat abandonment prevents multi-master situations.
    /// Verifies that when heartbeats fail consecutively, the job is abandoned
    /// and task execution is cancelled to prevent two workers executing the same job.
    /// </summary>
    [Fact]
    public async Task HeartbeatAbandonment_PreventsMultiMaster()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var workerId = "HeartbeatTest_Worker";
        var taskCancelled = false;
        var lockReleasedOnAbandonment = false;

        var job = new Job
        {
            Id = jobId,
            JobName = "HeartbeatTestJob",
            NextRunTime = DateTime.UtcNow.AddMinutes(-1),
            LockedBy = null,
            LockTimeoutMinutes = 2,
            IntervalMinutes = 10
        };

        var jobRepositoryMock = new Mock<IJobRepository>();

        jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var getJobCalled = false;
        jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (!getJobCalled)
                {
                    getJobCalled = true;
                    return job;
                }
                return null;
            });

        jobRepositoryMock
            .Setup(r => r.TryAcquireLockAsync(jobId, workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Heartbeat always fails - simulates network partition or database unavailability
        jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(jobId, workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Track lock release on failure (abandonment)
        jobRepositoryMock
            .Setup(r => r.ReleaseLockOnFailureAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                lockReleasedOnAbandonment = true;
                return true;
            });

        var taskExecutorMock = new Mock<ITaskExecutor>();
        taskExecutorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                try
                {
                    // Long task that should be cancelled when heartbeat fails
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                }
                catch (OperationCanceledException)
                {
                    taskCancelled = true;
                    throw;
                }
            });

        var workerLoggerMock = new Mock<ILogger<TaskExecutionWorker>>();
        var heartbeatLoggerMock = new Mock<ILogger<HeartbeatService>>();

        var worker = new TaskExecutionWorker(
            jobRepositoryMock.Object,
            taskExecutorMock.Object,
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(20), // Short heartbeat interval for faster test
            3, // Max consecutive failures
            workerId,
            workerLoggerMock.Object,
            heartbeatLoggerMock.Object);

        using var cts = new CancellationTokenSource();

        // Act - Run long enough for 3 consecutive heartbeat failures
        var workerTask = worker.StartAsync(cts.Token);

        // Wait for heartbeat failures to trigger abandonment
        // 3 failures * 20ms interval = 60ms + buffer
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(taskCancelled, "Task should have been cancelled due to heartbeat failure");
        Assert.True(lockReleasedOnAbandonment, "Lock should have been released on abandonment");
    }

    #endregion

    #region Test 8: Multiple Polling Cycles with Task Execution

    /// <summary>
    /// Integration test 8: End-to-end test with multiple polling cycles and task execution.
    /// Verifies that the worker can handle multiple complete cycles of:
    /// poll -> execute -> complete -> poll again.
    /// </summary>
    [Fact]
    public async Task MultiplePollingCycles_WithTaskExecution_CompletesSuccessfully()
    {
        // Arrange
        var workerId = "MultiCycle_Worker";
        var tasksExecuted = 0;
        var tasksCompleted = 0;
        var pollingCycles = 0;
        var maxTasksToExecute = 3;

        // Create jobs that will be returned one at a time
        var jobs = Enumerable.Range(1, maxTasksToExecute)
            .Select(i => new Job
            {
                Id = Guid.NewGuid(),
                JobName = $"MultiCycleJob_{i}",
                NextRunTime = DateTime.UtcNow.AddMinutes(-1),
                LockedBy = null,
                LockTimeoutMinutes = 2,
                IntervalMinutes = 10
            })
            .ToList();

        var currentJobIndex = 0;
        var currentJobLocked = false;

        var jobRepositoryMock = new Mock<IJobRepository>();

        jobRepositoryMock
            .Setup(r => r.ReclaimStaleLocksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollingCycles++;
                return 0;
            });

        jobRepositoryMock
            .Setup(r => r.GetAvailableJobAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                // Return next job if available and not locked
                if (currentJobIndex < maxTasksToExecute && !currentJobLocked)
                {
                    return jobs[currentJobIndex];
                }
                return null;
            });

        jobRepositoryMock
            .Setup(r => r.TryAcquireLockAsync(It.IsAny<Guid>(), workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string wid, CancellationToken ct) =>
            {
                if (currentJobIndex < maxTasksToExecute && !currentJobLocked)
                {
                    currentJobLocked = true;
                    return true;
                }
                return false;
            });

        jobRepositoryMock
            .Setup(r => r.UpdateHeartbeatAsync(It.IsAny<Guid>(), workerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        jobRepositoryMock
            .Setup(r => r.CompleteTaskAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                tasksCompleted++;
                currentJobLocked = false;
                currentJobIndex++;
                return true;
            });

        var taskExecutorMock = new Mock<ITaskExecutor>();
        taskExecutorMock
            .Setup(e => e.ExecuteAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                Interlocked.Increment(ref tasksExecuted);
                await Task.Delay(10, ct); // Quick execution
            });

        var workerLoggerMock = new Mock<ILogger<TaskExecutionWorker>>();
        var heartbeatLoggerMock = new Mock<ILogger<HeartbeatService>>();

        var worker = new TaskExecutionWorker(
            jobRepositoryMock.Object,
            taskExecutorMock.Object,
            TimeSpan.FromMilliseconds(30), // Short polling interval for fast test
            TimeSpan.FromMilliseconds(20),
            3,
            workerId,
            workerLoggerMock.Object,
            heartbeatLoggerMock.Object);

        using var cts = new CancellationTokenSource();

        // Act - Run for enough time to complete all 3 jobs
        var workerTask = worker.StartAsync(cts.Token);

        // Wait for all tasks to complete (3 tasks * ~30ms execution + polling)
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(maxTasksToExecute, tasksExecuted);
        Assert.Equal(maxTasksToExecute, tasksCompleted);
        Assert.True(pollingCycles >= maxTasksToExecute, $"Should have at least {maxTasksToExecute} polling cycles, got {pollingCycles}");
    }

    #endregion
}
