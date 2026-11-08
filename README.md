# HRM-Api - Authentication, RBAC & Attendance Management API

A high-performance, secure .NET 10 Minimal API microservice providing enterprise **Authentication, Role-Based Access Control (RBAC), Password Reset, and Daily Attendance Management** capabilities backed by **MongoDB**.

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
│   │   └── Attendance.cs                 # Attendance, AttendanceSettings, Holiday, AttendanceStatus enum & all DTOs
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
│   │   └── AttendanceEndpoints.cs        # Attendance API endpoint route definitions
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
- `EmailVerified` (`bool`)
- `LastLoginAt` (`DateTimeOffset?`)
- `CreatedAt` (`DateTimeOffset`)
- `UpdatedAt` (`DateTimeOffset`)

### 2. `Role` (`roles` collection)
- `Id` (`Guid`)
- `Name` (`string`, unique index, e.g. `"super admin"`, `"hr manager"`, `"user"`)
- `Description` (`string?`)
- `CreatedAt` & `UpdatedAt` (`DateTimeOffset`)

### 3. `Permission` (`permissions` collection)
- `Id` (`Guid`)
- `Resource` (`string`, e.g. `"users"`, `"roles"`, `"permissions"`, `"profile"`)
- `Action` (`string`, e.g. `"read"`, `"create"`, `"update"`, `"delete"`)
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
| `POST` | `/api/rbac/users/{userId}/roles` | `super admin` or `hr manager` | Assigns a role to a user. HR Managers may only assign the `user` role. |
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

---

## 👥 Default Seeded Users & Test Credentials

The database automatically seeds the following users and role assignments on application startup (idempotent — safe to run multiple times):

| Role | Email | Default Password | User ID | Goal / Title |
| :--- | :--- | :--- | :--- | :--- |
| **Super Admin** | `admin@company.com` | `AdminPass123!` | `a34f2cde-fda4-4451-a66d-b6e281d630fa` | System Administrator |
| **HR Manager** | `hrmanager@company.com` | `HrPass123!` | `2237bc48-8948-4d7d-b22d-48e3635707ec` | Human Resources Manager |
| **User 1** | `user1@company.com` | `UserPass123!` | `6ad3da0e-3b91-4657-85d0-258a29f25df3` | Employee User 1 |
| **User 2** | `user2@company.com` | `UserPass123!` | `37a4d393-814d-495e-9930-772d704660af` | Employee User 2 |
| **User 3** | `user3@company.com` | `UserPass123!` | `813407fd-da7a-4fdb-8aeb-097744f994ad` | Employee User 3 |

> **Note**: These demo users are created on first startup if they don't exist. **Password resets persist across restarts** — the seeder repairs `Status`/`EmailVerified` if they drift, but **never overwrites a changed password**. In production, use the `/api/rbac/bootstrap/superadmin` endpoint to create your first admin, then manage users via the RBAC endpoints.

---

## 🔒 Security Architecture

1. **No Email Enumeration**: `/api/auth/forgot-password` returns the exact same message regardless of whether the email exists.
2. **Hashed Reset Tokens**: Raw tokens (32 cryptographically random bytes, URL-safe base64) are sent via email only; only the SHA-256 hash is stored in the database.
3. **Atomic Single-Use Tokens**: Reset tokens are consumed using atomic MongoDB `UpdateOneAsync` filters, preventing replay or concurrent use.
4. **Session Invalidation**: Password changes and resets automatically revoke all active refresh tokens, forcing re-authentication across all devices.
5. **Dynamic Policy Authorization**: Handlers dynamically check permissions in the database using the format `Permission:<Resource>.<Action>` (e.g. `users.read`, `roles.create`).
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