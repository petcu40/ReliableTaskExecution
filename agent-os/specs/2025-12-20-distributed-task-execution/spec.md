# Specification: Distributed Task Execution System

## Goal
Build a proof-of-concept distributed task execution system that ensures exactly-once task execution across multiple worker instances using SQL Azure for coordination and leader election, with automatic failover when workers crash or tasks fail.

## User Stories
- As a backend developer, I want multiple worker instances to coordinate task execution so that each scheduled task runs exactly once without duplicates or missed executions.
- As a DevOps engineer, I want crashed workers to automatically failover so that tasks continue executing without manual intervention.

## Specific Requirements

**Leader Election via SQL Locking**
- Use atomic UPDATE WHERE pattern to claim tasks: `UPDATE Jobs SET LockedBy=@workerId, LockedAt=GETUTCDATE() WHERE Id=@jobId AND LockedBy IS NULL`
- Only one worker succeeds when multiple attempt simultaneous claims (SQL row-level locking guarantees atomicity)
- Worker ID should be a unique identifier (e.g., GUID or machine name + process ID)
- Use raw ADO.NET with Microsoft.Data.SqlClient for precise control over atomic operations
- No ORM (EF Core) to ensure transparent, predictable SQL execution

**Heartbeat Mechanism for Lock Extension**
- Lock timeout set to 2 minutes (LockTimeoutMinutes = 2)
- Worker sends heartbeat every 30 seconds while task is executing
- Heartbeat updates LockedAt to current UTC time, extending the lock window
- Use background timer or separate thread for heartbeat during task execution
- Track consecutive heartbeat failures
- If heartbeat fails 3 times consecutively, abandon (fail) the job immediately
  - This prevents multi-master situations where another worker reclaims the lock while the original worker continues executing
  - On abandonment: release lock, log error, leave NextRunTime unchanged for retry by another worker
  - Stop task execution via cancellation token

**Stale Lock Reclamation**
- When polling, check for stale locks: `WHERE LockedBy IS NOT NULL AND DATEADD(MINUTE, LockTimeoutMinutes, LockedAt) < GETUTCDATE()`
- Reclaim stale locks by setting LockedBy = NULL before attempting new claims
- This handles crashed workers that never released their locks

**Configurable Polling Interval**
- Workers poll for available tasks at configurable interval (default: 1 minute)
- Configuration via appsettings.json or environment variables
- Use Task.Delay with CancellationToken for polling loop
- Polling should continue until graceful shutdown is requested

**Configurable Task Interval**
- Tasks reschedule based on interval from task START time, not completion time
- Default interval: 10 minutes (IntervalMinutes = 10)
- On task completion: `NextRunTime = LockedAt + IntervalMinutes` (prevents timing drift)
- Clear lock fields: `LockedBy = NULL, LastRunTime = GETUTCDATE()`

**BackgroundService Hosting Pattern**
- Implement as BackgroundService within Microsoft.Extensions.Hosting Generic Host
- Override ExecuteAsync for main polling loop
- Respect CancellationToken for graceful shutdown (SIGTERM, Ctrl+C)
- Use ILogger for structured logging
- Configure services via dependency injection

**Failure Handling**
- On task exception: immediately release lock (`LockedBy = NULL`)
- Log error with full exception details
- Leave NextRunTime unchanged so another worker can retry quickly
- Do NOT increment any retry counter (out of scope for this PoC)

**Sample Task Implementation**
- Simple Console.WriteLine logging with timestamp and worker ID
- Demonstrate task duration with configurable delay (e.g., 5 seconds)
- Log task start, progress, and completion

**Database Schema**
- Jobs table with columns: Id (uniqueidentifier PK), JobName (nvarchar), NextRunTime (datetime2), LastRunTime (datetime2), LockedBy (nvarchar), LockedAt (datetime2), LockTimeoutMinutes (int), IntervalMinutes (int)
- Provide standalone SQL script for table creation
- Provide SQL script for seeding initial job row
- Use datetime2 for sub-second precision

**Azure Infrastructure Automation**
- PowerShell scripts for resource creation and cleanup
- Resource group: "cristp-reltaskexec"
- Region: "west us2"
- Accept subscription ID as parameter
- Use Az PowerShell module for Azure operations
- Create SQL Server and Database with Entra ID authentication enabled

**Authentication**
- Use Azure.Identity with DefaultAzureCredential
- Supports local development (Azure CLI authentication)
- Supports production (Managed Identity)
- No connection string passwords stored anywhere

**Resilience with Polly**
- Use Polly for transient fault handling on SQL operations
- Retry policy for connection timeouts and transient SQL errors
- Exponential backoff with jitter
- Circuit breaker for extended outages

## Visual Design
No visual assets provided.

## Existing Code to Leverage
No existing components identified for reuse. This is a greenfield implementation.

## Out of Scope
- Multiple job types (only single job type/instance supported)
- Job dependencies between tasks
- Priority queuing for task execution order
- Dead-letter handling for permanently failed tasks
- Admin UI for monitoring or management
- EF Core or any ORM usage
- Complex retry scheduling (exponential backoff on task failures)
- Distributed tracing or APM integration
- Job result storage or history
- Dynamic job creation at runtime
