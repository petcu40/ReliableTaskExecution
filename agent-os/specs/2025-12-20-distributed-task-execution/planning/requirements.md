# Spec Requirements: Distributed Task Execution System

## Initial Description

This is a distributed task execution system. The core features to implement are:

1. **Leader Election via SQL Locking**: Atomic row-level updates ensure only one worker claims each task, eliminating race conditions. Uses SQL Azure with UPDATE WHERE pattern to atomically claim tasks.

2. **Configurable Polling Interval**: Workers check for available tasks at a configurable interval (default: 1 minute)

3. **Configurable Task Interval**: Tasks reschedule based on configurable intervals (default: 10 minutes from task start, not completion)

The system uses:
- SQL Azure for coordination
- Entra ID (Azure AD) authentication
- Console application workers
- A Jobs table with fields: Id, JobName, NextRunTime, LastRunTime, LockedBy, LockedAt, LockTimeoutMinutes, IntervalMinutes

## Requirements Discussion

### First Round Questions

**Q1:** I assume this spec should cover all three core features together (Leader Election, Configurable Polling, and Configurable Task Interval) as a single cohesive implementation. Is that correct, or should we break these into separate, sequential specs?
**Answer:** Break into separate specs for each core feature (Leader Election, Configurable Polling, Configurable Task Interval)

**Q2:** The mission document shows a lock timeout of 5 minutes default, while the task interval is 10 minutes. I assume a task that runs longer than 5 minutes would have its lock reclaimed and potentially cause duplicate execution. Should the default lock timeout be longer than the expected maximum task duration, or should we implement a heartbeat mechanism to extend locks for long-running tasks?
**Answer:** Use heartbeat mechanism to extend locks. Lock timeout = 2 minutes. Every 30 seconds, extend the lock to cover the next 2 minutes.

**Q3:** I assume the initial implementation should support a single job type (one row in the Jobs table), with multiple workers competing to execute it. Is that correct, or should we design for multiple different job types from the start?
**Answer:** Single job type, single job instance, executed every 10 minutes.

**Q4:** When a task fails (throws an exception), I assume we should: (a) release the lock immediately, (b) log the error, and (c) leave NextRunTime unchanged so another worker can retry soon. Is that the correct failure behavior, or should failed tasks have a different retry schedule?
**Answer:** On failure: (a) release lock immediately, (b) log error, (c) leave NextRunTime unchanged for quick retry.

**Q5:** I assume the sample task implementation should be a simple, demonstrable action (like logging a timestamp or writing to a file) that proves the system works. Do you have a specific sample task in mind?
**Answer:** Console.WriteLine logging.

**Q6:** The tech stack mentions using Microsoft.Extensions.Hosting for graceful shutdown. I assume we should implement this as a BackgroundService within the Generic Host pattern. Is that correct, or do you prefer a simpler polling loop without the hosting infrastructure?
**Answer:** BackgroundService within the Generic Host pattern (Microsoft.Extensions.Hosting).

**Q7:** I assume the SQL scripts for table creation and job seeding should be standalone .sql files that users run manually. Is that correct, or should we provide any automation?
**Answer:** Standalone .sql files + PowerShell automation for DB resource creation/cleanup. Parameters: subscription id. Resource group: "cristp-reltaskexec", Region: "west us2".

**Q8:** Are there any features explicitly OUT of scope for this initial implementation?
**Answer:** Out of scope: Multiple job types, job dependencies, priority queuing, dead-letter handling, admin UI.

### Existing Code to Reference

No similar existing features identified for reference. No prototype or spike code exists for this system.

### Follow-up Questions

No follow-up questions were required.

## Visual Assets

### Files Provided:
No visual assets provided.

### Visual Insights:
N/A - No visual files were found in the visuals folder.

## Requirements Summary

### Functional Requirements
- Implement leader election via SQL row-level locking using UPDATE WHERE pattern
- Workers poll for available tasks at configurable intervals (default: 1 minute)
- Tasks reschedule based on configurable intervals (default: 10 minutes from task start)
- Heartbeat mechanism extends locks every 30 seconds for long-running tasks
- Lock timeout of 2 minutes (reclaimed if heartbeat stops)
- Single job type with single job instance executing every 10 minutes
- On task failure: release lock immediately, log error, leave NextRunTime unchanged for quick retry
- Sample task uses Console.WriteLine for demonstration
- Use Polly for retry/resilience patterns

### Technical Requirements
- .NET 8.0 LTS with C# 12
- Console application as BackgroundService within Generic Host pattern
- SQL Azure with Entra ID authentication
- Raw ADO.NET (no ORM) for precise control over atomic locking
- Azure.Identity with DefaultAzureCredential for authentication
- Microsoft.Extensions.Hosting for graceful shutdown handling

### Infrastructure Requirements
- Standalone .sql files for table creation and job seeding
- PowerShell automation for Azure DB resource creation/cleanup
- Parameters: subscription id
- Resource group: "cristp-reltaskexec"
- Region: "west us2"

### Reusability Opportunities
- No existing components identified for reuse
- This is a greenfield implementation

### Scope Boundaries

**In Scope:**
- Leader election via SQL locking (separate spec)
- Configurable polling interval (separate spec)
- Configurable task interval (separate spec)
- Heartbeat mechanism for lock extension
- Single job type/instance support
- Failure handling with immediate lock release
- Console.WriteLine sample task
- BackgroundService hosting pattern
- SQL scripts for database setup
- PowerShell automation for Azure resource management
- Polly for resilience patterns

**Out of Scope:**
- Multiple job types
- Job dependencies
- Priority queuing
- Dead-letter handling
- Admin UI
- EF Core or other ORM usage

### Technical Considerations
- Lock timeout of 2 minutes with 30-second heartbeat interval ensures safe margin
- Heartbeat prevents premature lock reclamation for long-running tasks
- Raw ADO.NET required for precise control over atomic UPDATE statements
- DefaultAzureCredential enables seamless local dev (Azure CLI) and production (Managed Identity) auth
- Breaking into separate specs allows incremental delivery and testing
