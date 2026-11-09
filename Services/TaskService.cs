using HrmApi.Data;
using HrmApi.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace HrmApi.Services;

public sealed class TaskService
{
    private readonly MongoDbContext _db;

    public TaskService(MongoDbContext db)
    {
        _db = db;
    }

    // Get tasks assigned TO a user (employee view)
    public async Task<TaskListResponse> GetTasksAsync(
        Guid userId,
        TaskState? status,
        TaskPriority? priority,
        string? category,
        string? search,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var filterBuilder = Builders<TaskItem>.Filter;
        var filter = filterBuilder.Eq(t => t.UserId, userId);

        if (status.HasValue)
        {
            filter &= filterBuilder.Eq(t => t.Status, status.Value);
        }

        if (priority.HasValue)
        {
            filter &= filterBuilder.Eq(t => t.Priority, priority.Value);
        }

        if (!string.IsNullOrWhiteSpace(category) && category != "All")
        {
            filter &= filterBuilder.Eq(t => t.Category, category);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchFilter = filterBuilder.Or(
                filterBuilder.Regex(t => t.Title, new BsonRegularExpression(search, "i")),
                filterBuilder.Regex(t => t.Description, new BsonRegularExpression(search, "i")),
                filterBuilder.Regex(t => t.Category, new BsonRegularExpression(search, "i"))
            );
            filter &= searchFilter;
        }

        var totalCount = await _db.Tasks.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var tasks = await _db.Tasks
            .Find(filter)
            .SortByDescending(t => t.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        return new TaskListResponse
        {
            Tasks = tasks.Select(MapToResponse).ToList(),
            TotalCount = (int)totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    // Get tasks created BY a project manager (tasks they assigned to others)
    public async Task<TaskListResponse> GetAssignedTasksAsync(
        Guid projectManagerId,
        TaskState? status,
        TaskPriority? priority,
        string? category,
        string? search,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var filterBuilder = Builders<TaskItem>.Filter;
        var filter = filterBuilder.Eq(t => t.AssignedByUserId, projectManagerId);

        if (status.HasValue)
        {
            filter &= filterBuilder.Eq(t => t.Status, status.Value);
        }

        if (priority.HasValue)
        {
            filter &= filterBuilder.Eq(t => t.Priority, priority.Value);
        }

        if (!string.IsNullOrWhiteSpace(category) && category != "All")
        {
            filter &= filterBuilder.Eq(t => t.Category, category);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchFilter = filterBuilder.Or(
                filterBuilder.Regex(t => t.Title, new BsonRegularExpression(search, "i")),
                filterBuilder.Regex(t => t.Description, new BsonRegularExpression(search, "i")),
                filterBuilder.Regex(t => t.Category, new BsonRegularExpression(search, "i"))
            );
            filter &= searchFilter;
        }

        var totalCount = await _db.Tasks.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        var tasks = await _db.Tasks
            .Find(filter)
            .SortByDescending(t => t.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        return new TaskListResponse
        {
            Tasks = tasks.Select(MapToResponse).ToList(),
            TotalCount = (int)totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<TaskResponse?> GetTaskByIdAsync(Guid taskId, Guid userId, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);

        return task != null ? MapToResponse(task) : null;
    }

    // Get task by ID regardless of ownership (for PM viewing assigned tasks)
    public async Task<TaskResponse?> GetTaskByIdForPmAsync(Guid taskId, Guid projectManagerId, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.AssignedByUserId == projectManagerId)
            .FirstOrDefaultAsync(cancellationToken);

        return task != null ? MapToResponse(task) : null;
    }

    // Create task and assign to another user (Project Manager only)
    public async Task<TaskResponse> CreateAssignedTaskAsync(Guid projectManagerId, CreateTaskRequest request, string projectManagerEmail, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Task title is required.");
        }

        if (request.Title.Length > 200)
        {
            throw new ArgumentException("Task title cannot exceed 200 characters.");
        }

        if (request.Description?.Length > 2000)
        {
            throw new ArgumentException("Task description cannot exceed 2000 characters.");
        }

        if (!Enum.IsDefined(typeof(TaskPriority), request.Priority))
        {
            throw new ArgumentException("Invalid priority.");
        }

        var validCategories = new[] { "Operations", "Compliance", "Security", "Goals", "Profile", "Projects" };
        if (!validCategories.Contains(request.Category))
        {
            throw new ArgumentException("Invalid category.");
        }

        if (!request.AssigneeUserId.HasValue)
        {
            throw new ArgumentException("AssigneeUserId is required for assigned tasks.");
        }

        // Verify assignee exists
        var assignee = await _db.Users.Find(u => u.Id == request.AssigneeUserId.Value).FirstOrDefaultAsync(cancellationToken);
        if (assignee == null)
        {
            throw new ArgumentException("Assignee user not found.");
        }

        var task = new TaskItem
        {
            UserId = request.AssigneeUserId.Value,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Priority = request.Priority,
            Category = request.Category,
            DueDate = request.DueDate,
            AssigneeEmail = assignee.Email,
            AssignedByUserId = projectManagerId,
            AssignedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _db.Tasks.InsertOneAsync(task, cancellationToken: cancellationToken);

        return MapToResponse(task);
    }

    // Update task by Project Manager (can reassign, update status, etc.)
    public async Task<TaskResponse> UpdateAssignedTaskAsync(Guid taskId, Guid projectManagerId, UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.AssignedByUserId == projectManagerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new KeyNotFoundException("Task not found or not authorized.");
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            if (request.Title.Length > 200)
            {
                throw new ArgumentException("Task title cannot exceed 200 characters.");
            }
            task.Title = request.Title.Trim();
        }

        if (request.Description != null)
        {
            if (request.Description.Length > 2000)
            {
                throw new ArgumentException("Task description cannot exceed 2000 characters.");
            }
            task.Description = request.Description.Trim();
        }

        if (request.Status.HasValue)
        {
            if (!Enum.IsDefined(typeof(TaskState), request.Status.Value))
            {
                throw new ArgumentException("Invalid task status.");
            }

            if (task.Status != request.Status.Value)
            {
                task.Status = request.Status.Value;

                if (request.Status.Value == TaskState.Completed && task.CompletedAtUtc == null)
                {
                    task.CompletedAtUtc = DateTimeOffset.UtcNow;
                }
                else if (request.Status.Value != TaskState.Completed)
                {
                    task.CompletedAtUtc = null;
                }
            }
        }

        if (request.Priority.HasValue)
        {
            if (!Enum.IsDefined(typeof(TaskPriority), request.Priority.Value))
            {
                throw new ArgumentException("Invalid priority.");
            }
            task.Priority = request.Priority.Value;
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            var validCategories = new[] { "Operations", "Compliance", "Security", "Goals", "Profile", "Projects" };
            if (!validCategories.Contains(request.Category))
            {
                throw new ArgumentException("Invalid category.");
            }
            task.Category = request.Category;
        }

        if (request.DueDate.HasValue)
        {
            task.DueDate = request.DueDate.Value;
        }

        // Handle reassignment
        if (request.AssigneeUserId.HasValue)
        {
            var assignee = await _db.Users.Find(u => u.Id == request.AssigneeUserId.Value).FirstOrDefaultAsync(cancellationToken);
            if (assignee == null)
            {
                throw new ArgumentException("Assignee user not found.");
            }
            task.UserId = request.AssigneeUserId.Value;
            task.AssigneeEmail = assignee.Email;
        }

        task.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _db.Tasks.ReplaceOneAsync(t => t.Id == taskId, task, cancellationToken: cancellationToken);

        return MapToResponse(task);
    }

    // Employee: Advance task status (forward only: Unopened -> Ongoing -> Completed -> Due if overdue)
    public async Task<TaskResponse> AdvanceTaskStatusAsync(Guid taskId, Guid userId, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new KeyNotFoundException("Task not found.");
        }

        return await AdvanceTaskStatusCoreAsync(task, taskId, canCycleBack: false, cancellationToken);
    }

    // Project Manager: Advance status on assigned task (can cycle, but Completed -> Due if overdue)
    public async Task<TaskResponse> AdvanceAssignedTaskStatusAsync(Guid taskId, Guid projectManagerId, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.AssignedByUserId == projectManagerId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new KeyNotFoundException("Task not found or not authorized.");
        }

        return await AdvanceTaskStatusCoreAsync(task, taskId, canCycleBack: true, cancellationToken);
    }

    // Shared core logic for advancing task status
    private async Task<TaskResponse> AdvanceTaskStatusCoreAsync(TaskItem task, Guid taskId, bool canCycleBack, CancellationToken cancellationToken = default)
    {
        var isOverdue = task.DueDate.HasValue
            && task.DueDate.Value < DateOnly.FromDateTime(DateTime.UtcNow)
            && task.Status != TaskState.Completed;

        task.Status = task.Status switch
        {
            TaskState.Unopened => TaskState.Ongoing,
            TaskState.Ongoing => TaskState.Completed,
            TaskState.Completed => isOverdue ? TaskState.Due : (canCycleBack ? TaskState.Unopened : TaskState.Completed),
            TaskState.Due => canCycleBack ? TaskState.Unopened : TaskState.Ongoing,
            _ => TaskState.Unopened
        };

        if (task.Status == TaskState.Completed)
        {
            task.CompletedAtUtc = DateTimeOffset.UtcNow;
        }
        else
        {
            task.CompletedAtUtc = null;
        }

        task.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _db.Tasks.ReplaceOneAsync(t => t.Id == taskId, task, cancellationToken: cancellationToken);

        return MapToResponse(task);
    }

    public async Task<TaskSummaryResponse> GetTaskSummaryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var totalCount = await _db.Tasks.CountDocumentsAsync(t => t.UserId == userId, cancellationToken: cancellationToken);
        var ongoingCount = await _db.Tasks.CountDocumentsAsync(t => t.UserId == userId && t.Status == TaskState.Ongoing, cancellationToken: cancellationToken);
        var completedCount = await _db.Tasks.CountDocumentsAsync(t => t.UserId == userId && t.Status == TaskState.Completed, cancellationToken: cancellationToken);
        var dueCount = await _db.Tasks.CountDocumentsAsync(t => t.UserId == userId && t.Status == TaskState.Due, cancellationToken: cancellationToken);
        var highPriorityCount = await _db.Tasks.CountDocumentsAsync(t => t.UserId == userId && t.Priority == TaskPriority.High && t.Status != TaskState.Completed, cancellationToken: cancellationToken);

        return new TaskSummaryResponse
        {
            TotalTasks = (int)totalCount,
            InProgressCount = (int)ongoingCount,
            CompletedCount = (int)completedCount,
            HighPriorityCount = (int)highPriorityCount,
            DueCount = (int)dueCount
        };
    }

