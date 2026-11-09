# HRM-Api - Authentication, RBAC & Attendance Management API

A high-performance, secure .NET 10 Minimal API microservice providing enterprise **Authentication, Role-Based Access Control (RBAC), Password Reset, Daily Attendance Management, Leave Management (Sick & Casual Leave), and Task Management** capabilities backed by **MongoDB**.

---

## 🏗️ Architecture & Technology Stack

- **Framework**: .NET 10 Web API (Minimal APIs)
- **Database**: MongoDB (`MongoDB.Driver` v2.30.0)
- **Authentication**: JWT Bearer Authentication (`Microsoft.AspNetCore.Authentication.JwtBearer`)
- **Password Hashing**: `Microsoft.AspNetCore.Identity.PasswordHasher<User>` (PBKDF2 with HMAC-SHA256/512)
- **Authorization**: Custom Dynamic RBAC Handler (`PermissionAuthorizationHandler`, `PermissionPolicyProvider`)
- **Email Delivery**: Abstraction layer supporting **Mailtrap** (SMTP for development) and **Mailgun** (REST API for production)
- **Rate Limiting**: Built-in fixed-window rate limiting on sensitive authentication routes (`Microsoft.AspNetCore.RateLimiting`)
- **Health Checks**: Enterprise diagnostics with MongoDB connectivity, collection checks, index verification, and JWT validation

---

## 📂 Project Directory Structure

```text
d:\All Codes\Projects\
├── HRM-Api\                              # Main Web API Microservice
│   ├── hrm-api.csproj                     # Web SDK project file (net10.0)
│   ├── Program.cs                        # Application bootstrap, DI, middleware & seeding
│   ├── Dockerfile                        # Multi-stage container build
│   ├── docker-entrypoint.sh              # Container startup script
│   ├── appsettings.json                  # Primary configuration
│   ├── appsettings.Development.json      # Development logging configuration
│   ├── appsettings.Production.json       # Production overrides (e.g. Kestrel limits)
│   ├── .env.example                      # Environment variables reference template
│   ├── README.md                         # Project documentation
│   ├── Data\
│   │   └── MongoDbContext.cs             # Collection accessors & compound index builders
│   ├── Models\
│   │   ├── User.cs                       # User identity model & UserStatus enum
│   │   ├── Role.cs                       # Role definition model
│   │   ├── Permission.cs                 # Permission definition model (Resource.Action)
│   │   ├── UserRole.cs                   # User-to-Role junction model
│   │   ├── RolePermission.cs             # Role-to-Permission junction model
│   │   ├── RefreshToken.cs               # Refresh token tracking & rotation state
│   │   ├── PasswordResetToken.cs         # SHA-256 hashed password reset token model
│   │   ├── Attendance.cs                 # Attendance, AttendanceSettings, Holiday, AttendanceStatus enum & all DTOs
│   │   ├── LeaveModels.cs                # LeaveType, LeaveStatus, LeaveBalance, LeaveRequest, DTOs
│   │   └── TaskModels.cs                 # TaskState, TaskPriority, TaskItem, DTOs
│   ├── Services\
│   │   ├── AuthService.cs                # Access/refresh token creation & validation
│   │   ├── AuthEndpoints.cs              # Authentication endpoint route definitions
│   │   ├── AuthorizationMiddleware.cs    # Dynamic RBAC policy provider & requirements
│   │   ├── PermissionService.cs          # Permission & role evaluation logic
│   │   ├── RbacEndpoints.cs              # RBAC administration endpoint routes
│   │   ├── RbacSeedService.cs            # Built-in permissions & roles seed loader
│   │   ├── PasswordResetTokenService.cs  # Cryptographic token generation & validation
│   │   ├── IEmailService.cs              # Email provider interface & EmailResult record
│   │   ├── MailtrapEmailService.cs       # SMTP email service implementation
│   │   ├── MailgunEmailService.cs        # Mailgun HTTP API service implementation
│   │   ├── AttendanceService.cs          # Core attendance business logic (check-in/out, reports, settings)
│   │   ├── AttendanceEndpoints.cs        # Attendance API endpoint route definitions
│   │   ├── LeaveService.cs               # Leave balance, application, approval logic
│   │   ├── LeaveEndpoints.cs             # Leave Management API endpoint route definitions
│   │   ├── TaskService.cs                # Task business logic (CRUD, filtering, status transitions, assignment)
│   │   └── ReportsService.cs             # Analytics & reporting service
│   ├── Endpoints\
│   │   ├── LeaveEndpoints.cs             # Leave Management API endpoint route definitions
│   │   ├── ReportsEndpoints.cs           # Reports & Analytics API endpoint route definitions
│   │   └── TaskEndpoints.cs              # Task Management API endpoint route definitions
│   └── Health\
│       └── HealthCheck.cs                # Diagnostics for MongoDB, collections, and JWT
```

---

## 💾 Data Models & MongoDB Schema

### 1. `User` (`users` collection)
- `Id` (`Guid`, stored as BSON string)
- `Email` (`string`, unique sparse index, lowercase)
- `PasswordHash` (`string`, PBKDF2 hashed)
- `Goal` (`string?`, optional profile bio/goal)
- `Status` (`UserStatus` enum: `Active = 1`, `Suspended = 2`, `Deleted = 3`)
- `EmailVerified` (`bool`, defaults to `true` for new registrations)
- `LastLoginAt` (`DateTimeOffset?`)
- `CreatedAt` (`DateTimeOffset`)
- `UpdatedAt` (`DateTimeOffset`)

### 2. `Role` (`roles` collection)
- `Id` (`Guid`)
- `Name` (`string`, unique index, e.g. `"super admin"`, `"hr manager"`, `"project manager"`, `"user"`)
- `Description` (`string?`)
- `CreatedAt` & `UpdatedAt` (`DateTimeOffset`)

### 3. `Permission` (`permissions` collection)
- `Id` (`Guid`)
- `Resource` (`string`, e.g. `"users"`, `"roles"`, `"permissions"`, `"profile"`, `"tasks"`)
- `Action` (`string`, e.g. `"read"`, `"create"`, `"update"`, `"delete"`, `"assign"`, `"view_assigned"`)
- `Description` (`string?`)
- Compound unique index on `(Resource, Action)`
- Calculated property: `FullName => $"{Resource}.{Action}"`

### 4. `UserRole` (`user_roles` collection)
- `Id` (`Guid`)
- `UserId` (`Guid`, indexed)
- `RoleId` (`Guid`, indexed)
- Compound unique index on `(UserId, RoleId)`
- `AssignedAt` (`DateTimeOffset`)
- `AssignedBy` (`Guid?`)

### 5. `RolePermission` (`role_permissions` collection)
- `Id` (`Guid`)
- `RoleId` (`Guid`, indexed)
- `PermissionId` (`Guid`, indexed)
- Compound unique index on `(RoleId, PermissionId)`
- `AssignedAt` (`DateTimeOffset`)

