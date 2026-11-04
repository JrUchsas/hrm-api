using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using System;

namespace ApiApp.Models;

public sealed class DateOnlySerializer : SerializerBase<DateOnly>
{
    public override DateOnly Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var dateTime = context.Reader.ReadDateTime();
        return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(dateTime).UtcDateTime);
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateOnly value)
    {
        var dateTime = value.ToDateTime(TimeOnly.MinValue);
        var dateTimeOffset = new DateTimeOffset(dateTime, TimeSpan.Zero);
        context.Writer.WriteDateTime(dateTimeOffset.ToUnixTimeMilliseconds());
    }
}