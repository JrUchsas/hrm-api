using HrmApi.Data;
using HrmApi.Models;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;

namespace HrmApi.Services;

public sealed class RbacSeedService(MongoDbContext db, IPasswordHasher<User> passwordHasher)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedPermissionsAsync(cancellationToken);
        await SeedRolesAsync(cancellationToken);
        await SeedRolePermissionsAsync(cancellationToken);
        await SeedDefaultUsersAsync(cancellationToken);
    }

    private async Task SeedPermissionsAsync(CancellationToken cancellationToken)
    {
        var permissions = new List<Permission>
        {
            // User management
            new() { Resource = "users", Action = "read", Description = "Read user information" },
            new() { Resource = "users", Action = "create", Description = "Create new users" },
            new() { Resource = "users", Action = "update", Description = "Update user information" },
            new() { Resource = "users", Action = "delete", Description = "Delete users" },
            new() { Resource = "users", Action = "manage_roles", Description = "Assign/remove roles from users" },

            // Role management
            new() { Resource = "roles", Action = "read", Description = "Read role information" },
            new() { Resource = "roles", Action = "create", Description = "Create new roles" },
            new() { Resource = "roles", Action = "update", Description = "Update role information" },
            new() { Resource = "roles", Action = "delete", Description = "Delete roles" },
            new() { Resource = "roles", Action = "manage_permissions", Description = "Assign/remove permissions from roles" },

            // Permission management
            new() { Resource = "permissions", Action = "read", Description = "Read permission information" },

            // Profile
            new() { Resource = "profile", Action = "read", Description = "Read own profile" },
            new() { Resource = "profile", Action = "update", Description = "Update own profile" },

            // Admin/System
            new() { Resource = "system", Action = "health", Description = "Access health endpoints" },
            new() { Resource = "system", Action = "config", Description = "Access configuration endpoints" },
            new() { Resource = "audit", Action = "read", Description = "Read audit logs" },
        };

        foreach (var permission in permissions)
        {
            var exists = await db.Permissions
                .Find(p => p.Resource == permission.Resource && p.Action == permission.Action)
                .AnyAsync(cancellationToken);

            if (!exists)
            {
                await db.Permissions.InsertOneAsync(permission, cancellationToken: cancellationToken);
            }
        }
    }

    private async Task SeedRolesAsync(CancellationToken cancellationToken)
    {
        var roles = new List<Role>
        {
            new() { Name = "super admin", Description = "Full system access - can manage users, roles, and permissions" },
            new() { Name = "hr manager", Description = "Manage users and view reports" },
            new() { Name = "user", Description = "Basic user with access to own data only" },
        };

        foreach (var role in roles)
        {
            var exists = await db.Roles
                .Find(r => r.Name == role.Name)
                .AnyAsync(cancellationToken);

            if (!exists)
            {
                await db.Roles.InsertOneAsync(role, cancellationToken: cancellationToken);
            }
        }
    }

    private async Task SeedRolePermissionsAsync(CancellationToken cancellationToken)
    {
        var roles = await db.Roles.Find(_ => true).ToListAsync(cancellationToken);
        var permissions = await db.Permissions.Find(_ => true).ToListAsync(cancellationToken);

        var permDict = permissions.ToDictionary(p => p.FullName, p => p.Id);
        var roleDict = roles.ToDictionary(r => r.Name, r => r.Id);

        var rolePermissions = new Dictionary<string, List<string>>
        {
            ["super admin"] = permissions.Select(p => p.FullName).ToList(),
            ["hr manager"] = new()
            {
                "users.read",
                "users.update",
                "roles.read",
                "permissions.read",
                "profile.read",
                "profile.update",
                "system.health",
            },
            ["user"] = new()
            {
                "profile.read",
                "profile.update",
            },
        };

        foreach (var (roleName, permNames) in rolePermissions)
        {
            if (!roleDict.TryGetValue(roleName, out var roleId))
                continue;

            foreach (var permName in permNames)
            {
                if (!permDict.TryGetValue(permName, out var permissionId))
                    continue;

                var exists = await db.RolePermissions
                    .Find(rp => rp.RoleId == roleId && rp.PermissionId == permissionId)
                    .AnyAsync(cancellationToken);

                if (!exists)
                {
                    await db.RolePermissions.InsertOneAsync(new RolePermission
                    {
                        RoleId = roleId,
                        PermissionId = permissionId
                    }, cancellationToken: cancellationToken);
                }
            }
        }
    }

    private async Task SeedDefaultUsersAsync(CancellationToken cancellationToken)
    {
        var defaultUsers = new (Guid Id, string Email, string Password, string RoleName, string Goal)[]
        {
            (
                Guid.Parse("a34f2cde-fda4-4451-a66d-b6e281d630fa"),
                "admin@company.com",
                "AdminPass123!",
                "super admin",
                "System Administrator"
            ),
            (
                Guid.Parse("2237bc48-8948-4d7d-b22d-48e3635707ec"),
                "hrmanager@company.com",
                "HrPass123!",
                "hr manager",
                "Human Resources Manager"
            ),
            (
                Guid.Parse("6ad3da0e-3b91-4657-85d0-258a29f25df3"),
                "user1@company.com",
                "UserPass123!",
                "user",
                "Employee User 1"
            ),
            (
                Guid.Parse("37a4d393-814d-495e-9930-772d704660af"),
                "user2@company.com",
                "UserPass123!",
                "user",
                "Employee User 2"
            ),
            (
                Guid.Parse("813407fd-da7a-4fdb-8aeb-097744f994ad"),
                "user3@company.com",
                "UserPass123!",
                "user",
                "Employee User 3"
            )
        };

        var roles = await db.Roles.Find(_ => true).ToListAsync(cancellationToken);
        var roleDict = roles.ToDictionary(r => r.Name, r => r.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var (id, email, password, roleName, goal) in defaultUsers)
        {
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var existingUser = await db.Users
                .Find(u => u.Email == normalizedEmail || u.Id == id)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingUser == null)
            {
                var user = new User
                {
                    Id = id,
                    Email = normalizedEmail,
                    PasswordHash = "pending",
                    Goal = goal,
                    Status = UserStatus.Active,
                    EmailVerified = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                user.PasswordHash = passwordHasher.HashPassword(user, password);
                await db.Users.InsertOneAsync(user, cancellationToken: cancellationToken);
                existingUser = user;
            }
            else
            {
                // Repair status/email-verified if they drifted, but DO NOT re-hash password
                // (password resets must persist across restarts)
                bool needsUpdate = false;
                if (existingUser.Status != UserStatus.Active)
                {
                    existingUser.Status = UserStatus.Active;
                    needsUpdate = true;
                }
                if (!existingUser.EmailVerified)
                {
                    existingUser.EmailVerified = true;
                    needsUpdate = true;
                }
                if (needsUpdate)
                {
                    existingUser.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.Users.ReplaceOneAsync(u => u.Id == existingUser.Id, existingUser, cancellationToken: cancellationToken);
                }
            }

            if (roleDict.TryGetValue(roleName, out var roleId))
            {
                var hasRole = await db.UserRoles
                    .Find(ur => ur.UserId == existingUser.Id && ur.RoleId == roleId)
                    .AnyAsync(cancellationToken);

                if (!hasRole)
                {
                    await db.UserRoles.InsertOneAsync(new UserRole
                    {
                        UserId = existingUser.Id,
                        RoleId = roleId,
                        AssignedBy = existingUser.Id
                    }, cancellationToken: cancellationToken);
                }
            }
        }
    }
}
