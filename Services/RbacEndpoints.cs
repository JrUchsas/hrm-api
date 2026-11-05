using System.Security.Claims;
using HrmApi.Data;
using HrmApi.Models;
using HrmApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;

namespace HrmApi.Endpoints;

public static class RbacEndpoints
{
    public static void MapRbacEndpoints(this WebApplication app)
    {
        var rbacGroup = app.MapGroup("/api/rbac")
            .RequireAuthorization()
            .WithTags("RBAC");

        // ============ BOOTSTRAP ============
        // Create first superadmin - only works if no superadmin exists
        rbacGroup.MapPost("/bootstrap/superadmin", async (
            BootstrapSuperAdminRequest request,
            MongoDbContext db,
            IPasswordHasher<User> hasher,
            CancellationToken cancellationToken) =>
        {
            // Check if any superadmin already exists
            var superAdminRole = await db.Roles.Find(r => r.Name == "super admin").FirstOrDefaultAsync(cancellationToken);
            if (superAdminRole == null)
            {
                return Results.BadRequest("Super admin role not found. Run seed first.");
            }

            var existingSuperAdmin = await db.UserRoles
                .Find(ur => ur.RoleId == superAdminRole.Id)
                .AnyAsync(cancellationToken);

            if (existingSuperAdmin)
            {
                return Results.Conflict("Super admin already exists. Bootstrap not allowed.");
            }

            // Create or find user
            var email = request.Email.Trim().ToLowerInvariant();
            var user = await db.Users.Find(u => u.Email == email).FirstOrDefaultAsync(cancellationToken);

            if (user == null)
            {
                user = new User
                {
                    Email = email,
                    PasswordHash = "pending",
                    Goal = request.Goal?.Trim(),
                    EmailVerified = true // Bootstrap user is pre-verified
                };
                user.PasswordHash = hasher.HashPassword(user, request.Password);
                await db.Users.InsertOneAsync(user, cancellationToken: cancellationToken);
            }
            else
            {
                // Update password
                user.PasswordHash = hasher.HashPassword(user, request.Password);
                user.EmailVerified = true;
                await db.Users.ReplaceOneAsync(u => u.Id == user.Id, user, cancellationToken: cancellationToken);
            }

            // Assign super admin role
            await db.UserRoles.InsertOneAsync(new UserRole
            {
                UserId = user.Id,
                RoleId = superAdminRole.Id,
                AssignedBy = user.Id // Self-assigned during bootstrap
            }, cancellationToken: cancellationToken);

            return Results.Ok(new { message = "Super admin created successfully", userId = user.Id, email = user.Email });
        })
        .AllowAnonymous() // Only accessible when no superadmin exists
        .WithName("BootstrapSuperAdmin");

        // ============ ROLES ============

        // Get all roles
        rbacGroup.MapGet("/roles", async (
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var roles = await db.Roles.Find(_ => true)
                .SortBy(r => r.Name)
                .ToListAsync(cancellationToken);

            var roleIds = roles.Select(r => r.Id).ToList();
            var rolePermissions = await db.RolePermissions
                .Find(rp => roleIds.Contains(rp.RoleId))
                .ToListAsync(cancellationToken);

            var permIds = rolePermissions.Select(rp => rp.PermissionId).Distinct().ToList();
            var permissions = await db.Permissions
                .Find(p => permIds.Contains(p.Id))
                .ToListAsync(cancellationToken);

            var permDict = permissions.ToDictionary(p => p.Id, p => p.FullName);
            var rolePermDict = rolePermissions.GroupBy(rp => rp.RoleId)
                .ToDictionary(g => g.Key, g => g.Select(rp => permDict[rp.PermissionId]).ToList());

            var response = roles.Select(r => new RoleResponse(
                r.Id, r.Name, r.Description, r.CreatedAt, r.UpdatedAt,
                rolePermDict.GetValueOrDefault(r.Id, [])
            )).ToList();

            return Results.Ok(response);
        })
        .RequireAuthorization("super admin") // Only Admin can list all roles
        .WithName("ListRoles");

        // Get role by ID
        rbacGroup.MapGet("/roles/{roleId:guid}", async (
            Guid roleId,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var role = await db.Roles.Find(r => r.Id == roleId).FirstOrDefaultAsync(cancellationToken);
            if (role == null) return Results.NotFound();

            var rolePerms = await db.RolePermissions
                .Find(rp => rp.RoleId == roleId)
                .ToListAsync(cancellationToken);

            var permIds = rolePerms.Select(rp => rp.PermissionId).ToList();
            var permissions = await db.Permissions
                .Find(p => permIds.Contains(p.Id))
                .ToListAsync(cancellationToken);

            var permNames = permissions.Select(p => p.FullName).ToList();

            return Results.Ok(new RoleResponse(role.Id, role.Name, role.Description, role.CreatedAt, role.UpdatedAt, permNames));
        })
        .RequireAuthorization("super admin")
        .WithName("GetRole");

        // Create role
        rbacGroup.MapPost("/roles", async (
            CreateRoleRequest request,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Role name is required");

            var exists = await db.Roles.Find(r => r.Name == request.Name.Trim()).AnyAsync(cancellationToken);
            if (exists) return Results.Conflict("Role already exists");

            var role = new Role
            {
                Name = request.Name.Trim(),
                Description = request.Description?.Trim()
            };
            await db.Roles.InsertOneAsync(role, cancellationToken: cancellationToken);

            // Assign permissions if provided
            if (request.PermissionIds != null && request.PermissionIds.Count > 0)
            {
                var validPermissions = await db.Permissions
                    .Find(p => request.PermissionIds.Contains(p.Id))
                    .ToListAsync(cancellationToken);

                var rolePermissions = validPermissions.Select(p => new RolePermission
                {
                    RoleId = role.Id,
                    PermissionId = p.Id
                }).ToList();

                if (rolePermissions.Count > 0)
                {
                    await db.RolePermissions.InsertManyAsync(rolePermissions, cancellationToken: cancellationToken);
                }
            }

            return Results.Created($"/api/rbac/roles/{role.Id}", new RoleResponse(role.Id, role.Name, role.Description, role.CreatedAt, role.UpdatedAt, []));
        })
        .RequireAuthorization("super admin")
        .WithName("CreateRole");

        // Update role
        rbacGroup.MapPut("/roles/{roleId:guid}", async (
            Guid roleId,
            UpdateRoleRequest request,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var role = await db.Roles.Find(r => r.Id == roleId).FirstOrDefaultAsync(cancellationToken);
            if (role == null) return Results.NotFound();

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                var exists = await db.Roles.Find(r => r.Name == request.Name.Trim() && r.Id != roleId).AnyAsync(cancellationToken);
                if (exists) return Results.Conflict("Role name already in use");
                role.Name = request.Name.Trim();
            }

            if (request.Description != null)
                role.Description = request.Description.Trim();

            role.UpdatedAt = DateTimeOffset.UtcNow;
            await db.Roles.ReplaceOneAsync(r => r.Id == roleId, role, cancellationToken: cancellationToken);

            return Results.Ok(new RoleResponse(role.Id, role.Name, role.Description, role.CreatedAt, role.UpdatedAt, []));
        })
        .RequireAuthorization("super admin")
        .WithName("UpdateRole");

