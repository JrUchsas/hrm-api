using HrmApi.Data;
using HrmApi.Models;
using MongoDB.Driver;

namespace HrmApi.Services;

public sealed class ReportsService(MongoDbContext db)
{
    /// <summary>
    /// Gets workforce summary: headcount, attendance rate, average hours for a date range.
    /// </summary>
    public async Task<WorkforceSummaryResponse> GetWorkforceSummaryAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        var settings = await db.AttendanceSettings.Find(_ => true).FirstOrDefaultAsync(cancellationToken);
        settings ??= new AttendanceSettings();

        // Get all active users
        var users = await db.Users
            .Find(u => u.Status == UserStatus.Active)
            .ToListAsync(cancellationToken);

        var headcount = users.Count;

        // Default to last 30 days if not specified
        var endDate = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = fromDate ?? endDate.AddDays(-29);

        // Get attendance records for the date range
        var filter = Builders<Attendance>.Filter.And(
            Builders<Attendance>.Filter.Gte(a => a.AttendanceDate, startDate),
            Builders<Attendance>.Filter.Lte(a => a.AttendanceDate, endDate)
        );

        var records = await db.Attendance
            .Find(filter)
            .ToListAsync(cancellationToken);

        // Calculate working days in range
        var workingDays = 0;
        var holidays = settings.Holidays
            .Where(h => h.Date >= startDate && h.Date <= endDate)
            .Select(h => h.Date)
            .ToHashSet();

        foreach (var h in settings.Holidays.Where(h => h.IsRecurring))
        {
            for (var d = startDate; d <= endDate; d = d.AddDays(1))
            {
                if (h.Date.Month == d.Month && h.Date.Day == d.Day)
                {
                    holidays.Add(d);
                }
            }
        }

        for (var d = startDate; d <= endDate; d = d.AddDays(1))
        {
            if (settings.WorkingDays.Contains(d.DayOfWeek) && !holidays.Contains(d))
            {
                workingDays++;
            }
        }

        // Calculate metrics
        var totalWorkingMinutes = records
            .Where(r => r.CheckOutAtUtc.HasValue && r.WorkingDurationMinutes > 0)
            .Sum(r => r.WorkingDurationMinutes);

        var daysWithCheckIn = records
            .Where(r => r.CheckInAtUtc.HasValue)
            .Select(r => r.AttendanceDate)
            .Distinct()
            .Count();

        // Attendance rate = (days with check-in) / (working days * headcount) * 100
        var expectedCheckIns = (long)workingDays * headcount;
        var attendanceRate = expectedCheckIns > 0
            ? (double)daysWithCheckIn / expectedCheckIns * 100
            : 0;

        // Average hours per day = total minutes / days with check-in / 60
        var avgHoursPerDay = daysWithCheckIn > 0
            ? (double)totalWorkingMinutes / daysWithCheckIn / 60
            : 0;

