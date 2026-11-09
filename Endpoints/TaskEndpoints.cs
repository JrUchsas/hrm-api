using System.Security.Claims;
using HrmApi.Data;
using HrmApi.Models;
using HrmApi.Services;
using Microsoft.AspNetCore.Authorization;
using MongoDB.Driver;

namespace HrmApi.Endpoints;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this WebApplication app)
    {
        var tasksGroup = app.MapGroup("/api/tasks")
            .WithTags("Task Management")
            .RequireAuthorization(); // All task endpoints require authentication

        // ============ EMPLOYEE/USER ENDPOINTS (view & complete assigned tasks) ============

        // GET /api/tasks - Get user's assigned tasks with filtering and pagination
        tasksGroup.MapGet("", async (
            ClaimsPrincipal user,
            TaskService taskService,
            TaskState? status = null,
            TaskPriority? priority = null,
            string? category = null,
            string? search = null,
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);
            var result = await taskService.GetTasksAsync(userId, status, priority, category, search, page, pageSize, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("GetMyTasks")
        .Produces<TaskListResponse>();

        // GET /api/tasks/summary - Get user's task summary counts
        tasksGroup.MapGet("/summary", async (
            ClaimsPrincipal user,
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);
            var summary = await taskService.GetTaskSummaryAsync(userId, cancellationToken);
            return Results.Ok(summary);
        })
        .WithName("GetMyTaskSummary")
        .Produces<TaskSummaryResponse>();

        // GET /api/tasks/categories - Get available categories
        tasksGroup.MapGet("/categories", async (
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var categories = await taskService.GetCategoriesAsync(cancellationToken);
            return Results.Ok(categories);
        })
        .WithName("GetTaskCategories")
        .Produces<List<string>>();

        // GET /api/tasks/{id} - Get a specific assigned task
        tasksGroup.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);
            var task = await taskService.GetTaskByIdAsync(id, userId, cancellationToken);
            if (task == null) return Results.NotFound("Task not found.");
            return Results.Ok(task);
        })
        .WithName("GetMyTaskById")
        .Produces<TaskResponse>();

        // POST /api/tasks/{id}/advance - Advance task status (Unopened -> Ongoing -> Completed)
        // Employees can only advance: Unopened -> Ongoing -> Completed (not back to Unopened)
        tasksGroup.MapPost("/{id:guid}/advance", async (
            Guid id,
            ClaimsPrincipal user,
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);

            try
            {
                var task = await taskService.AdvanceTaskStatusAsync(id, userId, cancellationToken);
                return Results.Ok(task);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound("Task not found.");
            }
        })
        .WithName("AdvanceMyTaskStatus")
        .Produces<TaskResponse>();

        // ============ PROJECT MANAGER ENDPOINTS (create & manage assigned tasks) ============

        // GET /api/tasks/assigned - Get tasks assigned BY the project manager
        tasksGroup.MapGet("/assigned", async (
            ClaimsPrincipal user,
            TaskService taskService,
            TaskState? status = null,
            TaskPriority? priority = null,
            string? category = null,
            string? search = null,
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);
            var result = await taskService.GetAssignedTasksAsync(userId, status, priority, category, search, page, pageSize, cancellationToken);
            return Results.Ok(result);
        })
        .RequireAuthorization("Permission:tasks.view_assigned")
        .WithName("GetAssignedTasks")
        .Produces<TaskListResponse>();

        // GET /api/tasks/assigned/summary - Get summary of assigned tasks
        tasksGroup.MapGet("/assigned/summary", async (
            ClaimsPrincipal user,
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);
            var summary = await taskService.GetAssignedTaskSummaryAsync(userId, cancellationToken);
            return Results.Ok(summary);
        })
        .RequireAuthorization("Permission:tasks.view_assigned")
        .WithName("GetAssignedTaskSummary")
        .Produces<TaskSummaryResponse>();

        // GET /api/tasks/assigned/{id} - Get specific assigned task
        tasksGroup.MapGet("/assigned/{id:guid}", async (
            Guid id,
            ClaimsPrincipal user,
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);
            var task = await taskService.GetTaskByIdForPmAsync(id, userId, cancellationToken);
            if (task == null) return Results.NotFound("Task not found or not authorized.");
            return Results.Ok(task);
        })
        .RequireAuthorization("Permission:tasks.view_assigned")
        .WithName("GetAssignedTaskById")
        .Produces<TaskResponse>();

        // POST /api/tasks/assigned - Create and assign task to another user (PM only)
        tasksGroup.MapPost("/assigned", async (
            CreateTaskRequest request,
            ClaimsPrincipal user,
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);
            var userEmail = user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("email")?.Value;

            try
            {
                var task = await taskService.CreateAssignedTaskAsync(userId, request, userEmail ?? string.Empty, cancellationToken);
                return Results.Created($"/api/tasks/assigned/{task.Id}", task);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .RequireAuthorization("Permission:tasks.assign")
        .WithName("CreateAssignedTask")
        .Produces<TaskResponse>();

        // PUT /api/tasks/assigned/{id} - Update assigned task (PM can reassign, update status, etc.)
        tasksGroup.MapPut("/assigned/{id:guid}", async (
            Guid id,
            UpdateTaskRequest request,
            ClaimsPrincipal user,
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);

            try
            {
                var task = await taskService.UpdateAssignedTaskAsync(id, userId, request, cancellationToken);
                return Results.Ok(task);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound("Task not found or not authorized.");
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        })
        .RequireAuthorization("Permission:tasks.assign")
        .WithName("UpdateAssignedTask")
        .Produces<TaskResponse>();

        // POST /api/tasks/assigned/{id}/advance - Advance assigned task status (PM can cycle)
        tasksGroup.MapPost("/assigned/{id:guid}/advance", async (
            Guid id,
            ClaimsPrincipal user,
            TaskService taskService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);

            try
            {
                var task = await taskService.AdvanceAssignedTaskStatusAsync(id, userId, cancellationToken);
                return Results.Ok(task);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound("Task not found or not authorized.");
            }
        })
        .RequireAuthorization("Permission:tasks.assign")
        .WithName("AdvanceAssignedTaskStatus")
        .Produces<TaskResponse>();
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var subClaim = user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (subClaim == null || !Guid.TryParse(subClaim, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user ID in token.");
        }

        return userId;
    }
}