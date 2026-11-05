using HrmApi.Data;
using HrmApi.Models;
using MongoDB.Driver;

namespace HrmApi.Services;

public sealed class RbacSeedService(MongoDbContext db)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedPermissionsAsync(cancellationToken);
        await SeedRolesAsync(cancellationToken);
        await SeedRolePermissionsAsync(cancellationToken);
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
}