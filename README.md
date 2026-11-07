# HRM-Api - Authentication & Role-Based Access Control (RBAC) API

A high-performance, secure .NET 10 Minimal API microservice providing enterprise **Authentication, Role-Based Access Control (RBAC), and Password Reset** capabilities backed by **MongoDB**.

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
- **Testing**: xUnit with `Mongo2Go` in-memory database and `Microsoft.AspNetCore.Mvc.Testing`

---

## 📂 Project Directory Structure

```text
d:\All Codes\Projects\
├── HRM-Api\                              # Main Web API Microservice
│   ├── ApiApp.csproj                     # Web SDK project file (net10.0)
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
│   │   └── PasswordResetToken.cs         # SHA-256 hashed password reset token model
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
│   │   └── MailgunEmailService.cs        # Mailgun HTTP API service implementation
│   └── Health\
│       └── HealthCheck.cs                # Diagnostics for MongoDB, collections, and JWT
│
└── HRM-Api.Tests\                        # Integration & Unit Test Suite
    ├── HRM-Api.Tests.csproj              # Test project file (net10.0)
    ├── TestBase.cs                       # Test fixture with Mongo2Go runner
    ├── TestWebApplicationFactory.cs     # Custom WebApplicationFactory for API testing
    ├── TestEmailService.cs               # Mock email service for test assertions
    ├── AuthEndpointsTests.cs             # 19 integration tests for /api/auth routes
    └── PasswordResetTokenServiceTests.cs # 10 unit tests for reset token lifecycle
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

### 3. Health & Diagnostics (`/health`, `/ready`, `/config`)

| Endpoint | Tag | Description |
| :--- | :--- | :--- |
| `GET /health` | `live` | Liveness probe verifying the ASP.NET Core process is responsive. |
| `GET /ready` | `ready` | Readiness probe verifying MongoDB ping, collections accessibility, and required indexes. |
| `GET /config` | `config` | Verifies JWT signing keys and required environment variables. |

---

## 👥 Default Seeded Users & Test Credentials

The database automatically seeds the following users and role assignments on application startup:

| Role | Email | Password | User ID | Goal / Title |
| :--- | :--- | :--- | :--- | :--- |
| **Super Admin** | `admin@company.com` | `AdminPass123!` | `a34f2cde-fda4-4451-a66d-b6e281d630fa` | System Administrator |
| **HR Manager** | `hrmanager@company.com` | `HrPass123!` | `2237bc48-8948-4d7d-b22d-48e3635707ec` | Human Resources Manager |
| **User 1** | `user1@company.com` | `UserPass123!` | `6ad3da0e-3b91-4657-85d0-258a29f25df3` | Employee User 1 |
| **User 2** | `user2@company.com` | `UserPass123!` | `37a4d393-814d-495e-9930-772d704660af` | Employee User 2 |
| **User 3** | `user3@company.com` | `UserPass123!` | `813407fd-da7a-4fdb-8aeb-097744f994ad` | Employee User 3 |

---

## 🔒 Security Architecture

1. **No Email Enumeration**: `/api/auth/forgot-password` returns the exact same message regardless of whether the email exists.
2. **Hashed Reset Tokens**: Raw tokens (32 cryptographically random bytes, URL-safe base64) are sent via email only; only the SHA-256 hash is stored in the database.
3. **Atomic Single-Use Tokens**: Reset tokens are consumed using atomic MongoDB `UpdateOneAsync` filters, preventing replay or concurrent use.
4. **Session Invalidation**: Password changes and resets automatically revoke all active refresh tokens, forcing re-authentication across all devices.
5. **Dynamic Policy Authorization**: Handlers dynamically check permissions in the database using the format `Permission:<Resource>.<Action>` (e.g. `users.read`, `roles.create`).
6. **Rate Limiting**: Fixed-window rate limiting is enforced on sensitive authentication endpoints to mitigate brute force and credential stuffing attacks.

---

## ⚙️ Configuration

### `appsettings.json`
```json
{
  "ConnectionStrings": {
    "MongoDB": "mongodb://localhost:27017"
  },
  "MongoDB": {
    "Database": "hrmdb"
  },
  "Auth": {
    "JwtKey": "REPLACE_WITH_SECURE_KEY_AT_LEAST_32_CHARS_IN_PRODUCTION",
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
  }
}
```

### Environment Variables (`.env`)
```env
ConnectionStrings__MongoDB=mongodb://localhost:27017
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
```

---

## 🧪 Testing

The solution includes an automated integration and unit test suite built with **xUnit** and **Mongo2Go**:

```bash
# Run all tests
dotnet test "d:/All Codes/Projects/HRM-Api.Tests/HRM-Api.Tests.csproj"
```

All 34 tests verify:
- Pre-seeded default users authentication and role resolution
- Registration, Login, Token Refresh, and Logout flows
- Forgot Password request handling & enumeration resistance
- Cryptographic token generation, hash storage, and expiry checks
- Single-use atomic consumption under concurrency
- Session revocation on password reset
- Built-in role and permission enforcement

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
