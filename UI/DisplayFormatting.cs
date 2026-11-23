using System.Globalization;
using Nofi.Core;
using TaskStatus = Nofi.Core.TaskStatus;

namespace Nofi.UI;

internal sealed record Choice<T>(string Label, T Value);

internal static class DisplayFormatting
{
    public static CultureInfo Russian { get; } = CultureInfo.GetCultureInfo("ru-RU");

    public static string DateTime(DateTimeOffset? time) =>
        time?.ToLocalTime().ToString("dd.MM.yy HH:mm:ss", Russian) ?? "—";

    public static string ChannelName(Channel channel) => channel == Channel.Email ? "Email" : "SMS";

    public static string ScheduleName(ScheduleOptions schedule)
    {
        var time = schedule.Time.ToString("HH:mm", Russian);
        return schedule.Kind switch
        {
            ScheduleKind.OneTime => $"{schedule.Date:dd.MM.yyyy} в {time}",
            ScheduleKind.Daily => $"Ежедневно в {time}",
            ScheduleKind.Weekly => $"{string.Join(", ", OrderedDays.Where(schedule.Days.Contains).Select(DayName))} в {time}",
            _ => $"Интервал {schedule.IntervalMinutes} мин"
        };
    }

    public static DayOfWeek[] OrderedDays { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
         DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public static string DayName(DayOfWeek day) => Russian.DateTimeFormat.GetAbbreviatedDayName(day);

    public static string OutcomeName(DeliveryOutcome outcome) => outcome switch
    {
        DeliveryOutcome.Success => "Успех",
        DeliveryOutcome.TransientError => "Временная ошибка",
        _ => "Постоянная ошибка"
    };
}

internal sealed class TaskRow(NotificationTask task)
{
    public NotificationTask Task { get; } = task;
    public string Recipient => Task.Definition.Recipient;
    public string Channel => DisplayFormatting.ChannelName(Task.Definition.Channel);
    public string Schedule => DisplayFormatting.ScheduleName(Task.Definition.Schedule);
    public string ScheduleDetails =>
        $"{Schedule}\n{TimeZoneInfo.FindSystemTimeZoneById(Task.Definition.Schedule.TimeZoneId).DisplayName}";
    public string NextRun => DisplayFormatting.DateTime(Task.NextRunUtc);
    public string LastRun => DisplayFormatting.DateTime(Task.LastRunUtc);
    public string Status => !Task.Enabled ? "Выключена" : Task.Status switch
    {
        TaskStatus.Waiting => "Ожидает",
        TaskStatus.Sending => "Отправляется",
        TaskStatus.Sent => "Отправлено",
        TaskStatus.WaitingForRetry => "Ожидает повтора",
        _ => "Ошибка доставки"
    };
    public string Tone => !Task.Enabled ? "" : Task.Status switch
    {
        TaskStatus.Sending => "Info",
        TaskStatus.Sent => "Good",
        TaskStatus.WaitingForRetry => "Warning",
        TaskStatus.Error => "Danger",
        _ => ""
    };
}

internal sealed class JournalRow(JournalEntry entry)
{
    public JournalEntry Entry { get; } = entry;
    public string Time => DisplayFormatting.DateTime(Entry.TimeUtc);
    public string Recipient => Entry.Recipient;
    public string Channel => DisplayFormatting.ChannelName(Entry.Channel);
    public int Attempt => Entry.Attempt;
    public string Result => DisplayFormatting.OutcomeName(Entry.Outcome);
    public string Tone => Entry.Outcome switch
    {
        DeliveryOutcome.Success => "Good",
        DeliveryOutcome.TransientError => "Warning",
        _ => "Danger"
    };
    public string Details => (Entry.Error, Entry.Delayed) switch
    {
        (null, true) => "Отправлено с задержкой",
        (null, false) => "—",
        (string error, true) => $"Запуск с задержкой. {error}",
        (string error, false) => error
    };
}
