using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Nofi.Core;
using TaskStatus = Nofi.Core.TaskStatus;

namespace Nofi.UI;

public partial class MainWindow : Window
{
    private readonly SchedulerService _scheduler;
    private readonly DispatcherTimer _refreshTimer;
    private SchedulerSnapshot? _displayed;
    private AppData? _journalData;
    private bool _initialized;
    private bool _refreshingFilters;
    private bool _busy;
    private bool _closeRequested;
    private bool _shuttingDown;
    private bool _closed;

    public MainWindow(SchedulerService scheduler)
    {
        _scheduler = scheduler;
        InitializeComponent();
        OutcomeFilter.ItemsSource = new Choice<DeliveryOutcome?>[]
        {
            new("Любой результат", null),
            new("Успех", DeliveryOutcome.Success),
            new("Временная ошибка", DeliveryOutcome.TransientError),
            new("Постоянная ошибка", DeliveryOutcome.PermanentError)
        };
        OutcomeFilter.SelectedIndex = 0;
        MissedRuns.ItemsSource = new Choice<MissedRunPolicy>[]
        {
            new("Отправить один пропущенный запуск", MissedRunPolicy.SendOnce),
            new("Продолжить со следующего запуска", MissedRunPolicy.SkipRecurring)
        };
        ReadSettingsIntoForm(AppSettings.Default);
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _initialized = true;
        Refresh();
        UpdateCommands();
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= WindowLoaded;
        _refreshTimer.Start();
        await RunCommandAsync(_scheduler.InitializeAsync);
    }

    private void Refresh()
    {
        var snapshot = _scheduler.Snapshot;
        var previous = _displayed;
        if (ReferenceEquals(previous, snapshot))
            return;
        _displayed = snapshot;

        StorageBanner.Visibility = VisibleWhen(snapshot.StorageMessage is not null);
        StorageMessage.Text = snapshot.StorageMessage;
        ReconnectButton.Visibility = VisibleWhen(!snapshot.IsReady);
        SchedulerState.Text = snapshot.IsReady
            ? (snapshot.IsPaused ? "Планировщик на паузе" : "Планировщик работает")
            : (snapshot.StorageMessage is null ? "Загрузка хранилища" : "Хранилище недоступно");
        SchedulerDot.Fill = Brush(snapshot.IsReady && !snapshot.IsPaused ? "GoodBrush" : "WarnBrush");
        PauseButton.Content = snapshot.IsPaused ? "Возобновить работу" : "Пауза планировщика";
        if (previous?.IsReady != true && snapshot.IsReady)
            ReadSettingsIntoForm(snapshot.Data.Settings);

        if (previous?.Data.Tasks != snapshot.Data.Tasks)
            RefreshTasks(snapshot.Data);
        if (Navigation.SelectedIndex == 1 && !ReferenceEquals(_journalData, snapshot.Data))
            RefreshJournal(snapshot.Data);
        UpdateCommands();
    }

    private void RefreshTasks(AppData data)
    {
        var selected = (TaskGrid.SelectedItem as TaskRow)?.Task.Id;
        var rows = data.Tasks.Values
            .OrderBy(task => task.NextRunUtc is null).ThenBy(task => task.NextRunUtc)
            .ThenBy(task => task.Definition.Recipient, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(task => task.Id)
            .Select(task => new TaskRow(task)).ToArray();
        TaskGrid.ItemsSource = rows;
        TaskGrid.SelectedItem = selected is { } id ? rows.FirstOrDefault(row => row.Task.Id == id) : null;
        TaskEmpty.Visibility = VisibleWhen(rows.Length == 0);

        var active = rows.Count(row => row.Task.NextRunUtc is not null);
        TotalTasks.Text = rows.Length.ToString("N0", DisplayFormatting.Russian);
        ActiveTasks.Text = active.ToString("N0", DisplayFormatting.Russian);
        RetryTasks.Text = rows.Count(row => row.Task.Status == TaskStatus.WaitingForRetry).ToString("N0", DisplayFormatting.Russian);
        ActiveSummary.Text = $"Активных задач: {ActiveTasks.Text}";
    }

    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
            return;

        ScheduleView.Visibility = VisibleWhen(Navigation.SelectedIndex == 0);
        JournalView.Visibility = VisibleWhen(Navigation.SelectedIndex == 1);
        SettingsView.Visibility = VisibleWhen(Navigation.SelectedIndex == 2);
        if (Navigation.SelectedIndex == 1 && !ReferenceEquals(_journalData, _scheduler.Snapshot.Data))
            RefreshJournal(_scheduler.Snapshot.Data);
    }

