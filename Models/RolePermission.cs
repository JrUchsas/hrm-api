using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace HrmApi.Models;

public sealed class RolePermission
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("roleId")]
    [BsonRepresentation(BsonType.String)]
    public Guid RoleId { get; set; }

    [BsonElement("permissionId")]
    [BsonRepresentation(BsonType.String)]
    public Guid PermissionId { get; set; }

    [BsonElement("assignedAt")]
    public DateTimeOffset AssignedAt { get; set; } = DateTimeOffset.UtcNow;
}