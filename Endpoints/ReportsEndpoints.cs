using System.Security.Claims;
using HrmApi.Data;
using HrmApi.Models;
using HrmApi.Services;
using Microsoft.AspNetCore.Authorization;
using MongoDB.Driver;

namespace HrmApi.Endpoints;

public static class ReportsEndpoints
{
    public static void MapReportsEndpoints(this WebApplication app)
    {
        var reportsGroup = app.MapGroup("/api/reports")
            .WithTags("Reports")
            .RequireAuthorization(); // All reports require authentication

        // GET /api/reports/summary - Workforce summary (headcount, attendance rate, avg hours)
        reportsGroup.MapGet("/summary", async (
            DateOnly? from,
            DateOnly? to,
            ReportsService reportsService,
            CancellationToken cancellationToken) =>
        {
            var summary = await reportsService.GetWorkforceSummaryAsync(from, to, cancellationToken);
            return Results.Ok(summary);
        })
        .WithName("GetWorkforceSummary")
        .Produces<WorkforceSummaryResponse>();

        // GET /api/reports/attendance-trend - Daily attendance trend for charts
        reportsGroup.MapGet("/attendance-trend", async (
            int days,
            ReportsService reportsService,
            CancellationToken cancellationToken) =>
        {
            if (days < 1 || days > 365) days = 30;
            var trend = await reportsService.GetAttendanceTrendAsync(days, cancellationToken);
            return Results.Ok(trend);
        })
        .WithName("GetAttendanceTrend")
        .Produces<AttendanceTrendResponse>();

        // GET /api/reports/late-employees - Employees who were late
        reportsGroup.MapGet("/late-employees", async (
            DateOnly? from,
            DateOnly? to,
            int page,
            int pageSize,
            ReportsService reportsService,
            CancellationToken cancellationToken) =>
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var result = await reportsService.GetLateEmployeesAsync(from, to, page, pageSize, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("GetLateEmployees")
        .Produces<LateEmployeesResponse>();

        // GET /api/reports/employees-on-leave - Employees on leave
        reportsGroup.MapGet("/employees-on-leave", async (
            DateOnly? from,
            DateOnly? to,
            int page,
            int pageSize,
            ReportsService reportsService,
            CancellationToken cancellationToken) =>
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var result = await reportsService.GetEmployeesOnLeaveAsync(from, to, page, pageSize, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("GetEmployeesOnLeave")
        .Produces<EmployeesOnLeaveResponse>();

        // GET /api/reports/time-worked - Time worked per user
        reportsGroup.MapGet("/time-worked", async (
            DateOnly? from,
            DateOnly? to,
            int page,
            int pageSize,
            ReportsService reportsService,
            CancellationToken cancellationToken) =>
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var result = await reportsService.GetTimeWorkedAsync(from, to, page, pageSize, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("GetTimeWorked")
        .Produces<TimeWorkedResponse>();

        // Admin-only detailed reports
        var adminReportsGroup = app.MapGroup("/api/admin/reports")
            .WithTags("Reports - Admin")
            .RequireAuthorization("HrOrAdmin"); // Requires HR or Admin role

        // GET /api/admin/reports/department-headcount - Headcount by department
        adminReportsGroup.MapGet("/department-headcount", async (
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var users = await db.Users
                .Find(u => u.Status == UserStatus.Active)
                .ToListAsync(cancellationToken);

            // Since we don't have departments in User model yet, group by role
            var userRoles = await db.UserRoles
                .Find(_ => true)
                .ToListAsync(cancellationToken);

            var roleIds = userRoles.Select(ur => ur.RoleId).Distinct().ToList();
            var roles = await db.Roles
                .Find(r => roleIds.Contains(r.Id))
                .ToListAsync(cancellationToken);
            var roleDict = roles.ToDictionary(r => r.Id, r => r.Name);

            var userRoleDict = userRoles.ToDictionary(ur => ur.UserId, ur => roleDict.TryGetValue(ur.RoleId, out var name) ? name : "Unknown");

            var departmentCounts = users
                .Where(u => u.Status == UserStatus.Active)
                .GroupBy(u => userRoleDict.TryGetValue(u.Id, out var dept) ? dept : "Unassigned")
                .Select(g => new DepartmentHeadcountEntry
                {
                    Department = g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(d => d.Count)
                .ToList();

            return Results.Ok(new DepartmentHeadcountResponse
            {
                Departments = departmentCounts,
                TotalHeadcount = users.Count(u => u.Status == UserStatus.Active)
            });
        })
        .WithName("GetDepartmentHeadcount")
        .Produces<DepartmentHeadcountResponse>();

        // GET /api/admin/reports/absent-employees - Employees absent
        adminReportsGroup.MapGet("/absent-employees", async (
            DateOnly? from,
            DateOnly? to,
            int page,
            int pageSize,
            MongoDbContext db,
            ReportsService reportsService,
            CancellationToken cancellationToken) =>
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var endDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var startDate = from ?? endDate.AddDays(-29);

            // Get absent records (including no-record on working days)
            var settings = await db.AttendanceSettings.Find(_ => true).FirstOrDefaultAsync(cancellationToken);
            settings ??= new AttendanceSettings();

            var allUsers = await db.Users
                .Find(u => u.Status == UserStatus.Active)
                .ToListAsync(cancellationToken);

            // Get attendance records
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

            var absentEntries = new List<AbsentEmployeeEntry>();

            foreach (var user in allUsers)
            {
                for (var d = startDate; d <= endDate; d = d.AddDays(1))
                {
                    var isWorkingDay = settings.WorkingDays.Contains(d.DayOfWeek);
                    var isHoliday = holidays.Contains(d);

                    if (!isWorkingDay || isHoliday) continue;

                    var dayRecords = recordsByDate.TryGetValue(d, out var recs) ? recs : new List<Attendance>();
                    var userRecord = dayRecords.FirstOrDefault(r => r.UserId == user.Id);

                    if (userRecord == null)
                    {
                        // No record = Absent
                        absentEntries.Add(new AbsentEmployeeEntry
                        {
                            UserId = user.Id,
                            UserEmail = user.Email,
                            Date = d,
                            Reason = "No check-in record"
                        });
                    }
                    else if (userRecord.Status == AttendanceStatus.Absent)
                    {
                        absentEntries.Add(new AbsentEmployeeEntry
                        {
                            UserId = user.Id,
                            UserEmail = user.Email,
                            Date = d,
                            Reason = "Marked absent"
                        });
                    }
                }
            }

            var totalCount = absentEntries.Count;
            var pagedEntries = absentEntries
                .OrderByDescending(e => e.Date)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            return Results.Ok(new AbsentEmployeesResponse
            {
                Entries = pagedEntries,
                TotalCount = totalCount,
                Page = page,
                PageSize = pageSize,
                PeriodStart = startDate,
                PeriodEnd = endDate
            });
        })
        .WithName("GetAbsentEmployees")
        .Produces<AbsentEmployeesResponse>();

        // GET /api/admin/reports/incomplete-employees - Employees with incomplete days
        adminReportsGroup.MapGet("/incomplete-employees", async (
            DateOnly? from,
            DateOnly? to,
            int page,
            int pageSize,
            MongoDbContext db,
            ReportsService reportsService,
            CancellationToken cancellationToken) =>
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var endDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var startDate = from ?? endDate.AddDays(-29);

            var filter = Builders<Attendance>.Filter.And(
                Builders<Attendance>.Filter.Gte(a => a.AttendanceDate, startDate),
                Builders<Attendance>.Filter.Lte(a => a.AttendanceDate, endDate),
                Builders<Attendance>.Filter.Eq(a => a.Status, AttendanceStatus.Incomplete)
            );

            var total = await db.Attendance.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

            var records = await db.Attendance
                .Find(filter)
                .SortByDescending(a => a.AttendanceDate)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(cancellationToken);

            var userIds = records.Select(r => r.UserId).Distinct().ToList();
            var users = await db.Users
                .Find(u => userIds.Contains(u.Id))
                .ToListAsync(cancellationToken);
            var userDict = users.ToDictionary(u => u.Id, u => u.Email);

            var entries = records.Select(r => new IncompleteEmployeeEntry
            {
                UserId = r.UserId,
                UserEmail = userDict.TryGetValue(r.UserId, out var email) ? email : "Unknown",
                Date = r.AttendanceDate,
                CheckInAtUtc = r.CheckInAtUtc,
                CheckOutAtUtc = r.CheckOutAtUtc,
                WorkingDurationMinutes = r.WorkingDurationMinutes
            }).ToList();

            return Results.Ok(new IncompleteEmployeesResponse
            {
                Entries = entries,
                TotalCount = (int)total,
                Page = page,
                PageSize = pageSize,
                PeriodStart = startDate,
                PeriodEnd = endDate
            });
        })
        .WithName("GetIncompleteEmployees")
        .Produces<IncompleteEmployeesResponse>();

        // GET /api/admin/reports/holiday-report - Holiday report
        adminReportsGroup.MapGet("/holiday-report", async (
            int? year,
            int? month,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var settings = await db.AttendanceSettings.Find(_ => true).FirstOrDefaultAsync(cancellationToken);
            settings ??= new AttendanceSettings();

            var targetYear = year ?? DateTime.UtcNow.Year;
            var targetMonth = month ?? DateTime.UtcNow.Month;

            if (targetYear < 2000 || targetYear > 2100) return Results.BadRequest("Invalid year");
            if (targetMonth < 1 || targetMonth > 12) return Results.BadRequest("Invalid month");

            var startDate = new DateOnly(targetYear, targetMonth, 1);
            var endDate = startDate.AddMonths(1).AddDays(-1);

            var holidays = new List<HolidayEntry>();

            // Static holidays
            foreach (var h in settings.Holidays.Where(h => h.Date >= startDate && h.Date <= endDate))
            {
                holidays.Add(new HolidayEntry
                {
                    Name = h.Name,
                    Date = h.Date,
                    IsRecurring = h.IsRecurring,
                    Type = "Static"
                });
            }

            // Recurring holidays for this month
            foreach (var h in settings.Holidays.Where(h => h.IsRecurring && h.Date.Month == targetMonth))
            {
                var date = new DateOnly(targetYear, h.Date.Month, h.Date.Day);
                holidays.Add(new HolidayEntry
                {
                    Name = h.Name,
                    Date = date,
                    IsRecurring = true,
                    Type = "Recurring"
                });
            }

            return Results.Ok(new HolidayReportResponse
            {
                Year = targetYear,
                Month = targetMonth,
                Holidays = holidays.OrderBy(h => h.Date).ToList()
            });
        })
        .WithName("GetHolidayReport")
        .Produces<HolidayReportResponse>();
    }
}

// Admin report DTOs
public sealed class DepartmentHeadcountEntry
{
    public string Department { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class DepartmentHeadcountResponse
{
    public List<DepartmentHeadcountEntry> Departments { get; set; } = new();
    public int TotalHeadcount { get; set; }
}

public sealed class AbsentEmployeeEntry
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class AbsentEmployeesResponse
{
    public List<AbsentEmployeeEntry> Entries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
}

public sealed class IncompleteEmployeeEntry
{
    public Guid UserId { get; set; }
    public string UserEmail { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public DateTimeOffset? CheckInAtUtc { get; set; }
    public DateTimeOffset? CheckOutAtUtc { get; set; }
    public int WorkingDurationMinutes { get; set; }
}

public sealed class IncompleteEmployeesResponse
{
    public List<IncompleteEmployeeEntry> Entries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
}

public sealed class HolidayEntry
{
    public string Name { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public bool IsRecurring { get; set; }
    public string Type { get; set; } = string.Empty; // "Static" or "Recurring"
}

public sealed class HolidayReportResponse
{
    public int Year { get; set; }
    public int Month { get; set; }
    public List<HolidayEntry> Holidays { get; set; } = new();
}