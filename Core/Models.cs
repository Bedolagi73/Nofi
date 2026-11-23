using System.Collections.Immutable;

namespace Nofi.Core;

public enum Channel { Email, Sms }
public enum ScheduleKind { OneTime, Daily, Weekly, Interval }
public enum TaskStatus { Waiting, Sending, Sent, WaitingForRetry, Error }
public enum DeliveryOutcome { Success, TransientError, PermanentError }
public enum MissedRunPolicy { SendOnce, SkipRecurring }

public sealed record ScheduleOptions(
    ScheduleKind Kind,
    string TimeZoneId,
    TimeOnly Time,
    DateOnly? Date,
    ImmutableArray<DayOfWeek> Days,
    int IntervalMinutes);

public sealed record TaskDefinition(string Recipient, string Text, Channel Channel, ScheduleOptions Schedule);

public sealed record NotificationTask(Guid Id, TaskDefinition Definition)
{
    public bool Enabled { get; init; } = true;
    public TaskStatus Status { get; init; } = TaskStatus.Waiting;
    public DateTimeOffset? ScheduledUtc { get; init; }
    public DateTimeOffset? RetryUtc { get; init; }
    public DateTimeOffset? LastRunUtc { get; init; }
    public int Attempts { get; init; }
    public bool Delayed { get; init; }
    public DateTimeOffset? NextRunUtc => Enabled ? RetryUtc ?? ScheduledUtc : null;
}

public sealed record JournalEntry(
    Guid Id,
    Guid TaskId,
    DateTimeOffset TimeUtc,
    DateTimeOffset ScheduledUtc,
    Channel Channel,
    string Recipient,
    string Message,
    int Attempt,
    DeliveryOutcome Outcome,
    string? Error,
    bool Delayed);

public sealed record RetryOptions(int MaxAttempts = 3, int InitialDelaySeconds = 30, double Multiplier = 2);

public sealed record FailureSimulationOptions(
    int FailurePercent = 0,
    int DelayMilliseconds = 0,
    int FailFirstAttempts = 0,
    string ForcedRecipient = "",
    bool ForcedFailureIsPermanent = true);

public sealed record AppSettings(
    RetryOptions Retry,
    FailureSimulationOptions Simulation,
    int PollMilliseconds,
    MissedRunPolicy MissedRuns)
{
    public static AppSettings Default { get; } = new(new(), new(), 1000, MissedRunPolicy.SendOnce);
}

public sealed record AppData(
    ImmutableDictionary<Guid, NotificationTask> Tasks,
    ImmutableArray<JournalEntry> Journal,
    AppSettings Settings)
{
    public static AppData Empty { get; } = new(
        ImmutableDictionary<Guid, NotificationTask>.Empty, [], AppSettings.Default);
}

public sealed record SchedulerSnapshot(AppData Data, bool IsReady, bool IsPaused, string? StorageMessage);

public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
