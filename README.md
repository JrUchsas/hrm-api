# HRM-Api - MongoDB Habit Tracking API

A lightweight, clean .NET 10 Minimal API for habit tracking and analytics using **MongoDB** (replacing EF Core / PostgreSQL) and omitting all AI features.

---

## 🏗️ Architecture & Technology Stack

- **Framework**: .NET 10 Web API (Minimal APIs)
- **Database**: MongoDB (`MongoDB.Driver` v2.25.0)
- **Authentication**: JWT Bearer Authentication (`Microsoft.AspNetCore.Authentication.JwtBearer`)
- **Password Hashing**: `Microsoft.AspNetCore.Identity.PasswordHasher<User>`
- **Health Checks**: `Microsoft.Extensions.Diagnostics.HealthChecks` + MongoDB health check

---

## 📂 Project Directory Structure

```text
D:\All Codes\Projects\HRM-Api\
├── .dockerignore
├── .env.example
├── .gitignore
├── HrmApi.csproj
├── Dockerfile
├── docker-entrypoint.sh
├── appsettings.json
├── appsettings.Development.json
├── README.md
├── Data/
│   └── MongoDbContext.cs
├── Models/
│   ├── User.cs
│   ├── Habit.cs
│   ├── HabitCheckIn.cs
│   └── InsightModels.cs
├── Services/
│   ├── AuthService.cs
│   └── InsightService.cs
└── Program.cs
```

---

## 💾 Data Models & MongoDB Mappings

### `User` (`users` collection)
- `Id` (`Guid`, stored as BSON String `BsonType.String`)
- `Username` (`string`, indexed as Unique)
- `PasswordHash` (`string`)
- `Goal` (`string?`)
- `CreatedAt` (`DateTimeOffset`)

### `Habit` (`habits` collection)
- `Id` (`Guid`)
- `UserId` (`Guid`, compound index with `IsActive`, `CreatedAt`)
- `Name` (`string`)
- `Description` (`string?`)
- `Unit` (`string`, e.g. "minutes", "pages")
- `Schedule` (`string`, e.g. "daily")
- `TargetValue` (`int`)
- `MinimumValue` (`int`)
- `IsActive` (`bool`)
- `CreatedAt` (`DateTimeOffset`)

### `HabitCheckIn` (`checkins` collection)
- `Id` (`Guid`)
- `HabitId` (`Guid`, compound index with `Date` as Unique)
- `Date` (`DateOnly`)
- `CompletedValue` (`int`)
- `Status` (`CheckInStatus` enum: `Missed`, `Minimum`, `Completed`, stored as String)
- `Obstacle` (`string?`)
- `Notes` (`string?`)
- `CreatedAt` (`DateTimeOffset`)

---

## 🔌 API Endpoints Reference

### Authentication
- `POST /api/auth/register` — Register a new user (`{ username, password, goal? }`)
- `POST /api/auth/login` — Authenticate user (`{ username, password }`) -> returns JWT token
- `GET /api/auth/me` `[Auth]` — Get current user identity

### Profile Management
- `GET /api/profile` `[Auth]` — Retrieve user profile
- `PUT /api/profile` `[Auth]` — Update user profile goal (`{ goal }`)

### Habits
- `GET /api/habits` `[Auth]` — List active habits for user
- `POST /api/habits` `[Auth]` — Create new habit (`{ name, unit, targetValue, minimumValue, schedule, description? }`)

### Dashboard & Check-ins
- `GET /api/dashboard?date={YYYY-MM-DD}` `[Auth]` — Get habits with check-in status for given date
- `POST /api/habits/{habitId}/check-ins` `[Auth]` — Create or update daily check-in (`{ date, completedValue, targetValue, obstacle?, notes? }`)

### Analytics & Insights
- `GET /api/insights?days=28` `[Auth]` — Habit performance statistics, completion rates, and obstacles

### System
- `GET /health` & `GET /ready` — Health check endpoints

---

