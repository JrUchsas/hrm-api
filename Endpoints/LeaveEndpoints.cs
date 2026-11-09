using System.Security.Claims;
using HrmApi.Data;
using HrmApi.Models;
using HrmApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace HrmApi.Endpoints;

public static class LeaveEndpoints
{
    public static void MapLeaveEndpoints(this WebApplication app)
    {
        // ===== MAIN API ROUTES (/api/leaves/*) =====
        // These are the primary backend routes

        var leavesGroup = app.MapGroup("/api/leaves")
            .WithTags("Leave Management")
            .RequireAuthorization(); // All leave endpoints require authentication

        // GET /api/leaves/balance - Get current user's leave balance for current year
        leavesGroup.MapGet("/balance", async (
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var balance = await leaveService.GetBalanceResponseAsync(userId, cancellationToken);
            return Results.Ok(balance);
        }).WithName("GetLeaveBalance");

        // GET /api/leaves/my-history - Get current user's leave application history
        leavesGroup.MapGet("/my-history", async (
            ClaimsPrincipal user,
            LeaveService leaveService,
            int page = 1,
            int pageSize = 20,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);

            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var history = await leaveService.GetUserLeaveHistoryAsync(userId, page, pageSize, cancellationToken);
            return Results.Ok(history);
        }).WithName("GetMyLeaveHistory");

        // POST /api/leaves/apply - Submit a new leave application
        leavesGroup.MapPost("/apply", async (
            ApplyLeaveRequestDto request,
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);

            if (!Enum.IsDefined(typeof(LeaveType), request.LeaveType))
            {
                return Results.BadRequest("Invalid leave type. Must be Sick or Casual.");
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest("Reason is required.");
            }

            if (request.Reason.Length > 500)
            {
                return Results.BadRequest("Reason cannot exceed 500 characters.");
            }

            try
            {
                var leaveRequest = await leaveService.ApplyLeaveAsync(userId, request, cancellationToken);
                return Results.Created($"/api/leaves/{leaveRequest.Id}", leaveRequest);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).WithName("ApplyLeave");

        // DELETE /api/leaves/{id}/cancel - Cancel a pending leave request
        leavesGroup.MapDelete("/{id:guid}/cancel", async (
            Guid id,
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            try
            {
                var cancelled = await leaveService.CancelLeaveAsync(id, userId, cancellationToken);
                return Results.Ok(new { message = "Leave request cancelled", cancelled });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound("Leave request not found.");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).WithName("CancelLeave");

        // Admin/HR endpoints - /api/admin/leaves/*
        var adminGroup = app.MapGroup("/api/admin/leaves")
            .WithTags("Leave Management - Admin")
            .RequireAuthorization("HrOrAdmin"); // Requires HR or Admin role

        // GET /api/admin/leaves/pending - Get all pending leave requests
        adminGroup.MapGet("/pending", async (
            LeaveService leaveService,
            LeaveStatus? status = null, // Optional status filter: Pending, Approved, Rejected, Cancelled
            int page = 1,
            int pageSize = 50,
            CancellationToken cancellationToken = default) =>
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 100) pageSize = 50;

            var result = await leaveService.GetLeaveRequestsAsync(status, page, pageSize, cancellationToken);
            return Results.Ok(result);
        }).WithName("GetPendingLeaveRequests");

        // POST /api/admin/leaves/{id}/review - Approve or reject a leave request
        adminGroup.MapPost("/{id:guid}/review", async (
            Guid id,
            ReviewLeaveRequestDto request,
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var reviewerUserId = GetUserId(user);

            if (!request.Approve && string.IsNullOrWhiteSpace(request.RejectionReason))
            {
                return Results.BadRequest("Rejection reason is required when rejecting a leave request.");
            }

            if (request.RejectionReason?.Length > 500)
            {
                return Results.BadRequest("Rejection reason cannot exceed 500 characters.");
            }

            try
            {
                var leaveRequest = await leaveService.ReviewLeaveAsync(
                    id,
                    reviewerUserId,
                    request.Approve,
                    request.RejectionReason?.Trim(),
                    cancellationToken);
                return Results.Ok(leaveRequest);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound("Leave request not found.");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).WithName("ReviewLeaveRequest");

        // POST /api/admin/leaves/balance/adjust - Adjust user's leave balance (HR/Admin)
        adminGroup.MapPost("/balance/adjust", async (
            AdjustLeaveBalanceDto request,
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var adminUserId = GetUserId(user);

            if (request.Year < 2000 || request.Year > 2100)
            {
                return Results.BadRequest("Invalid year. Must be between 2000 and 2100.");
            }

            if (request.TotalSickLeave.HasValue && request.TotalSickLeave < 0)
            {
                return Results.BadRequest("Total sick leave cannot be negative.");
            }
            if (request.UsedSickLeave.HasValue && request.UsedSickLeave < 0)
            {
                return Results.BadRequest("Used sick leave cannot be negative.");
            }
            if (request.TotalCasualLeave.HasValue && request.TotalCasualLeave < 0)
            {
                return Results.BadRequest("Total casual leave cannot be negative.");
            }
            if (request.UsedCasualLeave.HasValue && request.UsedCasualLeave < 0)
            {
                return Results.BadRequest("Used casual leave cannot be negative.");
            }

            try
            {
                var balance = await leaveService.AdjustBalanceAsync(
                    request.UserId,
                    request.Year,
                    request.TotalSickLeave,
                    request.UsedSickLeave,
                    request.TotalCasualLeave,
                    request.UsedCasualLeave,
                    cancellationToken);
                return Results.Ok(balance);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).WithName("AdjustLeaveBalance");
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var subClaim = user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (subClaim == null || !Guid.TryParse(subClaim, out var userId))
        {
            throw new UnauthorizedAccessException("Invalid user ID in token.");
        }

        return userId;
    }
}