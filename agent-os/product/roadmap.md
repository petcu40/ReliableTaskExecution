# Product Roadmap

## Development Phases

1. [x] **Database Schema and SQL Scripts** - Create the Jobs table schema with all required columns (Id, JobName, NextRunTime, LastRunTime, LockedBy, LockedAt, LockTimeoutMinutes, IntervalMinutes) and provide manual SQL scripts for table creation and initial job insertion. `XS`

2. [x] **Entra ID Authentication Setup** - Implement SQL Azure connection using Azure.Identity with DefaultAzureCredential, supporting both local development (Azure CLI auth) and deployed scenarios (Managed Identity). Configure connection string without passwords. `S`

3. [x] **Configuration System** - Implement strongly-typed configuration for polling interval, database connection, worker instance ID, and default lock timeout. Support appsettings.json and environment variable overrides. `XS`

4. [x] **Job Polling Logic** - Implement the polling loop that runs at configurable intervals (default 1 minute), querying for jobs where NextRunTime has passed and LockedBy is NULL or lock has timed out. `S`

5. [x] **Atomic Lock Acquisition** - Implement the atomic UPDATE statement that attempts to acquire a lock on an available job, using WHERE conditions to ensure only one worker succeeds. Return affected row count to determine success. `S`

6. [x] **Task Execution Framework** - Create the task execution wrapper that runs after lock acquisition, handles exceptions gracefully, and ensures the job is always released (success or failure). Include logging for observability. `M`

7. [x] **Job Completion and Rescheduling** - Implement the completion logic that clears LockedBy, updates LastRunTime, and sets NextRunTime to LockedAt + IntervalMinutes, ensuring consistent scheduling from task start time. `S`

8. [x] **Stale Lock Recovery** - Implement detection of stale locks (LockedAt + LockTimeoutMinutes < NOW) and include these jobs in the available jobs query, allowing recovery from crashed workers. `S`

9. [x] **Graceful Shutdown Handling** - Implement Console.CancelKeyPress and SIGTERM handling to complete in-progress tasks before shutdown, preventing unnecessary lock timeouts during deployments. `S`

10. [x] **Sample Task Implementation** - Create a demonstrable sample task (e.g., logging timestamp, writing to a file) that proves the system works and can be replaced with real business logic. `XS`

11. [x] **Multi-Instance Testing Guide** - Document how to run multiple instances locally with different worker IDs, observe the coordination behavior, and verify exactly-once execution. Include test scenarios for crash recovery. `S`

12. [ ] **Architecture Documentation** - Create comprehensive documentation with MermaidJS diagrams showing system architecture, worker coordination sequence, task state machine, and database schema. `S`

> Notes
> - Order reflects technical dependencies: schema first, then auth, then core polling/locking, then completion, then reliability features
> - Each item represents a complete, testable feature that can be verified independently
> - Total estimated effort: approximately 3-4 weeks for complete PoC
> - Items 1-7 represent MVP; items 8-12 add production-readiness and documentation
