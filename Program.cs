using HrmApi.Data;
using HrmApi.Endpoints;
using HrmApi.Health;
using HrmApi.Models;
using HrmApi.Services;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

Env.Load(); // Load .env file

var builder = WebApplication.CreateBuilder(args);

var mongoConnectionString = builder.Configuration.GetConnectionString("MongoDB")
    ?? throw new InvalidOperationException("ConnectionStrings:MongoDB is required.");

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
builder.Services.AddScoped<MongoDbContext>();
builder.Services.AddHealthChecks()
    .AddCheck<ComprehensiveHealthCheck>("comprehensive", tags: ["live", "ready"])
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<ConfigurationHealthCheck>("configuration", tags: ["config"]);

builder.Services.AddScoped<InsightService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<RbacSeedService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddHttpClient();

// Register RBAC authorization handler and policies
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddRbacAuthorization();

var jwtKey = builder.Configuration["Auth:JwtKey"] ?? throw new InvalidOperationException("Auth:JwtKey is required.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Auth:Issuer"] ?? "HrmApi",
        ValidAudience = builder.Configuration["Auth:Audience"] ?? "HrmApi",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});

builder.Services.AddAuthorization();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Create MongoDB indexes and seed RBAC data on startup
using (var scope = app.Services.CreateScope())
{
    var client = scope.ServiceProvider.GetRequiredService<IMongoClient>();
    var db = client.GetDatabase("hrmdb");

    // Drop and recreate users collection to handle schema migration
    await db.DropCollectionAsync("users");

    var dbContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
    await dbContext.CreateIndexesAsync();

    // Seed RBAC data
    var seedService = scope.ServiceProvider.GetRequiredService<RbacSeedService>();
    await seedService.SeedAsync();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Health check endpoints
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = WriteHealthResponse
});

app.MapHealthChecks("/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse
});

app.MapHealthChecks("/config", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("config"),
    ResponseWriter = WriteHealthResponse
});

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    var response = new
    {
        status = report.Status.ToString(),
        totalDuration = report.TotalDuration,
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            description = e.Value.Description,
            duration = e.Value.Duration,
            data = e.Value.Data
        })
    };
    return context.Response.WriteAsJsonAsync(response);
}

// Map authentication endpoints
app.MapAuthEndpoints();

// Map RBAC endpoints
app.MapRbacEndpoints();

// Map existing API endpoints (habits, check-ins, insights, profile)
MapApiEndpoints(app);

app.Run();

