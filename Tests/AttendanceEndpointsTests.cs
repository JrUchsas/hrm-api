using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HrmApi.Tests;

[CollectionDefinition("AttendanceEndpointsTests", DisableParallelization = true)]
public class AttendanceEndpointsTestsCollection { }

[Collection("AttendanceEndpointsTests")]
public sealed class AttendanceEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public AttendanceEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((context, config) =>
            {
                context.HostingEnvironment.EnvironmentName = "Testing";
            });
        });
        _client = factory.CreateClient();
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

    [Fact]
    public async Task Attendance_CheckIn_RequiresAuthentication()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/attendance/check-in", new { });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Attendance_CheckIn_WithValidToken_Works()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.PostAsJsonAsync("/api/attendance/check-in", new { });

        // Assert - May be OK or conflict if already checked in
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Conflict);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Attendance_CheckOut_RequiresAuthentication()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/attendance/check-out", new { });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Attendance_CheckOut_WithValidToken_Works()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.PostAsJsonAsync("/api/attendance/check-out", new { });

        // Assert - May be OK, NotFound, Conflict, or BadRequest if no check-in
        Assert.True(response.StatusCode == HttpStatusCode.OK ||
                    response.StatusCode == HttpStatusCode.NotFound ||
                    response.StatusCode == HttpStatusCode.Conflict ||
                    response.StatusCode == HttpStatusCode.BadRequest);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Attendance_GetMyAttendance_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/attendance/my");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Attendance_GetMyAttendance_WithValidToken_ReturnsData()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/attendance/my");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("records", out _));
        Assert.True(result.TryGetProperty("totalCount", out _));

        ClearAuthHeader();
    }

    [Fact]
    public async Task Attendance_GetMyToday_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/attendance/my/today");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Attendance_GetMyToday_WithValidToken_ReturnsData()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/attendance/my/today");

        // Assert - May be OK or NotFound if no record for today
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Attendance_GetAdminSettings_RequiresSuperAdmin()
    {
        // Arrange - Regular user
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/attendance/admin/settings");

        // Assert - Forbidden for regular user (requires super admin)
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Attendance_GetAdminSettings_WithAdminToken_ReturnsSettings()
    {
        // Arrange
        var token = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/attendance/admin/settings");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("workStartTime", out _));
        Assert.True(result.TryGetProperty("workEndTime", out _));
        Assert.True(result.TryGetProperty("lateCutoffMinutes", out _));
        Assert.True(result.TryGetProperty("minimumWorkingMinutes", out _));

        ClearAuthHeader();
    }

    [Fact]
    public async Task Attendance_Admin_DailyReport_RequiresHrOrAdmin()
    {
        // Arrange - Regular user
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/attendance/admin/daily?date=" + DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"));

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Attendance_Admin_DailyReport_WithAdminToken_Works()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);

        try
        {
            // Act
            var dateStr = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
            var response = await _client.GetAsync("/api/attendance/admin/daily?date=" + dateStr);

            // Assert - Log error if InternalServerError
            var content = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == HttpStatusCode.InternalServerError)
            {
                // Fail with details
                Assert.Fail($"InternalServerError: {content}");
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = JsonSerializer.Deserialize<JsonElement>(content);
            Assert.True(result.TryGetProperty("date", out _));
            Assert.True(result.TryGetProperty("records", out _));
        }
        finally
        {
            ClearAuthHeader();
        }
    }
}