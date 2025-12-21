# Raw Idea: Distributed Task Execution System

**Core Features for ReliableTaskExecution**

This is a distributed task execution system. The core features to implement are:

1. **Leader Election via SQL Locking**: Atomic row-level updates ensure only one worker claims each task, eliminating race conditions. Uses SQL Azure with UPDATE WHERE pattern to atomically claim tasks.

2. **Configurable Polling Interval**: Workers check for available tasks at a configurable interval (default: 1 minute)

3. **Configurable Task Interval**: Tasks reschedule based on configurable intervals (default: 10 minutes from task start, not completion)

The system uses:
- SQL Azure for coordination
- Entra ID (Azure AD) authentication
- Console application workers
- A Jobs table with fields: Id, JobName, NextRunTime, LastRunTime, LockedBy, LockedAt, LockTimeoutMinutes, IntervalMinutes
