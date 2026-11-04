using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ApiApp.Models;

public sealed class Habit
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("userId")]
    [BsonRepresentation(BsonType.String)]
    public Guid UserId { get; set; }

    [BsonElement("name")]
    public required string Name { get; set; }

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("unit")]
    public required string Unit { get; set; }

    [BsonElement("schedule")]
    public required string Schedule { get; set; }

    [BsonElement("targetValue")]
    public int TargetValue { get; set; }

    [BsonElement("minimumValue")]
    public int MinimumValue { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;

    [BsonElement("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}