# Tech Stack

## Runtime and Language

| Component | Choice | Version | Rationale |
|-----------|--------|---------|-----------|
| **Runtime** | .NET | 8.0 LTS | Long-term support, cross-platform, excellent performance |
| **Language** | C# | 12 | Modern language features, strong typing, async/await support |
| **Application Type** | Console Application | - | Minimal overhead, easy to deploy as Windows Service or container |

## Database

| Component | Choice | Rationale |
|-----------|--------|-----------|
| **Database Engine** | Azure SQL Database | Managed service, built-in HA, row-level locking support |
| **Connection Library** | Microsoft.Data.SqlClient | Official Microsoft SQL client with Entra ID support |
| **ORM** | None (raw ADO.NET) | Maximum control over SQL for atomic locking operations |

### SQL Azure Configuration
- **Service Tier**: Basic or Standard S0 (sufficient for PoC)
- **Compatibility Level**: 150 (SQL Server 2019)
- **Key Features Used**: Row-level locking, atomic UPDATE with WHERE conditions

## Authentication and Security

| Component | Choice | Rationale |
|-----------|--------|-----------|
| **Identity Provider** | Microsoft Entra ID | Azure-native, no password management |
| **Authentication Library** | Azure.Identity | Official SDK, DefaultAzureCredential pattern |
| **Local Development Auth** | Azure CLI | `az login` provides seamless local authentication |
| **Production Auth** | Managed Identity | System-assigned identity, zero credential management |

### Authentication Flow
```
Local Development:
  Developer -> Azure CLI (az login) -> DefaultAzureCredential -> SQL Azure

Production (Azure App Service/Container Apps):
  App -> System Managed Identity -> DefaultAzureCredential -> SQL Azure
```

## NuGet Packages

| Package | Purpose | Version Strategy |
|---------|---------|------------------|
| `Microsoft.Data.SqlClient` | SQL Azure connectivity | Latest stable |
| `Azure.Identity` | Entra ID authentication | Latest stable |
| `Microsoft.Extensions.Configuration` | Configuration management | Match .NET version |
| `Microsoft.Extensions.Configuration.Json` | appsettings.json support | Match .NET version |
| `Microsoft.Extensions.Configuration.EnvironmentVariables` | Environment variable overrides | Match .NET version |
| `Microsoft.Extensions.Hosting` | Generic host for graceful shutdown | Match .NET version |

## Project Structure

```
ReliableTaskExecution/
├── src/
│   └── ReliableTaskExecution/
│       ├── Program.cs                 # Entry point, host configuration
│       ├── Worker.cs                  # Background service with polling loop
│       ├── Configuration/
│       │   └── WorkerOptions.cs       # Strongly-typed configuration
│       ├── Data/
│       │   ├── JobRepository.cs       # Database access layer
│       │   └── SqlConnectionFactory.cs # Entra ID-authenticated connections
│       ├── Models/
│       │   └── Job.cs                 # Job entity
│       └── Tasks/
│           ├── IJobTask.cs            # Task interface
│           └── SampleTask.cs          # Example implementation
├── sql/
│   ├── 01-create-table.sql           # Jobs table DDL
│   └── 02-seed-job.sql               # Initial job insertion
├── docs/
│   └── architecture.md               # MermaidJS diagrams and specs
├── appsettings.json                  # Default configuration
├── appsettings.Development.json      # Local development overrides
└── ReliableTaskExecution.csproj      # Project file
```

## Configuration Schema

```json
{
  "Worker": {
    "InstanceId": "worker-1",
    "PollingIntervalSeconds": 60,
    "DefaultLockTimeoutMinutes": 5
  },
  "Database": {
    "ServerName": "your-server.database.windows.net",
    "DatabaseName": "ReliableTaskExecution",
    "UseEntraAuth": true
  }
}
```

## Deployment Options

| Environment | Hosting Option | Authentication |
|-------------|---------------|----------------|
| **Local Development** | `dotnet run` | Azure CLI (`az login`) |
| **Azure VM** | Windows Service or systemd | System Managed Identity |
| **Azure App Service** | WebJob or Always-On | System Managed Identity |
| **Azure Container Apps** | Container | System Managed Identity |
| **AKS** | Pod | Workload Identity |

## Development Tools

| Tool | Purpose |
|------|---------|
| **Visual Studio 2022** or **VS Code** | IDE |
| **Azure CLI** | Local authentication, resource management |
| **Azure Data Studio** or **SSMS** | Database management |
| **Docker** (optional) | Containerized deployment testing |

## Key Technical Decisions

### Why Raw ADO.NET Instead of EF Core?
The core locking mechanism requires precise control over the SQL UPDATE statement with specific WHERE conditions. EF Core's change tracking and abstraction would obscure this critical logic and potentially introduce race conditions.

### Why Console App Instead of Azure Functions?
Azure Functions with Timer Triggers don't natively support distributed locking across instances. This PoC demonstrates the explicit coordination pattern that Functions would need internally.

### Why DefaultAzureCredential?
This pattern automatically selects the appropriate credential based on environment:
- Azure CLI for local development
- Managed Identity in Azure
- Environment variables for CI/CD

No code changes required between environments.

### Why Polling Instead of SQL Service Broker?
Polling is simpler to implement, debug, and reason about. For a PoC demonstrating the locking pattern, the slight latency from polling (up to 1 minute) is acceptable. Production systems could add Service Broker notifications as an optimization.
