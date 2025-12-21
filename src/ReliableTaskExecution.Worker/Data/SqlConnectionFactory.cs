using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ReliableTaskExecution.Worker.Data;

/// <summary>
/// Factory for creating SQL connections authenticated via Azure Entra ID.
/// Uses DefaultAzureCredential for token acquisition, supporting both
/// local development (Azure CLI) and production (Managed Identity).
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly TokenCredential _credential;
    private readonly ILogger<SqlConnectionFactory> _logger;

    /// <summary>
    /// Azure SQL Database scope for token acquisition.
    /// </summary>
    private const string AzureSqlScope = "https://database.windows.net/.default";

    /// <summary>
    /// Initializes a new instance of the SqlConnectionFactory.
    /// </summary>
    /// <param name="configuration">Configuration containing the connection string.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public SqlConnectionFactory(
        IConfiguration configuration,
        ILogger<SqlConnectionFactory> logger)
        : this(configuration, new DefaultAzureCredential(), logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the SqlConnectionFactory with a custom credential.
    /// This constructor is primarily used for testing scenarios.
    /// </summary>
    /// <param name="configuration">Configuration containing the connection string.</param>
    /// <param name="credential">Token credential for authentication.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public SqlConnectionFactory(
        IConfiguration configuration,
        TokenCredential credential,
        ILogger<SqlConnectionFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(logger);

        _connectionString = configuration.GetConnectionString("SqlAzure")
            ?? throw new InvalidOperationException(
                "Connection string 'SqlAzure' not found in configuration.");
        _credential = credential;
        _logger = logger;
    }

    /// <summary>
    /// Creates and opens a new SQL connection authenticated with Azure Entra ID.
    /// The access token is acquired using DefaultAzureCredential.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An open SqlConnection ready for use.</returns>
    public async Task<SqlConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Acquiring Azure SQL access token...");

        var tokenRequestContext = new TokenRequestContext(new[] { AzureSqlScope });
        var accessToken = await _credential.GetTokenAsync(tokenRequestContext, cancellationToken);

        _logger.LogDebug("Access token acquired, expires at: {ExpiresOn}", accessToken.ExpiresOn);

        var connection = new SqlConnection(_connectionString)
        {
            AccessToken = accessToken.Token
        };

        _logger.LogDebug("Opening SQL connection to: {DataSource}", connection.DataSource);

        await connection.OpenAsync(cancellationToken);

        _logger.LogDebug("SQL connection opened successfully");

        return connection;
    }

    /// <summary>
    /// Disposes resources used by the factory.
    /// Note: Individual connections should be disposed by their consumers.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _logger.LogDebug("SqlConnectionFactory disposed");
        return ValueTask.CompletedTask;
    }
}
