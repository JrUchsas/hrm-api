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
            CancellationToken cancellationToken) =>
        {
            var pending = await leaveService.GetPendingRequestsAsync(cancellationToken);
            return Results.Ok(pending);
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

        // ===== FRONTEND COMPATIBILITY ALIASES (/api/leave/*) =====
        // These match the frontend's expected routes
        // Separate group to avoid route conflicts with /api/leaves/*

        var frontendGroup = app.MapGroup("/api/leave")
            .WithTags("Leave Management - Frontend Aliases")
            .RequireAuthorization();

        // GET /api/leave/types -> Returns leave type configs
        frontendGroup.MapGet("/types", async (
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var balance = await leaveService.GetBalanceResponseAsync(userId, cancellationToken);
            // Frontend expects: [{id: 1, name: "Sick", maxDays: 14, usedDays: 3, remainingDays: 11}, ...]
            var types = new[]
            {
                new
                {
                    id = 1,
                    name = "Sick",
                    maxDays = balance.TotalSickLeave,
                    usedDays = balance.UsedSickLeave,
                    remainingDays = balance.RemainingSickLeave
                },
                new
                {
                    id = 2,
                    name = "Casual",
                    maxDays = balance.TotalCasualLeave,
                    usedDays = balance.UsedCasualLeave,
                    remainingDays = balance.RemainingCasualLeave
                }
            };
            return Results.Ok(types);
        }).WithName("GetLeaveTypes");

        // GET /api/leave/my -> Maps to /api/leaves/my-history
        frontendGroup.MapGet("/my", async (
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
        }).WithName("GetMyLeaveHistoryAlias");

        // GET /api/leave/summary -> Summary of leave balances and pending counts
        frontendGroup.MapGet("/summary", async (
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken = default) =>
        {
            var userId = GetUserId(user);
            var balance = await leaveService.GetBalanceResponseAsync(userId, cancellationToken);

            // Also get pending count
            var pendingCount = await leaveService.GetPendingCountForUserAsync(userId, cancellationToken);

            var summary = new
            {
                sickLeave = new
                {
                    total = balance.TotalSickLeave,
                    used = balance.UsedSickLeave,
                    remaining = balance.RemainingSickLeave
                },
                casualLeave = new
                {
                    total = balance.TotalCasualLeave,
                    used = balance.UsedCasualLeave,
                    remaining = balance.RemainingCasualLeave
                },
                pendingRequests = pendingCount
            };
            return Results.Ok(summary);
        }).WithName("GetLeaveSummary");

        // POST /api/leave/apply -> Submit a new leave application
        frontendGroup.MapPost("/apply", async (
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
                return Results.Created($"/api/leave/{leaveRequest.Id}", leaveRequest);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        }).WithName("ApplyLeaveAlias");

        // DELETE /api/leave/{id}/cancel -> Cancel a pending leave request
        frontendGroup.MapDelete("/{id:guid}/cancel", async (
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
        }).WithName("CancelLeaveAlias");

        // Admin alias group: /api/admin/leave/*
        var adminAliasGroup = app.MapGroup("/api/admin/leave")
            .WithTags("Leave Management - Admin Aliases")
            .RequireAuthorization("HrOrAdmin");

        // GET /api/admin/leave/pending
        adminAliasGroup.MapGet("/pending", async (
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var pending = await leaveService.GetPendingRequestsAsync(cancellationToken);
            return Results.Ok(pending);
        }).WithName("GetPendingLeaveRequestsAlias");

        // POST /api/admin/leave/{id}/approve
        adminAliasGroup.MapPost("/{id:guid}/approve", async (
            Guid id,
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var reviewerUserId = GetUserId(user);
            try
            {
                var leaveRequest = await leaveService.ReviewLeaveAsync(
                    id,
                    reviewerUserId,
                    true,
                    null,
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
        }).WithName("ApproveLeave");

        // POST /api/admin/leave/{id}/reject
        adminAliasGroup.MapPost("/{id:guid}/reject", async (
            Guid id,
            ReviewLeaveRequestDto request,
            ClaimsPrincipal user,
            LeaveService leaveService,
            CancellationToken cancellationToken) =>
        {
            var reviewerUserId = GetUserId(user);

            if (string.IsNullOrWhiteSpace(request.RejectionReason))
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
                    false,
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
        }).WithName("RejectLeave");
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