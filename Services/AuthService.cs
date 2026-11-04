using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ApiApp.Models;
using Microsoft.IdentityModel.Tokens;

namespace ApiApp.Services;

public sealed class AuthService(IConfiguration configuration)
{
    public string CreateToken(User user)
    {
        var key = configuration["Auth:JwtKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32) throw new InvalidOperationException("Auth:JwtKey must be at least 32 characters.");
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(ClaimTypes.Name, user.Username) };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(configuration["Auth:Issuer"] ?? "ApiApp", configuration["Auth:Audience"] ?? "ApiApp", claims, expires: DateTime.UtcNow.AddDays(14), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}