        // Delete role
        rbacGroup.MapDelete("/roles/{roleId:guid}", async (
            Guid roleId,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var role = await db.Roles.Find(r => r.Id == roleId).FirstOrDefaultAsync(cancellationToken);
            if (role == null) return Results.NotFound();

            // Prevent deletion of system roles
            if (role.Name is "super admin" or "user")
                return Results.BadRequest("Cannot delete system roles");

            // Remove role-permission associations
            await db.RolePermissions.DeleteManyAsync(rp => rp.RoleId == roleId, cancellationToken);

            // Remove user-role associations
            await db.UserRoles.DeleteManyAsync(ur => ur.RoleId == roleId, cancellationToken);

            // Delete role
            await db.Roles.DeleteOneAsync(r => r.Id == roleId, cancellationToken);

            return Results.Ok(new { message = "Role deleted successfully" });
        })
        .RequireAuthorization("super admin")
        .WithName("DeleteRole");

        // Assign permissions to role
        rbacGroup.MapPost("/roles/{roleId:guid}/permissions", async (
            Guid roleId,
            AssignPermissionsRequest request,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var role = await db.Roles.Find(r => r.Id == roleId).FirstOrDefaultAsync(cancellationToken);
            if (role == null) return Results.NotFound();

            var permissions = await db.Permissions
                .Find(p => request.PermissionIds.Contains(p.Id))
                .ToListAsync(cancellationToken);

            var existing = await db.RolePermissions
                .Find(rp => rp.RoleId == roleId && request.PermissionIds.Contains(rp.PermissionId))
                .ToListAsync(cancellationToken);

            var existingPermIds = existing.Select(rp => rp.PermissionId).ToHashSet();
            var newRolePermissions = permissions
                .Where(p => !existingPermIds.Contains(p.Id))
                .Select(p => new RolePermission { RoleId = roleId, PermissionId = p.Id })
                .ToList();

            if (newRolePermissions.Count > 0)
                await db.RolePermissions.InsertManyAsync(newRolePermissions, cancellationToken: cancellationToken);

            return Results.Ok(new { message = "Permissions assigned", addedCount = newRolePermissions.Count });
        })
        .RequireAuthorization("super admin")
        .WithName("AssignPermissionsToRole");

