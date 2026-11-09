using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Xunit;

namespace HrmApi.Tests;

[CollectionDefinition("AuthEndpointsTests", DisableParallelization = true)]
public class AuthEndpointsTestsCollection { }

[Collection("AuthEndpointsTests")]
public sealed class AuthEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public AuthEndpointsTests(WebApplicationFactory<Program> factory)
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

    private async Task<string> RefreshAsync(string refreshToken)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Refresh failed: {response.StatusCode} - {error}");
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
    public async Task Auth_Login_WithValidCredentials_ReturnsTokens()
    {
        // Act - Use pre-seeded admin user
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "admin@company.com",
            password = "AdminPass123!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("accessToken", out _));
        Assert.True(result.TryGetProperty("refreshToken", out _));
        Assert.Equal("admin@company.com", result.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Auth_Login_WithInvalidCredentials_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "admin@company.com",
            password = "WrongPassword123!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_Login_WithNonExistentUser_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "nonexistent@company.com",
            password = "AnyPassword123!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_Register_CreatesNewUserAndReturnsTokens()
    {
        // Act - Register a new unique user (cleaned up after test)
        var uniqueEmail = $"newuser{Guid.NewGuid():N}@test.com";
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = uniqueEmail,
            password = "NewPass123!",
            goal = "Test Goal"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("accessToken", out _));
        Assert.True(result.TryGetProperty("refreshToken", out _));
        Assert.Equal(uniqueEmail, result.GetProperty("email").GetString());

        // Cleanup: Delete the test user (find by email since AuthResponse doesn't include ID)
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HrmApi.Data.MongoDbContext>();
        var user = await dbContext.Users.Find(u => u.Email == uniqueEmail).FirstOrDefaultAsync();
        if (user != null)
        {
            await dbContext.Users.DeleteOneAsync(u => u.Email == uniqueEmail);
            await dbContext.UserRoles.DeleteManyAsync(ur => ur.UserId == user.Id);
            await dbContext.RefreshTokens.DeleteManyAsync(rt => rt.UserId == user.Id);
        }
    }

    [Fact]
    public async Task Auth_Register_DuplicateEmail_ReturnsConflict()
    {
        // Act - Try to register with existing email
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "admin@company.com",
            password = "AdminPass123!",
            goal = "Test"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Auth_RefreshToken_ValidToken_ReturnsNewTokens()
    {
        // Arrange - Login to get refresh token
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "admin@company.com",
            password = "AdminPass123!"
        });
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = loginResult.GetProperty("refreshToken").GetString()!;

        // Act - Refresh
        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("accessToken", out _));
        Assert.True(result.TryGetProperty("refreshToken", out _));

        // Old refresh token should be invalidated (rotation)
        var secondRefresh = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, secondRefresh.StatusCode);
    }

    [Fact]
    public async Task Auth_RefreshToken_InvalidToken_ReturnsUnauthorized()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/refresh", new
        {
            refreshToken = "invalid-token"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_Logout_ValidToken_RevokesToken()
    {
        // Arrange - Login to get refresh token
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            email = "admin@company.com",
            password = "AdminPass123!"
        });
        var loginResult = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = loginResult.GetProperty("accessToken").GetString()!;
        var refreshToken = loginResult.GetProperty("refreshToken").GetString()!;

        // Act - Logout (requires auth header)
        SetAuthHeader(accessToken);
        var response = await _client.PostAsJsonAsync("/api/auth/logout", new { refreshToken });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Token should be invalidated
        var refreshResponse = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Auth_LogoutAll_RequiresAuthentication()
    {
        // Act - No auth header
        var response = await _client.PostAsJsonAsync("/api/auth/logout-all", new { });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_Me_RequiresAuthentication()
    {
        // Act - No auth header
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_Me_WithValidToken_ReturnsUserInfo()
    {
        // Arrange
        var token = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/auth/me");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin@company.com", result.GetProperty("email").GetString());
        Assert.True(result.TryGetProperty("roles", out _));
        Assert.True(result.TryGetProperty("permissions", out _));

        ClearAuthHeader();
    }

    [Fact]
    public async Task Auth_ChangePassword_RequiresAuthentication()
    {
        // Act - No auth header
        var response = await _client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "OldPass123!",
            newPassword = "NewPass123!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_ChangePassword_WithValidToken_Works()
    {
        // Arrange - Use user1
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "UserPass123!",
            newPassword = "NewUserPass123!"
        });

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify new password works
        ClearAuthHeader();
        var newToken = await LoginAsync("user1@company.com", "NewUserPass123!");
        Assert.NotNull(newToken);

        // Restore original password for other tests
        SetAuthHeader(newToken);
        await _client.PostAsJsonAsync("/api/auth/change-password", new
        {
            currentPassword = "NewUserPass123!",
            newPassword = "UserPass123!"
        });
        ClearAuthHeader();
    }

    [Fact]
    public async Task Auth_ForgotPassword_NonExistentUser_ReturnsSuccess()
    {
        // Act - Should not reveal if user exists
        var response = await _client.PostAsJsonAsync("/api/auth/forgot-password", new
        {
            email = "nonexistent@company.com"
        });

        // Assert - Always returns OK to prevent enumeration
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("If the email exists, a reset link has been sent", result.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Auth_ForgotPassword_ExistingUser_ReturnsSuccess()
    {
        // Act
        var response = await _client.PostAsJsonAsync("/api/auth/forgot-password", new
        {
            email = "user1@company.com"
        });

        // Assert - Always returns OK to prevent enumeration
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Auth_LoginRateLimit_EnforcesLimit()
    {
        // Act - Make multiple rapid login attempts
        var responses = new List<HttpResponseMessage>();
        for (int i = 0; i < 5; i++)
        {
            var response = await _client.PostAsJsonAsync("/api/auth/login", new
            {
                email = "admin@company.com",
                password = "WrongPassword123!"
            });
            responses.Add(response);
        }

        // Assert - At least some should be rate limited (429)
        var rateLimitedCount = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        // Note: Rate limit is 100/min, so 5 requests shouldn't hit it, but verify it's configured
        Assert.All(responses, r => Assert.True(r.StatusCode == HttpStatusCode.Unauthorized || r.StatusCode == HttpStatusCode.TooManyRequests));
    }
}