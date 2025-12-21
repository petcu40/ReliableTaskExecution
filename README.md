# Reliable Task Execution Worker

A proof-of-concept distributed task execution system that ensures exactly-once task execution across multiple worker instances using SQL Azure for coordination and leader election, with automatic failover when workers crash or tasks fail.

## Features

- **Exactly-Once Execution**: Uses atomic SQL UPDATE WHERE pattern for leader election, ensuring only one worker executes each task
- **Automatic Failover**: Stale lock reclamation handles crashed workers automatically
- **Heartbeat Mechanism**: Workers send heartbeats to extend locks during long-running tasks
- **Timing Drift Prevention**: NextRunTime is calculated from task start time (LockedAt), not completion time
- **Graceful Shutdown**: Properly handles SIGTERM and Ctrl+C signals
- **Resilience**: Polly retry policies with exponential backoff and circuit breaker for transient SQL failures
- **Azure Entra ID Authentication**: Passwordless authentication using DefaultAzureCredential

## Prerequisites

- .NET 8.0 SDK
- Azure SQL Database
- Azure CLI (for local development authentication)
- PowerShell with Az module (for infrastructure scripts)

## Quick Start

1. **Set up Azure resources** (optional - use your existing Azure SQL):
   ```powershell
   .\infrastructure\Create-AzureResources.ps1 -SubscriptionId "your-subscription-id"
   ```

2. **Run database scripts**:
   - Execute `database/001-CreateJobsTable.sql` to create the Jobs table
   - Execute `database/002-SeedJobData.sql` to insert a sample job

3. **Update configuration**:
   - Edit `appsettings.json` with your SQL Server name

4. **Run the worker**:
   ```bash
   cd src/ReliableTaskExecution.Worker
   dotnet run
   ```

## Configuration

### appsettings.json Options

All configuration is loaded from `appsettings.json` in the worker directory.

#### Connection Strings

| Setting | Description | Example |
|---------|-------------|---------|
| `ConnectionStrings:SqlAzure` | Azure SQL connection string (no password needed with Entra ID) | `Server=reltaskexec-xxxx.database.windows.net;Database=TaskExecutionDb;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;` |

#### Task Execution Options

Located under the `TaskExecution` section:

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `PollingIntervalSeconds` | int | 60 | Interval in seconds between polling cycles for available jobs |
| `TaskDurationSeconds` | int | 5 | Duration in seconds for the sample task execution (simulates work) |
| `HeartbeatIntervalSeconds` | int | 30 | Interval in seconds between heartbeat updates while executing a task |
| `LockTimeoutMinutes` | int | 2 | Lock timeout in minutes - if no heartbeat within this duration, lock is considered stale |
| `MaxConsecutiveHeartbeatFailures` | int | 3 | Maximum consecutive heartbeat failures before abandoning a job |

#### Logging Configuration

