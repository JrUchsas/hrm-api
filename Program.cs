using ApiApp.Data;
using ApiApp.Models;
using ApiApp.Services;
using DotNetEnv;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
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
builder.Services.AddHealthChecks();
builder.Services.AddScoped<InsightService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddHttpClient();

var jwtKey = builder.Configuration["Auth:JwtKey"] ?? throw new InvalidOperationException("Auth:JwtKey is required.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Auth:Issuer"] ?? "ApiApp",
        ValidAudience = builder.Configuration["Auth:Audience"] ?? "ApiApp",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
});
builder.Services.AddAuthorization();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Create MongoDB indexes on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
    await dbContext.CreateIndexesAsync();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapHealthChecks("/ready");

app.MapPost("/api/auth/register", async (RegisterRequest request, MongoDbContext db, IPasswordHasher<User> hasher, AuthService auth, CancellationToken cancellationToken) =>
{
    var username = request.Username.Trim().ToLowerInvariant();
    if (username.Length is < 3 or > 50 || request.Password.Length < 8) return Results.BadRequest("Username must be 3-50 characters and password must be at least 8 characters.");
    if (await db.Users.Find(u => u.Username == username).AnyAsync(cancellationToken)) return Results.Conflict("Username is already in use.");
    var user = new User { Username = username, PasswordHash = "pending", Goal = request.Goal?.Trim() };
    user.PasswordHash = hasher.HashPassword(user, request.Password);
    await db.Users.InsertOneAsync(user, cancellationToken: cancellationToken);
    return Results.Ok(new AuthResponse(auth.CreateToken(user), user.Username));
}).WithName("Register");

app.MapPost("/api/auth/login", async (LoginRequest request, MongoDbContext db, IPasswordHasher<User> hasher, AuthService auth, CancellationToken cancellationToken) =>
{
    var username = request.Username.Trim().ToLowerInvariant();
    var user = await db.Users.Find(u => u.Username == username).FirstOrDefaultAsync(cancellationToken);
    if (user is null || hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed) return Results.Unauthorized();
    return Results.Ok(new AuthResponse(auth.CreateToken(user), user.Username));
}).WithName("Login");

app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new { Username = user.Identity?.Name }))
    .RequireAuthorization().WithName("CurrentUser");

app.MapGet("/api/profile", async (ClaimsPrincipal user, MongoDbContext db, CancellationToken cancellationToken) =>
{
    var userId = UserId(user);
    var profile = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
    return profile is null ? Results.NotFound() : Results.Ok(new { profile.Username, profile.Goal });
}).RequireAuthorization().WithName("GetProfile");

app.MapPut("/api/profile", async (ClaimsPrincipal user, UpdateProfileRequest request, MongoDbContext db, CancellationToken cancellationToken) =>
{
    var userId = UserId(user);
    var profile = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
    if (profile is null) return Results.NotFound();
    if (request.Goal?.Length > 500) return Results.BadRequest("Goal must be 500 characters or fewer.");
    profile.Goal = string.IsNullOrWhiteSpace(request.Goal) ? null : request.Goal.Trim();
    await db.Users.ReplaceOneAsync(u => u.Id == userId, profile, cancellationToken: cancellationToken);
    return Results.Ok(new { profile.Username, profile.Goal });
}).RequireAuthorization().WithName("UpdateProfile");

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

app.Run();

static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") ?? throw new UnauthorizedAccessException());

public record RegisterRequest(string Username, string Password, string? Goal);
public record LoginRequest(string Username, string Password);
public record UpdateProfileRequest(string? Goal);
public record AuthResponse(string Token, string Username);
public record CreateHabitRequest(string Name, string Unit, int TargetValue, int MinimumValue, string Schedule, string? Description);
public record CreateCheckInRequest(DateOnly Date, int CompletedValue, int TargetValue, string? Obstacle, string? Notes);