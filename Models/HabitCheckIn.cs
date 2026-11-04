using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ApiApp.Models;

public enum CheckInStatus { Missed, Minimum, Completed }

public sealed class HabitCheckIn
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [BsonElement("habitId")]
    [BsonRepresentation(BsonType.String)]
    public Guid HabitId { get; set; }

    [BsonElement("date")]
    [BsonSerializer(typeof(DateOnlySerializer))]
    public DateOnly Date { get; set; }

    [BsonElement("completedValue")]
    public int CompletedValue { get; set; }

    [BsonElement("status")]
    [BsonRepresentation(BsonType.String)]
    public CheckInStatus Status { get; set; }

    [BsonElement("obstacle")]
    public string? Obstacle { get; set; }

    [BsonElement("notes")]
    public string? Notes { get; set; }

    [BsonElement("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}