Located under the `Logging` section:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "ReliableTaskExecution.Worker.Services.TaskExecutionWorker": "Information",
      "ReliableTaskExecution.Worker.Services.SampleTaskExecutor": "Information",
      "ReliableTaskExecution.Worker.Services.HeartbeatService": "Warning",
      "ReliableTaskExecution.Worker.Data.SqlConnectionFactory": "Debug",
      "ReliableTaskExecution.Worker.Data.JobRepository": "Debug",
      "SqlResiliencePolicies": "Warning"
    }
  }
}
```

### Environment Variable Overrides

All configuration settings can be overridden using environment variables with the following naming convention:

- Use double underscores (`__`) to represent section hierarchy
- Use uppercase for setting names (case-insensitive on Windows)

| Environment Variable | Overrides |
|---------------------|-----------|
| `ConnectionStrings__SqlAzure` | SQL connection string |
| `TaskExecution__PollingIntervalSeconds` | Polling interval |
| `TaskExecution__TaskDurationSeconds` | Task duration |
| `TaskExecution__HeartbeatIntervalSeconds` | Heartbeat interval |
| `TaskExecution__LockTimeoutMinutes` | Lock timeout |
| `TaskExecution__MaxConsecutiveHeartbeatFailures` | Max heartbeat failures |

Example:
```bash
export TaskExecution__PollingIntervalSeconds=30
export TaskExecution__HeartbeatIntervalSeconds=15
dotnet run
```

### Example Configurations

#### Development Configuration
```json
{
  "ConnectionStrings": {
    "SqlAzure": "Server=localhost;Database=TaskExecutionDb;Trusted_Connection=True;Encrypt=False;"
  },
  "TaskExecution": {
    "PollingIntervalSeconds": 10,
    "TaskDurationSeconds": 2,
    "HeartbeatIntervalSeconds": 5,
    "LockTimeoutMinutes": 1,
    "MaxConsecutiveHeartbeatFailures": 3
  }
}
```

#### Production Configuration
```json
{
  "ConnectionStrings": {
    "SqlAzure": "Server=your-server.database.windows.net;Database=TaskExecutionDb;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"
  },
  "TaskExecution": {
    "PollingIntervalSeconds": 60,
    "TaskDurationSeconds": 30,
    "HeartbeatIntervalSeconds": 30,
    "LockTimeoutMinutes": 5,
    "MaxConsecutiveHeartbeatFailures": 3
  }
}
```

#### High-Frequency Polling Configuration
```json
{
  "TaskExecution": {
    "PollingIntervalSeconds": 5,
    "HeartbeatIntervalSeconds": 10,
    "LockTimeoutMinutes": 1
  }
}
```

## Multi-Instance Deployment

To run multiple worker instances:

1. Each instance automatically generates a unique worker ID combining:
   - Machine name
   - Process ID
   - Unique GUID

2. Start multiple instances - they will coordinate via the database:
   ```bash
   # Terminal 1
   dotnet run

   # Terminal 2
   dotnet run

   # Terminal 3
   dotnet run
   ```

3. Only one worker will acquire each task at a time. Others will wait.

## Architecture

### Leader Election

Uses atomic `UPDATE ... WHERE LockedBy IS NULL` pattern:
```sql
UPDATE Jobs
SET LockedBy = @workerId, LockedAt = GETUTCDATE()
WHERE Id = @jobId AND LockedBy IS NULL
```
Only one worker succeeds when multiple attempt simultaneous claims.

### Heartbeat Mechanism

Workers extend their lock by updating `LockedAt` every 30 seconds:
```sql
UPDATE Jobs
SET LockedAt = GETUTCDATE()
WHERE Id = @jobId AND LockedBy = @workerId
```

If 3 consecutive heartbeats fail, the job is abandoned to prevent multi-master situations.

### Stale Lock Reclamation

Crashed workers are detected by checking for expired locks:
```sql
UPDATE Jobs
SET LockedBy = NULL
WHERE LockedBy IS NOT NULL
  AND DATEADD(MINUTE, LockTimeoutMinutes, LockedAt) < GETUTCDATE()
```

### Timing Drift Prevention

`NextRunTime` is calculated from `LockedAt` (task start), not completion time:
```sql
UPDATE Jobs
SET LockedBy = NULL,
    LastRunTime = GETUTCDATE(),
    NextRunTime = DATEADD(MINUTE, IntervalMinutes, LockedAt)
WHERE Id = @jobId
```

This ensures consistent scheduling regardless of task duration.

## Resilience Policies

### Retry Policy
- 4 retry attempts
- Exponential backoff: 1s, 2s, 4s, 8s
- Jitter added to prevent thundering herd

### Circuit Breaker
- Opens after 5 consecutive failures
- Half-open after 30 seconds
- Allows test request to determine if service recovered

## Database Schema

```sql
CREATE TABLE Jobs (
    Id uniqueidentifier PRIMARY KEY DEFAULT NEWID(),
    JobName nvarchar(256) NOT NULL,
    NextRunTime datetime2 NOT NULL,
    LastRunTime datetime2 NULL,
    LockedBy nvarchar(256) NULL,
    LockedAt datetime2 NULL,
    LockTimeoutMinutes int NOT NULL DEFAULT 2,
    IntervalMinutes int NOT NULL DEFAULT 10
);
```

## Testing

Run the test suite:
```bash
cd tests/ReliableTaskExecution.Worker.Tests
dotnet test
```

The test suite includes:
- Unit tests for all core components
- Integration tests for multi-worker coordination
- Tests for crash recovery scenarios
- Tests for timing drift prevention

## Cleanup

To remove Azure resources:
```powershell
.\infrastructure\Remove-AzureResources.ps1 -SubscriptionId "your-subscription-id"
```

## License

This is a proof-of-concept project for educational purposes.
