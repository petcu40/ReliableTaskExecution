-- ============================================================
-- Script: 001-CreateJobsTable.sql
-- Purpose: Creates the Jobs table for distributed task execution
-- Database: TaskExecutionDb (Azure SQL Database)
--
-- This table stores job definitions and their current execution
-- state, including lock information for leader election.
-- ============================================================

-- Drop table if exists (for rerunnable script)
IF OBJECT_ID('dbo.Jobs', 'U') IS NOT NULL
    DROP TABLE dbo.Jobs;
GO

-- Create the Jobs table
CREATE TABLE dbo.Jobs
(
    -- Primary key: Unique identifier for each job
    Id uniqueidentifier NOT NULL
        CONSTRAINT DF_Jobs_Id DEFAULT NEWID()
        CONSTRAINT PK_Jobs PRIMARY KEY CLUSTERED,

    -- Job name/identifier for human readability
    JobName nvarchar(256) NOT NULL,

    -- Scheduling: When this job should next be executed
    -- datetime2 provides sub-second precision (100 nanoseconds)
    NextRunTime datetime2 NOT NULL,

    -- Scheduling: When this job was last successfully completed
    LastRunTime datetime2 NULL,

    -- Lock fields for leader election and distributed coordination
    -- LockedBy: Worker ID that currently holds the lock (NULL = available)
    LockedBy nvarchar(256) NULL,

    -- LockedAt: When the lock was acquired (used for stale lock detection)
    LockedAt datetime2 NULL,

    -- Configuration: How long a lock can be held before considered stale
    -- Default 2 minutes allows for heartbeat failures before reclamation
    LockTimeoutMinutes int NOT NULL
        CONSTRAINT DF_Jobs_LockTimeoutMinutes DEFAULT 2,

    -- Configuration: Interval between task executions
    -- NextRunTime = LockedAt + IntervalMinutes (prevents timing drift)
    IntervalMinutes int NOT NULL
        CONSTRAINT DF_Jobs_IntervalMinutes DEFAULT 10
);
GO

-- ============================================================
-- Index: IX_Jobs_Polling
-- Purpose: Optimize polling queries for available jobs
--
-- Covers the WHERE clause pattern used when checking for jobs:
--   WHERE NextRunTime <= GETUTCDATE() AND LockedBy IS NULL
--
-- Also supports stale lock detection queries:
--   WHERE LockedBy IS NOT NULL
--     AND DATEADD(MINUTE, LockTimeoutMinutes, LockedAt) < GETUTCDATE()
-- ============================================================
CREATE NONCLUSTERED INDEX IX_Jobs_Polling
ON dbo.Jobs (NextRunTime, LockedBy)
INCLUDE (LockedAt, LockTimeoutMinutes);
GO

-- Add comments to table and columns for documentation
EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'Stores job definitions and execution state for distributed task coordination',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs';
GO

EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'Unique identifier for the job',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs',
    @level2type = N'COLUMN', @level2name = N'Id';
GO

EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'Human-readable name identifying the job',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs',
    @level2type = N'COLUMN', @level2name = N'JobName';
GO

EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'UTC time when this job should next be executed',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs',
    @level2type = N'COLUMN', @level2name = N'NextRunTime';
GO

EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'UTC time when this job was last successfully completed (NULL if never run)',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs',
    @level2type = N'COLUMN', @level2name = N'LastRunTime';
GO

EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'Worker ID holding the lock (NULL = job is available for execution)',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs',
    @level2type = N'COLUMN', @level2name = N'LockedBy';
GO

EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'UTC time when the lock was acquired (used for stale lock detection and NextRunTime calculation)',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs',
    @level2type = N'COLUMN', @level2name = N'LockedAt';
GO

EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'Minutes a lock can be held before considered stale (default: 2)',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs',
    @level2type = N'COLUMN', @level2name = N'LockTimeoutMinutes';
GO

EXEC sp_addextendedproperty
    @name = N'MS_Description',
    @value = N'Minutes between task executions, calculated from LockedAt to prevent drift (default: 10)',
    @level0type = N'SCHEMA', @level0name = N'dbo',
    @level1type = N'TABLE', @level1name = N'Jobs',
    @level2type = N'COLUMN', @level2name = N'IntervalMinutes';
GO

PRINT 'Jobs table created successfully.';
GO
