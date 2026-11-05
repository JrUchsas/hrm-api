using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HrmApi.Models;

public sealed class Permission
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("resource")]
    public required string Resource { get; set; }

    [BsonElement("action")]
    public required string Action { get; set; }

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string FullName => $"{Resource}.{Action}";
}