using HrmApi.Data;
using HrmApi.Models;
using MongoDB.Driver;

namespace HrmApi.Services;

public sealed class LeaveService(MongoDbContext db)
{
    private const int DefaultSickLeave = 14;
    private const int DefaultCasualLeave = 10;

    /// <summary>
    /// Gets or creates a leave balance for the user for the specified year.
    /// If no balance exists, creates one with default quotas.
    /// </summary>
    public async Task<LeaveBalance> GetOrCreateBalanceAsync(Guid userId, int year, CancellationToken cancellationToken = default, IClientSessionHandle? session = null)
    {
        var filter = Builders<LeaveBalance>.Filter.And(
            Builders<LeaveBalance>.Filter.Eq(lb => lb.UserId, userId),
            Builders<LeaveBalance>.Filter.Eq(lb => lb.Year, year));

        var balance = session != null
            ? await db.LeaveBalances.Find(session, filter).FirstOrDefaultAsync(cancellationToken)
            : await db.LeaveBalances.Find(filter).FirstOrDefaultAsync(cancellationToken);

        if (balance != null)
        {
            return balance;
        }

        // Create new balance with defaults
        balance = new LeaveBalance
        {
            UserId = userId,
            Year = year,
            TotalSickLeave = DefaultSickLeave,
            UsedSickLeave = 0,
            TotalCasualLeave = DefaultCasualLeave,
            UsedCasualLeave = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        if (session != null)
        {
            await db.LeaveBalances.InsertOneAsync(session, balance, cancellationToken: cancellationToken);
        }
        else
        {
            await db.LeaveBalances.InsertOneAsync(balance, cancellationToken: cancellationToken);
        }
        return balance;
    }

    /// <summary>
    /// Gets the leave balance for the current year, creating if necessary.
    /// </summary>
    public async Task<LeaveBalanceResponse> GetBalanceResponseAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var year = DateTimeOffset.UtcNow.Year;
        var balance = await GetOrCreateBalanceAsync(userId, year, cancellationToken);

        return new LeaveBalanceResponse
        {
            Year = balance.Year,
            TotalSickLeave = balance.TotalSickLeave,
            UsedSickLeave = balance.UsedSickLeave,
            RemainingSickLeave = balance.RemainingSickLeave,
            TotalCasualLeave = balance.TotalCasualLeave,
            UsedCasualLeave = balance.UsedCasualLeave,
            RemainingCasualLeave = balance.RemainingCasualLeave
        };
    }

