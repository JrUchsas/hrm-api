using HrmApi.Data;
using HrmApi.Models;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace HrmApi.Services;

public sealed class AttendanceService(MongoDbContext db, IConfiguration configuration, ILogger<AttendanceService> logger)
{
    private readonly TimeZoneInfo _businessTimezone = GetBusinessTimezone(configuration, logger);

    private static TimeZoneInfo GetBusinessTimezone(IConfiguration configuration, ILogger? logger = null)
    {
        var timezoneId = configuration["Attendance:BusinessTimezone"] ?? "Asia/Dhaka";
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch
        {
            logger?.LogWarning("Invalid timezone '{TimezoneId}' in configuration, falling back to UTC", timezoneId);
            return TimeZoneInfo.Utc;
        }
    }

    public DateOnly GetBusinessDate(DateTimeOffset utcNow)
    {
        var businessTime = TimeZoneInfo.ConvertTime(utcNow, _businessTimezone);
        return DateOnly.FromDateTime(businessTime.DateTime);
    }

    public DateTimeOffset GetBusinessDateStartUtc(DateOnly date)
    {
        var localStart = date.ToDateTime(TimeOnly.MinValue);
        return TimeZoneInfo.ConvertTimeToUtc(localStart, _businessTimezone);
    }

    public DateTimeOffset GetBusinessDateEndUtc(DateOnly date)
    {
        var localEnd = date.ToDateTime(TimeOnly.MaxValue);
        return TimeZoneInfo.ConvertTimeToUtc(localEnd, _businessTimezone);
    }

