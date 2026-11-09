using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using HrmApi.Data;
using HrmApi.Models;
using Xunit;

namespace HrmApi.Tests;

// Disable parallelization to avoid database conflicts between tests
[CollectionDefinition("TaskEndpointsTests", DisableParallelization = true)]
public class TaskEndpointsTestsCollection { }

[Collection("TaskEndpointsTests")]
public sealed class TaskEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;

    public TaskEndpointsTests(WebApplicationFactory<Program> factory)
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

    private async Task CleanupTasksAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
        await dbContext.Tasks.DeleteManyAsync(_ => true);
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

    private async Task<string> RegisterAsync(string email, string password, string? goal = null)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            goal
        });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Register failed for {email}: {response.StatusCode} - {error}");
        }
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        return result.GetProperty("accessToken").GetString()!;
    }

    private async Task AssignRoleAsync(string adminToken, string targetEmail, string roleName)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        // Get target user ID
        var usersResponse = await _client.GetAsync("/api/rbac/users");
        var usersJson = await usersResponse.Content.ReadFromJsonAsync<JsonElement>();
        var targetUser = usersJson.GetProperty("items").EnumerateArray()
            .FirstOrDefault(u => u.GetProperty("email").GetString() == targetEmail);

        if (targetUser.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"User {targetEmail} not found");

        var targetUserId = targetUser.GetProperty("id").GetGuid();

        // Get role ID
        var rolesResponse = await _client.GetAsync("/api/rbac/roles");
        var rolesJson = await rolesResponse.Content.ReadFromJsonAsync<JsonElement>();
        var role = rolesJson.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("name").GetString() == roleName);

        if (role.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Role {roleName} not found");

        var roleId = role.GetProperty("id").GetGuid();

        // Assign role - expects { "roleId": "guid" }
        var assignResponse = await _client.PostAsJsonAsync($"/api/rbac/users/{targetUserId}/roles", new { roleId = roleId.ToString() });
        var responseContent = await assignResponse.Content.ReadAsStringAsync();
        if (!assignResponse.IsSuccessStatusCode && assignResponse.StatusCode != HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException($"Failed to assign role: {assignResponse.StatusCode} - {responseContent}");
        }
        // If Conflict (409), role already assigned - that's fine
    }

    private void SetAuthHeader(string token)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private void ClearAuthHeader()
    {
        _client.DefaultRequestHeaders.Authorization = null;
    }

    // Helper to check enum status as string
    private void AssertStatus(JsonElement task, string expectedStatusName, string expectedColor, string expectedDisplay)
    {
        Assert.Equal(expectedStatusName, task.GetProperty("status").GetString());
        Assert.Equal(expectedColor, task.GetProperty("statusColor").GetString());
        Assert.Equal(expectedDisplay, task.GetProperty("statusDisplay").GetString());
    }

    private void AssertPriority(JsonElement task, string expectedPriorityName, string expectedDisplay)
    {
        Assert.Equal(expectedPriorityName, task.GetProperty("priority").GetString());
        Assert.Equal(expectedDisplay, task.GetProperty("priorityDisplay").GetString());
    }

    private async Task<(string adminToken, string pmToken, string user1Token, string user2Token, Guid user1Id, Guid user2Id)> SetupPmAndUsersAsync()
    {
        // Use pre-seeded users
        var adminToken = await LoginAsync("admin@company.com", "AdminPass123!");

        // Use hrmanager@company.com as PM (already exists, just needs PM role)
        var pmEmail = "hrmanager@company.com";
        var pmPassword = "HrPass123!";

        // Assign PM role to hrmanager using seeded admin
        await AssignRoleAsync(adminToken, pmEmail, "project manager");

        // Re-login as PM to get fresh token with new permissions
        var pmToken = await LoginAsync(pmEmail, pmPassword);

        // Use seeded user1 and user2 as employees
        var user1Email = "user1@company.com";
        var user1Password = "UserPass123!";
        var user1Token = await LoginAsync(user1Email, user1Password);

        var user2Email = "user2@company.com";
        var user2Password = "UserPass123!";
        var user2Token = await LoginAsync(user2Email, user2Password);

        // Get user IDs using seeded admin
        ClearAuthHeader();
        SetAuthHeader(adminToken);
        var usersResponse = await _client.GetAsync("/api/rbac/users");
        var usersJson = await usersResponse.Content.ReadFromJsonAsync<JsonElement>();
        var user1 = usersJson.GetProperty("items").EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == user1Email);
        var user2 = usersJson.GetProperty("items").EnumerateArray()
            .First(u => u.GetProperty("email").GetString() == user2Email);
        var user1Id = user1.GetProperty("id").GetGuid();
        var user2Id = user2.GetProperty("id").GetGuid();

        return (adminToken, pmToken, user1Token, user2Token, user1Id, user2Id);
    }

    [Fact]
    public async Task Tasks_RegularUserCannotAccessPmEndpoints()
    {
        await CleanupTasksAsync();
        // Arrange - Use pre-seeded regular user (user1@company.com)
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(userToken);

        // Act - Try to access PM-only endpoints as regular user
        var assignedResponse = await _client.GetAsync("/api/tasks/assigned");
        var assignedSummaryResponse = await _client.GetAsync("/api/tasks/assigned/summary");
        var createAssignedResponse = await _client.PostAsJsonAsync("/api/tasks/assigned", new
        {
            title = "Test",
            description = "Test",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            assigneeUserId = Guid.NewGuid().ToString()
        });

        // Assert - Should be forbidden
        Assert.Equal(HttpStatusCode.Forbidden, assignedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, assignedSummaryResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, createAssignedResponse.StatusCode);
    }

    [Fact]
    public async Task Tasks_RegularUserCannotCreateOwnTask()
    {
        await CleanupTasksAsync();
        // Arrange - Use pre-seeded regular user (user1@company.com)
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(userToken);

        // Act - Try to create a task (should fail - no endpoint for regular users to create)
        var createRequest = new
        {
            title = "My Own Task",
            description = "Created by me",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd")
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks", createRequest);

        // Assert - Should be method not allowed or not found (endpoint doesn't exist for regular users)
        Assert.Equal(HttpStatusCode.MethodNotAllowed, createResponse.StatusCode);
    }

    [Fact]
    public async Task Tasks_PmCanCreateAndAssignTaskToUser()
    {
        await CleanupTasksAsync();
        // Arrange - Setup PM and users
        var (_, pmToken, user1Token, _, user1Id, _) = await SetupPmAndUsersAsync();

        // Act - PM creates and assigns task
        SetAuthHeader(pmToken);

        var createRequest = new
        {
            title = "Test Assigned Task",
            description = "Assigned by PM",
            priority = "High",
            category = "Security",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            assigneeUserId = user1Id.ToString()
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks/assigned", createRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Test Assigned Task", createdTask.GetProperty("title").GetString());
        AssertStatus(createdTask, "Unopened", "cyan", "Unopened");
        Assert.True(createdTask.TryGetProperty("assignedByUserId", out _));
        Assert.True(createdTask.TryGetProperty("assignedAtUtc", out _));
        Assert.True(createdTask.TryGetProperty("assigneeEmail", out _));

        var taskId = createdTask.GetProperty("id").GetGuid();

        // Verify assignee can see the task in their own tasks
        ClearAuthHeader();
        SetAuthHeader(user1Token);
        var myTasksResponse = await _client.GetAsync("/api/tasks");
        var myTasksJson = await myTasksResponse.Content.ReadFromJsonAsync<JsonElement>();
        var myTasks = myTasksJson.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Contains(myTasks, t => t.GetProperty("id").GetGuid() == taskId);

        // PM can see it in their assigned list
        ClearAuthHeader();
        SetAuthHeader(pmToken);
        var assignedResponse = await _client.GetAsync("/api/tasks/assigned");
        var assignedJson = await assignedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var assignedTasks = assignedJson.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Contains(assignedTasks, t => t.GetProperty("id").GetGuid() == taskId);
    }

    [Fact]
    public async Task Tasks_EmployeeCanAdvanceTaskForwardOnly()
    {
        await CleanupTasksAsync();
        // Arrange - Setup PM and users
        var (_, pmToken, user1Token, _, user1Id, _) = await SetupPmAndUsersAsync();

        // PM creates task for user1
        SetAuthHeader(pmToken);
        var createRequest = new
        {
            title = "Task to Advance",
            description = "Will be advanced by employee",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            assigneeUserId = user1Id.ToString()
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks/assigned", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = createdTask.GetProperty("id").GetGuid();

        // Act & Assert - Employee advances: Unopened -> Ongoing
        ClearAuthHeader();
        SetAuthHeader(user1Token);

        var advanceResponse = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse.StatusCode);
        var advancedTask = await advanceResponse.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask, "Ongoing", "yellow", "Ongoing");

        // Employee advances: Ongoing -> Completed
        var advanceResponse2 = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse2.StatusCode);
        var advancedTask2 = await advanceResponse2.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask2, "Completed", "green", "Completed");

        // Employee tries to advance again - should stay Completed (not cycle back)
        var advanceResponse3 = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse3.StatusCode);
        var advancedTask3 = await advanceResponse3.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask3, "Completed", "green", "Completed"); // Stays Completed
    }

    [Fact]
    public async Task Tasks_PmCanCycleTaskStatus()
    {
        await CleanupTasksAsync();
        // Arrange - Setup PM and users
        var (_, pmToken, _, _, user1Id, _) = await SetupPmAndUsersAsync();

        // PM creates task
        SetAuthHeader(pmToken);
        var createRequest = new
        {
            title = "PM Task to Cycle",
            description = "PM can cycle status",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            assigneeUserId = user1Id.ToString()
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks/assigned", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = createdTask.GetProperty("id").GetGuid();

        // PM advances via assigned endpoint: Unopened -> Ongoing
        var advanceResponse = await _client.PostAsync($"/api/tasks/assigned/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse.StatusCode);
        var advancedTask = await advanceResponse.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask, "Ongoing", "yellow", "Ongoing");

        // PM advances: Ongoing -> Completed
        var advanceResponse2 = await _client.PostAsync($"/api/tasks/assigned/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse2.StatusCode);
        var advancedTask2 = await advanceResponse2.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask2, "Completed", "green", "Completed");

        // PM advances: Completed -> Unopened (PM can cycle back)
        var advanceResponse3 = await _client.PostAsync($"/api/tasks/assigned/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse3.StatusCode);
        var advancedTask3 = await advanceResponse3.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask3, "Unopened", "cyan", "Unopened");
    }

    [Fact]
    public async Task Tasks_PmCanReassignTask()
    {
        await CleanupTasksAsync();
        // Arrange - Setup PM and users
        var (_, pmToken, user1Token, user2Token, user1Id, user2Id) = await SetupPmAndUsersAsync();

        // PM creates task assigned to user1
        SetAuthHeader(pmToken);
        var createRequest = new
        {
            title = "Task to Reassign",
            description = "Will be reassigned",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            assigneeUserId = user1Id.ToString()
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks/assigned", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = createdTask.GetProperty("id").GetGuid();

        // Act - PM reassigns to user2
        var updateRequest = new { assigneeUserId = user2Id.ToString() };
        var updateResponse = await _client.PutAsJsonAsync($"/api/tasks/assigned/{taskId}", updateRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatedTask = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(user2Id, updatedTask.GetProperty("userId").GetGuid());

        // Verify via PM's assigned list
        ClearAuthHeader();
        SetAuthHeader(pmToken);
        var assignedResponse = await _client.GetAsync("/api/tasks/assigned");
        var assignedJson = await assignedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var assignedTasks = assignedJson.GetProperty("tasks").EnumerateArray().ToList();
        var updatedTaskInList = assignedTasks.First(t => t.GetProperty("id").GetGuid() == taskId);
        Assert.Equal(user2Id, updatedTaskInList.GetProperty("userId").GetGuid());

        // Verify user2 can see it in their tasks
        ClearAuthHeader();
        SetAuthHeader(user2Token);
        var user2TasksResponse = await _client.GetAsync("/api/tasks");
        var user2TasksJson = await user2TasksResponse.Content.ReadFromJsonAsync<JsonElement>();
        var user2Tasks = user2TasksJson.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Contains(user2Tasks, t => t.GetProperty("id").GetGuid() == taskId);
    }

    [Fact]
    public async Task Tasks_StatusColorMapping()
    {
        await CleanupTasksAsync();
        // Arrange - Setup PM and users
        var (_, pmToken, _, _, user1Id, _) = await SetupPmAndUsersAsync();

        // PM creates task
        SetAuthHeader(pmToken);
        var createRequest = new
        {
            title = "Color Test Task",
            description = "Testing status colors",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            assigneeUserId = user1Id.ToString()
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks/assigned", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Assert - Default status should be Unopened with cyan color
        AssertStatus(createdTask, "Unopened", "cyan", "Unopened");

        // PM advances to Ongoing (yellow)
        var taskId = createdTask.GetProperty("id").GetGuid();
        var advance1 = await _client.PostAsync($"/api/tasks/assigned/{taskId}/advance", null);
        var task1 = await advance1.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(task1, "Ongoing", "yellow", "Ongoing");

        // PM advances to Completed (green)
        var advance2 = await _client.PostAsync($"/api/tasks/assigned/{taskId}/advance", null);
        var task2 = await advance2.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(task2, "Completed", "green", "Completed");

        // PM advances to Unopened (cyan) - cycle back
        var advance3 = await _client.PostAsync($"/api/tasks/assigned/{taskId}/advance", null);
        var task3 = await advance3.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(task3, "Unopened", "cyan", "Unopened");
    }

    [Fact]
    public async Task Tasks_PmCanViewAssignedTaskSummary()
    {
        await CleanupTasksAsync();
        // Arrange - Setup PM and users
        var (_, pmToken, _, _, user1Id, _) = await SetupPmAndUsersAsync();

        // PM creates tasks
        SetAuthHeader(pmToken);
        for (int i = 0; i < 3; i++)
        {
            var response = await _client.PostAsJsonAsync("/api/tasks/assigned", new
            {
                title = $"Assigned Task {i}",
                description = "Test",
                priority = i == 0 ? "High" : "Medium",
                category = "Operations",
                dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
                assigneeUserId = user1Id.ToString()
            });
            var content = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.Created)
            {
                throw new InvalidOperationException($"Task creation failed: {response.StatusCode} - {content}");
            }
            var taskJson = await response.Content.ReadFromJsonAsync<JsonElement>();
            var taskId = taskJson.GetProperty("id").GetGuid();
        }

        // Verify tasks exist
        var listResponse = await _client.GetAsync("/api/tasks/assigned");
        var listContent = await listResponse.Content.ReadAsStringAsync();

        // Debug summary response
        var summaryDebugResponse = await _client.GetAsync("/api/tasks/assigned/summary");
        var summaryDebugContent = await summaryDebugResponse.Content.ReadAsStringAsync();

        // Act - Get PM assigned summary
        var summaryResponse = await _client.GetAsync("/api/tasks/assigned/summary");

        // Assert
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, summary.GetProperty("totalTasks").GetInt32());
        Assert.Equal(0, summary.GetProperty("inProgressCount").GetInt32()); // Unopened != Ongoing
        Assert.Equal(0, summary.GetProperty("completedCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("dueCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("highPriorityCount").GetInt32()); // One high priority
    }

    [Fact]
    public async Task Tasks_EmployeeCanViewOwnTasksAndSummary()
    {
        await CleanupTasksAsync();
        // Arrange - Setup PM and users
        var (_, pmToken, user1Token, _, user1Id, _) = await SetupPmAndUsersAsync();

        // PM creates 3 tasks for user1
        SetAuthHeader(pmToken);
        for (int i = 0; i < 3; i++)
        {
            var response = await _client.PostAsJsonAsync("/api/tasks/assigned", new
            {
                title = $"Employee Task {i}",
                description = "Test",
                priority = i == 0 ? "High" : "Medium",
                category = "Operations",
                dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
                assigneeUserId = user1Id.ToString()
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        // Act - User1 views their tasks
        ClearAuthHeader();
        SetAuthHeader(user1Token);

        var tasksResponse = await _client.GetAsync("/api/tasks");
        Assert.Equal(HttpStatusCode.OK, tasksResponse.StatusCode);
        var tasksJson = await tasksResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tasks = tasksJson.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(3, tasks.Count);

        var summaryResponse = await _client.GetAsync("/api/tasks/summary");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, summary.GetProperty("totalTasks").GetInt32());
        Assert.Equal(0, summary.GetProperty("inProgressCount").GetInt32()); // Unopened != Ongoing
        Assert.Equal(0, summary.GetProperty("completedCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("highPriorityCount").GetInt32());
    }

    [Fact]
    public async Task Tasks_CategoriesEndpoint()
    {
        await CleanupTasksAsync();
        // Arrange - Use pre-seeded regular user (user1@company.com)
        var userToken = await LoginAsync("user1@company.com", "UserPass123!");
        SetAuthHeader(userToken);

        // Act
        var response = await _client.GetAsync("/api/tasks/categories");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var categories = await response.Content.ReadFromJsonAsync<JsonElement>();
        var categoriesList = categories.EnumerateArray().ToList();

        // Should contain default categories
        Assert.Contains(categoriesList, c => c.GetString() == "Operations");
        Assert.Contains(categoriesList, c => c.GetString() == "Compliance");
        Assert.Contains(categoriesList, c => c.GetString() == "Security");
        Assert.Contains(categoriesList, c => c.GetString() == "Goals");
        Assert.Contains(categoriesList, c => c.GetString() == "Profile");
        Assert.Contains(categoriesList, c => c.GetString() == "Projects");
    }

    [Fact]
    public async Task Tasks_EmployeeCanViewSpecificTask()
    {
        await CleanupTasksAsync();
        // Arrange - Setup PM and users
        var (_, pmToken, user1Token, _, user1Id, _) = await SetupPmAndUsersAsync();

        // PM creates task for user1
        SetAuthHeader(pmToken);
        var createRequest = new
        {
            title = "Specific Task",
            description = "View specific task",
            priority = "High",
            category = "Security",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            assigneeUserId = user1Id.ToString()
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks/assigned", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = createdTask.GetProperty("id").GetGuid();

        // Act - User1 views specific task
        ClearAuthHeader();
        SetAuthHeader(user1Token);

        var getResponse = await _client.GetAsync($"/api/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var task = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Specific Task", task.GetProperty("title").GetString());
        Assert.Equal("Security", task.GetProperty("category").GetString());
        AssertStatus(task, "Unopened", "cyan", "Unopened");
    }
}