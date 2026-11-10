using System.Security.Claims;
using HrmApi.Data;
using HrmApi.Models;
using HrmApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using System.IO;

namespace HrmApi.Endpoints;

public sealed class UploadResponse
{
    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string ContentType { get; set; } = string.Empty;
}

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
                return Results.BadRequest(new { message = "Reason is required." });
            }

            if (request.Reason.Length > 500)
            {
                return Results.BadRequest(new { message = "Reason cannot exceed 500 characters." });
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

        // POST /api/leaves/upload - Upload PDF attachment for leave request
        leavesGroup.MapPost("/upload", async (
            IFormFile file,
            ClaimsPrincipal user,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);

            // Validate file
            if (file == null || file.Length == 0)
            {
                return Results.BadRequest(new { message = "No file uploaded." });
            }

            // Only allow PDF
            if (!file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { message = "Only PDF files are allowed." });
            }

            // Max 5MB
            const long maxSize = 5 * 1024 * 1024;
            if (file.Length > maxSize)
            {
                return Results.BadRequest(new { message = "File size cannot exceed 5MB." });
            }

            // Generate unique filename
            var extension = Path.GetExtension(file.FileName);
            var uniqueFileName = $"{Guid.NewGuid()}{extension}";
            var uploadsPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "leaves");
            Directory.CreateDirectory(uploadsPath);
            var filePath = Path.Combine(uploadsPath, uniqueFileName);

            // Save file
            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream, cancellationToken);

            // Return URL that can be used in leave application
            var url = $"/api/leaves/files/{uniqueFileName}";
            return Results.Ok(new UploadResponse
            {
                Url = url,
                FileName = file.FileName,
                Size = file.Length,
                ContentType = file.ContentType
            });
        }).DisableAntiforgery()
        .WithName("UploadLeaveAttachment")
        .Accepts<IFormFile>("multipart/form-data");

        // GET /api/leaves/files/{fileName} - Serve uploaded PDF
        leavesGroup.MapGet("/files/{fileName}", async (
            string fileName,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            // Prevent path traversal
            if (fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
            {
                return Results.NotFound();
            }

            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "leaves", fileName);
            if (!File.Exists(filePath))
            {
                return Results.NotFound();
            }

            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Results.File(fileStream, "application/pdf", fileName);
        }).WithName("GetLeaveAttachment")
        .AllowAnonymous(); // Allow access without auth for direct file links

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
                return Results.BadRequest(new { message = "Rejection reason is required when rejecting a leave request." });
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