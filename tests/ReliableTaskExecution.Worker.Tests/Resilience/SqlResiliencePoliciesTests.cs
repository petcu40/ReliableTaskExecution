using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Moq;
using Polly.CircuitBreaker;
using ReliableTaskExecution.Worker.Resilience;
using System.Reflection;
using Xunit;

namespace ReliableTaskExecution.Worker.Tests.Resilience;

/// <summary>
/// Unit tests for SQL resilience policies (Polly retry and circuit breaker).
/// </summary>
public class SqlResiliencePoliciesTests
{
    private readonly Mock<ILogger> _loggerMock;

    public SqlResiliencePoliciesTests()
    {
        _loggerMock = new Mock<ILogger>();
        _loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    /// <summary>
    /// Verify retry policy retries on transient SQL exceptions.
    /// </summary>
    [Fact]
    public async Task RetryPolicy_RetriesOnTransientSqlException()
    {
        // Arrange
        var policy = SqlResiliencePolicies.CreateRetryPolicy(_loggerMock.Object);
        var attemptCount = 0;

        // Create a transient SQL exception (error 40613 - database unavailable)
        var transientException = CreateSqlException(40613);

        // Act & Assert
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await policy.ExecuteAsync(async () =>
            {
                attemptCount++;
                if (attemptCount <= 5) // More than retry count to ensure all retries happen
                {
                    throw transientException;
                }
                await Task.CompletedTask;
            });
        });

