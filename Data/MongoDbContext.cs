using HrmApi.Models;
using MongoDB.Driver;

namespace HrmApi.Data;

public sealed class MongoDbContext
{
    private readonly IMongoDatabase _database;

    public MongoDbContext(IMongoClient client, IConfiguration configuration)
    {
        var databaseName = configuration["MongoDB:Database"] ?? "hrmdb";
        _database = client.GetDatabase(databaseName);
    }

    public IMongoCollection<User> Users => _database.GetCollection<User>("users");
    public IMongoCollection<Habit> Habits => _database.GetCollection<Habit>("habits");
    public IMongoCollection<HabitCheckIn> CheckIns => _database.GetCollection<HabitCheckIn>("checkins");
    public IMongoCollection<Role> Roles => _database.GetCollection<Role>("roles");
    public IMongoCollection<Permission> Permissions => _database.GetCollection<Permission>("permissions");
    public IMongoCollection<UserRole> UserRoles => _database.GetCollection<UserRole>("user_roles");
    public IMongoCollection<RolePermission> RolePermissions => _database.GetCollection<RolePermission>("role_permissions");
    public IMongoCollection<RefreshToken> RefreshTokens => _database.GetCollection<RefreshToken>("refresh_tokens");

    public async Task CreateIndexesAsync()
    {
        // Users indexes - use sparse index to allow documents without email field during migration
        var userIndexKeys = Builders<User>.IndexKeys.Ascending(u => u.Email);
        var sparseOptions = new CreateIndexOptions { Unique = true, Sparse = true };
        await Users.Indexes.CreateOneAsync(new CreateIndexModel<User>(userIndexKeys, sparseOptions));

        // Roles indexes
        var roleNameIndex = Builders<Role>.IndexKeys.Ascending(r => r.Name);
        await Roles.Indexes.CreateOneAsync(new CreateIndexModel<Role>(roleNameIndex, new CreateIndexOptions { Unique = true }));

        // Permissions indexes
        var permResourceActionIndex = Builders<Permission>.IndexKeys.Ascending(p => p.Resource).Ascending(p => p.Action);
        await Permissions.Indexes.CreateOneAsync(new CreateIndexModel<Permission>(permResourceActionIndex, new CreateIndexOptions { Unique = true }));

        // UserRoles indexes
        var userRoleUserIndex = Builders<UserRole>.IndexKeys.Ascending(ur => ur.UserId);
        await UserRoles.Indexes.CreateOneAsync(new CreateIndexModel<UserRole>(userRoleUserIndex));
        var userRoleRoleIndex = Builders<UserRole>.IndexKeys.Ascending(ur => ur.RoleId);
        await UserRoles.Indexes.CreateOneAsync(new CreateIndexModel<UserRole>(userRoleRoleIndex));
        var userRoleCompositeIndex = Builders<UserRole>.IndexKeys.Ascending(ur => ur.UserId).Ascending(ur => ur.RoleId);
        await UserRoles.Indexes.CreateOneAsync(new CreateIndexModel<UserRole>(userRoleCompositeIndex, new CreateIndexOptions { Unique = true }));

        // RolePermissions indexes
        var rolePermRoleIndex = Builders<RolePermission>.IndexKeys.Ascending(rp => rp.RoleId);
        await RolePermissions.Indexes.CreateOneAsync(new CreateIndexModel<RolePermission>(rolePermRoleIndex));
        var rolePermPermIndex = Builders<RolePermission>.IndexKeys.Ascending(rp => rp.PermissionId);
        await RolePermissions.Indexes.CreateOneAsync(new CreateIndexModel<RolePermission>(rolePermPermIndex));
        var rolePermCompositeIndex = Builders<RolePermission>.IndexKeys.Ascending(rp => rp.RoleId).Ascending(rp => rp.PermissionId);
        await RolePermissions.Indexes.CreateOneAsync(new CreateIndexModel<RolePermission>(rolePermCompositeIndex, new CreateIndexOptions { Unique = true }));

        // RefreshTokens indexes
        var refreshTokenIndex = Builders<RefreshToken>.IndexKeys.Ascending(rt => rt.Token);
        await RefreshTokens.Indexes.CreateOneAsync(new CreateIndexModel<RefreshToken>(refreshTokenIndex, new CreateIndexOptions { Unique = true }));
        var refreshTokenUserIndex = Builders<RefreshToken>.IndexKeys.Ascending(rt => rt.UserId);
        await RefreshTokens.Indexes.CreateOneAsync(new CreateIndexModel<RefreshToken>(refreshTokenUserIndex));
        var refreshTokenExpiryIndex = Builders<RefreshToken>.IndexKeys.Ascending(rt => rt.ExpiresAt);
        await RefreshTokens.Indexes.CreateOneAsync(new CreateIndexModel<RefreshToken>(refreshTokenExpiryIndex));

        // Habits indexes
        var habitUserIndexKeys = Builders<Habit>.IndexKeys.Ascending(h => h.UserId).Ascending(h => h.IsActive).Ascending(h => h.CreatedAt);
        await Habits.Indexes.CreateOneAsync(new CreateIndexModel<Habit>(habitUserIndexKeys));

        // CheckIns indexes
        var checkInIndexKeys = Builders<HabitCheckIn>.IndexKeys.Ascending(c => c.HabitId).Ascending(c => c.Date);
        await CheckIns.Indexes.CreateOneAsync(new CreateIndexModel<HabitCheckIn>(checkInIndexKeys, new CreateIndexOptions { Unique = true }));
    }
}