    private void RefreshJournal(AppData data)
    {
        var selection = TaskFilter.SelectedValue as Guid?;
        var labels = new Dictionary<Guid, string>();
        foreach (var entry in data.Journal.Reverse())
            labels.TryAdd(entry.TaskId, entry.Recipient);
        foreach (var task in data.Tasks.Values)
            labels[task.Id] = task.Definition.Recipient;

        _refreshingFilters = true;
        TaskFilter.ItemsSource = new[] { new Choice<Guid?>("Все задачи", null) }
            .Concat(labels.OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(pair => pair.Key)
                .Select(pair => new Choice<Guid?>($"{pair.Value} · {pair.Key.ToString("N")[..8]}", pair.Key)))
            .ToArray();
        TaskFilter.SelectedValue = selection;
        if (TaskFilter.SelectedIndex < 0)
            TaskFilter.SelectedIndex = 0;
        _refreshingFilters = false;
        _journalData = data;
        ApplyJournalFilters();
    }

    private void ApplyJournalFilters()
    {
        var start = StartFilter.SelectedDate is { } from ? DateOnly.FromDateTime(from) : (DateOnly?)null;
        var end = EndFilter.SelectedDate is { } to ? DateOnly.FromDateTime(to) : (DateOnly?)null;
        var selected = (JournalGrid.SelectedItem as JournalRow)?.Entry.Id;
        var rows = JournalReader.Filter(_scheduler.Snapshot.Data.Journal, TaskFilter.SelectedValue as Guid?,
            OutcomeFilter.SelectedValue as DeliveryOutcome?, start, end).Select(entry => new JournalRow(entry)).ToArray();
        JournalGrid.ItemsSource = rows;
        JournalGrid.SelectedItem = selected is { } id ? rows.FirstOrDefault(row => row.Entry.Id == id) : null;
        JournalCount.Text = $"Записей: {rows.Length:N0}";
        JournalEmpty.Visibility = VisibleWhen(rows.Length == 0);
        JournalEmpty.Text = start > end
            ? "Дата окончания должна быть не раньше даты начала."
            : (_scheduler.Snapshot.Data.Journal.IsEmpty ? "Попыток пока нет." : "По выбранным фильтрам записей нет.");
    }