static void MapApiEndpoints(WebApplication app)
{
    // Profile endpoints
    app.MapGet("/api/profile", async (ClaimsPrincipal user, MongoDbContext db, CancellationToken cancellationToken) =>
    {
        var userId = UserId(user);
        var profile = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
        return profile is null ? Results.NotFound() : Results.Ok(new { profile.Email, profile.Goal });
    }).RequireAuthorization().WithName("GetProfile");

    app.MapPut("/api/profile", async (ClaimsPrincipal user, UpdateProfileRequest request, MongoDbContext db, CancellationToken cancellationToken) =>
    {
        var userId = UserId(user);
        var profile = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
        if (profile is null) return Results.NotFound();
        if (request.Goal?.Length > 500) return Results.BadRequest("Goal must be 500 characters or fewer.");
        profile.Goal = string.IsNullOrWhiteSpace(request.Goal) ? null : request.Goal.Trim();
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        await db.Users.ReplaceOneAsync(u => u.Id == userId, profile, cancellationToken: cancellationToken);
        return Results.Ok(new { profile.Email, profile.Goal });
    }).RequireAuthorization().WithName("UpdateProfile");

    // Habits endpoints
    app.MapGet("/api/habits", async (ClaimsPrincipal user, MongoDbContext db, CancellationToken cancellationToken) =>
    {
        var userId = UserId(user);
        return Results.Ok(await db.Habits.Find(h => h.UserId == userId && h.IsActive).SortBy(h => h.CreatedAt).ToListAsync(cancellationToken));
    })
    .RequireAuthorization()
    .WithName("ListHabits");

    app.MapGet("/api/dashboard", async (ClaimsPrincipal user, MongoDbContext db, DateOnly? date, CancellationToken cancellationToken) =>
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var userId = UserId(user);
        var habits = await db.Habits.Find(h => h.UserId == userId && h.IsActive).SortBy(h => h.CreatedAt).ToListAsync(cancellationToken);
        var checkIns = await db.CheckIns.Find(c => c.Date == day).ToListAsync(cancellationToken);
        var checkInDict = checkIns.Where(c => habits.Any(h => h.Id == c.HabitId)).ToDictionary(c => c.HabitId);
        return Results.Ok(habits.Select(h => new HabitTodayDto(h.Id, h.Name, h.Unit, h.Schedule, h.TargetValue, h.MinimumValue, checkInDict.GetValueOrDefault(h.Id))));
    }).RequireAuthorization().WithName("GetDashboard");

    app.MapGet("/api/insights", async (ClaimsPrincipal user, InsightService insights, int? days, CancellationToken cancellationToken) =>
        Results.Ok(await insights.GetInsightsAsync(UserId(user), Math.Clamp(days ?? 28, 7, 90), cancellationToken))).RequireAuthorization().WithName("GetInsights");

    app.MapPost("/api/habits", async (ClaimsPrincipal user, CreateHabitRequest request, MongoDbContext db, CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.TargetValue <= 0 || request.MinimumValue <= 0 || request.MinimumValue > request.TargetValue)
            return Results.BadRequest("Name is required and minimum value must be positive and no greater than the target.");

        var habit = new Habit
        {
            Name = request.Name.Trim(), Description = request.Description?.Trim(), Unit = request.Unit.Trim(), Schedule = request.Schedule.Trim(),
            TargetValue = request.TargetValue, MinimumValue = request.MinimumValue, UserId = UserId(user)
        };
        await db.Habits.InsertOneAsync(habit, cancellationToken: cancellationToken);
        return Results.Created($"/api/habits/{habit.Id}", habit);
    }).RequireAuthorization().WithName("CreateHabit");

    app.MapPost("/api/habits/{habitId:guid}/check-ins", async (ClaimsPrincipal user, Guid habitId, CreateCheckInRequest request, MongoDbContext db, CancellationToken cancellationToken) =>
    {
        var userId = UserId(user);
        var habit = await db.Habits.Find(h => h.Id == habitId && h.UserId == userId && h.IsActive).FirstOrDefaultAsync(cancellationToken);
        if (habit is null) return Results.NotFound();
        if (request.CompletedValue < 0) return Results.BadRequest("Completed value cannot be negative.");

        var existingCheckIn = await db.CheckIns.Find(c => c.HabitId == habitId && c.Date == request.Date).FirstOrDefaultAsync(cancellationToken);
        var status = request.CompletedValue >= habit.TargetValue ? CheckInStatus.Completed : request.CompletedValue >= habit.MinimumValue ? CheckInStatus.Minimum : CheckInStatus.Missed;

        var checkIn = existingCheckIn ?? new HabitCheckIn
        {
            HabitId = habitId,
            Date = request.Date,
            CompletedValue = request.CompletedValue,
            Status = status,
            Obstacle = request.Obstacle?.Trim(),
            Notes = request.Notes?.Trim()
        };

        if (existingCheckIn != null)
        {
            checkIn.CompletedValue = request.CompletedValue;
            checkIn.Status = status;
            checkIn.Obstacle = request.Obstacle?.Trim();
            checkIn.Notes = request.Notes?.Trim();
            await db.CheckIns.ReplaceOneAsync(c => c.Id == checkIn.Id, checkIn, cancellationToken: cancellationToken);
        }
        else
        {
            await db.CheckIns.InsertOneAsync(checkIn, cancellationToken: cancellationToken);
        }

        return Results.Created($"/api/check-ins/{checkIn.Id}", new
        {
            checkIn.Id,
            checkIn.HabitId,
            checkIn.Date,
            checkIn.CompletedValue,
            checkIn.Status,
            checkIn.Obstacle,
            checkIn.Notes,
            checkIn.CreatedAt
        });
    }).RequireAuthorization().WithName("CreateCheckIn");
}

static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException());

static bool IsValidEmail(string email)
{
    try
    {
        var addr = new System.Net.Mail.MailAddress(email);
        return addr.Address == email;
    }
    catch
    {
        return false;
    }
}

// Request/Response DTOs for API endpoints
public record UpdateProfileRequest(string? Goal);
public record CreateHabitRequest(string Name, string Unit, int TargetValue, int MinimumValue, string Schedule, string? Description);
public record CreateCheckInRequest(DateOnly Date, int CompletedValue, int TargetValue, string? Obstacle, string? Notes);
public record HabitTodayDto(Guid Id, string Name, string Unit, string Schedule, int TargetValue, int MinimumValue, HabitCheckIn? CheckIn);