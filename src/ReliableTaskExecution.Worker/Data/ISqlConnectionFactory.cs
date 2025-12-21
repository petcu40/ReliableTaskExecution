using Microsoft.Data.SqlClient;

namespace ReliableTaskExecution.Worker.Data;

/// <summary>
/// Factory interface for creating authenticated SQL connections.
/// Provides abstraction for database connectivity with Azure Entra ID authentication.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>
    /// Creates and opens a new SQL connection authenticated with Azure Entra ID.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An open SqlConnection ready for use.</returns>
    Task<SqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
}