### 6. `RefreshToken` (`refresh_tokens` collection)
- `Id` (`Guid`)
- `Token` (`string`, unique index, cryptographically secure 64-byte base64)
- `UserId` (`Guid`, indexed)
- `ExpiresAt` (`DateTimeOffset`, indexed)
- `CreatedAt` (`DateTimeOffset`)
- `RevokedAt` (`DateTimeOffset?`)
- `ReplacedByToken` (`string?`)
- `DeviceInfo` & `IpAddress` (`string?`)

### 7. `PasswordResetToken` (`password_reset_tokens` collection)
- `Id` (`Guid`)
- `TokenHash` (`string`, unique index, SHA-256 hash of plain token)
- `UserId` (`Guid`, indexed)
- `ExpiresAt` (`DateTimeOffset`, indexed, 1-hour expiration)
- `ConsumedAt` (`DateTimeOffset?`, single-use protection)
- `CreatedAt` (`DateTimeOffset`)

### 8. `Attendance` (`attendance` collection)
- `Id` (`Guid`)
- `UserId` (`Guid`, indexed)
- `AttendanceDate` (`DateOnly`, stored as string in `yyyy-MM-dd` format)
- `CheckInAtUtc` (`DateTimeOffset?`, check-in timestamp in UTC)
- `CheckOutAtUtc` (`DateTimeOffset?`, check-out timestamp in UTC)
- `Status` (`AttendanceStatus` enum: `Present=1`, `Late=2`, `Absent=3`, `OnLeave=4`, `Incomplete=5`, `Holiday=6`)
- `WorkingDurationMinutes` (`int`, calculated at check-out)
- `LateMinutes` (`int`, minutes late at check-in)
- `Notes` (`string?`, optional notes)
- `CreatedAtUtc` & `UpdatedAtUtc` (`DateTimeOffset`)
- **Compound Unique Index** on `(UserId, AttendanceDate)` - prevents duplicate check-ins per day

### 9. `AttendanceSettings` (`attendance_settings` collection)
- `Id` (`Guid`)
- `BusinessTimezone` (`string`, IANA timezone ID, default `Asia/Dhaka`)
- `WorkStartTime` (`TimeOnly`, default `09:00`)
- `WorkEndTime` (`TimeOnly`, default `18:00`)
- `LateCutoffMinutes` (`int`, default `15` minutes after work start)
- `MinimumWorkingMinutes` (`int`, default `480` = 8 hours)
- `WorkingDays` (`List<DayOfWeek>`, default Mon-Fri)
- `Holidays` (`List<Holiday>`, static and recurring holidays)
- `CreatedAtUtc` & `UpdatedAtUtc` (`DateTimeOffset`)

### 10. `Holiday` (embedded in AttendanceSettings)
- `Id` (`Guid`)
- `Name` (`string`, holiday name)
- `Date` (`DateOnly`, specific date)
- `IsRecurring` (`bool`, if true, repeats annually on same month/day)
- `CreatedAtUtc` (`DateTimeOffset`)

### 11. `LeaveType` (enum)
- `Sick = 1`
- `Casual = 2`

### 12. `LeaveStatus` (enum)
- `Pending = 1`
- `Approved = 2`
- `Rejected = 3`

### 13. `LeaveBalance` (`leave_balances` collection)
- `Id` (`Guid`, stored as BSON string)
- `UserId` (`Guid`, indexed)
- `Year` (`int`, calendar year)
- `TotalSickLeave` (`int`, default 14)
- `UsedSickLeave` (`int`, default 0)
- `TotalCasualLeave` (`int`, default 10)
- `UsedCasualLeave` (`int`, default 0)
- `CreatedAtUtc` & `UpdatedAtUtc` (`DateTimeOffset`)
- **Compound Unique Index** on `(UserId, Year)` - one balance per user per year
- Computed properties: `RemainingSickLeave`, `RemainingCasualLeave`

### 14. `LeaveRequest` (`leave_requests` collection)
- `Id` (`Guid`, stored as BSON string)
- `UserId` (`Guid`, indexed)
- `LeaveType` (`LeaveType` enum)
- `StartDate` & `EndDate` (`DateOnly`)
- `DaysCount` (`int`, calculated as EndDate - StartDate + 1)
- `Reason` (`string`)
- `Status` (`LeaveStatus` enum, default Pending)
- `ReviewedByUserId` (`Guid?`, nullable)
- `ReviewedAtUtc` (`DateTimeOffset?`, nullable)
- `RejectionReason` (`string?`, required when rejected)
- `CreatedAtUtc` & `UpdatedAtUtc` (`DateTimeOffset`)
- **Indexes**: `UserId`, `Status`, `UserId+Status`, `StartDate+EndDate`

### 15. `LeaveRequestResponse` (API response DTO)
All leave request API responses include a computed `StatusDisplay` field for UI-friendly status labels:
- `Pending` → **"Awaiting review"**
- `Approved` → **"Approved"**
- `Rejected` → **"Rejected"**
- `Cancelled` → **"Cancelled"**

---

### 16. `TaskState` (enum)
- `Unopened = 1` → Display: "Unopened" | **Color: Cyan**
- `Ongoing = 2` → Display: "Ongoing" | **Color: Yellow**
- `Completed = 3` → Display: "Completed" | **Color: Green**
- `Due = 4` → Display: "Due" | **Color: Red**

### 17. `TaskPriority` (enum)
- `Low = 1` → Display: "Low"
- `Medium = 2` → Display: "Medium"
- `High = 3` → Display: "High"

### 18. `TaskItem` (`tasks` collection)
- `Id` (`Guid`, stored as BSON string)
- `UserId` (`Guid`, indexed)
- `Title` (`string`, required, max 200 chars)
- `Description` (`string`, optional, max 2000 chars)
- `Status` (`TaskState` enum, default Unopened)
- `Priority` (`TaskPriority` enum, default Medium)
- `Category` (`string`, default "Operations")
- `DueDate` (`DateOnly?`, optional)
- `CreatedAtUtc` & `UpdatedAtUtc` (`DateTimeOffset`)
- `CompletedAtUtc` (`DateTimeOffset?`)
- `AssigneeEmail` (`string?`, denormalized for quick display)
- `AssignedByUserId` (`Guid?`, for tasks assigned by Project Manager)
- `AssignedAtUtc` (`DateTimeOffset?`, when task was assigned)
- **Indexes**: `UserId`, `UserId+Status`, `UserId+Priority`, `UserId+Category`, `UserId+CreatedAtUtc`, `DueDate`, `AssignedByUserId`, `AssignedByUserId+Status`

