# HRM-Api - HR & Performance Management API

A .NET 10 Minimal API for HR Management with MongoDB, JWT authentication, role-based access control (RBAC), leave management, task assignment, and attendance tracking.

---

## 🏗️ Architecture & Technology Stack

- **Framework**: .NET 10 Web API (Minimal APIs)
- **Database**: MongoDB (`MongoDB.Driver` v2.30)
- **Authentication**: JWT Bearer Authentication with refresh token rotation
- **Authorization**: Dynamic RBAC with permission-based `AuthorizationHandler`
- **Health Checks**: Custom health checks (liveness, readiness, configuration)

---

## 📂 Project Directory Structure

```text
D:\All Codes\Projects\HRM-Api\
├── Program.cs
├── appsettings.json
├── .gitignore
├── Data/
│   └── MongoDbContext.cs
├── Endpoints/
│   ├── AuthEndpoints.cs
│   ├── LeaveEndpoints.cs
│   ├── ReportsEndpoints.cs
│   ├── RbacEndpoints.cs
│   ├── TaskEndpoints.cs
│   └── AttendanceEndpoints.cs
├── Models/
│   ├── User.cs
│   ├── LeaveModels.cs
│   ├── TaskModels.cs
│   ├── AttendanceModels.cs
│   └── FileStorageModels.cs
├── Services/
│   ├── AuthService.cs
│   ├── LeaveService.cs
│   ├── TaskService.cs
│   ├── AttendanceService.cs
│   ├── PermissionService.cs
│   ├── RbacSeedService.cs
│   ├── PasswordResetTokenService.cs
│   └── LocalFileStorageService.cs
├── Tests/
│   ├── AuthEndpointsTests.cs
│   ├── LeaveEndpointsTests.cs
│   ├── AttendanceEndpointsTests.cs
│   ├── RbacEndpointsTests.cs
│   ├── TaskEndpointsTests.cs
│   └── HealthEndpointsTests.cs
├── Memory/
│   └── MEMORY.md (project state index)
└── .claude/
```

---

## 🔐 Authentication & Authorization

### JWT Configuration
- `Auth:JwtKey` (32+ chars) or `JWT_SECRET` in `appsettings.json` / environment
- Token lifetime: 14 days + configurable refresh token expiry
- Claims: `sub` (user Id), `role`, `permissions`

### RBAC (Role-Based Access Control)
- **Dynamic permissions** read from MongoDB per-request (via `PermissionService`)
- **PermissionAuthorizationHandler**: grants access when user has required `Permission:resource_action`
- **Roles seeded by RbacSeedService**: super admin, hr manager, project manager, user
- **Permission matrix** defined in `RbacSeedService.SeedPermissionsAsync()`

### CORS
- **Development**: Allows any origin (`AllowAnyOrigin()`)
- **Production**: Configured via `Cors:AllowedOrigins` in `appsettings.json`

---

## 📡 API Endpoints

### Authentication (`/api/auth`)
- `POST /register` — Register new user
- `POST /login` — Authenticate, return access + refresh tokens
- `POST /refresh` — Rotate refresh token (atomic FindOneAndUpdate)
- `POST /logout` — Revoke current refresh token
- `POST /logout-all` — Revoke all user refresh tokens
- `GET /me` `[Auth]` — Get current user profile with roles/permissions
- `POST /forgot-password` — Request password reset link
- `POST /reset-password` — Reset password with token
- `POST /change-password` `[Auth]` — Change password (authenticated)

### Leave Management (`/api/leaves`)
- `GET /balance` `[Auth]` — Get current user's leave balance
- `GET /my-history` `[Auth]` — Get user's leave application history
- `POST /apply` `[Auth]` — Submit new leave application
  - Validates: end >= start, no overlap with pending/approved leaves, sufficient balance, **mandatory reason (max 500 chars)**
  - Optional: `attachmentUrl`, `attachmentName` from prior upload
- `DELETE /{id}/cancel` `[Auth]` — Cancel own pending leave request (deletes attachment if present)
- `POST /upload` `[Auth]` — Upload PDF attachment (max 5MB, PDF only)
  - Returns: `{ "url": "/api/leaves/files/{guid}.pdf", "fileName": "...", "size": 1234, "contentType": "application/pdf" }`
- `GET /files/{fileName}` `[Auth]` — Download uploaded PDF (requires authentication)
- **Admin** `GET /admin/pending` `[HrOrAdmin]` — List all pending requests (with optional status filter)
- **Admin** `POST /admin/{id}/review` `[HrOrAdmin]` — Approve/reject leave request (reject requires reason, max 500 chars; deletes attachment on reject)
- **Admin** `POST /admin/balance/adjust` `[SuperAdmin]` — Adjust user's leave balance

