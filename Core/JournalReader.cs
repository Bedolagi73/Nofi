namespace Nofi.Core;

public static class JournalReader
{
    public static IEnumerable<JournalEntry> Filter(
        IEnumerable<JournalEntry> journal,
        Guid? taskId,
        DeliveryOutcome? outcome,
        DateOnly? from,
        DateOnly? to)
    {
        return journal.Where(entry =>
        {
            var day = DateOnly.FromDateTime(entry.TimeUtc.LocalDateTime);
            return (taskId is null || entry.TaskId == taskId) &&
                   (outcome is null || entry.Outcome == outcome) &&
                   (from is null || day >= from) &&
                   (to is null || day <= to);
        }).OrderByDescending(entry => entry.TimeUtc);
    }
}
