using System.Diagnostics;

namespace ReliableTaskExecution.Worker.Configuration;

/// <summary>
/// Generates unique worker identifiers for distributed task coordination.
/// Each worker instance gets a unique ID combining machine name, process ID, and GUID.
/// </summary>
public static class WorkerIdGenerator
{
    /// <summary>
    /// Generates a unique worker ID.
    /// Format: {MachineName}_{ProcessId}_{Guid}
    /// </summary>
    /// <returns>A unique worker identifier string.</returns>
    public static string GenerateWorkerId()
    {
        var machineName = Environment.MachineName;
        var processId = Process.GetCurrentProcess().Id;
        var uniqueId = Guid.NewGuid().ToString("N");

        return $"{machineName}_{processId}_{uniqueId}";
    }

    /// <summary>
    /// Generates a shortened worker ID for display purposes.
    /// Format: {MachineName}_{ProcessId}
    /// </summary>
    /// <returns>A shorter worker identifier without the GUID component.</returns>
    public static string GenerateShortWorkerId()
    {
        var machineName = Environment.MachineName;
        var processId = Process.GetCurrentProcess().Id;

        return $"{machineName}_{processId}";
    }
}
