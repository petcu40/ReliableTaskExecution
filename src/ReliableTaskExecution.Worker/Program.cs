using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Registry;
using ReliableTaskExecution.Worker.Configuration;
using ReliableTaskExecution.Worker.Data;
using ReliableTaskExecution.Worker.Resilience;
using ReliableTaskExecution.Worker.Services;

namespace ReliableTaskExecution.Worker;

/// <summary>
/// Entry point for the Reliable Task Execution Worker.
/// Configures and runs the Generic Host with all required services.
/// </summary>
public class Program
{
    /// <summary>
    /// Policy key for the SQL retry policy in the policy registry.
    /// </summary>
    public const string SqlRetryPolicyKey = "SqlRetryPolicy";

    /// <summary>
    /// Policy key for the SQL circuit breaker policy in the policy registry.
    /// </summary>
    public const string SqlCircuitBreakerPolicyKey = "SqlCircuitBreakerPolicy";

    /// <summary>
    /// Policy key for the combined SQL resilience policy in the policy registry.
    /// </summary>
    public const string SqlCombinedPolicyKey = "SqlCombinedPolicy";

    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Configuration is automatically loaded from appsettings.json by CreateApplicationBuilder
        // Add strongly-typed configuration binding for TaskExecutionOptions
        builder.Services.Configure<TaskExecutionOptions>(
            builder.Configuration.GetSection(TaskExecutionOptions.SectionName));

        // Configure logging
        ConfigureLogging(builder);

        // Register Polly policies
        RegisterPollyPolicies(builder.Services);

        // Register database connectivity services
        // Singleton: SqlConnectionFactory is thread-safe and reuses Azure AD tokens
        builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();

        // Singleton: JobRepository creates fresh connections per operation via SqlConnectionFactory,
        // so it's effectively stateless and thread-safe. The "scoped per iteration" behavior
        // is achieved by the repository's design of creating new connections for each database operation.
        builder.Services.AddSingleton<IJobRepository, JobRepository>();

        // Generate a unique worker ID for this instance
        // This ID is used for lock acquisition and heartbeat identification
        var workerId = WorkerIdGenerator.GenerateWorkerId();
        builder.Services.AddSingleton(workerId);

        // Transient: SampleTaskExecutor is lightweight and can be created per request
        // Note: We use a factory to inject the worker ID
        builder.Services.AddTransient<ITaskExecutor>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TaskExecutionOptions>>();
            var logger = sp.GetRequiredService<ILogger<SampleTaskExecutor>>();
            var id = sp.GetRequiredService<string>();
            return new SampleTaskExecutor(id, options, logger);
        });

        // Register the background worker service
        builder.Services.AddHostedService<TaskExecutionWorker>();

        var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Reliable Task Execution Worker starting...");
        logger.LogInformation("Worker ID: {WorkerId}", workerId);

        await host.RunAsync();
    }

    /// <summary>
    /// Configures logging levels for different components.
    /// - Information: Task lifecycle events
    /// - Warning: Heartbeat failures
    /// - Error: Task failures
    /// - Debug: SQL operations
    /// </summary>
    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddDebug();

        // Default logging configuration is set via appsettings.json
        // This allows for runtime configuration without code changes
    }

    /// <summary>
    /// Registers Polly resilience policies for SQL operations.
    /// Policies are registered as named policies in a PolicyRegistry.
    /// </summary>
    private static void RegisterPollyPolicies(IServiceCollection services)
    {
        // Create and register the policy registry
        var registry = new PolicyRegistry();

        // We need to defer policy creation until we have access to a logger
        // Use a factory pattern to create policies with proper logging
        services.AddSingleton<IPolicyRegistry<string>>(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var policyLogger = loggerFactory.CreateLogger("SqlResiliencePolicies");

            var localRegistry = new PolicyRegistry
            {
                // Register individual policies for granular control
                [SqlRetryPolicyKey] = SqlResiliencePolicies.CreateRetryPolicy(policyLogger),
                [SqlCircuitBreakerPolicyKey] = SqlResiliencePolicies.CreateCircuitBreakerPolicy(policyLogger),

                // Register combined policy for convenience
                [SqlCombinedPolicyKey] = SqlResiliencePolicies.CreateCombinedPolicy(policyLogger)
            };

            return localRegistry;
        });

        // Register IReadOnlyPolicyRegistry for consumers that only need read access
        services.AddSingleton<IReadOnlyPolicyRegistry<string>>(sp =>
            sp.GetRequiredService<IPolicyRegistry<string>>());
    }
}
