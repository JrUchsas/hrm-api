using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HrmApi.Models;
using Microsoft.IdentityModel.Tokens;

namespace HrmApi.Services;

public sealed class AuthService(IConfiguration configuration)
{
    public string CreateToken(User user)
    {
        var key = configuration["Auth:JwtKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32) throw new InvalidOperationException("Auth:JwtKey must be at least 32 characters.");
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.Email, user.Email)
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(configuration["Auth:Issuer"] ?? "HrmApi", configuration["Auth:Audience"] ?? "HrmApi", claims, expires: DateTime.UtcNow.AddDays(14), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}