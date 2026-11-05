using HrmApi.Data;
using HrmApi.Models;
using MongoDB.Driver;

namespace HrmApi.Services;

public sealed class PermissionService(MongoDbContext db)
{
    public async Task<List<string>> GetUserPermissionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var userRoles = await db.UserRoles
            .Find(ur => ur.UserId == userId)
            .ToListAsync(cancellationToken);

        if (userRoles.Count == 0)
            return [];

        var roleIds = userRoles.Select(ur => ur.RoleId).ToList();

        var rolePermissions = await db.RolePermissions
            .Find(rp => roleIds.Contains(rp.RoleId))
            .ToListAsync(cancellationToken);

        if (rolePermissions.Count == 0)
            return [];

        var permissionIds = rolePermissions.Select(rp => rp.PermissionId).Distinct().ToList();

        var permissions = await db.Permissions
            .Find(p => permissionIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        return permissions.Select(p => p.FullName).ToList();
    }

    public async Task<List<string>> GetUserRolesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var userRoles = await db.UserRoles
            .Find(ur => ur.UserId == userId)
            .ToListAsync(cancellationToken);

        if (userRoles.Count == 0)
            return [];

        var roleIds = userRoles.Select(ur => ur.RoleId).ToList();

        var roles = await db.Roles
            .Find(r => roleIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        return roles.Select(r => r.Name).ToList();
    }

    public async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken = default)
    {
        var permissions = await GetUserPermissionsAsync(userId, cancellationToken);
        return permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> HasRoleAsync(Guid userId, string role, CancellationToken cancellationToken = default)
    {
        var roles = await GetUserRolesAsync(userId, cancellationToken);
        return roles.Contains(role, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> HasAnyPermissionAsync(Guid userId, IEnumerable<string> permissions, CancellationToken cancellationToken = default)
    {
        var userPermissions = await GetUserPermissionsAsync(userId, cancellationToken);
        return permissions.Any(p => userPermissions.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    public async Task<bool> HasAllPermissionsAsync(Guid userId, IEnumerable<string> permissions, CancellationToken cancellationToken = default)
    {
        var userPermissions = await GetUserPermissionsAsync(userId, cancellationToken);
        return permissions.All(p => userPermissions.Contains(p, StringComparer.OrdinalIgnoreCase));
    }
}