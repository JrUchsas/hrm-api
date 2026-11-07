using System.Security.Cryptography;
using HrmApi.Data;
using HrmApi.Models;
using MongoDB.Driver;

namespace HrmApi.Services;

public sealed class PasswordResetTokenService(MongoDbContext db)
{
    private readonly TimeSpan _tokenLifetime = TimeSpan.FromHours(1);

    public async Task<string> GenerateTokenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Generate cryptographically secure random token
        var randomBytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        var plainToken = Convert.ToBase64String(randomBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('='); // URL-safe base64

        // Hash the token for storage
        var tokenHash = HashToken(plainToken);

        var resetToken = new PasswordResetToken
        {
            TokenHash = tokenHash,
            UserId = userId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_tokenLifetime)
        };

        await db.PasswordResetTokens.InsertOneAsync(resetToken, cancellationToken: cancellationToken);

        return plainToken;
    }

    public async Task<User?> ValidateTokenAsync(string email, string plainToken, CancellationToken cancellationToken = default)
    {
        var tokenHash = HashToken(plainToken);

        var resetToken = await db.PasswordResetTokens
            .Find(prt => prt.TokenHash == tokenHash)
            .FirstOrDefaultAsync(cancellationToken);

        if (resetToken == null || !resetToken.IsValid)
        {
            return null;
        }

        // Verify the token belongs to the user with this email
        var user = await db.Users
            .Find(u => u.Id == resetToken.UserId && u.Email == email)
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null || user.Status != UserStatus.Active)
        {
            return null;
        }

        return user;
    }

    public async Task<bool> ConsumeTokenAsync(string plainToken, CancellationToken cancellationToken = default)
    {
        var tokenHash = HashToken(plainToken);

        var filter = Builders<PasswordResetToken>.Filter.And(
            Builders<PasswordResetToken>.Filter.Eq(prt => prt.TokenHash, tokenHash),
            Builders<PasswordResetToken>.Filter.Eq(prt => prt.ConsumedAt, null)
        );

        var update = Builders<PasswordResetToken>.Update
            .Set(prt => prt.ConsumedAt, DateTimeOffset.UtcNow);

        var result = await db.PasswordResetTokens.UpdateOneAsync(filter, update, cancellationToken: cancellationToken);

        return result.ModifiedCount > 0;
    }

    public async Task CleanupExpiredTokensAsync(CancellationToken cancellationToken = default)
    {
        // Delete tokens that are expired OR already consumed
        var deleteFilter = Builders<PasswordResetToken>.Filter.Or(
            Builders<PasswordResetToken>.Filter.Lt(prt => prt.ExpiresAt, DateTimeOffset.UtcNow),
            Builders<PasswordResetToken>.Filter.Ne(prt => prt.ConsumedAt, null)
        );

        await db.PasswordResetTokens.DeleteManyAsync(deleteFilter, cancellationToken);
    }

    private static string HashToken(string token)
    {
        using var sha256 = SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }
}