    /// <summary>
    /// Gets the user's leave application history with pagination.
    /// </summary>
    public async Task<LeaveHistoryResponse> GetUserLeaveHistoryAsync(
        Guid userId,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<LeaveRequest>.Filter.Eq(lr => lr.UserId, userId);

        var total = await db.LeaveRequests.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var requests = await db.LeaveRequests
            .Find(filter)
            .SortByDescending(lr => lr.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        // Get user emails for reviewer info
        var reviewerIds = requests
            .Where(r => r.ReviewedByUserId.HasValue)
            .Select(r => r.ReviewedByUserId!.Value)
            .Distinct()
            .ToList();

        var reviewers = await db.Users
            .Find(u => reviewerIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        var reviewerDict = reviewers.ToDictionary(r => r.Id, r => r.Email);

        return new LeaveHistoryResponse
        {
            Requests = requests.Select(r => MapToResponse(r, reviewerDict)).ToList(),
            TotalCount = (int)total,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Submits a new leave application with validation.
    /// </summary>
    public async Task<LeaveRequestResponse> ApplyLeaveAsync(
        Guid userId,
        ApplyLeaveRequestDto dto,
        CancellationToken cancellationToken = default)
    {
        // Validation: EndDate >= StartDate
        if (dto.EndDate < dto.StartDate)
        {
            throw new ArgumentException("End date must be on or after start date.");
        }

        // Calculate days count (inclusive of start and end dates)
        var daysCount = dto.EndDate.DayNumber - dto.StartDate.DayNumber + 1;

        if (daysCount <= 0)
        {
            throw new ArgumentException("Invalid date range: end date must be on or after start date.");
        }

        // Get or create balance for the year of the start date
        var year = dto.StartDate.Year;
        var balance = await GetOrCreateBalanceAsync(userId, year, cancellationToken);

        // Check remaining balance for the leave type
        var remaining = balance.GetRemaining(dto.LeaveType);
        if (daysCount > remaining)
        {
            var leaveTypeName = dto.LeaveType == LeaveType.Sick ? "Sick" : "Casual";
            throw new InvalidOperationException(
                $"Insufficient {leaveTypeName} leave balance. Requested: {daysCount} days, Remaining: {remaining} days.");
        }

        // Check for overlapping pending/approved leaves
        var overlapFilter = Builders<LeaveRequest>.Filter.And(
            Builders<LeaveRequest>.Filter.Eq(lr => lr.UserId, userId),
            Builders<LeaveRequest>.Filter.In(lr => lr.Status, new[] { LeaveStatus.Pending, LeaveStatus.Approved }),
            Builders<LeaveRequest>.Filter.Lte(lr => lr.StartDate, dto.EndDate),
            Builders<LeaveRequest>.Filter.Gte(lr => lr.EndDate, dto.StartDate)
        );

        var hasOverlap = await db.LeaveRequests.Find(overlapFilter).AnyAsync(cancellationToken);
        if (hasOverlap)
        {
            throw new InvalidOperationException("You already have a pending or approved leave that overlaps with the requested dates.");
        }

        // Create the leave request
        var leaveRequest = new LeaveRequest
        {
            UserId = userId,
            LeaveType = dto.LeaveType,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            DaysCount = daysCount,
            Reason = dto.Reason?.Trim() ?? string.Empty,
            Status = LeaveStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await db.LeaveRequests.InsertOneAsync(leaveRequest, cancellationToken: cancellationToken);

        return MapToResponse(leaveRequest, new Dictionary<Guid, string>());
    }

    /// <summary>
    /// Gets all pending leave requests across the company (for HR/Admin).
    /// </summary>
    public async Task<PendingLeaveRequestsResponse> GetPendingRequestsAsync(CancellationToken cancellationToken = default)
    {
        var filter = Builders<LeaveRequest>.Filter.Eq(lr => lr.Status, LeaveStatus.Pending);

        var total = await db.LeaveRequests.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var requests = await db.LeaveRequests
            .Find(filter)
            .SortByDescending(lr => lr.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        // Get user emails for all applicants and reviewers
        var userIds = requests
            .Select(r => r.UserId)
            .Concat(requests.Where(r => r.ReviewedByUserId.HasValue).Select(r => r.ReviewedByUserId!.Value))
            .Distinct()
            .ToList();

        var users = await db.Users
            .Find(u => userIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        var userDict = users.ToDictionary(u => u.Id, u => u.Email);

        return new PendingLeaveRequestsResponse
        {
            Requests = requests.Select(r => MapToResponse(r, userDict)).ToList(),
            TotalCount = (int)total
        };
    }

    /// <summary>
    /// Reviews (approves/rejects) a leave request.
    /// On approval: atomically increments the used balance.
    /// On rejection: marks as rejected without touching balance.
    /// </summary>
    public async Task<LeaveRequestResponse> ReviewLeaveAsync(
        Guid requestId,
        Guid reviewerUserId,
        bool approve,
        string? rejectionReason,
        CancellationToken cancellationToken = default)
    {
        var request = await db.LeaveRequests
            .Find(lr => lr.Id == requestId)
            .FirstOrDefaultAsync(cancellationToken);

        if (request == null)
        {
            throw new KeyNotFoundException("Leave request not found.");
        }

        if (request.Status != LeaveStatus.Pending)
        {
            throw new InvalidOperationException($"Leave request is already {request.Status} and cannot be reviewed.");
        }

        if (approve)
        {
            // Use a session for atomic balance update
            var session = await db.LeaveBalances.Database.Client.StartSessionAsync(cancellationToken: cancellationToken);
            try
            {
                session.StartTransaction();

                // Get balance for the year of the leave request (within transaction)
                var year = request.StartDate.Year;
                var balance = await GetOrCreateBalanceAsync(request.UserId, year, cancellationToken, session);

                // Verify sufficient balance still exists
                var remaining = balance.GetRemaining(request.LeaveType);
                if (request.DaysCount > remaining)
                {
                    throw new InvalidOperationException(
                        $"Insufficient balance at time of approval. Remaining: {remaining} days, Requested: {request.DaysCount} days.");
                }

                // Increment used balance
                balance.IncrementUsed(request.LeaveType, request.DaysCount);
                await db.LeaveBalances.ReplaceOneAsync(
                    session,
                    lb => lb.Id == balance.Id,
                    balance,
                    cancellationToken: cancellationToken);

                // Update leave request status
                request.Status = LeaveStatus.Approved;
                request.ReviewedByUserId = reviewerUserId;
                request.ReviewedAtUtc = DateTimeOffset.UtcNow;
                request.UpdatedAtUtc = DateTimeOffset.UtcNow;

                await db.LeaveRequests.ReplaceOneAsync(
                    session,
                    lr => lr.Id == request.Id,
                    request,
                    cancellationToken: cancellationToken);

                await session.CommitTransactionAsync(cancellationToken);
            }
            catch
            {
                await session.AbortTransactionAsync(cancellationToken);
                throw;
            }
            finally
            {
                session.Dispose();
            }
        }
        else
        {
            // Reject - no balance change needed
            if (string.IsNullOrWhiteSpace(rejectionReason))
            {
                throw new ArgumentException("Rejection reason is required when rejecting a leave request.");
            }

            request.Status = LeaveStatus.Rejected;
            request.ReviewedByUserId = reviewerUserId;
            request.ReviewedAtUtc = DateTimeOffset.UtcNow;
            request.RejectionReason = rejectionReason.Trim();
            request.UpdatedAtUtc = DateTimeOffset.UtcNow;

            await db.LeaveRequests.ReplaceOneAsync(
                lr => lr.Id == request.Id,
                request,
                cancellationToken: cancellationToken);
        }

        // Get reviewer email for response
        var reviewer = await db.Users
            .Find(u => u.Id == reviewerUserId)
            .FirstOrDefaultAsync(cancellationToken);

        var userDict = new Dictionary<Guid, string>();
        if (reviewer != null)
        {
            userDict[reviewerUserId] = reviewer.Email;
        }

        return MapToResponse(request, userDict);
    }

    /// <summary>
    /// Gets leave requests with optional status filter and pagination (for admin).
    /// </summary>
    public async Task<PendingLeaveRequestsResponse> GetLeaveRequestsAsync(
        LeaveStatus? status = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var filterBuilder = Builders<LeaveRequest>.Filter;
        var filter = filterBuilder.Empty;

        if (status.HasValue)
        {
            filter = filterBuilder.Eq(lr => lr.Status, status.Value);
        }

        var total = await db.LeaveRequests.CountDocumentsAsync(filter, cancellationToken: cancellationToken);

        var requests = await db.LeaveRequests
            .Find(filter)
            .SortByDescending(lr => lr.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

        // Get user emails for all applicants and reviewers
        var userIds = requests
            .Select(r => r.UserId)
            .Concat(requests.Where(r => r.ReviewedByUserId.HasValue).Select(r => r.ReviewedByUserId!.Value))
            .Distinct()
            .ToList();

        var users = await db.Users
            .Find(u => userIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        var userDict = users.ToDictionary(u => u.Id, u => u.Email);

        return new PendingLeaveRequestsResponse
        {
            Requests = requests.Select(r => MapToResponse(r, userDict)).ToList(),
            TotalCount = (int)total
        };
    }

    /// <summary>
    /// Gets the count of pending leave requests for a specific user.
    /// </summary>
    public async Task<int> GetPendingCountForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var filter = Builders<LeaveRequest>.Filter.And(
            Builders<LeaveRequest>.Filter.Eq(lr => lr.UserId, userId),
            Builders<LeaveRequest>.Filter.Eq(lr => lr.Status, LeaveStatus.Pending)
        );

        return (int)await db.LeaveRequests.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Cancels a pending leave request by the owner.
    /// </summary>
    public async Task<LeaveRequestResponse> CancelLeaveAsync(
        Guid requestId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var request = await db.LeaveRequests
            .Find(lr => lr.Id == requestId)
            .FirstOrDefaultAsync(cancellationToken);

        if (request == null)
        {
            throw new KeyNotFoundException("Leave request not found.");
        }

        if (request.UserId != userId)
        {
            throw new InvalidOperationException("You can only cancel your own leave requests.");
        }

        if (request.Status != LeaveStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot cancel a leave request that is already {request.Status}.");
        }

        request.Status = LeaveStatus.Cancelled;
        request.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.LeaveRequests.ReplaceOneAsync(
            lr => lr.Id == request.Id,
            request,
            cancellationToken: cancellationToken);

        // Get user email for response
        var user = await db.Users
            .Find(u => u.Id == userId)
            .FirstOrDefaultAsync(cancellationToken);

        var userDict = new Dictionary<Guid, string>();
        if (user != null)
        {
            userDict[userId] = user.Email;
        }

        return MapToResponse(request, userDict);
    }

    /// <summary>
    /// Adjusts an employee's leave balance (for HR/Admin).
    /// </summary>
    public async Task<LeaveBalanceResponse> AdjustBalanceAsync(
        Guid userId,
        int year,
        int? totalSickLeave,
        int? usedSickLeave,
        int? totalCasualLeave,
        int? usedCasualLeave,
        CancellationToken cancellationToken = default)
    {
        var balance = await GetOrCreateBalanceAsync(userId, year, cancellationToken);

        if (totalSickLeave.HasValue)
            balance.TotalSickLeave = Math.Max(0, totalSickLeave.Value);
        if (usedSickLeave.HasValue)
            balance.UsedSickLeave = Math.Max(0, usedSickLeave.Value);
        if (totalCasualLeave.HasValue)
            balance.TotalCasualLeave = Math.Max(0, totalCasualLeave.Value);
        if (usedCasualLeave.HasValue)
            balance.UsedCasualLeave = Math.Max(0, usedCasualLeave.Value);

        balance.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await db.LeaveBalances.ReplaceOneAsync(
            lb => lb.Id == balance.Id,
            balance,
            cancellationToken: cancellationToken);

        return new LeaveBalanceResponse
        {
            Year = balance.Year,
            TotalSickLeave = balance.TotalSickLeave,
            UsedSickLeave = balance.UsedSickLeave,
            RemainingSickLeave = balance.RemainingSickLeave,
            TotalCasualLeave = balance.TotalCasualLeave,
            UsedCasualLeave = balance.UsedCasualLeave,
            RemainingCasualLeave = balance.RemainingCasualLeave
        };
    }

    private static LeaveRequestResponse MapToResponse(LeaveRequest request, Dictionary<Guid, string> userEmailDict)
    {
        var response = new LeaveRequestResponse
        {
            Id = request.Id,
            UserId = request.UserId,
            LeaveType = request.LeaveType,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            DaysCount = request.DaysCount,
            Reason = request.Reason,
            Status = request.Status,
            ReviewedByUserId = request.ReviewedByUserId,
            ReviewedAtUtc = request.ReviewedAtUtc,
            RejectionReason = request.RejectionReason,
            CreatedAtUtc = request.CreatedAtUtc,
            UpdatedAtUtc = request.UpdatedAtUtc
        };

        if (userEmailDict.TryGetValue(request.UserId, out var userEmail))
        {
            response.UserEmail = userEmail;
        }

        if (request.ReviewedByUserId.HasValue && userEmailDict.TryGetValue(request.ReviewedByUserId.Value, out var reviewerEmail))
        {
            response.ReviewerEmail = reviewerEmail;
        }

        return response;
    }
}