### Task Management (`/api/tasks`)
- `GET /` `[Auth]` — Get user's assigned tasks (with filters)
- `GET /summary` `[Auth]` — Get task summary counts
- `GET /categories` `[Auth]` — Get available task categories
- `GET /{id}` `[Auth]` — Get specific assigned task
- `POST /{id}/advance` `[Auth]` — Employee: advance status (Unopened → Ongoing → Completed)
- `GET /assigned` `[Permission:tasks.view_assigned]` `[PM]` — Get tasks assigned BY PM
- `POST /assigned` `[Permission:tasks.assign]` `[PM]` — Create and assign task to user
- `PUT /assigned/{id}` `[Permission:tasks.assign]` `[PM]` — Reassign task, update status
- `POST /assigned/{id}/advance` `[Permission:tasks.assign]` `[PM]` — PM can cycle status back to Unopened

### Attendance (`/api/attendance`)
- `POST /check-in` `[Auth]` — Check in for the day
- `POST /check-out` `[Auth]` — Check out for the day
- `GET /my` `[Auth]` — Get personal attendance records
- `GET /my/today` `[Auth]` — Get today's attendance
- `GET /admin/daily?date=YYYY-MM-DD` `[SuperAdmin|HrManager]` — Admin daily report
- `GET /admin/settings` `[SuperAdmin]` — Get/Update attendance settings
- `POST /admin/leave` `[SuperAdmin]` — Mark user as on leave for a date

### RBAC (`/api/rbac`)
- `GET /users` `[SuperAdmin]` — List all users with roles
- `GET /roles` `[SuperAdmin]` — List all roles with permissions
- `GET /permissions` `[SuperAdmin]` — List all permissions
- `POST /users/{id}/roles` `[SuperAdmin|HrManager]` — Assign role to user
- `DELETE /users/{id}/roles/{roleId}` `[SuperAdmin|HrManager]` — Remove role from user
- `GET /users/{id}/roles` `[Auth]` — Get user's roles and permissions

---

## ⚙️ Configuration (`appsettings.json`)

```json
{
  "MongoDB": {
    "Database": "HRM-Api"
  },
  "Auth": {
    "JwtKey": "your-super-secret-jwt-key-at-least-32-characters-long",
    "Issuer": "HRM-Api",
    "Audience": "HRM-Api",
    "RefreshTokenExpiryDays": 30
  },
  "Cors": {
    "AllowedOrigins": [ "https://your-frontend.com" ]
  },
  "Attendance": {
    "BusinessTimezone": "Asia/Dhaka",
    "WorkStartTime": "09:00",
    "WorkEndTime": "18:00",
    "LateCutoffMinutes": 15,
    "MinimumWorkingMinutes": 480
  }
}
```

### Environment Variables (`.env` file recommended)
```bash
MONGODB_URI=mongodb://localhost:27017
MONGODB_DB=HRM-Api
AUTH_JWT_KEY=your-super-secret-jwt-key-at-least-32-characters-long
AUTH_ISSUER=HRM-Api
AUTH_AUDIENCE=HRM-Api
CORS_ALLOWED_ORIGINS=https://your-frontend.com
```

### MongoDB Atlas Connection String
```json
{
  "MongoDB": {
    "Database": "HRM-Api"
  }
}
```
Or via `MONGODB_URI` environment variable: `mongodb+srv://user:pass@cluster.mongodb.net/`

### File Storage Configuration (`appsettings.json`)
```json
{
  "FileStorage": {
    "Provider": "Local",
    "Local": {
      "UploadPath": "uploads/leaves",
      "BaseUrl": "/api/leaves/files"
    }
  }
}
```
- **Provider**: `"Local"` (default), `"AzureBlob"`, or `"S3"` (future)
- **UploadPath**: Relative path from app directory for local storage
- **BaseUrl**: Base URL path for generating download links

---

## 🗄️ MongoDB Indexes (Created on Startup)