        // Remove permission from role
        rbacGroup.MapDelete("/roles/{roleId:guid}/permissions/{permissionId:guid}", async (
            Guid roleId,
            Guid permissionId,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var result = await db.RolePermissions.DeleteOneAsync(rp => rp.RoleId == roleId && rp.PermissionId == permissionId, cancellationToken);
            if (result.DeletedCount == 0) return Results.NotFound();

            return Results.Ok(new { message = "Permission removed from role" });
        })
        .RequireAuthorization("super admin")
        .WithName("RemovePermissionFromRole");

        // ============ PERMISSIONS ============

        // Get all permissions
        rbacGroup.MapGet("/permissions", async (
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var permissions = await db.Permissions.Find(_ => true)
                .SortBy(p => p.Resource).ThenBy(p => p.Action)
                .ToListAsync(cancellationToken);

            return Results.Ok(permissions.Select(p => new PermissionResponse(p.Id, p.Resource, p.Action, p.Description, p.CreatedAt)).ToList());
        })
        .RequireAuthorization("super admin")
        .WithName("ListPermissions");

        // Create permission
        rbacGroup.MapPost("/permissions", async (
            CreatePermissionRequest request,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Resource) || string.IsNullOrWhiteSpace(request.Action))
                return Results.BadRequest("Resource and Action are required");

            var exists = await db.Permissions
                .Find(p => p.Resource == request.Resource.Trim() && p.Action == request.Action.Trim())
                .AnyAsync(cancellationToken);

            if (exists) return Results.Conflict("Permission already exists");

            var permission = new Permission
            {
                Resource = request.Resource.Trim(),
                Action = request.Action.Trim(),
                Description = request.Description?.Trim()
            };
            await db.Permissions.InsertOneAsync(permission, cancellationToken: cancellationToken);

