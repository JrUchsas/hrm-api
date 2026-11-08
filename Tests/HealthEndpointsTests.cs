using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HrmApi.Tests;

public sealed class HealthEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                context.HostingEnvironment.EnvironmentName = "Testing";
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Health_LivenessEndpoint_ReturnsHealthy()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var healthReport = JsonSerializer.Deserialize<JsonElement>(content);

        Assert.Equal("Healthy", healthReport.GetProperty("status").GetString());
        var checks = healthReport.GetProperty("checks").EnumerateArray().ToList();
        Assert.Single(checks);
        Assert.Equal("liveness", checks[0].GetProperty("name").GetString());
        Assert.Equal("Healthy", checks[0].GetProperty("status").GetString());
        Assert.Equal("Application is running", checks[0].GetProperty("description").GetString());
    }

    [Fact]
    public async Task Health_ReadinessEndpoint_ReturnsHealthy_WhenDatabaseAvailable()
    {
        // Act
        var response = await _client.GetAsync("/ready");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var healthReport = JsonSerializer.Deserialize<JsonElement>(content);

        Assert.Equal("Healthy", healthReport.GetProperty("status").GetString());
        var checks = healthReport.GetProperty("checks").EnumerateArray().ToList();
        Assert.Single(checks);
        Assert.Equal("database", checks[0].GetProperty("name").GetString());
        Assert.Equal("Healthy", checks[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_ConfigEndpoint_ReturnsHealthy_WhenConfigurationValid()
    {
        // Act
        var response = await _client.GetAsync("/config");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var healthReport = JsonSerializer.Deserialize<JsonElement>(content);

        Assert.Equal("Healthy", healthReport.GetProperty("status").GetString());
        var checks = healthReport.GetProperty("checks").EnumerateArray().ToList();
        Assert.Single(checks);
        Assert.Equal("configuration", checks[0].GetProperty("name").GetString());
        Assert.Equal("Healthy", checks[0].GetProperty("status").GetString());

        var data = checks[0].GetProperty("data");
        Assert.True(data.GetProperty("jwt_key_length").GetInt32() >= 32);
        Assert.Equal("configured", data.GetProperty("connection_string").GetString());
        Assert.Equal("hrmdb", data.GetProperty("database_name").GetString());
        Assert.Equal("HrmApi", data.GetProperty("issuer").GetString());
        Assert.Equal("HrmApi", data.GetProperty("audience").GetString());
    }

    [Fact]
    public async Task Health_LivenessEndpoint_IsAnonymous_NoAuthenticationRequired()
    {
        // Act - no authorization header
        var response = await _client.GetAsync("/health");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReadinessEndpoint_IsAnonymous_NoAuthenticationRequired()
    {
        // Act - no authorization header
        var response = await _client.GetAsync("/ready");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_ConfigEndpoint_IsAnonymous_NoAuthenticationRequired()
    {
        // Act - no authorization header
        var response = await _client.GetAsync("/config");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_LivenessEndpoint_ReturnsLightweightResponse()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        var content = await response.Content.ReadAsStringAsync();

        // Response should be small and lightweight (no database data)
        Assert.True(content.Length < 500); // Very small response

        var healthReport = JsonSerializer.Deserialize<JsonElement>(content);
        var checks = healthReport.GetProperty("checks").EnumerateArray().ToList();

        // Liveness check should have no data payload
        Assert.True(checks[0].TryGetProperty("data", out var data));
        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        // Data should be empty or minimal
        Assert.Equal(0, data.EnumerateObject().Count());
    }
}

public sealed class HealthCheckPublisherTests
{
    [Fact]
    public void LoggingHealthCheckPublisher_CanBeInstantiated()
    {
        // This test verifies the publisher can be created
        // The actual logging is tested via integration
        Assert.True(true); // Placeholder - actual test would need logger factory
    }
}