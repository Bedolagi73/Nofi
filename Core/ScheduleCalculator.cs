namespace Nofi.Core;

public static class ScheduleCalculator
{
    public static void Validate(ScheduleOptions schedule)
    {
        if (!Enum.IsDefined(schedule.Kind) || string.IsNullOrWhiteSpace(schedule.TimeZoneId))
            throw new ArgumentException("Выберите тип расписания и часовой пояс.");

        _ = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        if (schedule.Kind == ScheduleKind.OneTime && schedule.Date is null)
            throw new ArgumentException("Укажите дату однократной отправки.");
        if (schedule.Kind == ScheduleKind.Weekly &&
            (schedule.Days.IsDefaultOrEmpty || schedule.Days.Any(day => !Enum.IsDefined(day))))
            throw new ArgumentException("Выберите хотя бы один день недели.");
        if (schedule.Kind == ScheduleKind.Interval && schedule.IntervalMinutes is < 1 or > 525600)
            throw new ArgumentException("Интервал должен быть от 1 до 525 600 минут.");
    }

    public static DateTimeOffset FirstRun(ScheduleOptions schedule, DateTimeOffset now)
    {
        if (schedule.Kind == ScheduleKind.OneTime)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            return ToUtc(schedule.Date!.Value, schedule.Time, zone);
        }

        return NextRun(schedule, now, now);
    }

    public static DateTimeOffset NextRun(
        ScheduleOptions schedule, DateTimeOffset after, DateTimeOffset previous)
    {
        if (schedule.Kind == ScheduleKind.Interval)
        {
            var period = TimeSpan.FromMinutes(schedule.IntervalMinutes);
            var periods = Math.Max(0, (after - previous).Ticks / period.Ticks) + 1;
            return previous.AddTicks(periods * period.Ticks);
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        while (true)
        {
            if (schedule.Kind == ScheduleKind.Daily || schedule.Days.Contains(date.DayOfWeek))
            {
                var candidate = ToUtc(date, schedule.Time, zone);
                if (candidate > after)
                    return candidate;
            }
            date = date.AddDays(1);
        }
    }

    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time);
        // В дни перевода часов берём первую существующую минуту, а при повторе — первое вхождение.
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(1);

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