            return Results.Created($"/api/rbac/permissions/{permission.Id}", new PermissionResponse(permission.Id, permission.Resource, permission.Action, permission.Description, permission.CreatedAt));
        })
        .RequireAuthorization("super admin")
        .WithName("CreatePermission");

        // ============ USER ROLES ============

        // Get user's roles
        rbacGroup.MapGet("/users/{userId:guid}/roles", async (
            Guid userId,
            MongoDbContext db,
            PermissionService permService,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
            if (user == null) return Results.NotFound("User not found");

            var userRoles = await db.UserRoles.Find(ur => ur.UserId == userId).ToListAsync(cancellationToken);
            var roleIds = userRoles.Select(ur => ur.RoleId).ToList();
            var roles = await db.Roles.Find(r => roleIds.Contains(r.Id)).ToListAsync(cancellationToken);

            var permissions = await permService.GetUserPermissionsAsync(userId, cancellationToken);

            return Results.Ok(new UserRolesResponse(
                user.Id, user.Email,
                roles.Select(r => new RoleResponse(r.Id, r.Name, r.Description, r.CreatedAt, r.UpdatedAt, [])).ToList(),
                permissions
            ));
        })
        .RequireAuthorization(policy => policy.RequireRole("super admin", "hr manager"))
        .WithName("GetUserRoles");

        // Assign role to user - super admin can assign any role, hr manager can only assign "user" role
        rbacGroup.MapPost("/users/{userId:guid}/roles", async (
            Guid userId,
            AssignRoleRequest request,
            ClaimsPrincipal currentUser,
            MongoDbContext db,
            PermissionService permService,
            CancellationToken cancellationToken) =>
        {
            var currentUserId = GetUserId(currentUser);

            var user = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
            if (user == null) return Results.NotFound("User not found");

            var role = await db.Roles.Find(r => r.Id == request.RoleId).FirstOrDefaultAsync(cancellationToken);
            if (role == null) return Results.NotFound("Role not found");

            // Authorization check: super admin can assign any role, hr manager can only assign "user" role
            var currentUserRoles = await permService.GetUserRolesAsync(currentUserId, cancellationToken);
            var isSuperAdmin = currentUserRoles.Contains("super admin", StringComparer.OrdinalIgnoreCase);
            var isHrManager = currentUserRoles.Contains("hr manager", StringComparer.OrdinalIgnoreCase);

            if (!isSuperAdmin && !isHrManager)
            {
                return Results.Forbid();
            }

            // HR managers can ONLY assign "user" role
            if (isHrManager && !isSuperAdmin && !role.Name.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("HR managers can only assign user role");
            }

            // Check if already assigned
            var exists = await db.UserRoles.Find(ur => ur.UserId == userId && ur.RoleId == request.RoleId).AnyAsync(cancellationToken);
            if (exists) return Results.Conflict("Role already assigned to user");

            await db.UserRoles.InsertOneAsync(new UserRole
            {
                UserId = userId,
                RoleId = request.RoleId,
                AssignedBy = currentUserId
            }, cancellationToken: cancellationToken);

            return Results.Ok(new { message = "Role assigned successfully" });
        })
        .RequireAuthorization(policy => policy.RequireRole("super admin", "hr manager"))
        .WithName("AssignRoleToUser");

        // Remove role from user - super admin can remove any role, hr manager can only remove "user" role
        rbacGroup.MapDelete("/users/{userId:guid}/roles/{roleId:guid}", async (
            Guid userId,
            Guid roleId,
            ClaimsPrincipal currentUser,
            MongoDbContext db,
            PermissionService permService,
            CancellationToken cancellationToken) =>
        {
            var currentUserId = GetUserId(currentUser);

            var role = await db.Roles.Find(r => r.Id == roleId).FirstOrDefaultAsync(cancellationToken);
            if (role == null) return Results.NotFound("Role not found");

            // Authorization check: super admin can remove any role, hr manager can only remove "user" role
            var currentUserRoles = await permService.GetUserRolesAsync(currentUserId, cancellationToken);
            var isSuperAdmin = currentUserRoles.Contains("super admin", StringComparer.OrdinalIgnoreCase);
            var isHrManager = currentUserRoles.Contains("hr manager", StringComparer.OrdinalIgnoreCase);

            if (!isSuperAdmin && !isHrManager)
            {
                return Results.Forbid();
            }

            // HR managers can ONLY remove "user" role
            if (isHrManager && !isSuperAdmin && !role.Name.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("HR managers can only remove user role");
            }

            // Prevent removing the last super admin from themselves
            if (role.Name.Equals("super admin", StringComparison.OrdinalIgnoreCase) && userId == currentUserId)
            {
                return Results.BadRequest("Cannot remove your own super admin role");
            }

            var result = await db.UserRoles.DeleteOneAsync(ur => ur.UserId == userId && ur.RoleId == roleId, cancellationToken);
            if (result.DeletedCount == 0) return Results.NotFound();

            return Results.Ok(new { message = "Role removed from user" });
        })
        .RequireAuthorization(policy => policy.RequireRole("super admin", "hr manager"))
        .WithName("RemoveRoleFromUser");

        // ============ ADMIN USER MANAGEMENT ============

        // Get all users with roles
        rbacGroup.MapGet("/users", async (
            MongoDbContext db,
            PermissionService permService,
            int page = 1,
            int pageSize = 20,
            string? search = null,
            CancellationToken cancellationToken = default) =>
        {
            var filter = Builders<User>.Filter.Empty;

            if (!string.IsNullOrWhiteSpace(search))
            {
                filter &= Builders<User>.Filter.Regex(u => u.Email, new MongoDB.Bson.BsonRegularExpression(search, "i"));
            }

            var total = await db.Users.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
            var users = await db.Users.Find(filter)
                .SortByDescending(u => u.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Limit(pageSize)
                .ToListAsync(cancellationToken);

            var userIds = users.Select(u => u.Id).ToList();
            var userRoles = await db.UserRoles.Find(ur => userIds.Contains(ur.UserId)).ToListAsync(cancellationToken);
            var roleIds = userRoles.Select(ur => ur.RoleId).Distinct().ToList();
            var roles = await db.Roles.Find(r => roleIds.Contains(r.Id)).ToListAsync(cancellationToken);
            var roleDict = roles.ToDictionary(r => r.Id, r => r.Name);

            var userRoleDict = userRoles.GroupBy(ur => ur.UserId)
                .ToDictionary(g => g.Key, g => g.Select(ur => roleDict.GetValueOrDefault(ur.RoleId, "")).Where(x => !string.IsNullOrEmpty(x)).ToList());

            var response = users.Select(u => new UserAdminResponse(
                u.Id, u.Email, u.Goal, u.Status.ToString(), u.EmailVerified, u.LastLoginAt, u.CreatedAt,
                userRoleDict.GetValueOrDefault(u.Id, [])
            )).ToList();

            return Results.Ok(new PagedResponse<UserAdminResponse>(response, page, pageSize, total));
        })
        .RequireAuthorization("super admin")
        .WithName("ListUsersAdmin");

        // Update user status (suspend/activate)
        rbacGroup.MapPatch("/users/{userId:guid}/status", async (
            Guid userId,
            UpdateUserStatusRequest request,
            MongoDbContext db,
            CancellationToken cancellationToken) =>
        {
            var user = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
            if (user == null) return Results.NotFound();

            user.Status = request.Status;
            user.UpdatedAt = DateTimeOffset.UtcNow;

            // If suspending, revoke all refresh tokens
            if (request.Status == UserStatus.Suspended)
            {
                var tokens = await db.RefreshTokens.Find(rt => rt.UserId == userId && rt.IsActive).ToListAsync(cancellationToken);
                foreach (var token in tokens)
                {
                    token.RevokedAt = DateTimeOffset.UtcNow;
                    await db.RefreshTokens.ReplaceOneAsync(rt => rt.Id == token.Id, token, cancellationToken: cancellationToken);
                }
            }

            await db.Users.ReplaceOneAsync(u => u.Id == userId, user, cancellationToken: cancellationToken);

            return Results.Ok(new { message = $"User status updated to {request.Status}" });
        })
        .RequireAuthorization("super admin")
        .WithName("UpdateUserStatus");
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var subClaim = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (subClaim == null || !Guid.TryParse(subClaim, out var userId))
            throw new UnauthorizedAccessException();

        return userId;
    }
}

// DTOs
public record RoleResponse(Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<string> Permissions);
public record CreateRoleRequest(string Name, string? Description, List<Guid>? PermissionIds);
public record UpdateRoleRequest(string? Name, string? Description);

public record PermissionResponse(Guid Id, string Resource, string Action, string? Description, DateTimeOffset CreatedAt);
public record CreatePermissionRequest(string Resource, string Action, string? Description);

public record AssignPermissionsRequest(List<Guid> PermissionIds);
public record AssignRoleRequest(Guid RoleId);

public record UserRolesResponse(Guid UserId, string Email, List<RoleResponse> Roles, List<string> Permissions);
public record UserAdminResponse(Guid Id, string Email, string? Goal, string Status, bool EmailVerified, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt, List<string> Roles);

public record UpdateUserStatusRequest(UserStatus Status);

public record BootstrapSuperAdminRequest(string Email, string Password, string? Goal);

public record PagedResponse<T>(List<T> Items, int Page, int PageSize, long Total)
{
    public int TotalPages => (int)Math.Ceiling((double)Total / PageSize);
}