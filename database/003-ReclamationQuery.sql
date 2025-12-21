-- ============================================================
-- Script: 003-ReclamationQuery.sql
-- Purpose: Demonstrates stale lock detection and reclamation
-- Database: TaskExecutionDb (Azure SQL Database)
--
-- This script contains the query patterns used by workers to
-- detect and reclaim locks from crashed or unresponsive workers.
-- ============================================================

-- ============================================================
-- QUERY 1: Detect Stale Locks (Read-Only)
--
-- Purpose: Find jobs with expired locks that can be reclaimed
--
-- Logic explanation:
--   A lock is considered stale when:
--   1. LockedBy IS NOT NULL (job is currently locked by some worker)
--   2. The lock has been held longer than LockTimeoutMinutes
--      - LockedAt + LockTimeoutMinutes < Current UTC Time
--      - This means the worker holding the lock has either:
--        a) Crashed without releasing the lock
--        b) Lost network connectivity
--        c) Failed to send heartbeats
-- ============================================================
SELECT
    Id,
    JobName,
    LockedBy,
    LockedAt,
    LockTimeoutMinutes,
    GETUTCDATE() AS CurrentUtcTime,
    DATEADD(MINUTE, LockTimeoutMinutes, LockedAt) AS LockExpiresAt,
    DATEDIFF(SECOND, DATEADD(MINUTE, LockTimeoutMinutes, LockedAt), GETUTCDATE()) AS SecondsOverdue
FROM dbo.Jobs
WHERE
    -- Job must be locked by some worker
    LockedBy IS NOT NULL

    -- Lock expiration check:
    -- DATEADD adds LockTimeoutMinutes to LockedAt to get expiration time
    -- If expiration time is in the past (< GETUTCDATE), lock is stale
    AND DATEADD(MINUTE, LockTimeoutMinutes, LockedAt) < GETUTCDATE();
GO

-- ============================================================
-- QUERY 2: Reclaim Stale Locks (Atomic UPDATE)
--
-- Purpose: Clear locks from jobs where the lock has expired
--
-- This should be run by workers BEFORE attempting to acquire
-- new locks. It ensures crashed workers don't indefinitely
-- block job execution.
--
-- The UPDATE is atomic - SQL Server's row-level locking ensures
-- that if multiple workers try to reclaim the same lock
-- simultaneously, only one will succeed.
--
-- Important: We only clear the lock fields (LockedBy, LockedAt).
-- We do NOT modify NextRunTime because:
--   1. The task did not complete successfully
--   2. The task should be retried as soon as possible
--   3. Another worker can immediately pick it up
-- ============================================================
UPDATE dbo.Jobs
SET
    -- Clear the worker ID to make job available
    LockedBy = NULL,

    -- Clear the lock timestamp
    LockedAt = NULL

    -- Note: We intentionally do NOT update these fields:
    -- - NextRunTime: Leave as-is so job is immediately available
    -- - LastRunTime: Leave as-is since task did not complete
WHERE
    -- Job must be locked by some worker
    LockedBy IS NOT NULL

    -- Lock must be expired (held longer than timeout)
    AND DATEADD(MINUTE, LockTimeoutMinutes, LockedAt) < GETUTCDATE();

-- @@ROWCOUNT indicates how many stale locks were reclaimed
SELECT @@ROWCOUNT AS StaleLockReclaimed;
GO

-- ============================================================
-- QUERY 3: Lock Acquisition Pattern (Atomic UPDATE WHERE)
--
-- Purpose: Attempt to claim an available job for execution
--
-- This pattern ensures exactly-once execution:
-- - Multiple workers may try to acquire the same job simultaneously
-- - The WHERE clause includes LockedBy IS NULL
-- - SQL Server's row-level locking ensures only ONE UPDATE succeeds
-- - Workers check @@ROWCOUNT to determine if they acquired the lock
--
-- Parameters (to be supplied by application):
--   @jobId - The ID of the job to acquire
--   @workerId - Unique identifier for this worker instance
-- ============================================================
-- Example (parameterized in application code):
-- DECLARE @jobId uniqueidentifier = '...' -- From GetAvailableJob query
-- DECLARE @workerId nvarchar(256) = 'MACHINE_1234_abc123'
--
-- UPDATE dbo.Jobs
-- SET
--     LockedBy = @workerId,
--     LockedAt = GETUTCDATE()
-- WHERE
--     Id = @jobId
--     AND LockedBy IS NULL  -- Critical: Only succeed if still available
--
-- IF @@ROWCOUNT = 1
--     -- Lock acquired successfully, proceed with task execution
-- ELSE
--     -- Another worker claimed the job first, skip this one
GO

-- ============================================================
-- QUERY 4: Heartbeat Update Pattern
--
-- Purpose: Extend the lock by updating LockedAt timestamp
--
-- Workers send heartbeats every 30 seconds while executing
-- a task. This extends the lock window and proves the worker
-- is still alive. The heartbeat also verifies the worker still
-- owns the lock (WHERE LockedBy = @workerId).
--
-- If heartbeat fails (@@ROWCOUNT = 0), it means:
--   - Another worker reclaimed the lock (stale lock scenario)
--   - The job was deleted or modified
-- In this case, the worker should stop executing and not
-- update the job status.
-- ============================================================
-- Example (parameterized in application code):
-- DECLARE @jobId uniqueidentifier = '...'
-- DECLARE @workerId nvarchar(256) = 'MACHINE_1234_abc123'
--
-- UPDATE dbo.Jobs
-- SET
--     LockedAt = GETUTCDATE()
-- WHERE
--     Id = @jobId
--     AND LockedBy = @workerId  -- Only succeed if we still own the lock
--
-- IF @@ROWCOUNT = 1
--     -- Heartbeat successful, continue execution
-- ELSE
--     -- Lost the lock! Stop execution immediately to prevent multi-master
GO

-- ============================================================
-- SUMMARY: Complete Polling Cycle Pattern
--
-- 1. RECLAIM: Run stale lock reclamation (Query 2)
-- 2. FIND: Query for available jobs (NextRunTime <= NOW, LockedBy IS NULL)
-- 3. ACQUIRE: Attempt atomic lock acquisition (Query 3)
-- 4. EXECUTE: If acquired, run task with periodic heartbeats (Query 4)
-- 5. COMPLETE: Update NextRunTime = LockedAt + IntervalMinutes, clear lock
-- 6. WAIT: Sleep for polling interval, repeat from step 1
-- ============================================================