## ⚙️ Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "MongoDB": "mongodb://localhost:27017"
  },
  "MongoDB": {
    "Database": "HRM-Api"
  },
  "Auth": {
    "JwtKey": "your-super-secret-jwt-key-must-be-at-least-32-chars-long",
    "Issuer": "HRM-Api",
    "Audience": "HRM-Api"
  }
}
```

### MongoDB Atlas Connection String Format
```json
{
  "ConnectionStrings": {
    "MongoDB": "mongodb+srv://<username>:<password>@<cluster>.mongodb.net/"
  },
  "MongoDB": {
    "Database": "your-db-name"
  }
}
```

### Environment Variables (`.env` file)
```bash
ConnectionStrings__MongoDB=mongodb://localhost:27017
MongoDB__Database=HRM-Api
Auth__JwtKey=your-super-secret-jwt-key-at-least-32-characters-long
Auth__Issuer=HrmApi
Auth__Audience=HrmApi
```

---

## 🔐 Authentication Details

- **Token Format**: JWT (HS256)
- **Expiry**: 14 days
- **Claims**: `sub` (user Id), `name` (username)
- **Header**: `Authorization: Bearer <token>`

### Password Requirements
- Minimum 8 characters
- Username: 3-50 characters, lowercase, unique

---

## 📋 Validation Rules

### Habit Creation
- `name`: Required, non-empty
- `unit`: Required, non-empty
- `targetValue`: > 0
- `minimumValue`: > 0 and ≤ `targetValue`
- `schedule`: Required, non-empty (e.g., "daily", "weekly")

### Check-in Creation/Update
- `date`: Required, `DateOnly` format (YYYY-MM-DD)
- `completedValue`: ≥ 0
- `targetValue`: Must match habit's targetValue (for status calculation)
- `obstacle`: Optional, max 500 chars
- `notes`: Optional, max 1000 chars

### Profile Update
- `goal`: Optional, max 500 characters

---

## 🏃 Check-in Status Logic

Status is **automatically calculated** server-side based on `completedValue` vs habit's `targetValue` and `minimumValue`:

| Condition | Status | Description |
|-----------|--------|-------------|
| `completedValue >= targetValue` | `Completed` | Full target achieved |
| `completedValue >= minimumValue` | `Minimum` | Minimum threshold met |
| `completedValue < minimumValue` | `Missed` | Below minimum |

**Upsert Behavior**: POST to `/api/habits/{habitId}/check-ins` with same `habitId` + `date` updates existing check-in.

---

## 📊 Insights & Analytics

### `GET /api/insights?days=28`
Returns per-habit analytics for the rolling window (7-90 days, default 28):

```json
{
  "habitId": "guid",
  "habitName": "Morning Run",
  "daysObserved": 28,
  "completedDays": 20,
  "minimumDays": 5,
  "missedDays": 3,
  "completionRate": 0.89,
  "commonObstacle": "rain",
  "pattern": "Morning Run is being missed more often than completed.",
  "suggestedExperiment": "Try the 1 km minimum for one week."
}
```

**Fields**:
- `completionRate`: (completed + minimum) / daysObserved
- `commonObstacle`: Most frequent obstacle (case-insensitive grouping)
- `pattern`: Detected if missed > 3 AND missed > 50% of check-ins
- `suggestedExperiment`: Contextual suggestion based on pattern

---

## 🐳 Docker Usage

### Build
```bash
docker build -t habit-api .
```

### Run with Local MongoDB
```bash
docker run -d --name mongo mongo:latest
docker run -d -p 5000:8080 \
  -e ConnectionStrings__MongoDB=mongodb://host.docker.internal:27017 \
  -e MongoDB__Database=HRM-Api \
  -e Auth__JwtKey=your-super-secret-jwt-key-at-least-32-characters-long \
  habit-api
```

### Run with MongoDB Atlas
```bash
docker run -d -p 5000:8080 \
  -e ConnectionStrings__MongoDB="mongodb+srv://user:pass@cluster.mongodb.net/" \
  -e MongoDB__Database=HRM-Api \
  -e Auth__JwtKey=your-super-secret-jwt-key-at-least-32-characters-long \
  habit-api
```

---

## 🧪 Testing Examples

### Bash / Git Bash / Linux / macOS
```bash
# Register
curl -X POST http://localhost:5000/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"username": "testuser", "password": "password123", "goal": "Build habits"}'

# Login
TOKEN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username": "testuser", "password": "password123"}' | jq -r .token)

# Create habit
curl -X POST http://localhost:5000/api/habits \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name": "Run", "unit": "km", "targetValue": 5, "minimumValue": 1, "schedule": "daily"}'

# Check-in
curl -X POST http://localhost:5000/api/habits/HABIT_ID/check-ins \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"date": "2026-11-04", "completedValue": 5, "targetValue": 5}'

# Dashboard
curl "http://localhost:5000/api/dashboard" -H "Authorization: Bearer $TOKEN"

# Insights
curl "http://localhost:5000/api/insights?days=14" -H "Authorization: Bearer $TOKEN"
```

### Windows cmd.exe
```cmd
# Use @file for JSON to avoid quote escaping issues
echo {"username": "testuser", "password": "password123"} > login.json
curl -X POST http://localhost:5000/api/auth/login -H "Content-Type: application/json" -d @login.json

# Set token manually from response
set TOKEN=your-jwt-token-here

curl -X GET http://localhost:5000/api/habits -H "Authorization: Bearer %TOKEN%"
```

### PowerShell
```powershell
# Register
$register = Invoke-RestMethod -Uri "http://localhost:5000/api/auth/register" -Method Post -ContentType "application/json" -Body '{"username": "testuser", "password": "password123"}'
$token = $register.token

# Authenticated request
$headers = @{ Authorization = "Bearer $token" }
Invoke-RestMethod -Uri "http://localhost:5000/api/habits" -Method Get -Headers $headers
```

---

## ❌ Error Response Formats

| Status Code | Scenario | Response |
|-------------|----------|----------|
| `400` | Validation failed | `"Username must be 3-50 characters..."` |
| `401` | Missing/invalid/expired token | Empty / `WWW-Authenticate: Bearer` |
| `404` | Resource not found | Empty |
| `409` | Username already exists | `"Username is already in use."` |
| `500` | Server error (e.g., MongoDB down) | Empty |

---

## 🏥 Health Checks

| Endpoint | Purpose |
|----------|---------|
| `GET /health` | Liveness probe (process alive) |
| `GET /ready` | Readiness probe (MongoDB connected) |

Both return `200 OK` with body `"Healthy"` when passing.

---

## 🚀 How to Run & Test

1. **Start MongoDB** locally or use Atlas:
   ```bash
   docker run -d -p 27017:27017 mongo
   ```
2. **Configure** `appsettings.json` or `.env` with your connection string and JWT key (≥32 chars)
3. **Run the application**:
   ```bash
   dotnet run
   ```
4. **API available at**: `http://localhost:5000` (HTTP) / `https://localhost:5001` (HTTPS)
5. **Test endpoints** using curl, Postman, or the examples above.
