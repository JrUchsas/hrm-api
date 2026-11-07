using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HrmApi.Data;
using HrmApi.Models;
using HrmApi.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MongoDB.Driver;

namespace HrmApi.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var authGroup = app.MapGroup("/api/auth")
            .WithTags("Authentication")
            .RequireRateLimiting("auth-policy");

        // Register
        authGroup.MapPost("/register", async (
            RegisterRequest request,
            MongoDbContext db,
            IPasswordHasher<User> hasher,
            AuthService auth,
            PermissionService permService,
            CancellationToken cancellationToken) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            if (!IsValidEmail(email)) return Results.BadRequest("Invalid email format.");
            if (request.Password.Length < 8) return Results.BadRequest("Password must be at least 8 characters.");
            if (await db.Users.Find(u => u.Email == email).AnyAsync(cancellationToken)) return Results.Conflict("Email is already in use.");

            var user = new User { Email = email, PasswordHash = "pending", Goal = request.Goal?.Trim() };
            user.PasswordHash = hasher.HashPassword(user, request.Password);
            await db.Users.InsertOneAsync(user, cancellationToken: cancellationToken);

            // Assign default "user" role
            var userRole = await db.Roles.Find(r => r.Name == "user").FirstOrDefaultAsync(cancellationToken);
            if (userRole != null)
            {
                await db.UserRoles.InsertOneAsync(new UserRole { UserId = user.Id, RoleId = userRole.Id }, cancellationToken: cancellationToken);
            }

            var permissions = await permService.GetUserPermissionsAsync(user.Id, cancellationToken);
            var roles = await permService.GetUserRolesAsync(user.Id, cancellationToken);

            var accessToken = auth.CreateAccessToken(user, roles, permissions);
            var refreshToken = auth.GenerateRefreshToken();
            var refreshTokenExpiry = auth.GetRefreshTokenExpiry();

            await db.RefreshTokens.InsertOneAsync(new RefreshToken
            {
                Token = refreshToken,
                UserId = user.Id,
                ExpiresAt = refreshTokenExpiry,
                DeviceInfo = request.DeviceInfo,
                IpAddress = request.IpAddress
            }, cancellationToken: cancellationToken);

            return Results.Ok(new AuthResponse(accessToken, refreshToken, user.Email, auth.AccessTokenLifetime));
        }).WithName("Register");

        // Login
        authGroup.MapPost("/login", async (
            LoginRequest request,
            MongoDbContext db,
            IPasswordHasher<User> hasher,
            AuthService auth,
            PermissionService permService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            var user = await db.Users.Find(u => u.Email == email).FirstOrDefaultAsync(cancellationToken);

            if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
                return Results.Unauthorized();

            if (user.Status != UserStatus.Active)
                return Results.Problem("Account is not active.", statusCode: 403);

            // Update last login
            user.LastLoginAt = DateTimeOffset.UtcNow;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await db.Users.ReplaceOneAsync(u => u.Id == user.Id, user, cancellationToken: cancellationToken);

            var permissions = await permService.GetUserPermissionsAsync(user.Id, cancellationToken);
            var roles = await permService.GetUserRolesAsync(user.Id, cancellationToken);

            var accessToken = auth.CreateAccessToken(user, roles, permissions);
            var refreshToken = auth.GenerateRefreshToken();
            var refreshTokenExpiry = auth.GetRefreshTokenExpiry();

            await db.RefreshTokens.InsertOneAsync(new RefreshToken
            {
                Token = refreshToken,
                UserId = user.Id,
                ExpiresAt = refreshTokenExpiry,
                DeviceInfo = request.DeviceInfo,
                IpAddress = httpContext.Connection.RemoteIpAddress?.ToString()
            }, cancellationToken: cancellationToken);

            return Results.Ok(new AuthResponse(accessToken, refreshToken, user.Email, auth.AccessTokenLifetime));
        }).WithName("Login");

        // Refresh token
        authGroup.MapPost("/refresh", async (
            RefreshRequest request,
            MongoDbContext db,
            AuthService auth,
            PermissionService permService,
            IPasswordHasher<User> hasher,
            CancellationToken cancellationToken) =>
        {
            var storedToken = await db.RefreshTokens
                .Find(rt => rt.Token == request.RefreshToken)
                .FirstOrDefaultAsync(cancellationToken);

            if (storedToken == null || !storedToken.IsActive)
                return Results.Unauthorized();

            var user = await db.Users.Find(u => u.Id == storedToken.UserId).FirstOrDefaultAsync(cancellationToken);
            if (user == null || user.Status != UserStatus.Active)
                return Results.Unauthorized();

            // Revoke old refresh token (rotation)
            storedToken.RevokedAt = DateTimeOffset.UtcNow;
            storedToken.ReplacedByToken = auth.GenerateRefreshToken();
            await db.RefreshTokens.ReplaceOneAsync(rt => rt.Id == storedToken.Id, storedToken, cancellationToken: cancellationToken);

            // Create new tokens
            var permissions = await permService.GetUserPermissionsAsync(user.Id, cancellationToken);
            var roles = await permService.GetUserRolesAsync(user.Id, cancellationToken);

            var accessToken = auth.CreateAccessToken(user, roles, permissions);
            var newRefreshToken = auth.GenerateRefreshToken();
            var refreshTokenExpiry = auth.GetRefreshTokenExpiry();

            await db.RefreshTokens.InsertOneAsync(new RefreshToken
            {
                Token = newRefreshToken,
                UserId = user.Id,
                ExpiresAt = refreshTokenExpiry,
            }, cancellationToken: cancellationToken);

            return Results.Ok(new AuthResponse(accessToken, newRefreshToken, user.Email, auth.AccessTokenLifetime));
        }).WithName("RefreshToken");

        // Logout (revoke refresh token)
        authGroup.MapPost("/logout", async (
            LogoutRequest request,
            MongoDbContext db,
            AuthService auth,
            CancellationToken cancellationToken) =>
        {
            var storedToken = await db.RefreshTokens
                .Find(rt => rt.Token == request.RefreshToken)
                .FirstOrDefaultAsync(cancellationToken);

            if (storedToken != null && storedToken.IsActive)
            {
                storedToken.RevokedAt = DateTimeOffset.UtcNow;
                await db.RefreshTokens.ReplaceOneAsync(rt => rt.Id == storedToken.Id, storedToken, cancellationToken: cancellationToken);
            }

            return Results.Ok(new { message = "Logged out successfully" });
        }).RequireAuthorization().WithName("Logout");

        // Logout all devices (revoke all refresh tokens for user)
        authGroup.MapPost("/logout-all", async (
            ClaimsPrincipal user,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var update = Builders<RefreshToken>.Update.Set(rt => rt.RevokedAt, DateTimeOffset.UtcNow);
            var result = await db.RefreshTokens.UpdateManyAsync(
                rt => rt.UserId == userId && rt.RevokedAt == null,
                update,
                cancellationToken: cancellationToken);

            return Results.Ok(new { message = "Logged out from all devices", revokedCount = result.ModifiedCount });
        }).RequireAuthorization().WithName("LogoutAll");

        // Get current user with roles and permissions
        authGroup.MapGet("/me", async (
            ClaimsPrincipal user,
            MongoDbContext db,
            PermissionService permService,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var profile = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);

            if (profile is null) return Results.NotFound();

            var permissions = await permService.GetUserPermissionsAsync(userId, cancellationToken);
            var roles = await permService.GetUserRolesAsync(userId, cancellationToken);

            return Results.Ok(new MeResponse(profile.Email, profile.Goal, roles, permissions));
        }).RequireAuthorization().WithName("CurrentUser");

        // Forgot password
        authGroup.MapPost("/forgot-password", async (
            ForgotPasswordRequest request,
            MongoDbContext db,
            PasswordResetTokenService tokenService,
            IEmailService emailService,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();

            if (!IsValidEmail(email))
                return Results.BadRequest("Invalid email format.");

            var user = await db.Users.Find(u => u.Email == email).FirstOrDefaultAsync(cancellationToken);

            // Always return success to prevent email enumeration
            if (user == null || user.Status != UserStatus.Active)
                return Results.Ok(new { message = "If the email exists, a reset link has been sent" });

            // Generate reset token
            var plainToken = await tokenService.GenerateTokenAsync(user.Id, cancellationToken);

            // Build reset link from configured frontend URL
            var frontendUrl = configuration["App:FrontendUrl"] ?? "http://localhost:3000";
            var resetLink = $"{frontendUrl.TrimEnd('/')}/reset-password?token={plainToken}&email={Uri.EscapeDataString(email)}";

            // Send email
            var emailResult = await emailService.SendPasswordResetEmailAsync(email, resetLink, email, cancellationToken);

            if (!emailResult.Success)
            {
                // Log but don't expose to user
                Console.Error.WriteLine($"[FORGOT PASSWORD] Email delivery failed for {email}: {emailResult.ErrorMessage}");
            }

            return Results.Ok(new { message = "If the email exists, a reset link has been sent" });
        }).WithName("ForgotPassword");

        // Reset password
        authGroup.MapPost("/reset-password", async (
            ResetPasswordRequest request,
            MongoDbContext db,
            PasswordResetTokenService tokenService,
            IPasswordHasher<User> hasher,
            CancellationToken cancellationToken) =>
        {
            var email = request.Email.Trim().ToLowerInvariant();
            var plainToken = request.ResetToken;
            var newPassword = request.NewPassword;

            if (!IsValidEmail(email))
                return Results.BadRequest("Invalid email format.");

            if (string.IsNullOrWhiteSpace(plainToken))
                return Results.BadRequest("Reset token is required.");

            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
                return Results.BadRequest("New password must be at least 8 characters.");

            // Validate token
            var user = await tokenService.ValidateTokenAsync(email, plainToken, cancellationToken);

            if (user == null)
                return Results.BadRequest("Invalid or expired reset token.");

            // Consume the token (single-use)
            var consumed = await tokenService.ConsumeTokenAsync(plainToken, cancellationToken);
            if (!consumed)
                return Results.BadRequest("Invalid or expired reset token.");

            // Update password
            user.PasswordHash = hasher.HashPassword(user, newPassword);
            user.UpdatedAt = DateTimeOffset.UtcNow;

            // Revoke all refresh tokens (force re-login)
            var update = Builders<RefreshToken>.Update.Set(rt => rt.RevokedAt, DateTimeOffset.UtcNow);
            await db.RefreshTokens.UpdateManyAsync(
                rt => rt.UserId == user.Id && rt.RevokedAt == null,
                update,
                cancellationToken: cancellationToken);

            await db.Users.ReplaceOneAsync(u => u.Id == user.Id, user, cancellationToken: cancellationToken);

            return Results.Ok(new { message = "Password reset successfully" });
        }).WithName("ResetPassword");

        // Change password (authenticated)
        authGroup.MapPost("/change-password", async (
            ChangePasswordRequest request,
            ClaimsPrincipal user,
            MongoDbContext db,
            IPasswordHasher<User> hasher,
            CancellationToken cancellationToken) =>
        {
            var userId = GetUserId(user);
            var profile = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);

            if (profile is null) return Results.NotFound();

            if (hasher.VerifyHashedPassword(profile, profile.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
                return Results.BadRequest("Current password is incorrect");

            if (request.NewPassword.Length < 8) return Results.BadRequest("New password must be at least 8 characters");

            profile.PasswordHash = hasher.HashPassword(profile, request.NewPassword);
            profile.UpdatedAt = DateTimeOffset.UtcNow;

            // Revoke all refresh tokens (force re-login on other devices)
            var update = Builders<RefreshToken>.Update.Set(rt => rt.RevokedAt, DateTimeOffset.UtcNow);
            await db.RefreshTokens.UpdateManyAsync(
                rt => rt.UserId == userId && rt.RevokedAt == null,
                update,
                cancellationToken: cancellationToken);

            await db.Users.ReplaceOneAsync(u => u.Id == userId, profile, cancellationToken: cancellationToken);

            return Results.Ok(new { message = "Password changed successfully" });
        }).RequireAuthorization().WithName("ChangePassword");
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var subClaim = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (subClaim == null || !Guid.TryParse(subClaim, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }
}

// Request/Response DTOs
public record RegisterRequest(string Email, string Password, string? Goal, string? DeviceInfo, string? IpAddress);
public record LoginRequest(string Email, string Password, string? DeviceInfo);
public record RefreshRequest(string RefreshToken);
public record LogoutRequest(string RefreshToken);
public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Email, string NewPassword, string ResetToken);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record AuthResponse(string AccessToken, string RefreshToken, string Email, TimeSpan AccessTokenLifetime);
public record MeResponse(string Email, string? Goal, List<string> Roles, List<string> Permissions);