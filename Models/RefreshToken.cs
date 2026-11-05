using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HrmApi.Models;

public sealed class RefreshToken
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("token")]
    public required string Token { get; set; }

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; set; }

    [BsonElement("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [BsonElement("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [BsonElement("revokedAt")]
    public DateTimeOffset? RevokedAt { get; set; }

    [BsonElement("replacedByToken")]
    public string? ReplacedByToken { get; set; }

    [BsonElement("deviceInfo")]
    public string? DeviceInfo { get; set; }

    [BsonElement("ipAddress")]
    public string? IpAddress { get; set; }

    public bool IsRevoked => RevokedAt != null;
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsActive => !IsRevoked && !IsExpired;
}