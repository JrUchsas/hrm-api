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
    public IMongoCollection<Role> Roles => _database.GetCollection<Role>("roles");
    public IMongoCollection<Permission> Permissions => _database.GetCollection<Permission>("permissions");
    public IMongoCollection<UserRole> UserRoles => _database.GetCollection<UserRole>("user_roles");
    public IMongoCollection<RolePermission> RolePermissions => _database.GetCollection<RolePermission>("role_permissions");
    public IMongoCollection<RefreshToken> RefreshTokens => _database.GetCollection<RefreshToken>("refresh_tokens");
    public IMongoCollection<PasswordResetToken> PasswordResetTokens => _database.GetCollection<PasswordResetToken>("password_reset_tokens");
    public IMongoCollection<Attendance> Attendance => _database.GetCollection<Attendance>("attendance");
    public IMongoCollection<AttendanceSettings> AttendanceSettings => _database.GetCollection<AttendanceSettings>("attendance_settings");
    public IMongoCollection<LeaveBalance> LeaveBalances => _database.GetCollection<LeaveBalance>("leave_balances");
    public IMongoCollection<LeaveRequest> LeaveRequests => _database.GetCollection<LeaveRequest>("leave_requests");
    public IMongoCollection<TaskItem> Tasks => _database.GetCollection<TaskItem>("tasks");

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

        // PasswordResetTokens indexes
        var resetTokenHashIndex = Builders<PasswordResetToken>.IndexKeys.Ascending(prt => prt.TokenHash);
        await PasswordResetTokens.Indexes.CreateOneAsync(new CreateIndexModel<PasswordResetToken>(resetTokenHashIndex, new CreateIndexOptions { Unique = true }));
        var resetTokenUserIndex = Builders<PasswordResetToken>.IndexKeys.Ascending(prt => prt.UserId);
        await PasswordResetTokens.Indexes.CreateOneAsync(new CreateIndexModel<PasswordResetToken>(resetTokenUserIndex));
        var resetTokenExpiryIndex = Builders<PasswordResetToken>.IndexKeys.Ascending(prt => prt.ExpiresAt);
        await PasswordResetTokens.Indexes.CreateOneAsync(new CreateIndexModel<PasswordResetToken>(resetTokenExpiryIndex));

        // Attendance indexes
        var attendanceUserDateIndex = Builders<Attendance>.IndexKeys.Ascending(a => a.UserId).Ascending(a => a.AttendanceDate);
        await Attendance.Indexes.CreateOneAsync(new CreateIndexModel<Attendance>(attendanceUserDateIndex, new CreateIndexOptions { Unique = true }));
        var attendanceDateIndex = Builders<Attendance>.IndexKeys.Ascending(a => a.AttendanceDate);
        await Attendance.Indexes.CreateOneAsync(new CreateIndexModel<Attendance>(attendanceDateIndex));
        var attendanceUserIndex = Builders<Attendance>.IndexKeys.Ascending(a => a.UserId);
        await Attendance.Indexes.CreateOneAsync(new CreateIndexModel<Attendance>(attendanceUserIndex));
        var attendanceStatusIndex = Builders<Attendance>.IndexKeys.Ascending(a => a.Status);
        await Attendance.Indexes.CreateOneAsync(new CreateIndexModel<Attendance>(attendanceStatusIndex));

        // Leave Balance indexes
        var leaveBalanceUserYearIndex = Builders<LeaveBalance>.IndexKeys.Ascending(lb => lb.UserId).Ascending(lb => lb.Year);
        await LeaveBalances.Indexes.CreateOneAsync(new CreateIndexModel<LeaveBalance>(leaveBalanceUserYearIndex, new CreateIndexOptions { Unique = true }));
        var leaveBalanceUserIndex = Builders<LeaveBalance>.IndexKeys.Ascending(lb => lb.UserId);
        await LeaveBalances.Indexes.CreateOneAsync(new CreateIndexModel<LeaveBalance>(leaveBalanceUserIndex));

        // Leave Request indexes
        var leaveRequestUserIndex = Builders<LeaveRequest>.IndexKeys.Ascending(lr => lr.UserId);
        await LeaveRequests.Indexes.CreateOneAsync(new CreateIndexModel<LeaveRequest>(leaveRequestUserIndex));
        var leaveRequestStatusIndex = Builders<LeaveRequest>.IndexKeys.Ascending(lr => lr.Status);
        await LeaveRequests.Indexes.CreateOneAsync(new CreateIndexModel<LeaveRequest>(leaveRequestStatusIndex));
        var leaveRequestUserStatusIndex = Builders<LeaveRequest>.IndexKeys.Ascending(lr => lr.UserId).Ascending(lr => lr.Status);
        await LeaveRequests.Indexes.CreateOneAsync(new CreateIndexModel<LeaveRequest>(leaveRequestUserStatusIndex));
        var leaveRequestDateRangeIndex = Builders<LeaveRequest>.IndexKeys.Ascending(lr => lr.StartDate).Ascending(lr => lr.EndDate);
        await LeaveRequests.Indexes.CreateOneAsync(new CreateIndexModel<LeaveRequest>(leaveRequestDateRangeIndex));

        // Task indexes
        var taskUserIndex = Builders<TaskItem>.IndexKeys.Ascending(t => t.UserId);
        await Tasks.Indexes.CreateOneAsync(new CreateIndexModel<TaskItem>(taskUserIndex));
        var taskUserStatusIndex = Builders<TaskItem>.IndexKeys.Ascending(t => t.UserId).Ascending(t => t.Status);
        await Tasks.Indexes.CreateOneAsync(new CreateIndexModel<TaskItem>(taskUserStatusIndex));
        var taskUserPriorityIndex = Builders<TaskItem>.IndexKeys.Ascending(t => t.UserId).Ascending(t => t.Priority);
        await Tasks.Indexes.CreateOneAsync(new CreateIndexModel<TaskItem>(taskUserPriorityIndex));
        var taskUserCategoryIndex = Builders<TaskItem>.IndexKeys.Ascending(t => t.UserId).Ascending(t => t.Category);
        await Tasks.Indexes.CreateOneAsync(new CreateIndexModel<TaskItem>(taskUserCategoryIndex));
        var taskUserCreatedIndex = Builders<TaskItem>.IndexKeys.Ascending(t => t.UserId).Descending(t => t.CreatedAtUtc);
        await Tasks.Indexes.CreateOneAsync(new CreateIndexModel<TaskItem>(taskUserCreatedIndex));
        var taskDueDateIndex = Builders<TaskItem>.IndexKeys.Ascending(t => t.DueDate);
        await Tasks.Indexes.CreateOneAsync(new CreateIndexModel<TaskItem>(taskDueDateIndex));
    }
}