    private void JournalFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_refreshingFilters)
            ApplyJournalFilters();
    }

    private void JournalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
            return;
        var row = JournalGrid.SelectedItem as JournalRow;
        JournalDetails.Visibility = VisibleWhen(row is not null);
        JournalDetailText.Text = row is null ? "" : $"Задача {row.Entry.TaskId}\n" +
            $"По расписанию: {DisplayFormatting.DateTime(row.Entry.ScheduledUtc)}\n" +
            $"Получатель: {row.Recipient}\nСообщение: {row.Entry.Message}\n{row.Details}";
    }

    private void ResetFiltersClicked(object sender, RoutedEventArgs e)
    {
        _refreshingFilters = true;
        TaskFilter.SelectedIndex = 0;
        OutcomeFilter.SelectedIndex = 0;
        StartFilter.SelectedDate = null;
        EndFilter.SelectedDate = null;
        _refreshingFilters = false;
        ApplyJournalFilters();
    }

    private void TaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
            UpdateCommands();
    }

    private void UpdateCommands()
    {
        var ready = _scheduler.Snapshot.IsReady && !_busy && !_shuttingDown;
        var task = (TaskGrid.SelectedItem as TaskRow)?.Task;
        var editable = ready && task is not null && task.Status != TaskStatus.Sending;
        AddButton.IsEnabled = PauseButton.IsEnabled = SaveSettingsButton.IsEnabled = ready;
        EditButton.IsEnabled = ToggleButton.IsEnabled = DeleteButton.IsEnabled = editable;
        ReconnectButton.IsEnabled = !_busy && !_shuttingDown;
        ToggleButton.Content = task?.Enabled == false ? "Включить" : "Выключить";
    }

    private void AddClicked(object sender, RoutedEventArgs e) => EditTask(null);

    private void EditClicked(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is TaskRow row)
            EditTask(row.Task);
    }

    private void EditTask(NotificationTask? task)
    {
        var editor = new TaskEditor(task, definition => _scheduler.SaveTaskAsync(task?.Id, definition)) { Owner = this };
        if (editor.ShowDialog() == true)
            Refresh();
    }

    private async void ToggleClicked(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is TaskRow row)
            await RunCommandAsync(() => _scheduler.ToggleTaskAsync(row.Task.Id));
    }

    private async void DeleteClicked(object sender, RoutedEventArgs e)
    {
        if (TaskGrid.SelectedItem is not TaskRow row)
            return;
        var answer = MessageBox.Show(this, $"Удалить задачу для «{row.Recipient}»? Записи в журнале сохранятся.",
            "Удаление задачи", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
            await RunCommandAsync(() => _scheduler.DeleteTaskAsync(row.Task.Id));
    }

    private async void PauseClicked(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(() => _scheduler.SetPausedAsync(!_scheduler.Snapshot.IsPaused));

    private async void ReconnectClicked(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(_scheduler.InitializeAsync);

    private void ReadSettingsIntoForm(AppSettings settings)
    {
        MaxAttempts.Text = settings.Retry.MaxAttempts.ToString(CultureInfo.InvariantCulture);
        RetryDelay.Text = settings.Retry.InitialDelaySeconds.ToString(CultureInfo.InvariantCulture);
        RetryFactor.Text = settings.Retry.Multiplier.ToString(DisplayFormatting.Russian);
        FailurePercent.Text = settings.Simulation.FailurePercent.ToString(CultureInfo.InvariantCulture);
        SimulationDelay.Text = settings.Simulation.DelayMilliseconds.ToString(CultureInfo.InvariantCulture);
        FirstFailures.Text = settings.Simulation.FailFirstAttempts.ToString(CultureInfo.InvariantCulture);
        ForcedRecipient.Text = settings.Simulation.ForcedRecipient;
        PermanentFailure.IsChecked = settings.Simulation.ForcedFailureIsPermanent;
        PollInterval.Text = settings.PollMilliseconds.ToString(CultureInfo.InvariantCulture);
        MissedRuns.SelectedValue = settings.MissedRuns;
    }

    private async void SaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var max = ReadInt(MaxAttempts, "лимита попыток");
            var delay = ReadInt(RetryDelay, "базовой задержки");
            var factorInput = RetryFactor.Text.Trim().Replace(',', '.');
            if (!double.TryParse(factorInput, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var factor))
            {
                RetryFactor.Focus();
                throw new ArgumentException("Введите число для коэффициента роста.");
            }

            var simulation = new FailureSimulationOptions(
                ReadInt(FailurePercent, "вероятности сбоя"),
                ReadInt(SimulationDelay, "задержки имитации"),
                ReadInt(FirstFailures, "количества первых сбоев"),
                ForcedRecipient.Text.Trim(),
                PermanentFailure.IsChecked == true);
            var settings = new AppSettings(new(max, delay, factor), simulation,
                ReadInt(PollInterval, "интервала опроса"), (MissedRunPolicy)MissedRuns.SelectedValue);

            await RunCommandAsync(() => _scheduler.UpdateSettingsAsync(settings), inline: true);
        }
        catch (ArgumentException error)
        {
            ShowSettingsFeedback(error.Message, isError: true);
        }
    }

    private static int ReadInt(TextBox field, string name)
    {
        if (int.TryParse(field.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        field.Focus();
        throw new ArgumentException($"Введите целое число для поля {name}.");
    }

    private async Task RunCommandAsync(Func<Task> command, bool inline = false)
    {
        _busy = true;
        UpdateCommands();
        try
        {
            await command();
            if (inline)
                ShowSettingsFeedback("Настройки сохранены.", isError: false);
        }
        catch (OperationCanceledException) when (_shuttingDown) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (inline)
                ShowSettingsFeedback(error.Message, isError: true);
            else
                MessageBox.Show(this, error.Message, "Nofi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            Refresh();
            UpdateCommands();
            if (_closeRequested)
                Close();
        }
    }

    private void ShowSettingsFeedback(string message, bool isError)
    {
        SettingsFeedback.Text = message;
        SettingsFeedback.Foreground = Brush(isError ? "DangerBrush" : "GoodBrush");
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed)
            return;
        e.Cancel = true;
        if (_shuttingDown)
            return;
        if (_busy)
        {
            _closeRequested = true;
            return;
        }

        _shuttingDown = true;
        _refreshTimer.Stop();
        UpdateCommands();
        SchedulerState.Text = "Завершение работы";
        try
        {
            await _scheduler.DisposeAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Не удалось записать результаты последних попыток. {error.Message}",
                "Nofi", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _closed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;

    private Brush Brush(string key) => (Brush)FindResource(key);
}
