using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using HrmApi.Models;
using Xunit;

namespace HrmApi.Tests;

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

    [Fact]
    public async Task Tasks_RegularUserCannotAccessPmEndpoints()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

        SetAuthHeader(userToken);

        // Act - Try to access PM-only endpoints as regular user
        var assignedResponse = await _client.GetAsync("/api/tasks/assigned");
        var assignedSummaryResponse = await _client.GetAsync("/api/tasks/assigned/summary");

        // Assert - Should be forbidden
        Assert.Equal(HttpStatusCode.Forbidden, assignedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, assignedSummaryResponse.StatusCode);
    }

    [Fact]
    public async Task Tasks_UserCanCreateAndManageOwnTasks()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

        SetAuthHeader(userToken);

        // Act - Create a task
        var createRequest = new
        {
            title = "My Own Task",
            description = "Created by me",
            priority = "Medium", // String enum
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd")
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks", createRequest);

        // Assert
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("My Own Task", createdTask.GetProperty("title").GetString());
        AssertStatus(createdTask, "Unopened", "cyan", "Unopened");
        AssertPriority(createdTask, "Medium", "Medium");

        var taskId = createdTask.GetProperty("id").GetGuid();

        // Get task by ID
        var getResponse = await _client.GetAsync($"/api/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getTask = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(taskId, getTask.GetProperty("id").GetGuid());

        // List tasks
        var listResponse = await _client.GetAsync("/api/tasks");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tasks = listJson.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Contains(tasks, t => t.GetProperty("id").GetGuid() == taskId);

        // Get summary
        var summaryResponse = await _client.GetAsync("/api/tasks/summary");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, summary.GetProperty("totalTasks").GetInt32());
        Assert.Equal(0, summary.GetProperty("inProgressCount").GetInt32()); // Unopened doesn't count as Ongoing
    }

    [Fact]
    public async Task Tasks_UserCanAdvanceOwnTaskStatus()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

        SetAuthHeader(userToken);

        // Create task
        var createRequest = new
        {
            title = "Task to Advance",
            description = "Will be advanced",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd")
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = createdTask.GetProperty("id").GetGuid();

        // Act & Assert - Advance status: Unopened ("1") -> Ongoing ("2")
        var advanceResponse = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse.StatusCode);
        var advancedTask = await advanceResponse.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask, "Ongoing", "yellow", "Ongoing");

        // Advance again: Ongoing ("2") -> Completed ("3")
        var advanceResponse2 = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse2.StatusCode);
        var advancedTask2 = await advanceResponse2.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask2, "Completed", "green", "Completed");

        // Advance again: Completed ("3") -> Unopened ("1") - cycle
        var advanceResponse3 = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        Assert.Equal(HttpStatusCode.OK, advanceResponse3.StatusCode);
        var advancedTask3 = await advanceResponse3.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(advancedTask3, "Unopened", "cyan", "Unopened");
    }

    [Fact]
    public async Task Tasks_UserCanUpdateOwnTask()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

        SetAuthHeader(userToken);

        // Create task
        var createRequest = new
        {
            title = "Original Title",
            description = "Original Description",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd")
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = createdTask.GetProperty("id").GetGuid();

        // Act - Update task
        var updateRequest = new
        {
            title = "Updated Title",
            description = "Updated Description",
            status = "3", // Completed
            priority = "Low",
            category = "Security"
        };

        var updateResponse = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", updateRequest);

        // Assert
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatedTask = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated Title", updatedTask.GetProperty("title").GetString());
        Assert.Equal("Updated Description", updatedTask.GetProperty("description").GetString());
        AssertStatus(updatedTask, "Completed", "green", "Completed");
        AssertPriority(updatedTask, "Low", "Low");
        Assert.Equal("Security", updatedTask.GetProperty("category").GetString());
    }

    [Fact]
    public async Task Tasks_UserCanDeleteOwnTask()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

        SetAuthHeader(userToken);

        // Create task
        var createRequest = new
        {
            title = "Task to Delete",
            description = "Will be deleted",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd")
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = createdTask.GetProperty("id").GetGuid();

        // Act - Delete task
        var deleteResponse = await _client.DeleteAsync($"/api/tasks/{taskId}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // Verify deleted
        var getResponse = await _client.GetAsync($"/api/tasks/{taskId}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Tasks_StatusColorMapping()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

        SetAuthHeader(userToken);

        // Create task (default status = Unopened)
        var createRequest = new
        {
            title = "Color Test Task",
            description = "Testing status colors",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd")
        };

        var createResponse = await _client.PostAsJsonAsync("/api/tasks", createRequest);
        var createdTask = await createResponse.Content.ReadFromJsonAsync<JsonElement>();

        // Assert - Default status should be Unopened with cyan color
        AssertStatus(createdTask, "Unopened", "cyan", "Unopened");

        // Advance to Ongoing (yellow)
        var taskId = createdTask.GetProperty("id").GetGuid();
        var advance1 = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        var task1 = await advance1.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(task1, "Ongoing", "yellow", "Ongoing");

        // Advance to Completed (green)
        var advance2 = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        var task2 = await advance2.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(task2, "Completed", "green", "Completed");

        // Advance to Unopened (cyan) - cycle back
        var advance3 = await _client.PostAsync($"/api/tasks/{taskId}/advance", null);
        var task3 = await advance3.Content.ReadFromJsonAsync<JsonElement>();
        AssertStatus(task3, "Unopened", "cyan", "Unopened");
    }

    [Fact]
    public async Task Tasks_TaskFilteringAndPagination()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

        SetAuthHeader(userToken);

        // Create multiple tasks with different statuses and priorities
        for (int i = 0; i < 5; i++)
        {
            var priorities = new[] { "Low", "Medium", "High" };
            var priority = priorities[i % 3]; // Low, Medium, High, Low, Medium
            await _client.PostAsJsonAsync("/api/tasks", new
            {
                title = $"Task {i}",
                description = $"Description {i}",
                priority = priority,
                category = i % 2 == 0 ? "Operations" : "Security",
                dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd")
            });
        }

        // Advance some tasks to different statuses
        var listResponse = await _client.GetAsync("/api/tasks");
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tasks = listJson.GetProperty("tasks").EnumerateArray().ToList();

        // Advance first 2 tasks to Ongoing
        await _client.PostAsync($"/api/tasks/{tasks[0].GetProperty("id").GetGuid()}/advance", null);
        await _client.PostAsync($"/api/tasks/{tasks[1].GetProperty("id").GetGuid()}/advance", null);

        // Advance next 2 to Completed (advance twice: Unopened -> Ongoing -> Completed)
        await _client.PostAsync($"/api/tasks/{tasks[2].GetProperty("id").GetGuid()}/advance", null);
        await _client.PostAsync($"/api/tasks/{tasks[2].GetProperty("id").GetGuid()}/advance", null);
        await _client.PostAsync($"/api/tasks/{tasks[3].GetProperty("id").GetGuid()}/advance", null);
        await _client.PostAsync($"/api/tasks/{tasks[3].GetProperty("id").GetGuid()}/advance", null);

        // Test filtering by status="2" (Ongoing)
        var filterStatusResponse = await _client.GetAsync("/api/tasks?status=2");
        Assert.Equal(HttpStatusCode.OK, filterStatusResponse.StatusCode);
        var filterStatusJson = await filterStatusResponse.Content.ReadFromJsonAsync<JsonElement>();
        var ongoingTasks = filterStatusJson.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(2, ongoingTasks.Count);

        // Test filtering by priority="High"
        var filterPriorityResponse = await _client.GetAsync("/api/tasks?priority=High");
        Assert.Equal(HttpStatusCode.OK, filterPriorityResponse.StatusCode);
        var filterPriorityJson = await filterPriorityResponse.Content.ReadFromJsonAsync<JsonElement>();
        var highPriorityTasks = filterPriorityJson.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(1, highPriorityTasks.Count); // 1 high priority task created

        // Test filtering by category
        var filterCategoryResponse = await _client.GetAsync("/api/tasks?category=Security");
        Assert.Equal(HttpStatusCode.OK, filterCategoryResponse.StatusCode);
        var filterCategoryJson = await filterCategoryResponse.Content.ReadFromJsonAsync<JsonElement>();
        var securityTasks = filterCategoryJson.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(2, securityTasks.Count); // 2 Security tasks

        // Test pagination
        var page1Response = await _client.GetAsync("/api/tasks?page=1&pageSize=3");
        Assert.Equal(HttpStatusCode.OK, page1Response.StatusCode);
        var page1Json = await page1Response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, page1Json.GetProperty("tasks").EnumerateArray().Count());
        Assert.Equal(1, page1Json.GetProperty("page").GetInt32());
        Assert.Equal(3, page1Json.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, page1Json.GetProperty("totalPages").GetInt32()); // 5 tasks / 3 = 2 pages

        var page2Response = await _client.GetAsync("/api/tasks?page=2&pageSize=3");
        Assert.Equal(HttpStatusCode.OK, page2Response.StatusCode);
        var page2Json = await page2Response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, page2Json.GetProperty("tasks").EnumerateArray().Count());
        Assert.Equal(2, page2Json.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Tasks_CategoriesEndpoint()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

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
    public async Task Tasks_SummaryIncludesDueCount()
    {
        // Arrange - Create and login as regular user
        var userEmail = $"user_{Guid.NewGuid():N}@test.com";
        var userPassword = "UserPass123!";
        await RegisterAsync(userEmail, userPassword, "Regular User");
        var userToken = await LoginAsync(userEmail, userPassword);

        SetAuthHeader(userToken);

        // Create task with past due date
        var createRequest = new
        {
            title = "Overdue Task",
            description = "Past due",
            priority = "Medium",
            category = "Operations",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd") // Yesterday
        };

        await _client.PostAsJsonAsync("/api/tasks", createRequest);

        // Get the task ID
        var listResponse = await _client.GetAsync("/api/tasks");
        var listJson = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var task = listJson.GetProperty("tasks").EnumerateArray().First();
        var taskId = task.GetProperty("id").GetGuid();

        // Manually update to Due status via PUT
        await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new { status = "Due" }); // Due

        // Get summary
        var summaryResponse = await _client.GetAsync("/api/tasks/summary");
        Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);
        var summary = await summaryResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, summary.GetProperty("totalTasks").GetInt32());
        Assert.Equal(0, summary.GetProperty("inProgressCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("completedCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("dueCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("highPriorityCount").GetInt32());
    }
}