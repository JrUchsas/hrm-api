using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using HrmApi.Data;
using HrmApi.Models;

namespace HrmApi.Health;

public sealed class ComprehensiveHealthCheck(
    IMongoClient mongoClient,
    IConfiguration configuration,
    MongoDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, object>();
        var errors = new List<string>();

        // 1. MongoDB Connection & Ping
        try
        {
            var databaseName = configuration["MongoDB:Database"] ?? "hrmdb";
            var database = mongoClient.GetDatabase(databaseName);
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            results["mongodb_ping"] = "OK";
        }
        catch (Exception ex)
        {
            errors.Add($"MongoDB ping failed: {ex.Message}");
            results["mongodb_ping"] = "FAILED";
        }

        // 2. Database Collections Access
        try
        {
            var usersCount = await dbContext.Users.CountDocumentsAsync(FilterDefinition<User>.Empty, cancellationToken: cancellationToken);
            var habitsCount = await dbContext.Habits.CountDocumentsAsync(FilterDefinition<Habit>.Empty, cancellationToken: cancellationToken);
            var checkInsCount = await dbContext.CheckIns.CountDocumentsAsync(FilterDefinition<HabitCheckIn>.Empty, cancellationToken: cancellationToken);
            results["collections_accessible"] = "OK";
            results["collections_stats"] = new { users = usersCount, habits = habitsCount, checkins = checkInsCount };
        }
        catch (Exception ex)
        {
            errors.Add($"Collections access failed: {ex.Message}");
            results["collections_accessible"] = "FAILED";
        }

        // 3. Indexes Exist
        try
        {
            var userIndexes = await dbContext.Users.Indexes.ListAsync(cancellationToken: cancellationToken);
            var habitIndexes = await dbContext.Habits.Indexes.ListAsync(cancellationToken: cancellationToken);
            var checkInIndexes = await dbContext.CheckIns.Indexes.ListAsync(cancellationToken: cancellationToken);

            var userIndexList = await userIndexes.ToListAsync(cancellationToken);
            var habitIndexList = await habitIndexes.ToListAsync(cancellationToken);
            var checkInIndexList = await checkInIndexes.ToListAsync(cancellationToken);

            results["indexes"] = new
            {
                users = userIndexList.Count,
                habits = habitIndexList.Count,
                checkins = checkInIndexList.Count
            };
        }
        catch (Exception ex)
        {
            errors.Add($"Indexes check failed: {ex.Message}");
            results["indexes"] = "FAILED";
        }

        // 4. JWT Configuration
        try
        {
            var jwtKey = configuration["Auth:JwtKey"];
            var issuer = configuration["Auth:Issuer"] ?? "HrmApi";
            var audience = configuration["Auth:Audience"] ?? "HrmApi";

            if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
            {
                errors.Add("JWT key missing or too short (must be 32+ chars)");
                results["jwt_config"] = "INVALID";
            }
            else
            {
                results["jwt_config"] = new { issuer, audience, key_length = jwtKey.Length };
            }
        }
        catch (Exception ex)
        {
            errors.Add($"JWT config check failed: {ex.Message}");
            results["jwt_config"] = "FAILED";
        }

        // 5. Write Test (optional - lightweight)
        try
        {
            var testCollection = dbContext.Users.Database.GetCollection<BsonDocument>("__health_check_test");
            var testDoc = new BsonDocument { { "test", true }, { "timestamp", new BsonDateTime(DateTimeOffset.UtcNow.UtcDateTime) } };
            await testCollection.InsertOneAsync(testDoc, cancellationToken: cancellationToken);
            await testCollection.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", testDoc["_id"]), cancellationToken: cancellationToken);
            results["write_test"] = "OK";
        }
        catch (Exception ex)
        {
            errors.Add($"Write test failed: {ex.Message}");
            results["write_test"] = "FAILED";
        }

        // 6. Connection String Validation
        try
        {
            var connString = configuration.GetConnectionString("MongoDB");
            if (string.IsNullOrWhiteSpace(connString))
            {
                errors.Add("MongoDB connection string not configured");
                results["connection_string"] = "MISSING";
            }
            else
            {
                var parsed = MongoUrl.Create(connString);
                results["connection_string"] = new
                {
                    host = parsed.Server.Host,
                    database = parsed.DatabaseName,
                    tls = parsed.UseTls
                };
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Connection string validation failed: {ex.Message}");
            results["connection_string"] = "INVALID";
        }

        // Determine overall status
        var hasErrors = errors.Count > 0;
        var status = hasErrors ? HealthStatus.Unhealthy : HealthStatus.Healthy;

        var description = hasErrors
            ? string.Join("; ", errors)
            : "All health checks passed";

        return new HealthCheckResult(status, description, data: results);
    }
}

// Additional lightweight health checks for specific components

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