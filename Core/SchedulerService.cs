using System.Collections.Immutable;
using System.IO;
using Nofi.Core.Storage;

namespace Nofi.Core;

public sealed class SchedulerService : IAsyncDisposable
{
    private const int ParallelLimit = 5;

    private readonly ITaskRepository _repository;
    private readonly IDateTimeProvider _clock;
    private readonly NotificationDispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Dictionary<Guid, Task<DeliveryResult>> _sending = [];
    private readonly Task _loop;
    private SchedulerSnapshot _snapshot = new(AppData.Empty, false, false, null);
    private DateTimeOffset _missedThroughUtc = DateTimeOffset.MinValue;

    public SchedulerService(ITaskRepository repository, IDateTimeProvider clock, NotificationDispatcher dispatcher)
    {
        _repository = repository;
        _clock = clock;
        _dispatcher = dispatcher;
        _shutdownToken = _shutdown.Token;
        _loop = Task.Run(RunAsync);
    }

    public SchedulerSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public Task InitializeAsync() => RunExclusiveAsync(() =>
    {
        if (Snapshot.IsReady)
            return;

        try
        {
            var data = _repository.Open();
            var tasks = data.Tasks;
            foreach (var task in tasks.Values.Where(task => task.Status == TaskStatus.Sending))
            {
                // Зафиксированная попытка уже сменила бы этот статус в атомарном снимке.
                var status = task.Attempts == 0 ? TaskStatus.Waiting : TaskStatus.WaitingForRetry;
                tasks = tasks.SetItem(task.Id, task with { Status = status });
            }
            if (tasks != data.Tasks)
                data = _repository.Save(data with { Tasks = tasks }, []);

            _missedThroughUtc = _clock.UtcNow;
            Publish(new SchedulerSnapshot(data, true, false, _repository.Warning));
            Wake();
        }
        catch (Exception error) when (IsStorageError(error) || error is ArgumentException)
        {
            Publish(Snapshot with { StorageMessage = $"Хранилище недоступно. {error.Message}" });
        }
    });

    public Task SaveTaskAsync(Guid? id, TaskDefinition definition) => RunExclusiveAsync(() =>
    {
        var current = RequireData();
        var existing = id is { } taskId ? RequireEditableTask(current, taskId) : null;
        var now = _clock.UtcNow;
        var start = ValidateAndGetStart(definition, now);
        var task = new NotificationTask(existing?.Id ?? Guid.NewGuid(), definition)
        {
            Enabled = existing?.Enabled ?? true,
            ScheduledUtc = start,
            LastRunUtc = existing?.LastRunUtc
        };
        Save(current with { Tasks = current.Tasks.SetItem(task.Id, task) }, []);
        Wake();
    });

    public Task DeleteTaskAsync(Guid id) => RunExclusiveAsync(() =>
    {
        var current = RequireData();
        _ = RequireEditableTask(current, id);
        Save(current with { Tasks = current.Tasks.Remove(id) }, []);
    });

    public Task ToggleTaskAsync(Guid id) => RunExclusiveAsync(() =>
    {
        var current = RequireData();
        var task = RequireEditableTask(current, id);
        var now = _clock.UtcNow;
        var schedule = task.Definition.Schedule;

        NotificationTask updated;
        if (task.Enabled)
        {
            updated = task with
            {
                Enabled = false,
                Status = task.Status == TaskStatus.WaitingForRetry ? TaskStatus.Waiting : task.Status,
                RetryUtc = null,
                Attempts = 0,
                Delayed = false
            };
        }
        else
        {
            var scheduled = schedule.Kind == ScheduleKind.OneTime
                ? task.ScheduledUtc : ScheduleCalculator.FirstRun(schedule, now);
            updated = task with
            {
                Enabled = true,
                Status = scheduled is null ? task.Status : TaskStatus.Waiting,
                ScheduledUtc = scheduled,
                Delayed = schedule.Kind == ScheduleKind.OneTime && scheduled < now
            };
        }

        Save(current with { Tasks = current.Tasks.SetItem(id, updated) }, []);
        Wake();
    });

