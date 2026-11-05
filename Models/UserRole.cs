using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HrmApi.Models;

public sealed class UserRole
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; set; }

    [BsonElement("roleId")]
    [BsonRepresentation(BsonType.String)]
    public Guid RoleId { get; set; }

    [BsonElement("assignedAt")]
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;

    [BsonElement("assignedBy")]
    [BsonRepresentation(BsonType.String)]
    public Guid? AssignedBy { get; set; }
}