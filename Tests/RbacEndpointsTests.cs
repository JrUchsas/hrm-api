using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HrmApi.Tests;

[CollectionDefinition("RbacEndpointsTests", DisableParallelization = true)]
public class RbacEndpointsTestsCollection { }

[Collection("RbacEndpointsTests")]
public sealed class RbacEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public RbacEndpointsTests(WebApplicationFactory<Program> factory)
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
    public async Task Rbac_GetUsers_RequiresAuthentication()
    {
        // Act
        var response = await _client.GetAsync("/api/rbac/users");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rbac_GetUsers_RegularUser_Forbidden()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/rbac/users");

        // Assert - Regular user cannot access
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_GetUsers_AdminToken_ReturnsUsers()
    {
        // Arrange
        var token = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/rbac/users");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.TryGetProperty("Items", out var items));
        Assert.True(result.TryGetProperty("Total", out var total));
        var users = items.EnumerateArray().ToList();
        Assert.True(users.Count >= 5); // At least 5 seeded users (admin, hr, pm, user1-3)

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_GetRoles_AdminToken_ReturnsRoles()
    {
        // Arrange
        var token = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/rbac/roles");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roles = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rolesList = roles.EnumerateArray().ToList();
        Assert.Contains(rolesList, r => r.GetProperty("name").GetString() == "super admin");
        Assert.Contains(rolesList, r => r.GetProperty("name").GetString() == "hr manager");
        Assert.Contains(rolesList, r => r.GetProperty("name").GetString() == "project manager");
        Assert.Contains(rolesList, r => r.GetProperty("name").GetString() == "user");

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_GetPermissions_AdminToken_ReturnsPermissions()
    {
        // Arrange
        var token = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(token);

        // Act
        var response = await _client.GetAsync("/api/rbac/permissions");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var perms = await response.Content.ReadFromJsonAsync<JsonElement>();
        var permsList = perms.EnumerateArray().ToList();
        Assert.True(permsList.Count > 0);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_AssignRole_AdminToken_Works()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);

        // Get user2 ID
        var usersResponse = await _client.GetAsync("/api/rbac/users");
        var usersJson = await usersResponse.Content.ReadFromJsonAsync<JsonElement>();
        var user2 = usersJson.GetProperty("items").EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == "user2@company.com");
        var user2Id = user2.GetProperty("id").GetGuid();

        // Get "project manager" role ID
        var rolesResponse = await _client.GetAsync("/api/rbac/roles");
        var rolesJson = await rolesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pmRole = rolesJson.EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "project manager");
        var pmRoleId = pmRole.GetProperty("id").GetGuid();

        // Act - Assign PM role to user2
        var response = await _client.PostAsJsonAsync($"/api/rbac/users/{user2Id}/roles", new { roleId = pmRoleId.ToString() });

        // Assert - OK or Conflict if already assigned
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Conflict);

        // Verify by re-fetching user roles
        var userRolesResponse = await _client.GetAsync($"/api/rbac/users/{user2Id}/roles");
        Assert.Equal(HttpStatusCode.OK, userRolesResponse.StatusCode);
        var userRoles = await userRolesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var assignedRoles = userRoles.GetProperty("roles").EnumerateArray().ToList();
        Assert.Contains(assignedRoles, r => r.GetProperty("name").GetString() == "project manager");

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_RemoveRole_AdminToken_Works()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);

        // Get user3 ID (should only have "user" role)
        var usersResponse = await _client.GetAsync("/api/rbac/users");
        var usersJson = await usersResponse.Content.ReadFromJsonAsync<JsonElement>();
        var user3 = usersJson.GetProperty("items").EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == "user3@company.com");
        var user3Id = user3.GetProperty("id").GetGuid();

        // Get "user" role ID
        var rolesResponse = await _client.GetAsync("/api/rbac/roles");
        var rolesJson = await rolesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var userRole = rolesJson.EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "user");
        var userRoleId = userRole.GetProperty("id").GetGuid();

        // Act - Remove user role from user3
        var response = await _client.DeleteAsync($"/api/rbac/users/{user3Id}/roles/{userRoleId}");

        // Assert - OK or NotFound if already removed
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_CreateRole_AdminToken_Works()
    {
        // Arrange
        var token = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(token);

        // Act - Create a new role
        var roleName = $"test-role-{Guid.NewGuid():N}";
        var response = await _client.PostAsJsonAsync("/api/rbac/roles", new
        {
            name = roleName,
            description = "Test role for integration test"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(roleName, result.GetProperty("name").GetString());

        // Cleanup - Delete the created role
        var roleId = result.GetProperty("id").GetGuid();
        await _client.DeleteAsync($"/api/rbac/roles/{roleId}");

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_CreateRole_DuplicateName_ReturnsConflict()
    {
        // Arrange
        var token = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(token);

        // Act - Try to create existing role
        var response = await _client.PostAsJsonAsync("/api/rbac/roles", new
        {
            name = "user",
            description = "Duplicate"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_AssignPermissionToRole_AdminToken_Works()
    {
        // Arrange
        var token = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(token);

        // Get a role and permission
        var rolesResponse = await _client.GetAsync("/api/rbac/roles");
        var rolesJson = await rolesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var testRole = rolesJson.EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "hr manager");
        var roleId = testRole.GetProperty("id").GetGuid();

        var permsResponse = await _client.GetAsync("/api/rbac/permissions");
        var permsJson = await permsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var perm = permsJson.EnumerateArray().First();
        var permId = perm.GetProperty("id").GetGuid();

        // Act - Assign permission to role (expects PermissionIds list)
        var response = await _client.PostAsJsonAsync($"/api/rbac/roles/{roleId}/permissions", new { PermissionIds = new[] { permId.ToString() } });

        // Assert - OK or Conflict if already assigned
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Conflict)
        {
            var error = await response.Content.ReadAsStringAsync();
            Assert.Fail($"Expected OK or Conflict, got {response.StatusCode}: {error}");
        }
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Conflict);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_RegularUserCannotAccessAnyRbacEndpoints()
    {
        // Arrange
        var token = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(token);

        // Act - Try various RBAC endpoints
        var usersResponse = await _client.GetAsync("/api/rbac/users");
        var rolesResponse = await _client.GetAsync("/api/rbac/roles");
        var permsResponse = await _client.GetAsync("/api/rbac/permissions");

        // Assert - All forbidden
        Assert.Equal(HttpStatusCode.Forbidden, usersResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, rolesResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, permsResponse.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_AdminToken_CreatesUser()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);

        var newEmail = $"newuser-{Guid.NewGuid():N}@company.com";

        // Act - Enroll new employee
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = newEmail,
            password = "NewUserPass123!",
            goal = "New employee goal"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(newEmail, result.GetProperty("email").GetString());
        Assert.Equal("New employee goal", result.GetProperty("goal").GetString());
        Assert.Equal("Active", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("emailVerified").GetBoolean());
        Assert.Equal("user", result.GetProperty("role").GetString());

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_HrManagerToken_CreatesUserWithDefaultRole()
    {
        // Arrange
        var hrToken = await LoginAsync("hrmanager@company.com", "HrPass123!");
        SetAuthHeader(hrToken);

        var newEmail = $"hrnewuser-{Guid.NewGuid():N}@company.com";

        // Act - Enroll new employee (HR manager can only assign "user" role)
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = newEmail,
            password = "NewUserPass123!",
            goal = "HR enrolled employee"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(newEmail, result.GetProperty("email").GetString());
        Assert.Equal("user", result.GetProperty("role").GetString());

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_HrManagerCannotAssignNonUserRole()
    {
        // Arrange
        var hrToken = await LoginAsync("hrmanager@company.com", "HrPass123!");

        // Get "project manager" role ID using admin token (HR manager can't list roles)
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);
        var rolesResponse = await _client.GetAsync("/api/rbac/roles");
        Assert.Equal(HttpStatusCode.OK, rolesResponse.StatusCode);
        var rolesContent = await rolesResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Roles response: {rolesContent}");
        var rolesJson = JsonDocument.Parse(rolesContent).RootElement;
        var pmRole = rolesJson.EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "project manager");
        var pmRoleId = pmRole.GetProperty("id").GetGuid();
        ClearAuthHeader();

        // Now use HR manager token
        SetAuthHeader(hrToken);
        var newEmail = $"hrnewuser2-{Guid.NewGuid():N}@company.com";

        // Act - Try to enroll with project manager role
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = newEmail,
            password = "NewUserPass123!",
            goal = "Should fail",
            roleId = pmRoleId.ToString()
        });

        // Assert - HR manager cannot assign non-user role
        var errorContent = await response.Content.ReadAsStringAsync();
        Console.WriteLine($"Enroll response status: {response.StatusCode}");
        Console.WriteLine($"Enroll response: {errorContent}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("only assign user role", errorContent);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_AdminToken_CanAssignAnyRole()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);

        // Get "project manager" role ID
        var rolesResponse = await _client.GetAsync("/api/rbac/roles");
        var rolesJson = await rolesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var pmRole = rolesJson.EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "project manager");
        var pmRoleId = pmRole.GetProperty("id").GetGuid();

        var newEmail = $"adminnewuser-{Guid.NewGuid():N}@company.com";

        // Act - Enroll with project manager role
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = newEmail,
            password = "NewUserPass123!",
            goal = "Admin enrolled PM",
            roleId = pmRoleId.ToString()
        });

        // Assert - Admin can assign any role
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("project manager", result.GetProperty("role").GetString());

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_DuplicateEmail_ReturnsConflict()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);

        // Act - Try to enroll existing user
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = "user1@company.com",
            password = "NewUserPass123!",
            goal = "Duplicate"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("already in use", error);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_InvalidEmail_ReturnsBadRequest()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);

        // Act - Invalid email
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = "invalid-email",
            password = "NewUserPass123!",
            goal = "Test"
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid email", error);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_ShortPassword_ReturnsBadRequest()
    {
        // Arrange
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");
        SetAuthHeader(adminToken);

        // Act - Short password
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = "shortpass@company.com",
            password = "123",
            goal = "Test"
        });

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("8 characters", error);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_RegularUser_Forbidden()
    {
        // Arrange
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(userToken);

        // Act
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = "new@company.com",
            password = "NewUserPass123!",
            goal = "Test"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        ClearAuthHeader();
    }

    [Fact]
    public async Task Rbac_EnrollEmployee_RequiresAuthentication()
    {
        // Act - No auth
        var response = await _client.PostAsJsonAsync("/api/rbac/users/enroll", new
        {
            email = "new@company.com",
            password = "NewUserPass123!",
            goal = "Test"
        });

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}