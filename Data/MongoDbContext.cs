using ApiApp.Models;
using MongoDB.Driver;

namespace ApiApp.Data;

public sealed class MongoDbContext
{
    private readonly IMongoDatabase _database;

    public MongoDbContext(IMongoClient client, IConfiguration configuration)
    {
        var databaseName = configuration["MongoDB:Database"] ?? "hrmdb";
        _database = client.GetDatabase(databaseName);
    }

    public IMongoCollection<User> Users => _database.GetCollection<User>("users");
    public IMongoCollection<Habit> Habits => _database.GetCollection<Habit>("habits");
    public IMongoCollection<HabitCheckIn> CheckIns => _database.GetCollection<HabitCheckIn>("checkins");

    public async Task CreateIndexesAsync()
    {
        // Users indexes
        var userIndexKeys = Builders<User>.IndexKeys.Ascending(u => u.Username);
        await Users.Indexes.CreateOneAsync(new CreateIndexModel<User>(userIndexKeys, new CreateIndexOptions { Unique = true }));

        // Habits indexes
        var habitUserIndexKeys = Builders<Habit>.IndexKeys.Ascending(h => h.UserId).Ascending(h => h.IsActive).Ascending(h => h.CreatedAt);
        await Habits.Indexes.CreateOneAsync(new CreateIndexModel<Habit>(habitUserIndexKeys));

        // CheckIns indexes
        var checkInIndexKeys = Builders<HabitCheckIn>.IndexKeys.Ascending(c => c.HabitId).Ascending(c => c.Date);
        await CheckIns.Indexes.CreateOneAsync(new CreateIndexModel<HabitCheckIn>(checkInIndexKeys, new CreateIndexOptions { Unique = true }));
    }
}