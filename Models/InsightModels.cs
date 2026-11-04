namespace HrmApi.Models;

public sealed record HabitTodayDto(
    Guid Id,
    string Name,
    string Unit,
    string Schedule,
    int TargetValue,
    int MinimumValue,
    HabitCheckIn? CheckIn);

public sealed record HabitInsightDto(
    Guid HabitId,
    string HabitName,
    int DaysObserved,
    int CompletedDays,
    int MinimumDays,
    int MissedDays,
    double CompletionRate,
    string? CommonObstacle,
    string? Pattern,
    string? SuggestedExperiment);

public sealed record WeeklyReflectionDto(
    DateOnly WeekStart,
    DateOnly WeekEnd,
    int PlannedCheckIns,
    int CompletedDays,
    int MinimumDays,
    int MissedDays,
    string Headline,
    IReadOnlyList<string> Observations,
    string Experiment,
    string? Goal = null,
    string? GoalConnection = null,
    string? AiSummary = null);