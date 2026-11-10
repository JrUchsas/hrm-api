using System.IO;
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
        var createContent = await createResponse.Content.ReadAsStringAsync();
        if (!createResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Create leave failed: {createResponse.StatusCode} - {createContent}");
        }
        var jsonDoc = JsonDocument.Parse(createContent);
        var leaveId = jsonDoc.RootElement.GetProperty("id").GetGuid();

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
        await CleanupLeaveRequestsAsync();
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
        // Read response content first
        var createContent = await createResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Create response status: {createResponse.StatusCode}");
        Console.WriteLine($"Create response content: {createContent}");
        Console.WriteLine($"Content-Type: {createResponse.Content.Headers.ContentType}");
        if (!createResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Create leave failed: {createResponse.StatusCode} - {createContent}");
        }
        // Parse as JSON
        var jsonDoc = JsonDocument.Parse(createContent);
        var leaveId = jsonDoc.RootElement.GetProperty("id").GetGuid();

        // Act - Admin rejects without reason
        SetAuthHeader(adminToken);
        var response = await _client.PostAsJsonAsync($"/api/admin/leaves/{leaveId}/review", new
        {
            approve = false
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"Reject response: {error}");
        // Error format: {"message":"Rejection reason is required when rejecting a leave request."}
        var startsWithBadRequest = error.StartsWith("{") && error.Contains("\"message\"");
        var hasRejectionReason = error.Contains("rejection reason", StringComparison.OrdinalIgnoreCase);
        Assert.True(startsWithBadRequest && hasRejectionReason);

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

    [Fact]
    public async Task Leave_Upload_RequiresAuthentication()
    {
        // Act - No auth header
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[100]), "file", "test.pdf");
        var response = await _client.PostAsync("/api/leaves/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leave_Upload_ValidPdf_ReturnsUrl()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Create a simple PDF content (minimal PDF)
        var pdfContent = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\nxref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n193\n%%EOF";
        var pdfBytes = System.Text.Encoding.ASCII.GetBytes(pdfContent);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "test.pdf");

        // Act
        var response = await _client.PostAsync("/api/leaves/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("url", out _));
        Assert.True(result.TryGetProperty("fileName", out _));
        Assert.True(result.TryGetProperty("size", out _));
        Assert.True(result.TryGetProperty("contentType", out _));
        Assert.Equal("application/pdf", result.GetProperty("contentType").GetString());
        Assert.StartsWith("/api/leaves/files/", result.GetProperty("url").GetString());

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Upload_NonPdf_ReturnsBadRequest()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("not a pdf"u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", "test.txt");

        // Act
        var response = await _client.PostAsync("/api/leaves/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("Only PDF files are allowed", error);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Upload_EmptyFile_ReturnsBadRequest()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(new byte[0]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "empty.pdf");

        // Act
        var response = await _client.PostAsync("/api/leaves/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("No file uploaded", error);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Upload_Over5MB_ReturnsBadRequest()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Create a 6MB "PDF"
        var largePdf = new byte[6 * 1024 * 1024];
        largePdf[0] = 0x25; // '%'
        largePdf[1] = 0x50; // 'P'
        largePdf[2] = 0x44; // 'D'
        largePdf[3] = 0x46; // 'F'

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(largePdf);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "large.pdf");

        // Act
        var response = await _client.PostAsync("/api/leaves/upload", content);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("5MB", error);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Download_RequiresAuthentication()
    {
        // Act - No auth header
        var response = await _client.GetAsync("/api/leaves/files/nonexistent.pdf");

        // Assert - Should be 401 (auth required) not 404
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leave_Download_ValidFile_ReturnsPdf()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // First upload a file
        var pdfContent = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\nxref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n193\n%%EOF";
        var pdfBytes = System.Text.Encoding.ASCII.GetBytes(pdfContent);

        using var uploadContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        uploadContent.Add(fileContent, "file", "test.pdf");

        var uploadResponse = await _client.PostAsync("/api/leaves/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var url = uploadResult.GetProperty("url").GetString()!;
        var fileName = Path.GetFileName(url);

        // Act - Download the file
        var response = await _client.GetAsync($"/api/leaves/files/{fileName}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var downloadedBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(downloadedBytes.Length > 0);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Download_NonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act - Try to download non-existent file
        var response = await _client.GetAsync("/api/leaves/files/nonexistent-file.pdf");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Download_PathTraversal_ReturnsNotFound()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act - Try path traversal
        var response = await _client.GetAsync("/api/leaves/files/..%2F..%2Fetc%2Fpasswd");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_ApplyLeave_WithAttachment_Works()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // First upload a file
        var pdfContent = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\nxref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n193\n%%EOF";
        var pdfBytes = System.Text.Encoding.ASCII.GetBytes(pdfContent);

        using var uploadContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        uploadContent.Add(fileContent, "file", "medical.pdf");

        var uploadResponse = await _client.PostAsync("/api/leaves/upload", uploadContent);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);
        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var attachmentUrl = uploadResult.GetProperty("url").GetString()!;
        var attachmentName = uploadResult.GetProperty("fileName").GetString()!;

        // Act - Apply leave with attachment
        var response = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1, // Sick
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "Medical leave with attachment",
            attachmentUrl = attachmentUrl,
            attachmentName = attachmentName
        });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(attachmentUrl, result.GetProperty("attachmentUrl").GetString());
        Assert.Equal(attachmentName, result.GetProperty("attachmentName").GetString());

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_CancelLeave_DeletesAttachmentFile()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Upload a file
        var pdfContent = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\nxref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n193\n%%EOF";
        var pdfBytes = System.Text.Encoding.ASCII.GetBytes(pdfContent);

        using var uploadContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        uploadContent.Add(fileContent, "file", "test.pdf");

        var uploadResponse = await _client.PostAsync("/api/leaves/upload", uploadContent);
        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var attachmentUrl = uploadResult.GetProperty("url").GetString()!;
        var fileName = Path.GetFileName(attachmentUrl);

        // Create leave request with attachment
        var createResponse = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "To cancel with attachment",
            attachmentUrl = attachmentUrl,
            attachmentName = "test.pdf"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var leaveId = created.GetProperty("id").GetGuid();

        // Act - Cancel the leave request
        var response = await _client.DeleteAsync($"/api/leaves/{leaveId}/cancel");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify file is deleted - try to download it
        var downloadResponse = await _client.GetAsync($"/api/leaves/files/{fileName}");
        Assert.Equal(HttpStatusCode.NotFound, downloadResponse.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_Admin_Reject_DeletesAttachmentFile()
    {
        // Arrange
        await CleanupLeaveRequestsAsync();
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");

        SetAuthHeader(userToken);

        // Upload a file
        var pdfContent = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\nxref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n0000000115 00000 n \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n193\n%%EOF";
        var pdfBytes = System.Text.Encoding.ASCII.GetBytes(pdfContent);

        using var uploadContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(pdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        uploadContent.Add(fileContent, "file", "test.pdf");

        var uploadResponse = await _client.PostAsync("/api/leaves/upload", uploadContent);
        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
        var attachmentUrl = uploadResult.GetProperty("url").GetString()!;
        var fileName = Path.GetFileName(attachmentUrl);

        // Create leave request with attachment
        var createResponse = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "To reject with attachment",
            attachmentUrl = attachmentUrl,
            attachmentName = "test.pdf"
        });
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var leaveId = created.GetProperty("id").GetGuid();

        // Act - Admin rejects the leave request
        SetAuthHeader(adminToken);
        var response = await _client.PostAsJsonAsync($"/api/admin/leaves/{leaveId}/review", new
        {
            approve = false,
            rejectionReason = "Not approved"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rejected", result.GetProperty("status").GetString());

        // Verify file is deleted - try to download it
        var downloadResponse = await _client.GetAsync($"/api/leaves/files/{fileName}");
        Assert.Equal(HttpStatusCode.NotFound, downloadResponse.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Leave_ErrorFormat_IsConsistent()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act - Apply leave without reason (validation error)
        var response = await _client.PostAsJsonAsync("/api/leaves/apply", new
        {
            leaveType = 1,
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            reason = "" // Empty reason
        });

        // Assert - Error should be JSON with "message" field
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("{", error);
        Assert.Contains("\"message\"", error);
        Assert.Contains("Reason is required", error);

        ClearAuthHeader();
    }
}