        // Should have attempted 5 times (1 initial + 4 retries)
        Assert.Equal(5, attemptCount);
    }

    /// <summary>
    /// Verify retry policy does not retry on non-transient SQL exceptions.
    /// </summary>
    [Fact]
    public async Task RetryPolicy_DoesNotRetry_OnNonTransientSqlException()
    {
        // Arrange
        var policy = SqlResiliencePolicies.CreateRetryPolicy(_loggerMock.Object);
        var attemptCount = 0;

        // Create a non-transient SQL exception (error 547 - foreign key constraint violation)
        var nonTransientException = CreateSqlException(547);

        // Act & Assert
        await Assert.ThrowsAsync<SqlException>(async () =>
        {
            await policy.ExecuteAsync(async () =>
            {
                attemptCount++;
                throw nonTransientException;
            });
        });

        // Should have attempted only once (no retries for non-transient errors)
        Assert.Equal(1, attemptCount);
    }

    /// <summary>
    /// Verify circuit breaker opens after consecutive failures.
    /// </summary>
    [Fact]
    public async Task CircuitBreaker_OpensAfterConsecutiveFailures()
    {
        // Arrange
        var policy = SqlResiliencePolicies.CreateCircuitBreakerPolicy(_loggerMock.Object);
        var transientException = CreateSqlException(40613);

        // Act - Cause 5 consecutive failures
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await policy.ExecuteAsync(() => throw transientException);
            }
            catch (SqlException)
            {
                // Expected
            }
        }

        // Assert - Next call should throw BrokenCircuitException
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await policy.ExecuteAsync(() => Task.CompletedTask);
        });
    }

    /// <summary>
    /// Verify IsTransientError correctly identifies transient errors.
    /// </summary>
    [Theory]
    [InlineData(-2, true)]    // Timeout
    [InlineData(40613, true)] // Database unavailable
    [InlineData(40501, true)] // Service busy
    [InlineData(10929, true)] // Too many requests
    [InlineData(547, false)]  // Foreign key constraint (not transient)
    [InlineData(2627, false)] // Primary key violation (not transient)
    [InlineData(515, false)]  // Cannot insert null (not transient)
    public void IsTransientError_CorrectlyIdentifiesTransientErrors(int errorNumber, bool expectedIsTransient)
    {
        // Arrange
        var exception = CreateSqlException(errorNumber);

        // Act
        var isTransient = SqlResiliencePolicies.IsTransientError(exception);

        // Assert
        Assert.Equal(expectedIsTransient, isTransient);
    }

    /// <summary>
    /// Verify combined policy applies both retry and circuit breaker.
    /// </summary>
    [Fact]
    public async Task CombinedPolicy_AppliesRetryThenCircuitBreaker()
    {
        // Arrange
        var policy = SqlResiliencePolicies.CreateCombinedPolicy(_loggerMock.Object);
        var transientException = CreateSqlException(40613);
        var totalAttempts = 0;

        // Act - Execute enough times to trigger circuit breaker
        // Each execution retries 5 times, need 5 executions to trigger circuit breaker
        for (int execution = 0; execution < 5; execution++)
        {
            try
            {
                await policy.ExecuteAsync(async () =>
                {
                    totalAttempts++;
                    throw transientException;
                });
            }
            catch (SqlException)
            {
                // Expected during retries
            }
            catch (BrokenCircuitException)
            {
                // Circuit is open
                break;
            }
        }

        // Assert - Circuit should be open now, next call should fail fast
        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
        {
            await policy.ExecuteAsync(() => Task.CompletedTask);
        });
    }

    /// <summary>
    /// Helper method to create a SqlException with a specific error number.
    /// Uses reflection since SqlException has no public constructor.
    /// </summary>
    private static SqlException CreateSqlException(int errorNumber)
    {
        // Get SqlError constructors
        var sqlErrorType = typeof(SqlError);
        var constructors = sqlErrorType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);

        SqlError? sqlError = null;

        // Try each constructor
        foreach (var constructor in constructors)
        {
            var parameters = constructor.GetParameters();

            try
            {
                // Build arguments based on the exact parameter types
                var args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    var paramType = param.ParameterType;

                    if (paramType == typeof(int))
                    {
                        // First int is error number, second might be line number
                        args[i] = (param.Name?.Contains("line", StringComparison.OrdinalIgnoreCase) == true ||
                                   param.Name?.Contains("Line", StringComparison.OrdinalIgnoreCase) == true)
                            ? 0
                            : errorNumber;
                    }
                    else if (paramType == typeof(byte))
                    {
                        args[i] = (byte)0;
                    }
                    else if (paramType == typeof(string))
                    {
                        args[i] = "test";
                    }
                    else if (paramType == typeof(uint))
                    {
                        args[i] = (uint)0;
                    }
                    else if (paramType == typeof(Exception))
                    {
                        args[i] = null;
                    }
                    else
                    {
                        args[i] = paramType.IsValueType ? Activator.CreateInstance(paramType) : null;
                    }
                }

                sqlError = (SqlError)constructor.Invoke(args);
                if (sqlError != null && sqlError.Number == errorNumber)
                {
                    break;
                }
            }
            catch
            {
                // Try next constructor
                sqlError = null;
            }
        }

        if (sqlError == null)
        {
            throw new InvalidOperationException(
                $"Could not create SqlError with error number {errorNumber}");
        }

        // Create SqlErrorCollection and add the error
        var errorCollection = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            null,
            null)!;

        var addMethod = typeof(SqlErrorCollection).GetMethod(
            "Add",
            BindingFlags.NonPublic | BindingFlags.Instance);
        addMethod!.Invoke(errorCollection, new object[] { sqlError });

        // Create SqlException
        var sqlExceptionType = typeof(SqlException);
        var exceptionConstructors = sqlExceptionType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance);

        foreach (var constructor in exceptionConstructors)
        {
            var parameters = constructor.GetParameters();
            try
            {
                var args = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var param = parameters[i];
                    var paramType = param.ParameterType;

                    if (paramType == typeof(string))
                    {
                        args[i] = "Test SQL Exception";
                    }
                    else if (paramType == typeof(SqlErrorCollection))
                    {
                        args[i] = errorCollection;
                    }
                    else if (paramType == typeof(Exception))
                    {
                        args[i] = null;
                    }
                    else if (paramType == typeof(Guid))
                    {
                        args[i] = Guid.Empty;
                    }
                    else
                    {
                        args[i] = paramType.IsValueType ? Activator.CreateInstance(paramType) : null;
                    }
                }

                var exception = (SqlException)constructor.Invoke(args);
                if (exception != null && exception.Errors.Count > 0)
                {
                    return exception;
                }
            }
            catch
            {
                // Try next constructor
            }
        }

        throw new InvalidOperationException("Could not create SqlException");
    }
}