    // Project Manager: Get summary of tasks they assigned
    public async Task<TaskSummaryResponse> GetAssignedTaskSummaryAsync(Guid projectManagerId, CancellationToken cancellationToken = default)
    {
        var totalCount = await _db.Tasks.CountDocumentsAsync(t => t.AssignedByUserId == projectManagerId, cancellationToken: cancellationToken);
        var ongoingCount = await _db.Tasks.CountDocumentsAsync(t => t.AssignedByUserId == projectManagerId && t.Status == TaskState.Ongoing, cancellationToken: cancellationToken);
        var completedCount = await _db.Tasks.CountDocumentsAsync(t => t.AssignedByUserId == projectManagerId && t.Status == TaskState.Completed, cancellationToken: cancellationToken);
        var dueCount = await _db.Tasks.CountDocumentsAsync(t => t.AssignedByUserId == projectManagerId && t.Status == TaskState.Due, cancellationToken: cancellationToken);
        var highPriorityCount = await _db.Tasks.CountDocumentsAsync(t => t.AssignedByUserId == projectManagerId && t.Priority == TaskPriority.High && t.Status != TaskState.Completed, cancellationToken: cancellationToken);

        return new TaskSummaryResponse
        {
            TotalTasks = (int)totalCount,
            InProgressCount = (int)ongoingCount,
            CompletedCount = (int)completedCount,
            HighPriorityCount = (int)highPriorityCount,
            DueCount = (int)dueCount
        };
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        var categories = await _db.Tasks
            .Distinct<string>("category", FilterDefinition<TaskItem>.Empty)
            .ToListAsync(cancellationToken);

        var defaultCategories = new[] { "Operations", "Compliance", "Security", "Goals", "Profile", "Projects" };
        return categories.Union(defaultCategories).Distinct().OrderBy(c => c).ToList();
    }

    private static TaskResponse MapToResponse(TaskItem task) => new TaskResponse
    {
        Id = task.Id,
        UserId = task.UserId,
        Title = task.Title,
        Description = task.Description,
        Status = task.Status,
        Priority = task.Priority,
        Category = task.Category,
        DueDate = task.DueDate,
        CreatedAtUtc = task.CreatedAtUtc,
        UpdatedAtUtc = task.UpdatedAtUtc,
        CompletedAtUtc = task.CompletedAtUtc,
        AssigneeEmail = task.AssigneeEmail,
        AssignedByUserId = task.AssignedByUserId,
        AssignedAtUtc = task.AssignedAtUtc
    };
}