    public Task UpdateSettingsAsync(AppSettings settings) => RunExclusiveAsync(() =>
    {
        var current = RequireData();
        SettingsValidation.Validate(settings);
        var tasks = current.Tasks;
        if (settings.Retry.MaxAttempts < current.Settings.Retry.MaxAttempts)
        {
            var now = _clock.UtcNow;
            foreach (var task in tasks.Values.Where(task =>
                         task.Status == TaskStatus.WaitingForRetry && task.Attempts >= settings.Retry.MaxAttempts))
            {
                tasks = tasks.SetItem(task.Id, NotificationDispatcher.FinishOccurrence(task, now, delivered: false));
            }
        }
        Save(current with { Tasks = tasks, Settings = settings }, []);
        Wake();
    });

    public Task SetPausedAsync(bool paused) => RunExclusiveAsync(() =>
    {
        _ = RequireData();
        if (Snapshot.IsPaused && !paused)
            _missedThroughUtc = _clock.UtcNow;
        Publish(Snapshot with { IsPaused = paused });
        Wake();
    });

    private async Task RunAsync()
    {
        var cancellationToken = _shutdownToken;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (Snapshot.IsReady)
                        Tick(cancellationToken);
                }
                catch (Exception error) when (IsStorageError(error))
                {
                    Publish(Snapshot with { StorageMessage = $"Не удалось записать данные. Повторим автоматически. {error.Message}" });
                }
                finally
                {
                    _gate.Release();
                }

