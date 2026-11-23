using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nofi.Core.Storage;

public interface ITaskRepository : IDisposable
{
    string? Warning { get; }
    AppData Open();
    AppData Save(AppData state, ImmutableArray<JournalEntry> entries);
    void FlushJournal();
}

public sealed class JsonTaskRepository(string directory) : ITaskRepository
{
    private const int JournalLimit = 10_000;
    private const int JournalAfterRotation = 9_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        IgnoreReadOnlyProperties = true
    };

    private readonly string _tasksPath = Path.Combine(directory, "tasks.json");
    private readonly string _journalPath = Path.Combine(directory, "journal.jsonl");
    private FileStream? _lock;
    private ImmutableArray<JournalEntry> _diskJournal = [];
    private ImmutableArray<JournalEntry> _pending = [];
    private HashSet<Guid> _diskEntryIds = [];
    private bool _reloadJournal;
    private string? _repairNote;

    public string? Warning { get; private set; }

    public AppData Open()
    {
        Dispose();
        _repairNote = null;
        Warning = null;
        Directory.CreateDirectory(directory);
        _lock = new FileStream(Path.Combine(directory, "storage.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        try
        {
            var stored = ReadTasks();
            _pending = stored.PendingJournal;
            _reloadJournal = true;
            ReloadJournal();
            TryFlushAfterCommit();
            return new AppData(stored.Tasks.ToImmutableDictionary(task => task.Id), MergeJournal(_diskJournal, PendingOnDisk()), stored.Settings);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public AppData Save(AppData state, ImmutableArray<JournalEntry> entries)
    {
        FlushJournal();
        var stored = new StoredData(1, state.Tasks.Values.ToImmutableArray(), state.Settings, entries);
        // Снимок фиксирует результат попытки и данные журнала одной заменой файла.
        WriteAtomically(_tasksPath, stream => JsonSerializer.Serialize(stream, stored, JsonOptions));
        _pending = entries;
        TryFlushAfterCommit();
        return state with { Journal = MergeJournal(_diskJournal, PendingOnDisk()) };
    }

    public void FlushJournal()
    {
        if (_reloadJournal)
            ReloadJournal();
        if (_pending.IsEmpty)
            return;

        var missing = PendingOnDisk();
        if (!missing.IsEmpty)
        {
            var updated = MergeJournal(_diskJournal, missing);
            try
            {
                if (_diskJournal.Length + missing.Length > JournalLimit)
                {
                    WriteAtomically(_journalPath, stream => WriteEntries(stream, updated));
                    _diskEntryIds = updated.Select(entry => entry.Id).ToHashSet();
                }
                else
                {
                    using var file = new FileStream(_journalPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    WriteEntries(file, missing);
                    file.Flush(flushToDisk: true);
                    _diskEntryIds.UnionWith(missing.Select(entry => entry.Id));
                }
                _diskJournal = updated;
            }
            catch
            {
                // Запись могла оборваться в середине строки. Перед повтором перечитаем файл.
                _reloadJournal = true;
                throw;
            }
        }

        _pending = [];
        Warning = _repairNote;
    }

    private void TryFlushAfterCommit()
    {
        try
        {
            FlushJournal();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Warning = $"Журнал сохранён в снимке и будет дозаписан при восстановлении доступа. {error.Message}";
        }
    }

    private StoredData ReadTasks()
    {
        var empty = new StoredData(1, [], AppSettings.Default, []);
        try
        {
            using var file = File.OpenRead(_tasksPath);
            var stored = JsonSerializer.Deserialize<StoredData>(file, JsonOptions)
                ?? throw new JsonException("Пустой снимок.");
            if (stored.Version > 1)
                throw new IOException("Файл задач создан более новой версией Nofi.");
            Validate(stored);
            return stored;
        }
        catch (FileNotFoundException)
        {
            return empty;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            NoteRepair($"Файл задач повреждён. Исходник сохранён в {BackupBrokenFile(_tasksPath)}");
            WriteAtomically(_tasksPath, stream => JsonSerializer.Serialize(stream, empty, JsonOptions));
            return empty;
        }
    }

    private static void Validate(StoredData stored)
    {
        if (stored.Version != 1 || stored.Tasks.IsDefault || stored.PendingJournal.IsDefault ||
            stored.Settings is null || stored.PendingJournal.Any(entry => !IsValidEntry(entry)))
            throw new JsonException("Некорректная структура снимка.");

        SettingsValidation.Validate(stored.Settings);
        var ids = new HashSet<Guid>();
        foreach (var task in stored.Tasks)
        {
            if (task is null || task.Id == Guid.Empty || !ids.Add(task.Id) ||
                !Enum.IsDefined(task.Status) || task.Attempts is < 0 or > 50 ||
                (task.Status == TaskStatus.Sending &&
                    (!task.Enabled || task.ScheduledUtc is null || (task.Attempts > 0 && task.RetryUtc is null))) ||
                (task.Status == TaskStatus.WaitingForRetry &&
                    (!task.Enabled || task.ScheduledUtc is null || task.RetryUtc is null || task.Attempts == 0)) ||
                task.Definition is null || task.Definition.Schedule is null || task.Definition.Recipient is null ||
                task.Definition.Text is null || !Enum.IsDefined(task.Definition.Channel))
                throw new JsonException("Некорректная запись задачи.");

            ScheduleCalculator.Validate(task.Definition.Schedule);
        }
    }

    private static bool IsValidEntry(JournalEntry? entry) =>
        entry is not null && entry.Id != Guid.Empty && entry.TaskId != Guid.Empty && entry.Attempt > 0 &&
        entry.Recipient is not null && entry.Message is not null &&
        Enum.IsDefined(entry.Channel) && Enum.IsDefined(entry.Outcome);

    private void ReloadJournal()
    {
        var entries = new Queue<JournalEntry>(JournalLimit);
        var ids = new HashSet<Guid>();
        var repair = false;
        try
        {
            using var file = new FileStream(_journalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 0)
            {
                file.Seek(-1, SeekOrigin.End);
                repair = file.ReadByte() != '\n';
                file.Position = 0;
            }

            using var reader = new StreamReader(file);
            while (reader.ReadLine() is { } line)
            {
                JournalEntry? entry = null;
                try
                {
                    entry = JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    // Остальные строки остаются доступны даже при повреждении одной записи.
                }

                if (!IsValidEntry(entry) || !ids.Add(entry!.Id))
                {
                    repair = true;
                    continue;
                }
                if (entries.Count == JournalLimit)
                {
                    ids.Remove(entries.Dequeue().Id);
                    repair = true;
                }
                entries.Enqueue(entry);
            }
        }
        catch (FileNotFoundException)
        {
            // При первом запуске файл журнала появится после первой попытки.
        }

        var recovered = entries.ToImmutableArray();
        if (repair)
        {
            NoteRepair($"Журнал восстановлен. Исходник сохранён в {BackupBrokenFile(_journalPath)}");
            WriteAtomically(_journalPath, stream => WriteEntries(stream, recovered));
        }
        _diskJournal = recovered;
        _diskEntryIds = ids;
        _reloadJournal = false;
        Warning = _repairNote;
    }

    private ImmutableArray<JournalEntry> PendingOnDisk() =>
        _pending.Where(entry => !_diskEntryIds.Contains(entry.Id)).ToImmutableArray();

    private static ImmutableArray<JournalEntry> MergeJournal(
        ImmutableArray<JournalEntry> previous, ImmutableArray<JournalEntry> added)
    {
        var combined = previous.AddRange(added);
        return combined.Length > JournalLimit ? combined[^JournalAfterRotation..] : combined;
    }

    private static void WriteEntries(FileStream stream, ImmutableArray<JournalEntry> entries)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n" };
        foreach (var entry in entries)
            writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
    }

    private static void WriteAtomically(string path, Action<FileStream> write)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void NoteRepair(string message) =>
        _repairNote = _repairNote is null ? message : $"{_repairNote} {message}";

    private static string BackupBrokenFile(string path)
    {
        var backup = $"{path}.broken-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        File.Copy(path, backup);
        return Path.GetFileName(backup);
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }

    private sealed record StoredData(
        int Version,
        ImmutableArray<NotificationTask> Tasks,
        AppSettings Settings,
        ImmutableArray<JournalEntry> PendingJournal);
}