    public async Task<AttendanceSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await db.AttendanceSettings.Find(_ => true).FirstOrDefaultAsync(cancellationToken);
        if (settings == null)
        {
            settings = new AttendanceSettings();
            await db.AttendanceSettings.InsertOneAsync(settings, cancellationToken: cancellationToken);
        }
        return settings;
    }

    public async Task<CheckInResponse> CheckInAsync(Guid userId, CheckInRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var businessDate = GetBusinessDate(now);
        var settings = await GetSettingsAsync(cancellationToken);

        // Check if already checked in today
        var existingRecord = await db.Attendance
            .Find(a => a.UserId == userId && a.AttendanceDate == businessDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingRecord != null)
        {
            return new CheckInResponse
            {
                AttendanceId = existingRecord.Id,
                AttendanceDate = existingRecord.AttendanceDate,
                CheckInAtUtc = existingRecord.CheckInAtUtc!.Value,
                Status = existingRecord.Status,
                Message = "Already checked in today"
            };
        }

        // Determine status based on check-in time
        var businessTime = TimeZoneInfo.ConvertTime(now, _businessTimezone);
        var workStart = settings.WorkStartTime;
        var lateCutoff = workStart.AddMinutes(settings.LateCutoffMinutes);

        var status = AttendanceStatus.Present;
        var lateMinutes = 0;

        if (businessTime.TimeOfDay > lateCutoff.ToTimeSpan())
        {
            status = AttendanceStatus.Late;
            lateMinutes = (int)(businessTime.TimeOfDay - workStart.ToTimeSpan()).TotalMinutes;
        }

        var record = new Attendance
        {
            UserId = userId,
            AttendanceDate = businessDate,
            CheckInAtUtc = now,
            Status = status,
            LateMinutes = lateMinutes,
            Notes = request.Notes?.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        try
        {
            await db.Attendance.InsertOneAsync(record, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Handle race condition - another request created the record
            var duplicate = await db.Attendance
                .Find(a => a.UserId == userId && a.AttendanceDate == businessDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (duplicate != null)
            {
                return new CheckInResponse
                {
                    AttendanceId = duplicate.Id,
                    AttendanceDate = duplicate.AttendanceDate,
                    CheckInAtUtc = duplicate.CheckInAtUtc!.Value,
                    Status = duplicate.Status,
                    Message = "Already checked in today"
                };
            }
            throw;
        }

        return new CheckInResponse
        {
            AttendanceId = record.Id,
            AttendanceDate = record.AttendanceDate,
            CheckInAtUtc = record.CheckInAtUtc.Value,
            Status = record.Status,
            Message = "Check-in successful"
        };
    }

    public async Task<CheckOutResponse> CheckOutAsync(Guid userId, CheckOutRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var businessDate = GetBusinessDate(now);

        var record = await db.Attendance
            .Find(a => a.UserId == userId && a.AttendanceDate == businessDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (record == null)
        {
            throw new InvalidOperationException("No check-in record found for today. Please check in first.");
        }

        if (record.CheckOutAtUtc != null)
        {
            throw new InvalidOperationException("Already checked out today");
        }

        if (record.CheckInAtUtc == null)
        {
            throw new InvalidOperationException("Invalid attendance record: missing check-in time");
        }

        // Validate check-out is after check-in
        if (now < record.CheckInAtUtc)
        {
            throw new InvalidOperationException("Check-out time cannot be earlier than check-in time");
        }

        var workingDuration = (int)(now - record.CheckInAtUtc.Value).TotalMinutes;

        var update = Builders<Attendance>.Update
            .Set(a => a.CheckOutAtUtc, now)
            .Set(a => a.WorkingDurationMinutes, workingDuration)
            .Set(a => a.UpdatedAtUtc, now)
            .Set(a => a.Notes, string.IsNullOrWhiteSpace(request.Notes)
                ? record.Notes
                : $"{record.Notes}\nCheckout: {request.Notes}");

        // Update status based on working duration
        var settings = await GetSettingsAsync(cancellationToken);
        var newStatus = record.Status;
        if (workingDuration < settings.MinimumWorkingMinutes && record.Status == AttendanceStatus.Present)
        {
            newStatus = AttendanceStatus.Incomplete;
        }
        update = update.Set(a => a.Status, newStatus);

        await db.Attendance.UpdateOneAsync(
            a => a.Id == record.Id,
            update,
            cancellationToken: cancellationToken);

        return new CheckOutResponse
        {
            AttendanceId = record.Id,
            AttendanceDate = record.AttendanceDate,
            CheckInAtUtc = record.CheckInAtUtc.Value,
            CheckOutAtUtc = now,
            WorkingDurationMinutes = workingDuration,
            Status = newStatus,
            Message = "Check-out successful"
        };
    }

    public async Task<TodayAttendanceResponse> GetTodayAttendanceAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var businessDate = GetBusinessDate(DateTimeOffset.UtcNow);

        var record = await db.Attendance
            .Find(a => a.UserId == userId && a.AttendanceDate == businessDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (record == null)
        {
            return new TodayAttendanceResponse { HasRecord = false };
        }

        return new TodayAttendanceResponse
        {
            HasRecord = true,
            Record = MapToRecordResponse(record)
        };
    }

    public async Task<AttendanceHistoryResponse> GetPersonalHistoryAsync(
        Guid userId,
        DateOnly? fromDate,
        DateOnly? toDate,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<Attendance>.Filter.Eq(a => a.UserId, userId);

        if (fromDate.HasValue)
        {
            filter &= Builders<Attendance>.Filter.Gte(a => a.AttendanceDate, fromDate.Value);
        }
        if (toDate.HasValue)
        {
            filter &= Builders<Attendance>.Filter.Lte(a => a.AttendanceDate, toDate.Value);
        }

        var total = await db.Attendance.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var records = await db.Attendance
            .Find(filter)
            .SortByDescending(a => a.AttendanceDate)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        return new AttendanceHistoryResponse
        {
            Records = records.Select(MapToRecordResponse).ToList(),
            TotalCount = (int)total,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AdminDailyReportResponse> GetAdminDailyReportAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);

        var isWorkingDay = settings.WorkingDays.Contains(date.DayOfWeek);
        var isHoliday = settings.Holidays.Any(h => h.Date == date || (h.IsRecurring && h.Date.Month == date.Month && h.Date.Day == date.Day));

        // Use a simpler approach: get users and attendance records separately
        // This avoids the DateOnly vs string comparison issue in aggregation
        var users = await db.Users
            .Find(u => u.Status == UserStatus.Active)
            .ToListAsync(cancellationToken);

        var startOfDayUtc = GetBusinessDateStartUtc(date);
        var endOfDayUtc = GetBusinessDateEndUtc(date);

        // Get attendance records for this date
        var records = await db.Attendance
            .Find(a => a.AttendanceDate == date)
            .ToListAsync(cancellationToken);

        var recordDict = records.ToDictionary(r => r.UserId, r => r);

        var responseRecords = new List<AdminDailyAttendanceResponse>();
        int presentCount = 0, lateCount = 0, absentCount = 0, onLeaveCount = 0, noRecordCount = 0;

        foreach (var user in users)
        {
            AttendanceStatus status;
            DateTimeOffset? checkInAtUtc = null;
            DateTimeOffset? checkOutAtUtc = null;
            int workingDurationMinutes = 0;
            int lateMinutes = 0;
            bool hasRecord = false;

            if (recordDict.TryGetValue(user.Id, out var record))
            {
                hasRecord = true;
                status = record.Status;
                checkInAtUtc = record.CheckInAtUtc;
                checkOutAtUtc = record.CheckOutAtUtc;
                workingDurationMinutes = record.WorkingDurationMinutes;
                lateMinutes = record.LateMinutes;

                if (status == AttendanceStatus.Present) presentCount++;
                else if (status == AttendanceStatus.Late) lateCount++;
                else if (status == AttendanceStatus.Absent) absentCount++;
                else if (status == AttendanceStatus.OnLeave) onLeaveCount++;
            }
            else
            {
                hasRecord = false;
                if (!isWorkingDay)
                {
                    status = AttendanceStatus.Holiday;
                }
                else if (isHoliday)
                {
                    status = AttendanceStatus.Holiday;
                }
                else
                {
                    status = AttendanceStatus.Absent;
                    absentCount++;
                }
                noRecordCount++;
            }

            responseRecords.Add(new AdminDailyAttendanceResponse
            {
                UserId = user.Id,
                UserEmail = user.Email,
                UserGoal = user.Goal,
                AttendanceDate = date,
                CheckInAtUtc = checkInAtUtc,
                CheckOutAtUtc = checkOutAtUtc,
                Status = status,
                WorkingDurationMinutes = workingDurationMinutes,
                LateMinutes = lateMinutes,
                HasRecord = hasRecord
            });
        }

        return new AdminDailyReportResponse
        {
            Date = date,
            Records = responseRecords,
            TotalUsers = responseRecords.Count,
            PresentCount = presentCount,
            LateCount = lateCount,
            AbsentCount = absentCount,
            OnLeaveCount = onLeaveCount,
            NoRecordCount = noRecordCount
        };
    }

    public async Task<AdminMonthlySummaryResponse> GetAdminMonthlySummaryAsync(
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var startDate = new DateOnly(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        // Get all active users
        var users = await db.Users
            .Find(u => u.Status == UserStatus.Active)
            .ToListAsync(cancellationToken);

        // Get attendance records for the month
        var records = await db.Attendance
            .Find(a => a.AttendanceDate >= startDate && a.AttendanceDate <= endDate)
            .ToListAsync(cancellationToken);

        var recordsByUser = records
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Calculate working days in month
        var workingDays = 0;
        var holidays = settings.Holidays
            .Where(h => h.Date >= startDate && h.Date <= endDate)
            .Select(h => h.Date)
            .ToHashSet();

        // Add recurring holidays
        foreach (var h in settings.Holidays.Where(h => h.IsRecurring))
        {
            if (h.Date.Month == month)
            {
                holidays.Add(new DateOnly(year, h.Date.Month, h.Date.Day));
            }
        }

        for (var d = startDate; d <= endDate; d = d.AddDays(1))
        {
            if (settings.WorkingDays.Contains(d.DayOfWeek) && !holidays.Contains(d))
            {
                workingDays++;
            }
        }

        var summaries = new List<MonthlySummaryResponse>();

        foreach (var user in users)
        {
            var userRecords = recordsByUser.TryGetValue(user.Id, out var r) ? r : new List<Attendance>();
            var recordDict = userRecords.ToDictionary(r => r.AttendanceDate, r => r);

            int presentDays = 0, lateDays = 0, absentDays = 0, onLeaveDays = 0, incompleteDays = 0;
            int totalWorkingMinutes = 0;

            for (var d = startDate; d <= endDate; d = d.AddDays(1))
            {
                var isWorkingDay = settings.WorkingDays.Contains(d.DayOfWeek);
                var isHoliday = holidays.Contains(d);

                if (!isWorkingDay || isHoliday)
                    continue;

                if (recordDict.TryGetValue(d, out var record))
                {
                    if (record.Status == AttendanceStatus.Present)
                    {
                        presentDays++;
                        totalWorkingMinutes += record.WorkingDurationMinutes;
                    }
                    else if (record.Status == AttendanceStatus.Late)
                    {
                        lateDays++;
                        totalWorkingMinutes += record.WorkingDurationMinutes;
                    }
                    else if (record.Status == AttendanceStatus.Absent)
                    {
                        absentDays++;
                    }
                    else if (record.Status == AttendanceStatus.OnLeave)
                    {
                        onLeaveDays++;
                    }
                    else if (record.Status == AttendanceStatus.Incomplete)
                    {
                        incompleteDays++;
                        totalWorkingMinutes += record.WorkingDurationMinutes;
                    }
                }
                else
                {
                    absentDays++;
                }
            }

            var attendancePercentage = workingDays > 0
                ? (double)(presentDays + lateDays) / workingDays * 100
                : 0;

            var avgHours = (presentDays + lateDays + incompleteDays) > 0
                ? (double)totalWorkingMinutes / (presentDays + lateDays + incompleteDays) / 60
                : 0;

            summaries.Add(new MonthlySummaryResponse
            {
                Year = year,
                Month = month,
                UserId = user.Id,
                UserEmail = user.Email,
                WorkingDaysInMonth = workingDays,
                PresentDays = presentDays,
                LateDays = lateDays,
                AbsentDays = absentDays,
                OnLeaveDays = onLeaveDays,
                IncompleteDays = incompleteDays,
                TotalWorkingMinutes = totalWorkingMinutes,
                AttendancePercentage = Math.Round(attendancePercentage, 2),
                AverageWorkingHoursPerDay = Math.Round(avgHours, 2)
            });
        }

        return new AdminMonthlySummaryResponse
        {
            Year = year,
            Month = month,
            Summaries = summaries
        };
    }

    private static AttendanceRecordResponse MapToRecordResponse(Attendance record)
    {
        return new AttendanceRecordResponse
        {
            Id = record.Id,
            UserId = record.UserId,
            AttendanceDate = record.AttendanceDate,
            CheckInAtUtc = record.CheckInAtUtc,
            CheckOutAtUtc = record.CheckOutAtUtc,
            Status = record.Status,
            WorkingDurationMinutes = record.WorkingDurationMinutes,
            LateMinutes = record.LateMinutes,
            Notes = record.Notes,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }
}