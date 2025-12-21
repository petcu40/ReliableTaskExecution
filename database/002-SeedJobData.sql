-- ============================================================
-- Script: 002-SeedJobData.sql
-- Purpose: Seeds the Jobs table with initial job data
-- Database: TaskExecutionDb (Azure SQL Database)
--
-- This script is idempotent - it can be run multiple times
-- without creating duplicate data.
-- ============================================================

-- Check if the SampleTask job already exists
IF NOT EXISTS (SELECT 1 FROM dbo.Jobs WHERE JobName = N'SampleTask')
BEGIN
    -- Insert the sample job for the proof-of-concept
    INSERT INTO dbo.Jobs
    (
        Id,
        JobName,
        NextRunTime,
        LastRunTime,
        LockedBy,
        LockedAt,
        LockTimeoutMinutes,
        IntervalMinutes
    )
    VALUES
    (
        NEWID(),                    -- Id: Generate a new unique identifier
        N'SampleTask',              -- JobName: Name for the sample task
        GETUTCDATE(),               -- NextRunTime: Set to current UTC time (immediately available)
        NULL,                       -- LastRunTime: NULL since never executed
        NULL,                       -- LockedBy: NULL (available for execution)
        NULL,                       -- LockedAt: NULL (not locked)
        2,                          -- LockTimeoutMinutes: 2 minutes before lock considered stale
        10                          -- IntervalMinutes: Run every 10 minutes
    );

    PRINT 'SampleTask job created successfully.';
END
ELSE
BEGIN
    PRINT 'SampleTask job already exists - skipping insert.';
END
GO

-- Verify the seed data
SELECT
    Id,
    JobName,
    NextRunTime,
    LastRunTime,
    LockedBy,
    LockedAt,
    LockTimeoutMinutes,
    IntervalMinutes
FROM dbo.Jobs
WHERE JobName = N'SampleTask';
GO
