using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HrmApi.Models;

public sealed class Attendance
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; set; }

    [BsonElement("attendanceDate")]
    public DateOnly AttendanceDate { get; set; }

    [BsonElement("checkInAtUtc")]
    public DateTimeOffset? CheckInAtUtc { get; set; }

    [BsonElement("checkOutAtUtc")]
    public DateTimeOffset? CheckOutAtUtc { get; set; }

    [BsonElement("status")]
    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;

    [BsonElement("workingDurationMinutes")]
    public int WorkingDurationMinutes { get; set; } = 0;

    [BsonElement("lateMinutes")]
    public int LateMinutes { get; set; } = 0;

    [BsonElement("notes")]
    public string? Notes { get; set; }

    [BsonElement("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [BsonElement("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum AttendanceStatus
{
    Present = 1,
    Late = 2,
    Absent = 3,
    OnLeave = 4,
    Incomplete = 5, // Checked in but not checked out
    Holiday = 6
}

public sealed class AttendanceSettings
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("businessTimezone")]
    public string BusinessTimezone { get; set; } = "Asia/Dhaka";

    [BsonElement("workStartTime")]
    public TimeOnly WorkStartTime { get; set; } = new TimeOnly(9, 0); // 9:00 AM

    [BsonElement("workEndTime")]
    public TimeOnly WorkEndTime { get; set; } = new TimeOnly(18, 0); // 6:00 PM

    [BsonElement("lateCutoffMinutes")]
    public int LateCutoffMinutes { get; set; } = 15; // Late if check-in after 9:15 AM

    [BsonElement("minimumWorkingMinutes")]
    public int MinimumWorkingMinutes { get; set; } = 480; // 8 hours for full day

    [BsonElement("workingDays")]
    public List<DayOfWeek> WorkingDays { get; set; } = new()
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday
    };

    [BsonElement("holidays")]
    public List<Holiday> Holidays { get; set; } = new();

    [BsonElement("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [BsonElement("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Holiday
{
    [BsonElement("date")]
    public DateOnly Date { get; set; }

    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("isRecurring")]
    public bool IsRecurring { get; set; } = false; // e.g., every year on this date
}

public sealed class CheckInResponse
{
    public Guid AttendanceId { get; set; }
    public DateOnly AttendanceDate { get; set; }
    public DateTimeOffset CheckInAtUtc { get; set; }
    public AttendanceStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class CheckOutResponse
{
    public Guid AttendanceId { get; set; }
    public DateOnly AttendanceDate { get; set; }
    public DateTimeOffset CheckInAtUtc { get; set; }
    public DateTimeOffset CheckOutAtUtc { get; set; }
    public int WorkingDurationMinutes { get; set; }
    public AttendanceStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class AttendanceRecordResponse
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public DateOnly AttendanceDate { get; set; }
    public DateTimeOffset? CheckInAtUtc { get; set; }
    public DateTimeOffset? CheckOutAtUtc { get; set; }
    public AttendanceStatus Status { get; set; }
    public int WorkingDurationMinutes { get; set; }
    public int LateMinutes { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class TodayAttendanceResponse
{
    public bool HasRecord { get; set; }
    public AttendanceRecordResponse? Record { get; set; }
}

public sealed class AttendanceHistoryResponse
{
    public List<AttendanceRecordResponse> Records { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed class AdminDailyAttendanceResponse
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public string? UserGoal { get; set; }
    public DateOnly AttendanceDate { get; set; }
    public DateTimeOffset? CheckInAtUtc { get; set; }
    public DateTimeOffset? CheckOutAtUtc { get; set; }
    public AttendanceStatus Status { get; set; }
    public int WorkingDurationMinutes { get; set; }
    public int LateMinutes { get; set; }
    public bool HasRecord { get; set; }
}

public sealed class AdminDailyReportResponse
{
    public DateOnly Date { get; set; }
    public List<AdminDailyAttendanceResponse> Records { get; set; } = new();
    public int TotalUsers { get; set; }
    public int PresentCount { get; set; }
    public int LateCount { get; set; }
    public int AbsentCount { get; set; }
    public int OnLeaveCount { get; set; }
    public int NoRecordCount { get; set; }
}

public sealed class MonthlySummaryResponse
{
    public int Year { get; set; }
    public int Month { get; set; }
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public int WorkingDaysInMonth { get; set; }
    public int PresentDays { get; set; }
    public int LateDays { get; set; }
    public int AbsentDays { get; set; }
    public int OnLeaveDays { get; set; }
    public int IncompleteDays { get; set; }
    public int TotalWorkingMinutes { get; set; }
    public double AttendancePercentage { get; set; }
    public double AverageWorkingHoursPerDay { get; set; }
}

public sealed class AdminMonthlySummaryResponse
{
    public int Year { get; set; }
    public int Month { get; set; }
    public List<MonthlySummaryResponse> Summaries { get; set; } = new();
}

public sealed class CheckInRequest
{
    public string? Notes { get; set; }
}

public sealed class CheckOutRequest
{
    public string? Notes { get; set; }
}