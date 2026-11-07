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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;

Env.Load(); // Load .env file

var builder = WebApplication.CreateBuilder(args);

var mongoConnectionString = builder.Configuration.GetConnectionString("MongoDB");
if (string.IsNullOrWhiteSpace(mongoConnectionString))
{
    mongoConnectionString = builder.Configuration["MONGODB_URI"];
}
if (string.IsNullOrWhiteSpace(mongoConnectionString))
{
    if (builder.Environment.IsEnvironment("Testing"))
    {
        mongoConnectionString = "mongodb://localhost:27017";
    }
    else
    {
        throw new InvalidOperationException("MongoDB connection string is missing. Please set ConnectionStrings__MongoDB or MONGODB_URI in your .env file or environment variables.");
    }
}

builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));
builder.Services.AddScoped<MongoDbContext>();
builder.Services.AddHealthChecks()
    .AddCheck<ComprehensiveHealthCheck>("comprehensive", tags: ["live", "ready"])
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
    .AddCheck<ConfigurationHealthCheck>("configuration", tags: ["config"]);

builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<RbacSeedService>();
builder.Services.AddScoped<PasswordResetTokenService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddHttpClient();

// Register email service based on configuration
var emailProvider = builder.Configuration["Email:Provider"]?.ToLowerInvariant() ?? "mailtrap";
if (emailProvider == "mailgun")
{
    builder.Services.AddScoped<IEmailService, MailgunEmailService>();
}
else
{
    builder.Services.AddScoped<IEmailService, MailtrapEmailService>();
}

// Register RBAC authorization handler and policies
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddRbacAuthorization();

var jwtKey = builder.Configuration["Auth:JwtKey"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    jwtKey = builder.Configuration["JWT_SECRET"] ?? builder.Configuration["JWT_KEY"];
}
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
{
    if (builder.Environment.IsEnvironment("Testing"))
    {
        jwtKey = "test-jwt-key-that-is-at-least-32-characters-long";
    }
    else
    {
        throw new InvalidOperationException("Auth:JwtKey (or JWT_SECRET) is required and must be at least 32 characters long. Please set it in your .env file or environment variables.");
    }
}
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

// Register Rate Limiting for auth protection
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth-policy", opt =>
    {
        opt.PermitLimit = 100;
        opt.Window = TimeSpan.FromMinutes(1);
    });
});

var app = builder.Build();

// Create MongoDB indexes and seed default RBAC data on startup
using (var scope = app.Services.CreateScope())
{
    try
    {
        var dbContext = scope.ServiceProvider.GetRequiredService<MongoDbContext>();
        await dbContext.CreateIndexesAsync();

        // Seed default RBAC data (idempotent: checks for existing roles and permissions)
        var seedService = scope.ServiceProvider.GetRequiredService<RbacSeedService>();
        await seedService.SeedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Failed to initialize MongoDB indexes or seed RBAC data on startup.");
    }
}

app.UseCors();
app.UseRateLimiter();
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

app.Run();