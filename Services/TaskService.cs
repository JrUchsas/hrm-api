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

    public async Task<TaskResponse?> GetTaskByIdAsync(Guid taskId, Guid userId, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);

        return task != null ? MapToResponse(task) : null;
    }

    public async Task<TaskResponse> CreateTaskAsync(Guid userId, CreateTaskRequest request, string userEmail, CancellationToken cancellationToken = default)
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

        var task = new TaskItem
        {
            UserId = userId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Priority = request.Priority,
            Category = request.Category,
            DueDate = request.DueDate,
            AssigneeEmail = userEmail,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _db.Tasks.InsertOneAsync(task, cancellationToken: cancellationToken);

        return MapToResponse(task);
    }

    public async Task<TaskResponse> UpdateTaskAsync(Guid taskId, Guid userId, UpdateTaskRequest request, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new KeyNotFoundException("Task not found.");
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

                if (request.Status.Value == TaskState.Done && task.CompletedAtUtc == null)
                {
                    task.CompletedAtUtc = DateTimeOffset.UtcNow;
                }
                else if (request.Status.Value != TaskState.Done)
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

        task.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await _db.Tasks.ReplaceOneAsync(t => t.Id == taskId, task, cancellationToken: cancellationToken);

        return MapToResponse(task);
    }

    public async Task<bool> DeleteTaskAsync(Guid taskId, Guid userId, CancellationToken cancellationToken = default)
    {
        var result = await _db.Tasks.DeleteOneAsync(t => t.Id == taskId && t.UserId == userId, cancellationToken);
        return result.DeletedCount > 0;
    }

    public async Task<TaskResponse> ToggleTaskStatusAsync(Guid taskId, Guid userId, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new KeyNotFoundException("Task not found.");
        }

        task.Status = task.Status == TaskState.Done ? TaskState.Todo : TaskState.Done;

        if (task.Status == TaskState.Done)
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

    public async Task<TaskResponse> AdvanceTaskStatusAsync(Guid taskId, Guid userId, CancellationToken cancellationToken = default)
    {
        var task = await _db.Tasks
            .Find(t => t.Id == taskId && t.UserId == userId)
            .FirstOrDefaultAsync(cancellationToken);

        if (task == null)
        {
            throw new KeyNotFoundException("Task not found.");
        }

        task.Status = task.Status switch
        {
            TaskState.Todo => TaskState.InProgress,
            TaskState.InProgress => TaskState.Done,
            TaskState.Done => TaskState.Todo,
            _ => TaskState.Todo
        };

        if (task.Status == TaskState.Done)
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
        var inProgressCount = await _db.Tasks.CountDocumentsAsync(t => t.UserId == userId && t.Status == TaskState.InProgress, cancellationToken: cancellationToken);
        var completedCount = await _db.Tasks.CountDocumentsAsync(t => t.UserId == userId && t.Status == TaskState.Done, cancellationToken: cancellationToken);
        var highPriorityCount = await _db.Tasks.CountDocumentsAsync(t => t.UserId == userId && t.Priority == TaskPriority.High && t.Status != TaskState.Done, cancellationToken: cancellationToken);

        return new TaskSummaryResponse
        {
            TotalTasks = (int)totalCount,
            InProgressCount = (int)inProgressCount,
            CompletedCount = (int)completedCount,
            HighPriorityCount = (int)highPriorityCount
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
        AssigneeEmail = task.AssigneeEmail
    };
}