        return new WorkforceSummaryResponse
        {
            Headcount = headcount,
            AttendanceRate = Math.Round(attendanceRate, 1),
            AverageHoursPerDay = Math.Round(avgHoursPerDay, 2),
            PeriodStart = startDate,
            PeriodEnd = endDate,
            WorkingDaysInPeriod = workingDays
        };
    }

    /// <summary>
    /// Gets daily attendance trend for charting (last N days).
    /// </summary>
    public async Task<AttendanceTrendResponse> GetAttendanceTrendAsync(
        int days = 30,
        CancellationToken cancellationToken = default)
    {
        var settings = await db.AttendanceSettings.Find(_ => true).FirstOrDefaultAsync(cancellationToken);
        settings ??= new AttendanceSettings();

        var endDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = endDate.AddDays(-days + 1);

        // Get all active users
        var users = await db.Users
            .Find(u => u.Status == UserStatus.Active)
            .ToListAsync(cancellationToken);

        var headcount = users.Count;

        // Get attendance records for the date range
        var filter = Builders<Attendance>.Filter.And(
            Builders<Attendance>.Filter.Gte(a => a.AttendanceDate, startDate),
            Builders<Attendance>.Filter.Lte(a => a.AttendanceDate, endDate)
        );

        var records = await db.Attendance
            .Find(filter)
            .ToListAsync(cancellationToken);

        // Calculate holidays
        var holidays = settings.Holidays
            .Where(h => h.Date >= startDate && h.Date <= endDate)
            .Select(h => h.Date)
            .ToHashSet();

        foreach (var h in settings.Holidays.Where(h => h.IsRecurring))
        {
            for (var d = startDate; d <= endDate; d = d.AddDays(1))
            {
                if (h.Date.Month == d.Month && h.Date.Day == d.Day)
                {
                    holidays.Add(d);
                }
            }
        }

        var recordsByDate = records
            .GroupBy(r => r.AttendanceDate)
            .ToDictionary(g => g.Key, g => g.ToList());

        var trendData = new List<DailyTrendPoint>();

        for (var d = startDate; d <= endDate; d = d.AddDays(1))
        {
            var isWorkingDay = settings.WorkingDays.Contains(d.DayOfWeek);
            var isHoliday = holidays.Contains(d);

            if (!isWorkingDay || isHoliday)
            {
                trendData.Add(new DailyTrendPoint
                {
                    Date = d,
                    IsWorkingDay = false,
                    IsHoliday = isHoliday,
                    Present = 0,
                    Late = 0,
                    Absent = 0,
                    OnLeave = 0,
                    NoRecord = 0,
                    AttendanceRate = 0
                });
                continue;
            }

            var dayRecords = recordsByDate.TryGetValue(d, out var recs) ? recs : new List<Attendance>();

            var present = dayRecords.Count(r => r.Status == AttendanceStatus.Present);
            var late = dayRecords.Count(r => r.Status == AttendanceStatus.Late);
            var onLeave = dayRecords.Count(r => r.Status == AttendanceStatus.OnLeave);
            var absent = dayRecords.Count(r => r.Status == AttendanceStatus.Absent);
            var noRecord = headcount - dayRecords.Count;

            var attendanceRate = headcount > 0
                ? (double)(present + late) / headcount * 100
                : 0;

            trendData.Add(new DailyTrendPoint
            {
                Date = d,
                IsWorkingDay = true,
                IsHoliday = false,
                Present = present,
                Late = late,
                Absent = absent,
                OnLeave = onLeave,
                NoRecord = noRecord,
                AttendanceRate = Math.Round(attendanceRate, 1)
            });
        }

        return new AttendanceTrendResponse
        {
            PeriodStart = startDate,
            PeriodEnd = endDate,
            Headcount = headcount,
            DataPoints = trendData
        };
    }

    /// <summary>
    /// Gets late employees report for a date range.
    /// </summary>
    public async Task<LateEmployeesResponse> GetLateEmployeesAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var endDate = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = fromDate ?? endDate.AddDays(-29);

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        // Get late records
        var filter = Builders<Attendance>.Filter.And(
            Builders<Attendance>.Filter.Gte(a => a.AttendanceDate, startDate),
            Builders<Attendance>.Filter.Lte(a => a.AttendanceDate, endDate),
            Builders<Attendance>.Filter.Eq(a => a.Status, AttendanceStatus.Late)
        );

        var total = await db.Attendance.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var records = await db.Attendance
            .Find(filter)
            .SortByDescending(a => a.AttendanceDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        // Get user emails
        var userIds = records.Select(r => r.UserId).Distinct().ToList();
        var users = await db.Users
            .Find(u => userIds.Contains(u.Id))
            .ToListAsync(cancellationToken);
        var userDict = users.ToDictionary(u => u.Id, u => u.Email);

        var lateEntries = records.Select(r => new LateEmployeeEntry
        {
            UserId = r.UserId,
            UserEmail = userDict.TryGetValue(r.UserId, out var email) ? email : "Unknown",
            Date = r.AttendanceDate,
            CheckInAtUtc = r.CheckInAtUtc,
            LateMinutes = r.LateMinutes
        }).ToList();

        return new LateEmployeesResponse
        {
            Entries = lateEntries,
            TotalCount = (int)total,
            Page = page,
            PageSize = pageSize,
            PeriodStart = startDate,
            PeriodEnd = endDate
        };
    }

    /// <summary>
    /// Gets employees on leave report for a date range.
    /// </summary>
    public async Task<EmployeesOnLeaveResponse> GetEmployeesOnLeaveAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var endDate = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = fromDate ?? endDate.AddDays(-29);

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        // Get on-leave records
        var filter = Builders<Attendance>.Filter.And(
            Builders<Attendance>.Filter.Gte(a => a.AttendanceDate, startDate),
            Builders<Attendance>.Filter.Lte(a => a.AttendanceDate, endDate),
            Builders<Attendance>.Filter.Eq(a => a.Status, AttendanceStatus.OnLeave)
        );

        var total = await db.Attendance.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var records = await db.Attendance
            .Find(filter)
            .SortByDescending(a => a.AttendanceDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        // Get user emails
        var userIds = records.Select(r => r.UserId).Distinct().ToList();
        var users = await db.Users
            .Find(u => userIds.Contains(u.Id))
            .ToListAsync(cancellationToken);
        var userDict = users.ToDictionary(u => u.Id, u => u.Email);

        var leaveEntries = records.Select(r => new EmployeeOnLeaveEntry
        {
            UserId = r.UserId,
            UserEmail = userDict.TryGetValue(r.UserId, out var email) ? email : "Unknown",
            Date = r.AttendanceDate,
            Notes = r.Notes
        }).ToList();

        return new EmployeesOnLeaveResponse
        {
            Entries = leaveEntries,
            TotalCount = (int)total,
            Page = page,
            PageSize = pageSize,
            PeriodStart = startDate,
            PeriodEnd = endDate
        };
    }

    /// <summary>
    /// Gets time worked report per user for a date range.
    /// </summary>
    public async Task<TimeWorkedResponse> GetTimeWorkedAsync(
        DateOnly? fromDate,
        DateOnly? toDate,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var endDate = toDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = fromDate ?? endDate.AddDays(-29);

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 100) pageSize = 20;

        // Get all active users
        var users = await db.Users
            .Find(u => u.Status == UserStatus.Active)
            .ToListAsync(cancellationToken);

        // Get attendance records with check-out for the date range
        var filter = Builders<Attendance>.Filter.And(
            Builders<Attendance>.Filter.Gte(a => a.AttendanceDate, startDate),
            Builders<Attendance>.Filter.Lte(a => a.AttendanceDate, endDate),
            Builders<Attendance>.Filter.Ne(a => a.CheckOutAtUtc, null),
            Builders<Attendance>.Filter.Gt(a => a.WorkingDurationMinutes, 0)
        );

        var records = await db.Attendance
            .Find(filter)
            .ToListAsync(cancellationToken);

        // Group by user
        var recordsByUser = records
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var userSummaries = users.Select(u =>
        {
            var userRecords = recordsByUser.TryGetValue(u.Id, out var recs) ? recs : new List<Attendance>();
            var totalMinutes = userRecords.Sum(r => r.WorkingDurationMinutes);
            var daysWorked = userRecords.Count;
            var avgMinutesPerDay = daysWorked > 0 ? totalMinutes / (double)daysWorked : 0;
            var lateDays = userRecords.Count(r => r.Status == AttendanceStatus.Late);
            var onLeaveDays = userRecords.Count(r => r.Status == AttendanceStatus.OnLeave);

            return new UserTimeWorkedSummary
            {
                UserId = u.Id,
                UserEmail = u.Email,
                TotalMinutes = totalMinutes,
                TotalHours = Math.Round(totalMinutes / 60.0, 2),
                DaysWorked = daysWorked,
                AverageHoursPerDay = Math.Round(avgMinutesPerDay / 60.0, 2),
                LateDays = lateDays,
                OnLeaveDays = onLeaveDays
            };
        })
        .OrderByDescending(s => s.TotalMinutes)
        .ToList();

        var totalCount = userSummaries.Count;
        var pagedSummaries = userSummaries
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new TimeWorkedResponse
        {
            Summaries = pagedSummaries,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            PeriodStart = startDate,
            PeriodEnd = endDate
        };
    }
}

