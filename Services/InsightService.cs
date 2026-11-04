using ApiApp.Data;
using ApiApp.Models;
using MongoDB.Driver;

namespace ApiApp.Services;

public sealed class InsightService(MongoDbContext db)
{
    public async Task<IReadOnlyList<HabitInsightDto>> GetInsightsAsync(Guid userId, int days, CancellationToken cancellationToken)
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-days + 1));
        var habits = await db.Habits.Find(h => h.UserId == userId && h.IsActive).SortBy(h => h.CreatedAt).ToListAsync(cancellationToken);
        var checkIns = await db.CheckIns.Find(c => c.HabitId != Guid.Empty && c.Date >= from).ToListAsync(cancellationToken);

        // Filter checkIns to only those belonging to user's habits
        var userHabitIds = habits.Select(h => h.Id).ToHashSet();
        var userCheckIns = checkIns.Where(c => userHabitIds.Contains(c.HabitId)).ToList();

        return habits.Select(h =>
        {
            var items = userCheckIns.Where(c => c.HabitId == h.Id).ToList();
            var obstacle = items.Where(c => !string.IsNullOrWhiteSpace(c.Obstacle))
                .GroupBy(c => c.Obstacle!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            var missed = items.Count(c => c.Status == CheckInStatus.Missed);
            var pattern = missed >= 3 && missed > items.Count / 2 ? $"{h.Name} is being missed more often than completed." : null;
            var experiment = missed >= 3 ? $"Try the {h.MinimumValue} {h.Unit} minimum for one week." : "Keep the current target and notice what helps it happen.";
            return new HabitInsightDto(h.Id, h.Name, days, items.Count(c => c.Status == CheckInStatus.Completed), items.Count(c => c.Status == CheckInStatus.Minimum), missed,
                Math.Round(items.Count(c => c.Status != CheckInStatus.Missed) / (double)days, 2), obstacle?.Key, pattern, experiment);
        }).ToList();
    }

    public async Task<WeeklyReflectionDto> GetReflectionAsync(Guid userId, DateOnly weekStart, CancellationToken cancellationToken)
    {
        var weekEnd = weekStart.AddDays(6);
        var habits = await db.Habits.Find(h => h.UserId == userId && h.IsActive).ToListAsync(cancellationToken);
        var checkIns = await db.CheckIns.Find(c => c.HabitId != Guid.Empty && c.Date >= weekStart && c.Date <= weekEnd).ToListAsync(cancellationToken);

        // Filter checkIns to only those belonging to user's habits
        var userHabitIds = habits.Select(h => h.Id).ToHashSet();
        var userCheckIns = checkIns.Where(c => userHabitIds.Contains(c.HabitId)).ToList();

        var completed = userCheckIns.Count(c => c.Status == CheckInStatus.Completed);
        var minimum = userCheckIns.Count(c => c.Status == CheckInStatus.Minimum);
        var missed = Math.Max(0, habits.Count * 7 - userCheckIns.Count);
        var obstacle = userCheckIns.Where(c => !string.IsNullOrWhiteSpace(c.Obstacle)).GroupBy(c => c.Obstacle!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        var user = await db.Users.Find(u => u.Id == userId).FirstOrDefaultAsync(cancellationToken);
        var observations = new List<string> { $"You logged {userCheckIns.Count} of {habits.Count * 7} planned check-ins." };
        if (habits.Count > 0) observations.Add($"Your active habits are: {string.Join(", ", habits.Select(h => h.Name))}.");
        if (completed > 0) observations.Add($"You reached the full target {completed} time{(completed == 1 ? "" : "s") }.");
        if (minimum > 0) observations.Add($"Your minimum version kept {minimum} session{(minimum == 1 ? "" : "s")} alive.");
        if (obstacle is not null) observations.Add($"Your most common obstacle was {obstacle}.");
        var experiment = minimum > completed ? "Run a minimum-first week: commit to the smallest version before aiming higher." : "Choose one consistent cue for your hardest habit and test it for seven days.";
        var goalConnection = string.IsNullOrWhiteSpace(user?.Goal)
            ? "Add a personal goal so each habit can be connected to what matters to you."
            : $"Each small session is practice toward your goal: {user.Goal.Trim()}.";
        return new WeeklyReflectionDto(weekStart, weekEnd, habits.Count * 7, completed, minimum, missed,
            completed + minimum >= Math.Max(1, habits.Count * 4) ? "Your consistency is building." : "This week gave us useful evidence.", observations, experiment, user?.Goal, goalConnection);
    }
}