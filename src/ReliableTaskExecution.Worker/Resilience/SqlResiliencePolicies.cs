using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace ReliableTaskExecution.Worker.Resilience;

/// <summary>
/// Provides Polly resilience policies for SQL operations.
/// Includes retry with exponential backoff and circuit breaker patterns.
/// </summary>
public static class SqlResiliencePolicies
{
    /// <summary>
    /// SQL Server transient error numbers that warrant retry.
    /// </summary>
    private static readonly int[] TransientErrorNumbers =
    {
        // Connection/Network errors
        -2,     // Timeout expired
        20,     // Instance doesn't support encryption
        64,     // Connection was successfully established but login failed
        233,    // Connection initialization error
        10053,  // Software caused connection abort
        10054,  // Connection reset by peer
        10060,  // Connection timed out
        10928,  // Resource limit reached
        10929,  // Too many requests
        40197,  // Service error processing request
        40501,  // Service busy
        40613,  // Database unavailable
        49918,  // Not enough resources to process request
        49919,  // Cannot process create or update request
        49920,  // Cannot process request due to too many operations in progress
        4060,   // Cannot open database
        4221,   // Login to read-secondary failed

        // SQL Azure specific transient errors
        10922,  // Partner server information change
        10936,  // Resource limit reached
        11001,  // Host not found
    };

    /// <summary>
    /// Creates a retry policy for SQL operations with exponential backoff and jitter.
    /// Retry attempts: 4 (1s, 2s, 4s, 8s base delays)
    /// </summary>
    /// <param name="logger">Logger for retry events.</param>
    /// <returns>An async retry policy for SQL exceptions.</returns>
    public static AsyncRetryPolicy CreateRetryPolicy(ILogger logger)
    {
        return Policy
            .Handle<SqlException>(ex => IsTransientError(ex))
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 4,
                sleepDurationProvider: (retryAttempt, context) =>
                {
                    // Exponential backoff: 1s, 2s, 4s, 8s
                    var baseDelay = TimeSpan.FromSeconds(Math.Pow(2, retryAttempt - 1));

                    // Add jitter: +/- 25% to prevent thundering herd
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(-250, 250) * retryAttempt);

                    return baseDelay + jitter;
                },
                onRetryAsync: (exception, timeSpan, retryCount, context) =>
                {
                    logger.LogWarning(
                        exception,
                        "SQL operation failed, retrying in {Delay}ms (attempt {RetryCount}/4). Error: {ErrorMessage}",
                        timeSpan.TotalMilliseconds,
                        retryCount,
                        exception.Message);
                    return Task.CompletedTask;
                });
    }

    /// <summary>
    /// Creates a circuit breaker policy for SQL operations.
    /// Opens after 5 consecutive failures, half-open after 30 seconds.
    /// </summary>
    /// <param name="logger">Logger for circuit breaker events.</param>
    /// <returns>An async circuit breaker policy for SQL exceptions.</returns>
    public static AsyncCircuitBreakerPolicy CreateCircuitBreakerPolicy(ILogger logger)
    {
        return Policy
            .Handle<SqlException>(ex => IsTransientError(ex))
            .Or<TimeoutException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (exception, duration) =>
                {
                    logger.LogError(
                        exception,
                        "Circuit breaker opened. SQL operations will fail fast for {Duration} seconds. Error: {ErrorMessage}",
                        duration.TotalSeconds,
                        exception.Message);
                },
                onReset: () =>
                {
                    logger.LogInformation("Circuit breaker reset. SQL operations will be attempted again.");
                },
                onHalfOpen: () =>
                {
                    logger.LogInformation("Circuit breaker half-open. Testing SQL connectivity...");
                });
    }

    /// <summary>
    /// Creates a combined policy wrapping retry inside circuit breaker.
    /// The circuit breaker wraps the retry policy: if retries keep failing,
    /// the circuit eventually opens to prevent continuous retry storms.
    /// </summary>
    /// <param name="logger">Logger for policy events.</param>
    /// <returns>A wrapped async policy combining circuit breaker and retry.</returns>
    public static AsyncPolicy CreateCombinedPolicy(ILogger logger)
    {
        var retryPolicy = CreateRetryPolicy(logger);
        var circuitBreakerPolicy = CreateCircuitBreakerPolicy(logger);

        // Circuit breaker wraps retry: if retries keep failing, circuit opens
        return Policy.WrapAsync(circuitBreakerPolicy, retryPolicy);
    }

    /// <summary>
    /// Determines if a SQL exception is transient and should trigger retry.
    /// </summary>
    /// <param name="exception">The SQL exception to evaluate.</param>
    /// <returns>True if the error is transient and retry-able.</returns>
    public static bool IsTransientError(SqlException exception)
    {
        if (exception == null)
        {
            return false;
        }

        foreach (SqlError error in exception.Errors)
        {
            if (TransientErrorNumbers.Contains(error.Number))
            {
                return true;
            }
        }

        return false;
    }
}