// DTOs

public sealed class WorkforceSummaryResponse
{
    public int Headcount { get; set; }
    public double AttendanceRate { get; set; } // percentage
    public double AverageHoursPerDay { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public int WorkingDaysInPeriod { get; set; }
}

public sealed class AttendanceTrendResponse
{
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public int Headcount { get; set; }
    public List<DailyTrendPoint> DataPoints { get; set; } = new();
}

public sealed class DailyTrendPoint
{
    public DateOnly Date { get; set; }
    public bool IsWorkingDay { get; set; }
    public bool IsHoliday { get; set; }
    public int Present { get; set; }
    public int Late { get; set; }
    public int Absent { get; set; }
    public int OnLeave { get; set; }
    public int NoRecord { get; set; }
    public double AttendanceRate { get; set; }
}

public sealed class LateEmployeesResponse
{
    public List<LateEmployeeEntry> Entries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
}

public sealed class LateEmployeeEntry
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DateTimeOffset? CheckInAtUtc { get; set; }
    public int LateMinutes { get; set; }
}

public sealed class EmployeesOnLeaveResponse
{
    public List<EmployeeOnLeaveEntry> Entries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
}

public sealed class EmployeeOnLeaveEntry
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string? Notes { get; set; }
}

public sealed class TimeWorkedResponse
{
    public List<UserTimeWorkedSummary> Summaries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
}

public sealed class UserTimeWorkedSummary
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public int TotalMinutes { get; set; }
    public double TotalHours { get; set; }
    public int DaysWorked { get; set; }
    public double AverageHoursPerDay { get; set; }
    public int LateDays { get; set; }
    public int OnLeaveDays { get; set; }
}