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
        // Authorization policy for HR/Admin
        // This should be registered in Program.cs: options.AddPolicy("HrOrAdmin", policy => policy.RequireRole("Admin", "HR"));

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

        // Admin/HR endpoints
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