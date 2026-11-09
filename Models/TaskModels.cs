using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HrmApi.Models;

public enum TaskState
{
    Todo = 1,
    InProgress = 2,
    Done = 3
}

public enum TaskPriority
{
    Low = 1,
    Medium = 2,
    High = 3
}

public sealed class TaskItem
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; set; }

    [BsonElement("title")]
    public string Title { get; set; } = string.Empty;

    [BsonElement("description")]
    public string Description { get; set; } = string.Empty;

    [BsonElement("status")]
    public TaskState Status { get; set; } = TaskState.Todo;

    [BsonElement("priority")]
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    [BsonElement("category")]
    public string Category { get; set; } = "Operations";

    [BsonElement("dueDate")]
    public DateOnly? DueDate { get; set; }

    [BsonElement("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [BsonElement("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [BsonElement("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }

    [BsonElement("assigneeEmail")]
    public string? AssigneeEmail { get; set; }
}

// DTOs for API requests/responses
public sealed class CreateTaskRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public string Category { get; set; } = "Operations";
    public DateOnly? DueDate { get; set; }
}

public sealed class UpdateTaskRequest
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public TaskState? Status { get; set; }
    public TaskPriority? Priority { get; set; }
    public string? Category { get; set; }
    public DateOnly? DueDate { get; set; }
}

public sealed class TaskResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TaskState Status { get; set; }
    public TaskPriority Priority { get; set; }
    public string Category { get; set; } = string.Empty;
    public DateOnly? DueDate { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? AssigneeEmail { get; set; }

    // Display-friendly status for UI
    public string StatusDisplay => Status switch
    {
        TaskState.Todo => "To do",
        TaskState.InProgress => "In progress",
        TaskState.Done => "Done",
        _ => Status.ToString()
    };

    // Display-friendly priority for UI
    public string PriorityDisplay => Priority switch
    {
        TaskPriority.Low => "Low",
        TaskPriority.Medium => "Medium",
        TaskPriority.High => "High",
        _ => Priority.ToString()
    };

    public bool IsOverdue => DueDate.HasValue && DueDate.Value < DateOnly.FromDateTime(DateTime.UtcNow) && Status != TaskState.Done;
}

public sealed class TaskListResponse
{
    public List<TaskResponse> Tasks { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}

public sealed class TaskSummaryResponse
{
    public int TotalTasks { get; set; }
    public int InProgressCount { get; set; }
    public int CompletedCount { get; set; }
    public int HighPriorityCount { get; set; }
}