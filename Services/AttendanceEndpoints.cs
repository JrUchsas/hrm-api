using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HrmApi.Data;
using HrmApi.Models;
using HrmApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace HrmApi.Endpoints;

public static class AttendanceEndpoints
{
    public static void MapAttendanceEndpoints(this WebApplication app)
    {
        var attendanceGroup = app.MapGroup("/api/attendance")
            .RequireAuthorization()
            .WithTags("Attendance");

        // ============ PERSONAL ATTENDANCE ============

        // Check-in
        attendanceGroup.MapPost("/check-in", async (
            CheckInRequest request,
            ClaimsPrincipal user,
            AttendanceService attendanceService,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var result = await attendanceService.CheckInAsync(userId, request, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("CheckIn")
        .Produces<CheckInResponse>();

        // Check-out
        attendanceGroup.MapPost("/check-out", async (
            CheckOutRequest request,
            ClaimsPrincipal user,
            AttendanceService attendanceService,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            try
            {
                var result = await attendanceService.CheckOutAsync(userId, request, cancellationToken);
                return Results.Ok(result);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        })
        .WithName("CheckOut")
        .Produces<CheckOutResponse>();

        // Get today's attendance
        attendanceGroup.MapGet("/my/today", async (
            ClaimsPrincipal user,
            AttendanceService attendanceService,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var result = await attendanceService.GetTodayAttendanceAsync(userId, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("GetMyTodayAttendance")
        .Produces<TodayAttendanceResponse>();

        // Get personal attendance history with date filtering
        attendanceGroup.MapGet("/my", async (
            ClaimsPrincipal user,
            AttendanceService attendanceService,
            DateOnly? from,
            DateOnly? to,
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);

            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var result = await attendanceService.GetPersonalHistoryAsync(userId, from, to, page, pageSize, cancellationToken);
            return Results.Ok(result);
        })
        .WithName("GetMyAttendanceHistory")
        .Produces<AttendanceHistoryResponse>();

        // ============ ADMIN ATTENDANCE ============

        // Admin daily report
        attendanceGroup.MapGet("/admin/daily", async (
            DateOnly date,
            AttendanceService attendanceService,
            CancellationToken cancellationToken) =>
        {
            var result = await attendanceService.GetAdminDailyReportAsync(date, cancellationToken);
            return Results.Ok(result);
        })
        .RequireAuthorization(policy => policy.RequireRole("super admin", "hr manager"))
        .WithName("GetAdminDailyReport")
        .Produces<AdminDailyReportResponse>();

        // Admin monthly summary
        attendanceGroup.MapGet("/admin/summary", async (
            int year,
            int month,
            AttendanceService attendanceService,
            CancellationToken cancellationToken) =>
        {
            if (year < 2000 || year > 2100)
                return Results.BadRequest("Invalid year");

            if (month < 1 || month > 12)
                return Results.BadRequest("Invalid month");

            var result = await attendanceService.GetAdminMonthlySummaryAsync(year, month, cancellationToken);
            return Results.Ok(result);
        })
        .RequireAuthorization(policy => policy.RequireRole("super admin", "hr manager"))
        .WithName("GetAdminMonthlySummary")
        .Produces<AdminMonthlySummaryResponse>();

        // Admin: Get specific user's attendance (for HR managers)
        attendanceGroup.MapGet("/admin/users/{userId:guid}", async (
            Guid userId,
            DateOnly? from,
            DateOnly? to,
            AttendanceService attendanceService,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
            if (user == null) return Results.NotFound("User not found");

            var result = await attendanceService.GetPersonalHistoryAsync(userId, from, to, 1, 100, cancellationToken);

            var response = new
            {
                UserId = user.Id,
                UserEmail = user.Email,
                UserGoal = user.Goal,
                Records = result.Records
            };

            return Results.Ok(response);
        })
        .RequireAuthorization(policy => policy.RequireRole("super admin", "hr manager"))
        .WithName("GetUserAttendanceByAdmin")
        .Produces<AttendanceHistoryResponse>();

        // Admin: Update attendance settings
        attendanceGroup.MapPut("/admin/settings", async (
            UpdateAttendanceSettingsRequest request,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var settings = await db.AttendanceSettings.Find(_ => true).FirstOrDefaultAsync(cancellationToken);
            if (settings == null)
            {
                settings = new AttendanceSettings();
                await db.AttendanceSettings.InsertOneAsync(settings, cancellationToken: cancellationToken);
            }

            settings.BusinessTimezone = request.BusinessTimezone ?? settings.BusinessTimezone;
            settings.WorkStartTime = request.WorkStartTime ?? settings.WorkStartTime;
            settings.WorkEndTime = request.WorkEndTime ?? settings.WorkEndTime;
            settings.LateCutoffMinutes = request.LateCutoffMinutes ?? settings.LateCutoffMinutes;
            settings.MinimumWorkingMinutes = request.MinimumWorkingMinutes ?? settings.MinimumWorkingMinutes;
            settings.WorkingDays = request.WorkingDays ?? settings.WorkingDays;
            settings.Holidays = request.Holidays ?? settings.Holidays;
            settings.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await db.AttendanceSettings.ReplaceOneAsync(s => s.Id == settings.Id, settings, cancellationToken: cancellationToken);

            return Results.Ok(new { message = "Attendance settings updated", settings });
        })
        .RequireAuthorization("super admin")
        .WithName("UpdateAttendanceSettings");

        // Admin: Get attendance settings
        attendanceGroup.MapGet("/admin/settings", async (
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var settings = await db.AttendanceSettings.Find(_ => true).FirstOrDefaultAsync(cancellationToken);
            if (settings == null)
            {
                settings = new AttendanceSettings();
                await db.AttendanceSettings.InsertOneAsync(settings, cancellationToken: cancellationToken);
            }
            return Results.Ok(settings);
        })
        .RequireAuthorization("super admin")
        .WithName("GetAttendanceSettings");

        // Admin: Mark user as on leave for a date
        attendanceGroup.MapPost("/admin/leave", async (
            MarkLeaveRequest request,
            MongoDbContext db,
            AttendanceService attendanceService,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users.Find(u => u.Id == request.UserId).FirstOrDefaultAsync(cancellationToken);
            if (user == null) return Results.NotFound("User not found");

            var existingRecord = await db.Attendance
                .Find(a => a.UserId == request.UserId && a.AttendanceDate == request.Date)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingRecord != null)
            {
                existingRecord.Status = AttendanceStatus.OnLeave;
                existingRecord.Notes = request.Reason;
                existingRecord.UpdatedAtUtc = DateTimeOffset.UtcNow;
                await db.Attendance.ReplaceOneAsync(a => a.Id == existingRecord.Id, existingRecord, cancellationToken: cancellationToken);
            }
            else
            {
                var record = new Attendance
                {
                    UserId = request.UserId,
                    AttendanceDate = request.Date,
                    Status = AttendanceStatus.OnLeave,
                    Notes = request.Reason,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                await db.Attendance.InsertOneAsync(record, cancellationToken: cancellationToken);
            }

            return Results.Ok(new { message = "Leave marked successfully" });
        })
        .RequireAuthorization("super admin")
        .WithName("MarkUserLeave");

        // Admin: Delete attendance record
        attendanceGroup.MapDelete("/admin/{attendanceId:guid}", async (
            Guid attendanceId,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var result = await db.Attendance.DeleteOneAsync(a => a.Id == attendanceId, cancellationToken);
            if (result.DeletedCount == 0) return Results.NotFound();

            return Results.Ok(new { message = "Attendance record deleted" });
        })
        .RequireAuthorization("super admin")
        .WithName("DeleteAttendanceRecord");
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var subClaim = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (subClaim == null || !Guid.TryParse(subClaim, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }
}

public sealed record UpdateAttendanceSettingsRequest(
    string? BusinessTimezone,
    TimeOnly? WorkStartTime,
    TimeOnly? WorkEndTime,
    int? LateCutoffMinutes,
    int? MinimumWorkingMinutes,
    List<DayOfWeek>? WorkingDays,
    List<Holiday>? Holidays);

public sealed record MarkLeaveRequest(
    Guid UserId,
    DateOnly Date,
    string Reason);