| Collection | Index | Filter |
|---|---|---|
| `users` | Unique + Sparse on `email` | Allows migration without email field |
| `roles` | Unique on `name` | — |
| `permissions` | Unique on `resource+action` | — |
| `user_roles` | Unique on `userId+roleId` | — |
| `role_permissions` | Unique on `roleId+permissionId` | — |
| `refresh_tokens` | Unique on `token` | — |
| `refresh_tokens` | Index on `userId` | — |
| `refresh_tokens` | Index on `expiresAt` | — |
| `password_reset_tokens` | Unique on `tokenHash` | — |
| `password_reset_tokens` | Index on `userId` | — |
| `password_reset_tokens` | Index on `expiresAt` | — |
| `attendance` | Unique on `userId+attendanceDate` | — |
| `attendance` | Index on `attendanceDate` | — |
| `attendance` | Index on `userId` | — |
| `attendance` | Index on `status` | — |
| `leave_balances` | Unique on `userId+year` | — |
| `leave_balances` | Index on `userId` | — |
| `leave_requests` | Index on `userId` | — |
| `leave_requests` | Index on `status` | — |
| `leave_requests` | Index on `userId+status` | — |
| `leave_requests` | **Partial Unique on `userId+startDate+endDate`** (Pending/Approved) | Prevents overlapping leaves |
| `tasks` | Index on `userId` | — |
| `tasks` | Index on `userId+status` | — |
| `tasks` | Index on `userId+priority` | — |
| `tasks` | Index on `userId+category` | — |
| `tasks` | Index on `userId-desc(createdAtUtc)` | — |
| `tasks` | Index on `dueDate` | — |
| `tasks` | Index on `assignedByUserId` | — |
| `tasks` | Index on `assignedByUserId+status` | — |
| `tasks` | **Compound on `assignedByUserId+dueDate`** | PM "due soon" queries |

---

## 🔄 Refresh Token Rotation (Security Feature)

The `/api/auth/refresh` endpoint uses **atomic FindOneAndUpdate** to prevent concurrent refresh issuing:

1. Find refresh token by value where `RevokedAt == null`
2. Atomically set `RevokedAt = now` and `ReplacedByToken = new token`
3. Issue new access token + new refresh token
4. Old refresh token becomes invalid immediately

This prevents an attacker from issuing multiple valid refresh tokens simultaneously.

---

## 🧪 Testing

All integration tests use `[Collection("TestName")]` to prevent parallel DB conflicts and rely on pre-seeded users from `RbacSeedService`.

### Running Tests
```bash
dotnet test Tests/Tests.csproj --no-build
# Or specific test suites:
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TaskEndpointsTests"
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AuthEndpointsTests"
```

### Test Coverage
| Test File | Tests | Status |
|-----------|-------|--------|
| TaskEndpointsTests.cs | 11 | All passing |
| HealthEndpointsTests.cs | 7 | All passing |
| AuthEndpointsTests.cs | 16 | All passing |
| LeaveEndpointsTests.cs | 30 | All passing |
| AttendanceEndpointsTests.cs | 12 | All passing |
| RbacEndpointsTests.cs | 11 | 9 passing |

---

## 🐳 Docker

```bash
# Build
docker build -t hrm-api .

# Run with local MongoDB
docker run -d --name mongo mongo:8
docker run -d -p 8080:8080 \
  -e ConnectionStrings__MongoDB=mongodb://host.docker.internal:27017 \
  -e MongoDB__Database=HRM-Api \
  -e Auth__JwtKey=your-32-plus-char-key \
  hrm-api

# Or with Docker Compose (create docker-compose.yml)
```

---

## 📋 Development Notes

### Adding New Endpoints
1. Create endpoint class in `Endpoints/` following the `Map*Endpoints` pattern
2. Register in `Program.cs` via `app.Map*Endpoints()`
3. Add test in `Tests/` with `[Collection("TestName")]`
4. Add MongoDB index if needed in `MongoDbContext.CreateIndexesAsync()`

### Adding New Permissions
1. Add to `RbacSeedService.SeedPermissionsAsync()`
2. Add policy to `PermissionAuthorizationHandler`
3. Add `RequirePermission:` or `RequireRole:` in endpoint configs
4. Update test assertions for new permission names

### Database Migrations
- Indexes are created idempotently on `Program.cs` startup via `CreateIndexesAsync()`
- Partial unique indexes enforce business rules (e.g., no overlapping leaves)
- All seeds are idempotent (checks for existing data before inserting)

---

## ❌ Error Response Format

| Status | Example |
|--------|---------|
| `400 Bad Request` | `{ "message": "Invalid date range..." }` |
| `401 Unauthorized` | Empty body / `WWW-Authenticate: Bearer` |
| `403 Forbidden` | `{ "message": "Insufficient permissions" }` |
| `404 Not Found` | Empty body |
| `409 Conflict` | `{ "message": "Email is already in use." }` |
| `429 Too Many Requests` | Rate limit exceeded (100/min per IP) |
| `500 Internal Server` | `{ "message": "Error details..." }` |

---

## 📄 License

Internal business project — see CLAUDE.md for attribution terms.