                await _wake.WaitAsync(Snapshot.Data.Settings.PollMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Завершение приложения отменяет ещё не закончившиеся имитации.
        }
    }

    private void Tick(CancellationToken cancellationToken)
    {
        _repository.FlushJournal();
        var state = Snapshot.Data;
        var now = _clock.UtcNow;
        if (_sending.Count == 0 &&
            (Snapshot.IsPaused || !state.Tasks.Values.Any(task => task.NextRunUtc <= now)))
        {
            RefreshStorageWarning();
            return;
        }

        var tasks = state.Tasks.ToBuilder();
        var entries = ImmutableArray.CreateBuilder<JournalEntry>();
        var finished = new List<Guid>();
        CollectFinished(tasks, entries, finished);

        var claimed = new List<NotificationTask>();
        var slots = ParallelLimit - _sending.Count + finished.Count;
        if (!Snapshot.IsPaused && slots > 0 && !cancellationToken.IsCancellationRequested)
        {
            var due = tasks.Values.Where(task =>
                    task.Status != TaskStatus.Sending && task.NextRunUtc <= now)
                .OrderBy(task => task.NextRunUtc).ThenBy(task => task.Id).ToArray();
            foreach (var task in due)
            {
                var elapsed = task.Attempts == 0 && task.ScheduledUtc < _missedThroughUtc;
                var recurring = task.Definition.Schedule.Kind != ScheduleKind.OneTime;
                if (elapsed && recurring && state.Settings.MissedRuns == MissedRunPolicy.SkipRecurring)
                {
                    tasks[task.Id] = NotificationDispatcher.FinishOccurrence(task, now, delivered: true);
                    continue;
                }
                if (task.Attempts >= state.Settings.Retry.MaxAttempts)
                {
                    tasks[task.Id] = NotificationDispatcher.FinishOccurrence(task, now, delivered: false);
                    continue;
                }
                if (claimed.Count == slots)
                    break;

                var sending = task with { Status = TaskStatus.Sending, Delayed = task.Delayed || elapsed };
                tasks[task.Id] = sending;
                claimed.Add(sending);
            }
        }

        var updated = tasks.ToImmutable();
        if (updated == state.Tasks)
        {
            RefreshStorageWarning();
            return;
        }

        Save(state with { Tasks = updated }, entries.ToImmutable());
        foreach (var id in finished)
            _sending.Remove(id);
        foreach (var task in claimed)
        {
            var delivery = _dispatcher.DispatchAsync(task, state.Settings.Simulation, cancellationToken);
            _sending.Add(task.Id, delivery);
            _ = delivery.ContinueWith(static (_, scheduler) => ((SchedulerService)scheduler!).Wake(),
                this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void CollectFinished(
        ImmutableDictionary<Guid, NotificationTask>.Builder tasks,
        ImmutableArray<JournalEntry>.Builder entries, List<Guid> finished)
    {
        foreach (var (id, delivery) in _sending)
        {
            if (!delivery.IsCompletedSuccessfully)
                continue;

            var result = delivery.Result;
            tasks[id] = NotificationDispatcher.Complete(tasks[id], result, Snapshot.Data.Settings.Retry);
            entries.Add(result.Entry);
            finished.Add(id);
        }
    }

    private static DateTimeOffset ValidateAndGetStart(TaskDefinition definition, DateTimeOffset now)
    {
        if (definition.Recipient.Length > 254 || !Enum.IsDefined(definition.Channel) ||
            !(definition.Channel == Channel.Email
                ? RecipientValidation.IsValidEmail(definition.Recipient)
                : RecipientValidation.IsValidPhone(definition.Recipient)))
            throw new ArgumentException("Укажите корректный email или номер телефона для выбранного канала.");
        if (string.IsNullOrWhiteSpace(definition.Text) || definition.Text.Length > 2000)
            throw new ArgumentException("Введите текст уведомления длиной от 1 до 2000 символов.");

        ScheduleCalculator.Validate(definition.Schedule);
        var start = ScheduleCalculator.FirstRun(definition.Schedule, now);
        if (start <= now)
            throw new ArgumentException("Время однократной отправки должно быть в будущем.");
        return start;
    }

    private async Task RunExclusiveAsync(Action action)
    {
        await Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _shutdownToken.ThrowIfCancellationRequested();
                action();
            }
            finally
            {
                _gate.Release();
            }
        }).ConfigureAwait(false);
    }

    private AppData RequireData() => Snapshot.IsReady
        ? Snapshot.Data : throw new InvalidOperationException("Сначала подключите хранилище.");

    private static NotificationTask RequireEditableTask(AppData state, Guid id)
    {
        if (!state.Tasks.TryGetValue(id, out var task))
            throw new InvalidOperationException("Задача уже удалена.");
        if (task.Status == TaskStatus.Sending)
            throw new InvalidOperationException("Дождитесь завершения текущей попытки.");
        return task;
    }

    private void Save(AppData data, ImmutableArray<JournalEntry> entries)
    {
        var committed = _repository.Save(data, entries);
        Publish(Snapshot with { Data = committed, StorageMessage = _repository.Warning });
    }

    private void RefreshStorageWarning()
    {
        if (Snapshot.StorageMessage != _repository.Warning)
            Publish(Snapshot with { StorageMessage = _repository.Warning });
    }

    private void Publish(SchedulerSnapshot snapshot)
    {
        if (snapshot != Snapshot)
            Volatile.Write(ref _snapshot, snapshot);
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException;

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _loop.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try
            {
                await Task.WhenAll(_sending.Values).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Незавершённые записи будут восстановлены как ожидающие при следующем старте.
            }

            var state = Snapshot.Data;
            var tasks = state.Tasks.ToBuilder();
            var entries = ImmutableArray.CreateBuilder<JournalEntry>();
            CollectFinished(tasks, entries, []);
            if (entries.Count > 0)
                Save(state with { Tasks = tasks.ToImmutable() }, entries.ToImmutable());
        }
        finally
        {
            _repository.Dispose();
            _gate.Release();
            _shutdown.Dispose();
        }
    }
}
