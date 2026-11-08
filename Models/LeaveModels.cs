using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HrmApi.Models;

public enum LeaveType
{
    Sick = 1,
    Casual = 2
}

public enum LeaveStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4
}

public sealed class LeaveBalance
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; set; }

    [BsonElement("year")]
    public int Year { get; set; }

    [BsonElement("totalSickLeave")]
    public int TotalSickLeave { get; set; } = 14;

    [BsonElement("usedSickLeave")]
    public int UsedSickLeave { get; set; } = 0;

    [BsonElement("totalCasualLeave")]
    public int TotalCasualLeave { get; set; } = 10;

    [BsonElement("usedCasualLeave")]
    public int UsedCasualLeave { get; set; } = 0;

    [BsonElement("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [BsonElement("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    // Computed properties (not stored in DB)
    [BsonIgnore]
    public int RemainingSickLeave => TotalSickLeave - UsedSickLeave;

    [BsonIgnore]
    public int RemainingCasualLeave => TotalCasualLeave - UsedCasualLeave;

    public int GetRemaining(LeaveType leaveType) => leaveType switch
    {
        LeaveType.Sick => RemainingSickLeave,
        LeaveType.Casual => RemainingCasualLeave,
        _ => 0
    };

    public int GetUsed(LeaveType leaveType) => leaveType switch
    {
        LeaveType.Sick => UsedSickLeave,
        LeaveType.Casual => UsedCasualLeave,
        _ => 0
    };

    public int GetTotal(LeaveType leaveType) => leaveType switch
    {
        LeaveType.Sick => TotalSickLeave,
        LeaveType.Casual => TotalCasualLeave,
        _ => 0
    };

    public void IncrementUsed(LeaveType leaveType, int days)
    {
        switch (leaveType)
        {
            case LeaveType.Sick:
                UsedSickLeave += days;
                break;
            case LeaveType.Casual:
                UsedCasualLeave += days;
                break;
        }
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    public void DecrementUsed(LeaveType leaveType, int days)
    {
        switch (leaveType)
        {
            case LeaveType.Sick:
                UsedSickLeave = Math.Max(0, UsedSickLeave - days);
                break;
            case LeaveType.Casual:
                UsedCasualLeave = Math.Max(0, UsedCasualLeave - days);
                break;
        }
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }
}

public sealed class LeaveRequest
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; set; }

    [BsonElement("leaveType")]
    public LeaveType LeaveType { get; set; }

    [BsonElement("startDate")]
    public DateOnly StartDate { get; set; }

    [BsonElement("endDate")]
    public DateOnly EndDate { get; set; }

    [BsonElement("daysCount")]
    public int DaysCount { get; set; }

    [BsonElement("reason")]
    public string Reason { get; set; } = string.Empty;

    [BsonElement("status")]
    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;

    [BsonElement("reviewedByUserId")]
    [BsonRepresentation(BsonType.String)]
    public Guid? ReviewedByUserId { get; set; }

    [BsonElement("reviewedAtUtc")]
    public DateTimeOffset? ReviewedAtUtc { get; set; }

    [BsonElement("rejectionReason")]
    public string? RejectionReason { get; set; }

    [BsonElement("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [BsonElement("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

// DTOs

public sealed class ApplyLeaveRequestDto
{
    public LeaveType LeaveType { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class ReviewLeaveRequestDto
{
    public bool Approve { get; set; }
    public string? RejectionReason { get; set; }
}

public sealed class LeaveBalanceResponse
{
    public int Year { get; set; }
    public int TotalSickLeave { get; set; }
    public int UsedSickLeave { get; set; }
    public int RemainingSickLeave { get; set; }
    public int TotalCasualLeave { get; set; }
    public int UsedCasualLeave { get; set; }
    public int RemainingCasualLeave { get; set; }
}

public sealed class LeaveRequestResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public LeaveType LeaveType { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public int DaysCount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public LeaveStatus Status { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewerEmail { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class LeaveHistoryResponse
{
    public List<LeaveRequestResponse> Requests { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}

public sealed class PendingLeaveRequestsResponse
{
    public List<LeaveRequestResponse> Requests { get; set; } = new();
    public int TotalCount { get; set; }
}

public sealed class AdjustLeaveBalanceDto
{
    public Guid UserId { get; set; }
    public int Year { get; set; }
    public int? TotalSickLeave { get; set; }
    public int? UsedSickLeave { get; set; }
    public int? TotalCasualLeave { get; set; }
    public int? UsedCasualLeave { get; set; }
}