### 19. `TaskResponse` (API response DTO)
All task responses include computed fields for UI:
- `statusDisplay`: "Unopened" | "Ongoing" | "Completed" | "Due"
- `statusColor`: "cyan" | "yellow" | "green" | "red"
- `priorityDisplay`: "Low" | "Medium" | "High"
- `isOverdue`: `true` if `dueDate` < today and status != `Completed`
- `assignedByUserId`, `assignedAtUtc`, `assignedByEmail` (for assigned tasks)

---

## 🔌 API Endpoints Reference

### 1. Authentication Endpoints (`/api/auth`)

| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `POST` | `/api/auth/register` | Anonymous | Register a new user account; default `user` role assigned; issues access & refresh tokens. |
| `POST` | `/api/auth/login` | Anonymous | Authenticates with email and password; updates `LastLoginAt`; issues token pair. |
| `POST` | `/api/auth/refresh` | Anonymous | Refreshes access token via refresh token rotation (revokes old, issues new pair). |
| `POST` | `/api/auth/logout` | `[Auth]` | Revokes active refresh token. |
| `POST` | `/api/auth/logout-all` | `[Auth]` | Revokes all active refresh tokens for the current user across all devices. |
| `GET` | `/api/auth/me` | `[Auth]` | Returns current user profile, assigned roles, and evaluated permissions. |
| `POST` | `/api/auth/forgot-password` | Anonymous | Requests a password reset link. Enumeration-safe (identical response for existing and non-existing emails). |
| `POST` | `/api/auth/reset-password` | Anonymous | Validates and atomically consumes single-use reset token; updates password; revokes all active sessions. |
| `POST` | `/api/auth/change-password` | `[Auth]` | Changes password with verification of current password; revokes other device sessions. |

### 2. Role-Based Access Control Endpoints (`/api/rbac`)

| Method | Endpoint | Required Role / Permission | Description |
| :--- | :--- | :--- | :--- |
| `POST` | `/api/rbac/bootstrap/superadmin` | Anonymous | One-time bootstrap endpoint to create the initial superadmin. Automatically disabled once any superadmin exists. |
| `GET` | `/api/rbac/roles` | `super admin` | Lists all roles with their associated permissions. |
| `GET` | `/api/rbac/roles/{roleId}` | `super admin` | Retrieves specific role with permissions. |
| `POST` | `/api/rbac/roles` | `super admin` | Creates a new custom role with optional permission IDs. |
| `PUT` | `/api/rbac/roles/{roleId}` | `super admin` | Updates role name and description. |
| `DELETE` | `/api/rbac/roles/{roleId}` | `super admin` | Deletes a custom role (built-in `super admin` and `user` roles are protected). |
| `POST` | `/api/rbac/roles/{roleId}/permissions` | `super admin` | Assigns permissions to a role. |
| `DELETE` | `/api/rbac/roles/{roleId}/permissions/{permissionId}` | `super admin` | Removes a permission from a role. |
| `GET` | `/api/rbac/permissions` | `super admin` | Lists all available system permissions. |
| `POST` | `/api/rbac/permissions` | `super admin` | Registers a new system permission (`Resource` + `Action`). |
| `GET` | `/api/rbac/users/{userId}/roles` | `super admin` or `hr manager` | Gets user's assigned roles and resolved permission list. |
| `POST` | `/api/rbac/users/{userId}/roles` | `super admin` or `hr manager` | Assigns a role to a user. HR Managers may only assign the `user` role. **Super Admin can assign `project manager` role.** |
| `DELETE` | `/api/rbac/users/{userId}/roles/{roleId}` | `super admin` or `hr manager` | Removes a role from a user (guards against removing own super admin role). |
| `GET` | `/api/rbac/users` | `super admin` | Paginated search of all users with roles, status, and verification flags. |
| `PATCH` | `/api/rbac/users/{userId}/status` | `super admin` | Updates user status (`Active`, `Suspended`). Suspending immediately revokes all refresh tokens. |

### 3. Attendance Management Endpoints (`/api/attendance`)

| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `POST` | `/api/attendance/check-in` | `[Auth]` | Check in for today (business timezone-aware). Prevents duplicate check-ins. Returns late status if past cutoff. |
| `POST` | `/api/attendance/check-out` | `[Auth]` | Check out for today. Calculates working duration. Marks `Incomplete` if below minimum minutes. |
| `GET` | `/api/attendance/my/today` | `[Auth]` | Get current user's attendance record for today (business date). |
| `GET` | `/api/attendance/my` | `[Auth]` | Get current user's attendance history with pagination and date filters. |
| `GET` | `/api/attendance/admin/daily` | `super admin` or `hr manager` | Admin daily report for a specific date (defaults to today). Shows all users with status: Present, Late, Absent, OnLeave, Holiday, NoRecord. |
| `GET` | `/api/attendance/admin/summary` | `super admin` or `hr manager` | Admin monthly summary per user: present/late/absent/on-leave/incomplete days, total working minutes, attendance %, avg hours/day. |
| `GET` | `/api/attendance/admin/settings` | `super admin` or `hr manager` | Get current attendance settings (work hours, late cutoff, working days, holidays). |
| `PUT` | `/api/attendance/admin/settings` | `super admin` | Update attendance settings (late cutoff, minimum working minutes, working days, holidays). |
| `POST` | `/api/attendance/admin/leave` | `super admin` or `hr manager` | Mark a user as on leave for a specific date (creates/updates attendance record with `OnLeave` status). |
| `DELETE` | `/api/attendance/admin/{attendanceId}` | `super admin` | Delete an attendance record (admin override). |

**Attendance Features:**
- **Business Timezone**: All dates calculated in configured timezone (default `Asia/Dhaka`), not server UTC
- **Unique Constraint**: MongoDB compound unique index on `(UserId, AttendanceDate)` prevents race conditions
- **Late Detection**: Configurable `LateCutoffMinutes` (default 15) after `WorkStartTime` (default 09:00)
- **Working Hours**: `WorkStartTime`/`WorkEndTime` (default 09:00-18:00), `MinimumWorkingMinutes` (default 480 = 8h)
- **Working Days**: Configurable weekdays (default Mon-Fri), excludes weekends and holidays
- **Holidays**: Static + recurring (monthly) holidays supported in settings
- **Statuses**: `Present`, `Late`, `Absent`, `OnLeave`, `Incomplete`, `Holiday`

### 4. Health & Diagnostics (`/health`, `/ready`, `/config`)

| Endpoint | Tag | Description |
| :--- | :--- | :--- |
| `GET /health` | `live` | Liveness probe verifying the ASP.NET Core process is responsive. |
| `GET /ready` | `ready` | Readiness probe verifying MongoDB ping, collections accessibility, and required indexes. |
| `GET /config` | `config` | Verifies JWT signing keys and required environment variables. |

