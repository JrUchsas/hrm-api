using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HrmApi.Models;
using Microsoft.IdentityModel.Tokens;

namespace HrmApi.Services;

public sealed class AuthService(IConfiguration configuration)
{
    private readonly TimeSpan _accessTokenLifetime = TimeSpan.FromMinutes(15);
    private readonly TimeSpan _refreshTokenLifetime = TimeSpan.FromDays(7);

    public string CreateAccessToken(User user, IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        var key = configuration["Auth:JwtKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32) throw new InvalidOperationException("Auth:JwtKey must be at least 32 characters.");

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
        };

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim("permission", p)));

        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: configuration["Auth:Issuer"] ?? "HrmApi",
            audience: configuration["Auth:Audience"] ?? "HrmApi",
            claims: claims,
            expires: DateTime.UtcNow.Add(_accessTokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        return Convert.ToBase64String(randomBytes);
    }

    public DateTimeOffset GetAccessTokenExpiry() => DateTimeOffset.UtcNow.Add(_accessTokenLifetime);
    public DateTimeOffset GetRefreshTokenExpiry() => DateTimeOffset.UtcNow.Add(_refreshTokenLifetime);

    public TimeSpan AccessTokenLifetime => _accessTokenLifetime;
    public TimeSpan RefreshTokenLifetime => _refreshTokenLifetime;

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var key = configuration["Auth:JwtKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32) return null;

        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = configuration["Auth:Issuer"] ?? "HrmApi",
            ValidAudience = configuration["Auth:Audience"] ?? "HrmApi",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        try
        {
            var principal = handler.ValidateToken(token, validationParameters, out _);
            return principal;
        }
        catch
        {
            return null;
        }
    }

    public Guid? GetUserIdFromToken(string token)
    {
        var principal = ValidateToken(token);
        if (principal == null) return null;

        var subClaim = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(subClaim, out var userId) ? userId : null;
    }

    public List<string> GetRolesFromToken(string token)
    {
        var principal = ValidateToken(token);
        if (principal == null) return [];

        return principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
    }

    public List<string> GetPermissionsFromToken(string token)
    {
        var principal = ValidateToken(token);
        if (principal == null) return [];

        return principal.FindAll("permission").Select(c => c.Value).ToList();
    }
}