using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using HrmApi.Data;
using HrmApi.Models;

namespace HrmApi.Health;

/// <summary>
/// Lightweight liveness check - only confirms the application process is running.
/// Does NOT check any external dependencies. Used by Render to detect if the process is alive.
/// </summary>
public sealed class LivenessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(HealthCheckResult.Healthy("Application is running"));
    }
}

/// <summary>
/// Health check publisher that logs health check failures for observability.
/// Does not log sensitive information.
/// </summary>
public sealed class LoggingHealthCheckPublisher(
    ILogger<LoggingHealthCheckPublisher> logger) : IHealthCheckPublisher
{
    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        if (report.Status == HealthStatus.Unhealthy)
        {
            var failedChecks = report.Entries
                .Where(e => e.Value.Status == HealthStatus.Unhealthy)
                .Select(e => $"{e.Key}: {e.Value.Description}")
                .ToList();

            logger.LogWarning("Health check failed: {FailedChecks}. Total duration: {Duration}",
                string.Join(", ", failedChecks), report.TotalDuration);
        }
        else if (report.Status == HealthStatus.Degraded)
        {
            var degradedChecks = report.Entries
                .Where(e => e.Value.Status == HealthStatus.Degraded)
                .Select(e => $"{e.Key}: {e.Value.Description}")
                .ToList();

            logger.LogWarning("Health check degraded: {DegradedChecks}. Total duration: {Duration}",
                string.Join(", ", degradedChecks), report.TotalDuration);
        }
        else
        {
            logger.LogInformation("Health check healthy. Checks: {Count}. Total duration: {Duration}",
                report.Entries.Count, report.TotalDuration);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Readiness check - confirms the application can serve requests by verifying database connectivity.
/// Used by Render to determine if the service is ready to receive traffic.
/// </summary>
public sealed class DatabaseHealthCheck(IMongoClient mongoClient, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var databaseName = configuration["MongoDB:Database"] ?? "hrmdb";
            var database = mongoClient.GetDatabase(databaseName);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("Database responsive");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unavailable", ex);
        }
    }
}

/// <summary>
/// Comprehensive configuration check - validates JWT key, connection string, and other required configuration.
/// Used for /config endpoint to diagnose configuration issues.
/// </summary>
public sealed class ConfigurationHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var data = new Dictionary<string, object>();

        // JWT Key
        var jwtKey = configuration["Auth:JwtKey"];
        if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
            errors.Add("Auth:JwtKey must be 32+ characters");
        else
            data["jwt_key_length"] = jwtKey.Length;

        // Connection String
        var connString = configuration.GetConnectionString("MongoDB");
        if (string.IsNullOrWhiteSpace(connString))
            errors.Add("ConnectionStrings:MongoDB is required");
        else
            data["connection_string"] = "configured";

        // Database Name
        var dbName = configuration["MongoDB:Database"];
        data["database_name"] = dbName ?? "hrmdb (default)";

        // Issuer/Audience
        data["issuer"] = configuration["Auth:Issuer"] ?? "HrmApi (default)";
        data["audience"] = configuration["Auth:Audience"] ?? "HrmApi (default)";

        return Task.FromResult(errors.Count > 0
            ? HealthCheckResult.Unhealthy(string.Join("; ", errors), data: data)
            : HealthCheckResult.Healthy("Configuration valid", data));
    }
}