### 5. Leave Management Endpoints (`/api/leaves`, `/api/admin/leaves`, `/api/leave`, `/api/admin/leave`)

#### User Endpoints (All authenticated roles: `user`, `hr manager`, `super admin`, `project manager`)

**Primary Routes (`/api/leaves/*`):**
| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/leaves/balance` | `[Auth]` | Get current user's leave balances for current year. Auto-creates with defaults (Sick: 14, Casual: 10) if not exists. |
| `GET` | `/api/leaves/my-history` | `[Auth]` | Get current user's leave application history with pagination. |
| `POST` | `/api/leaves/apply` | `[Auth]` | Submit new leave application. Validates dates, balance, and overlapping leaves. |
| `DELETE` | `/api/leaves/{id}/cancel` | `[Auth]` | Cancel a pending leave request (only by owner). |

**Frontend-Compatible Aliases (`/api/leave/*`):**
These routes match the frontend's expected API paths.
| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/leave/types` | `[Auth]` | Get leave type configurations with balances. Returns: `[{id, name, maxDays, usedDays, remainingDays}, ...]` |
| `GET` | `/api/leave/my` | `[Auth]` | Get current user's leave application history with pagination (alias for `/my-history`). |
| `GET` | `/api/leave/summary` | `[Auth]` | Get summary: sick/casual balances + pending request count. |
| `POST` | `/api/leave/apply` | `[Auth]` | Submit new leave application (alias for `/api/leaves/apply`). |
| `DELETE` | `/api/leave/{id}/cancel` | `[Auth]` | Cancel a pending leave request (alias for `/api/leaves/{id}/cancel`). |

#### HR / Admin Endpoints (Requires `super admin` or `hr manager` role)

**Primary Routes (`/api/admin/leaves/*`):**
| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/admin/leaves/pending` | `HrOrAdmin` | View all pending leave requests across company with applicant details. |
| `POST` | `/api/admin/leaves/{id}/review` | `HrOrAdmin` | Approve or reject a leave request. Approve: atomically deducts balance. Reject: requires reason, no balance change. |

**Frontend-Compatible Aliases (`/api/admin/leave/*`):**
| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/admin/leave/pending` | `HrOrAdmin` | View leave requests with optional status filter and pagination. Query params: `status` (Pending\|Approved\|Rejected\|Cancelled), `page` (default 1), `pageSize` (default 50, max 100). Omit `status` for all statuses. |
| `POST` | `/api/admin/leave/{id}/approve` | `HrOrAdmin` | Approve a leave request (deducts balance atomically). |
| `POST` | `/api/admin/leave/{id}/reject` | `HrOrAdmin` | Reject a leave request (requires `rejectionReason`, no balance change). |

**Leave Application Validation:**
- `EndDate` must be ≥ `StartDate`
- `DaysCount` = `EndDate - StartDate + 1`
- Must not exceed remaining balance for the leave type
- No overlapping `Pending` or `Approved` leaves for the same user

**Default Quotas (per calendar year):**
- Sick Leave: 14 days
- Casual Leave: 10 days
- Balance records auto-created on first access if missing

**Response Enhancement:**
All leave request responses now include a `StatusDisplay` field that maps internal status enums to UI-friendly labels:
| Internal Status | `StatusDisplay` Value |
| :--- | :--- |
| `Pending` | `"Awaiting review"` |
| `Approved` | `"Approved"` |
| `Rejected` | `"Rejected"` |
| `Cancelled` | `"Cancelled"` |

This matches the frontend's expected display text (e.g., "Awaiting review" sub-label for pending requests).

### 6. Reports & Analytics Endpoints (`/api/reports`, `/api/admin/reports`)

These endpoints provide workforce analytics and operational reports for dashboards and management.

#### User Endpoints (All authenticated roles: `user`, `hr manager`, `super admin`, `project manager`)

| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/reports/summary` | `[Auth]` | Workforce summary: headcount, attendance rate %, average hours per day. Query params: `from`, `to` (date range, defaults to last 30 days). |
| `GET` | `/api/reports/attendance-trend` | `[Auth]` | Daily attendance trend for charts. Query param: `days` (default 30, max 365). Returns daily Present/Late/Absent/OnLeave/NoRecord counts and attendance rate. |
| `GET` | `/api/reports/late-employees` | `[Auth]` | Paginated list of late entries. Query params: `from`, `to`, `page`, `pageSize`. |
| `GET` | `/api/reports/employees-on-leave` | `[Auth]` | Paginated list of employees on leave. Query params: `from`, `to`, `page`, `pageSize`. |
| `GET` | `/api/reports/time-worked` | `[Auth]` | Time worked per user (total hours, days worked, avg hours/day, late days, on-leave days). Query params: `from`, `to`, `page`, `pageSize`. |

#### HR / Admin Endpoints (Requires `super admin` or `hr manager` role)

| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/admin/reports/department-headcount` | `HrOrAdmin` | Headcount grouped by department/role. |
| `GET` | `/api/admin/reports/absent-employees` | `HrOrAdmin` | Paginated list of absent employees (no check-in or marked absent). Query params: `from`, `to`, `page`, `pageSize`. |
| `GET` | `/api/admin/reports/incomplete-employees` | `HrOrAdmin` | Paginated list of incomplete work days (checked in but below minimum hours). Query params: `from`, `to`, `page`, `pageSize`. |
| `GET` | `/api/admin/reports/holiday-report` | `HrOrAdmin` | Holiday list for a month. Query params: `year`, `month`. |

**Date Range Parameters** (used by most endpoints):
- `from` (optional): Start date in `yyyy-MM-dd` format. Defaults to 30 days ago.
- `to` (optional): End date in `yyyy-MM-dd` format. Defaults to today.

**Pagination Parameters** (used by paginated endpoints):
- `page` (default: 1, min: 1)
- `pageSize` (default: 20, min: 1, max: 100)

---

### 7. Task Management Endpoints (`/api/tasks`)

These endpoints provide a personal task management system for employees to organize work, track deliverables, and hit milestones, plus **Project Manager** capabilities for assigning and tracking tasks across team members.

All endpoints require authentication (`[Auth]`). Users can only access their own tasks. **Project Managers** have additional endpoints under `/api/tasks/assigned/*` (requires `Permission:tasks.assign` and `Permission:tasks.view_assigned`).

#### User Endpoints (All authenticated users)

| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/tasks` | `[Auth]` | List user's tasks with filtering and pagination. Query params: `status` (1=Unopened, 2=Ongoing, 3=Completed, 4=Due), `priority` (1=Low, 2=Medium, 3=High), `category`, `search` (text search in title/description/category), `page` (default 1), `pageSize` (default 20, max 100). |
| `GET` | `/api/tasks/summary` | `[Auth]` | Get task summary counts for dashboard. Returns: `totalTasks`, `inProgressCount`, `completedCount`, `dueCount`, `highPriorityCount`. |
| `GET` | `/api/tasks/categories` | `[Auth]` | Get available categories (dynamic + defaults: Operations, Compliance, Security, Goals, Profile, Projects). |
| `GET` | `/api/tasks/{id}` | `[Auth]` | Get a specific task by ID. |
| `POST` | `/api/tasks` | `[Auth]` | Create a new task. Body: `title` (required, max 200 chars), `description` (optional, max 2000 chars), `priority` (1=Low, 2=Medium, 3=High, default 2), `category` (must be valid category, default "Operations"), `dueDate` (optional, `yyyy-MM-dd`). |
| `PUT` | `/api/tasks/{id}` | `[Auth]` | Update a task (partial updates). Body: any combination of `title`, `description`, `status`, `priority`, `category`, `dueDate`. |
| `DELETE` | `/api/tasks/{id}` | `[Auth]` | Delete a task. |
| `POST` | `/api/tasks/{id}/advance` | `[Auth]` | Advance status cycle: `Unopened` → `Ongoing` → `Completed` → `Unopened`. |

#### Project Manager Endpoints (Requires `project manager` role / `Permission:tasks.assign`, `Permission:tasks.view_assigned`)

| Method | Endpoint | Auth | Description |
| :--- | :--- | :--- | :--- |
| `GET` | `/api/tasks/assigned` | `Permission:tasks.view_assigned` | List tasks assigned BY the project manager with filtering and pagination. |
| `GET` | `/api/tasks/assigned/summary` | `Permission:tasks.view_assigned` | Get summary of assigned tasks: `totalTasks`, `inProgressCount`, `completedCount`, `dueCount`, `highPriorityCount`. |
| `GET` | `/api/tasks/assigned/{id}` | `Permission:tasks.view_assigned` | Get a specific assigned task by ID. |
| `POST` | `/api/tasks/assigned` | `Permission:tasks.assign` | Create and assign a task to another user. Body: same as `POST /api/tasks` plus required `assigneeUserId` (Guid). |
| `PUT` | `/api/tasks/assigned/{id}` | `Permission:tasks.assign` | Update an assigned task (PM can reassign to different user, change status, etc.). |
| `POST` | `/api/tasks/assigned/{id}/advance` | `Permission:tasks.assign` | Advance status on assigned task: `Unopened` → `Ongoing` → `Completed` → `Unopened`. |

**Task State Enum:**
- `Unopened = 1` → Display: "Unopened" | **Color: Cyan**
- `Ongoing = 2` → Display: "Ongoing" | **Color: Yellow**
- `Completed = 3` → Display: "Completed" | **Color: Green**
- `Due = 4` → Display: "Due" | **Color: Red**

**Task Priority Enum:**
- `Low = 1` → Display: "Low"
- `Medium = 2` → Display: "Medium"
- `High = 3` → Display: "High"

**Valid Categories:** `Operations`, `Compliance`, `Security`, `Goals`, `Profile`, `Projects`

**Response Fields:**
All task responses include computed fields for UI:
- `statusDisplay`: "Unopened" | "Ongoing" | "Completed" | "Due"
- `statusColor`: "cyan" | "yellow" | "green" | "red"
- `priorityDisplay`: "Low" | "Medium" | "High"
- `isOverdue`: `true` if `dueDate` < today and status != `Completed`
- `assignedByUserId`, `assignedAtUtc`, `assignedByEmail` (for assigned tasks)

**Task Application Validation:**
- `Title` is required (max 200 chars)
- `Description` max 2000 chars
- `Priority` must be valid enum value
- `Category` must be from valid list
- `DueDate` optional, format `yyyy-MM-dd`
- For assigned tasks: `assigneeUserId` must be a valid user

**Example: User Task Management Flow**
```javascript
// Get task summary for dashboard
const summary = await fetch('https://hrm-api-tjfa.onrender.com/api/tasks/summary', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { totalTasks, inProgressCount, completedCount, dueCount, highPriorityCount } = await summary.json();

// List tasks with filters
const tasks = await fetch('https://hrm-api-tjfa.onrender.com/api/tasks?status=1&priority=3&category=Security&search=2FA', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { tasks: taskList, totalCount, page, pageSize, totalPages } = await tasks.json();

// Create a new task
const newTask = await fetch('https://hrm-api-tjfa.onrender.com/api/tasks', {
  method: 'POST',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${accessToken}`
  },
  body: JSON.stringify({ 
    title: 'Setup 2FA',
    description: 'Add mobile authenticator app for security compliance',
    priority: 3, // High
    category: 'Security',
    dueDate: '2026-11-15'
  })
});
const task = await newTask.json();

// Advance task status (Unopened → Ongoing)
const advanced = await fetch(`https://hrm-api-tjfa.onrender.com/api/tasks/${task.id}/advance`, {
  method: 'POST',
  headers: { 'Authorization': `Bearer ${accessToken}` }
});

// Update task
const updated = await fetch(`https://hrm-api-tjfa.onrender.com/api/tasks/${task.id}`, {
  method: 'PUT',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${accessToken}`
  },
  body: JSON.stringify({ 
    title: 'Updated task title',
    status: 2, // Ongoing
    priority: 1 // Low
  })
});

// Get available categories
const categories = await fetch('https://hrm-api-tjfa.onrender.com/api/tasks/categories', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
// Returns: ["Compliance", "Goals", "Operations", "Profile", "Projects", "Security"]

// Delete task
await fetch(`https://hrm-api-tjfa.onrender.com/api/tasks/${task.id}`, {
  method: 'DELETE',
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
```

**Example: Project Manager Task Assignment Flow**
```javascript
// Project Manager: Get summary of assigned tasks
const pmSummary = await fetch('https://hrm-api-tjfa.onrender.com/api/tasks/assigned/summary', {
  headers: { 'Authorization': `Bearer ${pmAccessToken}` }
});
const { totalTasks, inProgressCount, completedCount, dueCount, highPriorityCount } = await pmSummary.json();

// Project Manager: List all tasks they assigned
const assignedTasks = await fetch('https://hrm-api-tjfa.onrender.com/api/tasks/assigned?status=2&page=1&pageSize=20', {
  headers: { 'Authorization': `Bearer ${pmAccessToken}` }
});
const { tasks: pmTaskList, totalCount } = await assignedTasks.json();

// Project Manager: Create and assign task to team member
const assignedTask = await fetch('https://hrm-api-tjfa.onrender.com/api/tasks/assigned', {
  method: 'POST',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${pmAccessToken}`
  },
  body: JSON.stringify({ 
    title: 'Implement user authentication',
    description: 'Add JWT-based auth with refresh token rotation',
    priority: 3, // High
    category: 'Security',
    dueDate: '2026-11-30',
    assigneeUserId: '6ad3da0e-3b91-4657-85d0-258a29f25df3' // User 1's ID
  })
});
const newAssignedTask = await assignedTask.json();
// Response includes assignedByUserId, assignedAtUtc, assigneeEmail

// Project Manager: Advance assigned task status (Unopened → Ongoing)
const advanced = await fetch(`https://hrm-api-tjfa.onrender.com/api/tasks/assigned/${newAssignedTask.id}/advance`, {
  method: 'POST',
  headers: { 'Authorization': `Bearer ${pmAccessToken}` }
});

// Project Manager: Reassign task to different user
const reassigned = await fetch(`https://hrm-api-tjfa.onrender.com/api/tasks/assigned/${newAssignedTask.id}`, {
  method: 'PUT',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${pmAccessToken}`
  },
  body: JSON.stringify({ 
    assigneeUserId: '37a4d393-814d-495e-9930-772d704660af' // User 2's ID
  })
});

// Project Manager: Get specific assigned task details
const taskDetail = await fetch(`https://hrm-api-tjfa.onrender.com/api/tasks/assigned/${newAssignedTask.id}`, {
  headers: { 'Authorization': `Bearer ${pmAccessToken}` }
});
const { statusDisplay, statusColor, assigneeEmail, assignedByUserId, assignedAtUtc } = await taskDetail.json();
// statusDisplay: "Unopened" | "Ongoing" | "Completed" | "Due"
// statusColor: "cyan" | "yellow" | "green" | "red"
```

---

## 👥 Default Seeded Users & Test Credentials

The database automatically seeds the following users and role assignments on application startup (idempotent — safe to run multiple times):

| Role | Email | Default Password | User ID | Goal / Title |
| :--- | :--- | :--- | :--- | :--- |
| **Super Admin** | `admin@company.com` | `AdminPass123!` | `a34f2cde-fda4-4451-a66d-b6e281d630fa` | System Administrator |
| **HR Manager** | `hrmanager@company.com` | `HrPass123!` | `2237bc48-8948-4d7d-b22d-48e3635707ec` | Human Resources Manager |
| **Project Manager** | `pm@company.com` | `PmPass123!` | `b8e9f2a1-5c3d-4e7f-9a1b-2c3d4e5f6a7b` | Project Manager |
| **User 1** | `user1@company.com` | `UserPass123!` | `6ad3da0e-3b91-4657-85d0-258a29f25df3` | Employee User 1 |
| **User 2** | `user2@company.com` | `UserPass123!` | `37a4d393-814d-495e-9930-772d704660af` | Employee User 2 |
| **User 3** | `user3@company.com` | `UserPass123!` | `813407fd-da7a-4fdb-8aeb-097744f994ad` | Employee User 3 |

> **Note**: These demo users are created on first startup if they don't exist. **Password resets persist across restarts** — the seeder repairs `Status`/`EmailVerified` if they drift, but **never overwrites a changed password**. In production, use the `/api/rbac/bootstrap/superadmin` endpoint to create your first admin, then manage users via the RBAC endpoints. **Super Admin and HR Manager can assign the Project Manager role to any user** via `/api/rbac/users/{userId}/roles`.

---

## 🔒 Security Architecture

1. **No Email Enumeration**: `/api/auth/forgot-password` returns the exact same message regardless of whether the email exists.
2. **Hashed Reset Tokens**: Raw tokens (32 cryptographically random bytes, URL-safe base64) are sent via email only; only the SHA-256 hash is stored in the database.
3. **Atomic Single-Use Tokens**: Reset tokens are consumed using atomic MongoDB `UpdateOneAsync` filters, preventing replay or concurrent use.
4. **Session Invalidation**: Password changes and resets automatically revoke all active refresh tokens, forcing re-authentication across all devices.
5. **Dynamic Policy Authorization**: Handlers dynamically check permissions in the database using the format `Permission:<Resource>.<Action>` (e.g. `users.read`, `roles.create`, `tasks.assign`).
6. **Rate Limiting**: Fixed-window rate limiting is enforced on sensitive authentication endpoints to mitigate brute force and credential stuffing attacks.

### Password Reset Without an Email Provider (Development)

When `Email__Mailtrap__Username` and `Email__Mailtrap__Password` are **empty or not set**, the API runs in **development mode**: instead of sending an SMTP email, it logs the reset link to the console/stdout.

**To retrieve the reset link in Render:**
1. In Render Dashboard → **Environment**, delete `Email__Mailtrap__Username` and `Email__Mailtrap__Password` (or leave them empty)
2. Ensure `App__FrontendUrl` is set to your Vercel frontend URL (e.g., `https://worknest-alpha-ebon.vercel.app`)
3. Save — Render restarts the service
4. Trigger `POST /api/auth/forgot-password` from your frontend
5. Open Render **Logs** → find the line starting with `[DEV MODE] Reset link:`
6. Copy that full URL — it opens the live "Set a new password" dialog on your Vercel frontend

> **Important**: Placeholder values like `your-mailtrap-username` are **not** development mode — the service attempts real SMTP and silently fails. The credentials must be completely empty for dev mode.

---

## ⚙️ Configuration

### `appsettings.json`
```json
{
  "ConnectionStrings": {
    "MongoDB": ""
  },
  "MongoDB": {
    "Database": "hrmdb"
  },
  "Auth": {
    "JwtKey": "",
    "Issuer": "HrmApi",
    "Audience": "HrmApi"
  },
  "Email": {
    "Provider": "mailtrap",
    "FromEmail": "noreply@hrmapi.local",
    "FromName": "HRM API",
    "Mailtrap": {
      "Host": "sandbox.smtp.mailtrap.io",
      "Port": "587",
      "Username": "",
      "Password": ""
    },
    "Mailgun": {
      "Domain": "",
      "ApiKey": ""
    }
  },
  "App": {
    "FrontendUrl": "http://localhost:3000"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

### Environment Variables (`.env`)
```env
ConnectionStrings__MongoDB=mongodb+srv://user:pass@cluster.mongodb.net/hrmdb
MongoDB__Database=hrmdb
Auth__JwtKey=your-super-secret-jwt-key-at-least-32-characters-long
Auth__Issuer=HrmApi
Auth__Audience=HrmApi
Email__Provider=mailtrap
Email__FromEmail=noreply@yourdomain.com
Email__FromName=HRM API
Email__Mailtrap__Host=sandbox.smtp.mailtrap.io
Email__Mailtrap__Port=587
Email__Mailtrap__Username=your-mailtrap-username
Email__Mailtrap__Password=your-mailtrap-password
Email__Mailgun__Domain=your-mailgun-domain
Email__Mailgun__ApiKey=your-mailgun-api-key
App__FrontendUrl=http://localhost:3000
Attendance__BusinessTimezone=Asia/Dhaka
Attendance__WorkStartTime=09:00
Attendance__WorkEndTime=18:00
Attendance__LateCutoffMinutes=15
Attendance__MinimumWorkingMinutes=480
```

> **Important**: Never commit `.env` to version control. Use `.env.example` as a template. In production (Render, Azure, etc.), set these as environment variables in the platform dashboard.

---

## 🐳 Docker Deployment

### Build Image
```bash
docker build -t hrm-api .
```

### Run with MongoDB
```bash
docker run -d -p 5000:8080 \
  -e ConnectionStrings__MongoDB=mongodb://host.docker.internal:27017 \
  -e MongoDB__Database=hrmdb \
  -e Auth__JwtKey=your-super-secret-jwt-key-at-least-32-characters-long \
  hrm-api
```

---

## ☁️ Render Deployment

### 1. Prerequisites
- MongoDB Atlas cluster (or any MongoDB instance accessible from Render)
- **Optional**: Mailtrap account (for password reset emails) — see "Development Mode" below if you don't have one
- GitHub repository connected to Render

### 2. Create Web Service
1. Go to **https://dashboard.render.com** → **New** → **Web Service**
2. Connect your GitHub repo (`JrUchsas/hrm-api`)
3. Settings:
   - **Name**: `hrm-api` (or your chosen name, e.g., `hrm-api-tjfa`)
   - **Runtime**: `Docker`
   - **Region**: Choose closest to your users
   - **Plan**: Free (for testing) or Starter/Standard (for production)

### 3. Environment Variables (Required)
In Render Dashboard → **Environment** tab, add:

| Key | Value | Notes |
|-----|-------|-------|
| `ConnectionStrings__MongoDB` | `mongodb+srv://user:pass@cluster.mongodb.net/hrmdb` | Your Atlas connection string |
| `MongoDB__Database` | `hrmdb` | Database name |
| `Auth__JwtKey` | **Generate: `openssl rand -base64 32`** | **Must be 32+ chars, unique per environment** |
| `Auth__Issuer` | `HrmApi` | |
| `Auth__Audience` | `HrmApi` | |
| `Email__Provider` | `mailtrap` | |
| `Email__FromEmail` | `noreply@yourdomain.com` | Use verified domain |
| `Email__FromName` | `HRM API` | |
| `Email__Mailtrap__Host` | `sandbox.smtp.mailtrap.io` | |
| `Email__Mailtrap__Port` | `2525` | **Use 2525 on Render (port 587 often blocked). Works with Mailtrap sandbox.** |
| `Email__Mailtrap__Username` | `your-mailtrap-username` | From Mailtrap Inbox → SMTP Settings |
| `Email__Mailtrap__Password` | `your-mailtrap-password` | From Mailtrap Inbox → SMTP Settings |
| `App__FrontendUrl` | `https://your-frontend-domain.com` | **Your actual Vercel/frontend URL** (used in reset emails) |
| `Attendance__BusinessTimezone` | `Asia/Dhaka` | Business timezone for date calculations |
| `Attendance__WorkStartTime` | `09:00` | Work day start time (24h format) |
| `Attendance__WorkEndTime` | `18:00` | Work day end time (24h format) |
| `Attendance__LateCutoffMinutes` | `15` | Minutes after work start considered late |
| `Attendance__MinimumWorkingMinutes` | `480` | Minimum working minutes for full day (8h = 480) |

> ⚠️ **Important**: Use **port 2525** on Render (port 587 is often blocked by cloud providers). Mailtrap sandbox supports both ports. Get credentials from Mailtrap Inbox → **SMTP Settings** tab.

### 4. Deploy
- Click **Create Web Service**
- Render builds from `Dockerfile` and deploys automatically
- Your API will be live at: `https://<your-service-name>.onrender.com` (e.g., `https://hrm-api-tjfa.onrender.com`)

### 5. Verify Deployment
```bash
# Health checks (replace with your actual service URL)
curl https://hrm-api-tjfa.onrender.com/health
curl https://hrm-api-tjfa.onrender.com/ready
curl https://hrm-api-tjfa.onrender.com/config

# Test auth
curl -X POST https://hrm-api-tjfa.onrender.com/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@company.com","password":"AdminPass123!"}'
```

### 6. Password Reset in Development Mode (No Mailtrap)
If you left Mailtrap credentials empty:
1. Trigger `POST /api/auth/forgot-password` from your frontend
2. Open Render **Logs** for the service
3. Find `[DEV MODE] Reset link: https://your-frontend.com/reset-password?token=...`
4. Copy that URL — it opens the password reset page on your Vercel frontend directly

---

## 🔗 Frontend Integration

### Base URL
```
https://<your-service-name>.onrender.com/api
```
Example: `https://hrm-api-tjfa.onrender.com/api`

### Example: Login Flow
```javascript
// Login
const response = await fetch('https://hrm-api-tjfa.onrender.com/api/auth/login', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ email: 'user@company.com', password: 'UserPass123!' })
});
const { accessToken, refreshToken } = await response.json();

// Use access token
const profile = await fetch('https://hrm-api-tjfa.onrender.com/api/auth/me', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
```

### Example: Attendance Check-In/Out Flow
```javascript
// Check in (call at start of work day)
const checkIn = await fetch('https://hrm-api-tjfa.onrender.com/api/attendance/check-in', {
  method: 'POST',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${accessToken}`
  },
  body: JSON.stringify({ notes: 'Arrived at office' })
});
const { attendanceId, status, message } = await checkIn.json();

// Get today's status
const today = await fetch('https://hrm-api-tjfa.onrender.com/api/attendance/my/today', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { hasRecord, record } = await today.json();

// Check out (call at end of work day)
const checkOut = await fetch('https://hrm-api-tjfa.onrender.com/api/attendance/check-out', {
  method: 'POST',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${accessToken}`
  },
  body: JSON.stringify({ notes: 'Leaving for the day' })
});
const { workingDurationMinutes, status: finalStatus } = await checkOut.json();

// Get personal history (last 30 days)
const history = await fetch('https://hrm-api-tjfa.onrender.com/api/attendance/my?page=1&pageSize=30', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { records, totalCount } = await history.json();
```

### Example: Admin Daily Report
```javascript
// Admin: Get daily report for today
const dailyReport = await fetch('https://hrm-api-tjfa.onrender.com/api/attendance/admin/daily', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { date, records, presentCount, lateCount, absentCount, onLeaveCount } = await dailyReport.json();

// Admin: Get monthly summary for November 2026
const monthlySummary = await fetch('https://hrm-api-tjfa.onrender.com/api/attendance/admin/summary?year=2026&month=11', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { year, month, summaries } = await monthlySummary.json();

// Admin: Update attendance settings
const updateSettings = await fetch('https://hrm-api-tjfa.onrender.com/api/attendance/admin/settings', {
  method: 'PUT',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${adminAccessToken}`
  },
  body: JSON.stringify({ 
    lateCutoffMinutes: 10,
    minimumWorkingMinutes: 420,
    workingDays: ["Monday","Tuesday","Wednesday","Thursday","Friday"],
    holidays: [
      { date: "2026-12-25", name: "Christmas Day", isRecurring: true },
      { date: "2026-01-01", name: "New Year Day", isRecurring: true }
    ]
  })
});
```

### Example: Leave Management Flow
```javascript
// User: Check leave balance (primary route)
const balance = await fetch('https://hrm-api-tjfa.onrender.com/api/leaves/balance', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { remainingSickLeave, remainingCasualLeave } = await balance.json();

// User: Get leave types with balances (frontend-compatible)
const types = await fetch('https://hrm-api-tjfa.onrender.com/api/leave/types', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const leaveTypes = await types.json();
// Returns: [{id: 1, name: "Sick", maxDays: 14, usedDays: 3, remainingDays: 11}, ...]

// User: Get leave summary (balances + pending count)
const summary = await fetch('https://hrm-api-tjfa.onrender.com/api/leave/summary', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { sickLeave, casualLeave, pendingRequests } = await summary.json();

// User: Apply for leave (frontend-compatible route)
const applyLeave = await fetch('https://hrm-api-tjfa.onrender.com/api/leave/apply', {
  method: 'POST',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${accessToken}`
  },
  body: JSON.stringify({ 
    leaveType: 1, // 1=Sick, 2=Casual
    startDate: "2026-11-15",
    endDate: "2026-11-17",
    reason: "Medical appointment"
  })
});
const leaveRequest = await applyLeave.json();

// User: View own leave history (frontend-compatible)
const history = await fetch('https://hrm-api-tjfa.onrender.com/api/leave/my?page=1&pageSize=10', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { requests } = await history.json();

// Each request includes StatusDisplay field:
// "Awaiting review" (for Pending), "Approved", "Rejected", or "Cancelled"
requests.forEach(r => console.log(`${r.leaveType}: ${r.statusDisplay}`));

// User: Cancel a pending leave request
const cancel = await fetch(`https://hrm-api-tjfa.onrender.com/api/leave/${leaveRequest.id}/cancel`, {
  method: 'DELETE',
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { message, cancelled } = await cancel.json();

// HR/Admin: View all pending requests (frontend-compatible)
const pending = await fetch('https://hrm-api-tjfa.onrender.com/api/admin/leave/pending', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { requests: pendingRequests } = await pending.json();

// HR/Admin: View approved requests with pagination (new status filter)
const approved = await fetch('https://hrm-api-tjfa.onrender.com/api/admin/leave/pending?status=Approved&page=1&pageSize=20', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { requests: approvedRequests } = await approved.json();

// HR/Admin: View all statuses (All statuses = omit status param)
const allRequests = await fetch('https://hrm-api-tjfa.onrender.com/api/admin/leave/pending?page=1&pageSize=50', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { requests: allLeaveRequests, totalCount } = await allRequests.json();

// HR/Admin: Approve a leave request (frontend-compatible)
const approve = await fetch(`https://hrm-api-tjfa.onrender.com/api/admin/leave/${leaveRequest.id}/approve`, {
  method: 'POST',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${adminAccessToken}`
  }
});

// HR/Admin: Reject a leave request (frontend-compatible)
const reject = await fetch(`https://hrm-api-tjfa.onrender.com/api/admin/leave/${leaveRequest.id}/reject`, {
  method: 'POST',
  headers: { 
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${adminAccessToken}`
  },
  body: JSON.stringify({ 
    rejectionReason: "Insufficient team coverage during that period" 
  })
});
```

### Example: Reports Flow
```javascript
// User: Get workforce summary (last 30 days by default)
const summary = await fetch('https://hrm-api-tjfa.onrender.com/api/reports/summary', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { headcount, attendanceRate, averageHoursPerDay, periodStart, periodEnd } = await summary.json();

// User: Get attendance trend for chart (last 30 days)
const trend = await fetch('https://hrm-api-tjfa.onrender.com/api/reports/attendance-trend?days=30', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { dataPoints } = await trend.json();
// dataPoints: [{date, present, late, absent, onLeave, noRecord, attendanceRate, isWorkingDay, isHoliday}, ...]

// User: Get late employees (last 30 days, paginated)
const lateEmployees = await fetch('https://hrm-api-tjfa.onrender.com/api/reports/late-employees?page=1&pageSize=20', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { entries: lateEntries, totalCount } = await lateEmployees.json();

// User: Get employees on leave (last 30 days, paginated)
const onLeave = await fetch('https://hrm-api-tjfa.onrender.com/api/reports/employees-on-leave?page=1&pageSize=20', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { entries: leaveEntries } = await onLeave.json();

// User: Get time worked per user (last 30 days, paginated)
const timeWorked = await fetch('https://hrm-api-tjfa.onrender.com/api/reports/time-worked?page=1&pageSize=20', {
  headers: { 'Authorization': `Bearer ${accessToken}` }
});
const { summaries: timeWorkedSummaries } = await timeWorked.json();
// Each: {userId, userEmail, totalHours, daysWorked, averageHoursPerDay, lateDays, onLeaveDays}

// Admin: Get department headcount
const deptHeadcount = await fetch('https://hrm-api-tjfa.onrender.com/api/admin/reports/department-headcount', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { departments, totalHeadcount } = await deptHeadcount.json();

// Admin: Get absent employees (last 30 days, paginated)
const absentEmployees = await fetch('https://hrm-api-tjfa.onrender.com/api/admin/reports/absent-employees?page=1&pageSize=20', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { entries: absentEntries, totalCount: absentTotal } = await absentEmployees.json();

// Admin: Get incomplete employees (last 30 days, paginated)
const incompleteEmployees = await fetch('https://hrm-api-tjfa.onrender.com/api/admin/reports/incomplete-employees?page=1&pageSize=20', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { entries: incompleteEntries } = await incompleteEmployees.json();

// Admin: Get holiday report for November 2026
const holidayReport = await fetch('https://hrm-api-tjfa.onrender.com/api/admin/reports/holiday-report?year=2026&month=11', {
  headers: { 'Authorization': `Bearer ${adminAccessToken}` }
});
const { holidays } = await holidayReport.json();
// Each: {name, date, isRecurring, type: "Static"|"Recurring"}
```

### CORS Configuration
For production, update `Program.cs` to restrict origins:
```csharp
// Replace AllowAnyOrigin() with:
policy.WithOrigins("https://your-frontend-domain.com")
      .AllowAnyHeader()
      .AllowAnyMethod()
      .AllowCredentials();
```

---

## 📝 License

MIT License - feel free to use in your projects.