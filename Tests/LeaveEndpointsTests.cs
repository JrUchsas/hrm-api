using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Xunit;

namespace HrmApi.Tests;

[CollectionDefinition("LeaveEndpointsTests", DisableParallelization = true)]
public class LeaveEndpointsTestsCollection { }

[Collection("LeaveEndpointsTests")]
public sealed class LeaveEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public LeaveEndpointsTests(WebApplicationFactory<Program> factory)
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

    private async Task<string> LoginAsync(string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new { email, password });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Login failed for {email}: {response.StatusCode} - {error}");
        }
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("accessToken").GetString()!;
    }

    private void SetAuthHeader(string token)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private void ClearAuthHeader()
    {
        _client.DefaultRequestHeaders.Authorization = null;
    }

    private async Task CleanupLeaveRequestsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HrmApi.Data.MongoDbContext>();
        await dbContext.LeaveRequests.DeleteManyAsync(FilterDefinition<HrmApi.Models.LeaveRequest>.Empty);
        await dbContext.LeaveBalances.DeleteManyAsync(FilterDefinition<HrmApi.Models.LeaveBalance>.Empty);
    }

    [Fact]
    public async Task Leave_GetBalance_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/leaves/balance");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leave_GetBalance_WithValidToken_ReturnsBalance()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/leaves/balance");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("year", out _));
        Assert.True(result.TryGetProperty("totalSickLeave", out _));
        Assert.True(result.TryGetProperty("remainingSickLeave", out _));
        Assert.True(result.TryGetProperty("totalCasualLeave", out _));
        Assert.True(result.TryGetProperty("remainingCasualLeave", out _));

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_ApplyLeave_RequiresAuthentication()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1, // Sick
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "Test reason"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leave_ApplyLeave_WithValidToken_CreatesRequest()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1, // Sick
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "Sick leave for health"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("id", out _));
        Assert.Equal("Sick", result.GetProperty("leaveType").GetString());
        Assert.Equal("Pending", result.GetProperty("status").GetString());

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_ApplyLeave_InvalidDates_ReturnsBadRequest()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act - End date before start date
        var response = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1, // Sick
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            reason = "Invalid dates"
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_ApplyLeave_OverlappingPending_ReturnsBadRequest()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // First leave request
        var first = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1, // Sick
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"),
            reason = "First leave"
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // Second overlapping leave request
        var second = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 2, // Casual
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(4)).ToString("yyyy-MM-dd"),
            reason = "Overlapping leave"
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        var error = await second.Content.ReadAsStringAsync();
        Assert.Contains("overlap", error, StringComparison.OrdinalIgnoreCase);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_GetMyHistory_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/leaves/my-history");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leave_GetMyHistory_WithValidToken_ReturnsHistory()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Create a leave request first
        await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "Test"
        });

        // Act
        var response = await _client.GetAsync("/api/leaves/my-history");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("requests", out _));
        Assert.True(result.TryGetProperty("totalCount", out _));

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_CancelLeave_RequiresAuthentication()
    {
        // Act
        var response = await _client.DeleteAsync($"/api/leaves/{Guid.NewGuid()}/cancel");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leave_CancelLeave_ValidPendingRequest_Works()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Create a leave request
        var createResponse = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "To cancel"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var leaveId = created.GetProperty("id").GetGuid();

        // Act - Cancel
        var response = await _client.DeleteAsync($"/api/leaves/{leaveId}/cancel");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Leave request cancelled", result.GetProperty("message").GetString());

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_CancelLeave_AlreadyApproved_ReturnsBadRequest()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");

        SetAuthHeader(userToken);
        var createResponse = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "To approve"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var leaveId = created.GetProperty("id").GetGuid();

        // Admin approves it
        SetAuthHeader(adminToken);
        await _client.PostAsJsonAsync($"/api/admin/leaves/{leaveId}/review", new
        {
            approve = true
        });

        // User tries to cancel
        SetAuthHeader(userToken);
        var response = await _client.DeleteAsync($"/api/leaves/{leaveId}/cancel");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Admin_GetPending_RequiresHrOrAdminRole()
    {
        // Arrange - Regular user token
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/admin/leaves/pending");

        // Assert - Forbidden for regular user
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Admin_GetPending_WithAdminToken_Works()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");

        // User creates a leave request
        SetAuthHeader(userToken);
        await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "Admin pending test"
        });

        // Admin views pending
        SetAuthHeader(adminToken);
        var response = await _client.GetAsync("/api/admin/leaves/pending");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("requests", out _));
        Assert.True(result.TryGetProperty("totalCount", out _));

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Admin_ReviewApprove_Works()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");

        SetAuthHeader(userToken);
        var createResponse = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1, // Sick
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "Approve test"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var leaveId = created.GetProperty("id").GetGuid();

        // Act - Admin approves
        SetAuthHeader(adminToken);
        var response = await _client.PostAsJsonAsync($"/api/admin/leaves/{leaveId}/review", new
        {
            approve = true
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approved", result.GetProperty("status").GetString());

        // Verify balance was deducted
        SetAuthHeader(userToken);
        var balanceResponse = await _client.GetAsync("/api/leaves/balance");
        var balance = await balanceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, balance.GetProperty("remainingSickLeave").GetInt32()); // 14 - 2 = 12

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Admin_ReviewReject_RequiresReason()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");

        SetAuthHeader(userToken);
        var createResponse = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "Reject test"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var leaveId = created.GetProperty("id").GetGuid();

        // Act - Admin rejects without reason
        SetAuthHeader(adminToken);
        var response = await _client.PostAsJsonAsync($"/api/admin/leaves/{leaveId}/review", new
        {
            approve = false
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("rejection reason", error, StringComparison.OrdinalIgnoreCase);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Admin_ReviewReject_WithReason_Works()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");

        SetAuthHeader(userToken);
        var createResponse = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "Reject with reason"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var leaveId = created.GetProperty("id").GetGuid();

        // Act - Admin rejects with reason
        SetAuthHeader(adminToken);
        var response = await _client.PostAsJsonAsync($"/api/admin/leaves/{leaveId}/review", new
        {
            approve = false,
            rejectionReason = "Not enough notice"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rejected", result.GetProperty("status").GetString());
        Assert.Equal("Not enough notice", result.GetProperty("rejectionReason").GetString());

        // Balance should NOT be deducted
        SetAuthHeader(userToken);
        var balanceResponse = await _client.GetAsync("/api/leaves/balance");
        var balance = await balanceResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(14, balance.GetProperty("remainingSickLeave").GetInt32()); // Still 14

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Admin_AdjustBalance_Works()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");

        // Get user1's ID
        SetAuthHeader(adminToken);
        var usersResponse = await _client.GetAsync("/api/rbac/users");
        var usersJson = await usersResponse.Content.ReadFromJsonAsync<JsonElement>();
        var user1 = usersJson.GetProperty("items").EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == "user1@company.com");
        var user1Id = user1.GetProperty("id").GetGuid();

        // Act - Adjust balance
        var response = await _client.PostAsJsonAsync("/api/admin/leaves/balance/adjust", new
        {
            userId = user1Id.ToString(),
            year = DateTimeOffset.UtcNow.Year,
            totalSickLeave = 20,
            usedSickLeave = 5,
            totalCasualLeave = 15,
            usedCasualLeave = 3
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(20, result.GetProperty("totalSickLeave").GetInt32());
        Assert.Equal(5, result.GetProperty("usedSickLeave").GetInt32());
        Assert.Equal(15, result.GetProperty("totalCasualLeave").GetInt32());
        Assert.Equal(3, result.GetProperty("usedCasualLeave").GetInt32());

        ClearAuthHeader();
    }
}