using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Nofi.Core;

namespace Nofi.UI;

public partial class TaskEditor : Window
{
    private readonly Func<TaskDefinition, Task> _save;
    private readonly Dictionary<DayOfWeek, CheckBox> _days = [];
    private bool _saving;

    public TaskEditor(NotificationTask? task, Func<TaskDefinition, Task> save)
    {
        _save = save;
        InitializeComponent();

        var definition = task?.Definition;
        var schedule = definition?.Schedule;
        var defaultTime = DateTime.Now.AddMinutes(5);
        EditorTitle.Text = Title = task is null ? "Новая задача" : "Редактирование задачи";
        ChannelInput.ItemsSource = new Choice<Channel>[] { new("Email", Channel.Email), new("SMS", Channel.Sms) };
        ScheduleInput.ItemsSource = new Choice<ScheduleKind>[]
        {
            new("Однократно", ScheduleKind.OneTime), new("Ежедневно", ScheduleKind.Daily),
            new("Еженедельно", ScheduleKind.Weekly), new("Через равные интервалы", ScheduleKind.Interval)
        };

        TimeZoneInput.ItemsSource = TimeZoneInfo.GetSystemTimeZones();
        TimeZoneInput.SelectedValue = schedule?.TimeZoneId ?? TimeZoneInfo.Local.Id;
        ChannelInput.SelectedValue = definition?.Channel ?? Channel.Email;
        ScheduleInput.SelectedValue = schedule?.Kind ?? ScheduleKind.OneTime;
        RecipientInput.Text = definition?.Recipient ?? "";
        MessageInput.Text = definition?.Text ?? "";
        TimeInput.Text = (schedule?.Time ?? TimeOnly.FromDateTime(defaultTime)).ToString("HH:mm", CultureInfo.InvariantCulture);
        DateInput.SelectedDate = schedule?.Date?.ToDateTime(TimeOnly.MinValue) ?? defaultTime.Date;
        IntervalInput.Text = (schedule is { IntervalMinutes: > 0 } ? schedule.IntervalMinutes : 5).ToString(CultureInfo.InvariantCulture);
        foreach (var day in DisplayFormatting.OrderedDays)
        {
            var check = new CheckBox
            {
                Content = DisplayFormatting.DayName(day),
                Margin = new Thickness(0, 0, 10, 0),
                IsChecked = schedule?.Days.Contains(day) == true
            };
            AutomationProperties.SetName(check, DisplayFormatting.Russian.DateTimeFormat.GetDayName(day));
            _days.Add(day, check);
            DaysInput.Children.Add(check);
        }
    }

    private void EditorLoaded(object sender, RoutedEventArgs e) => RecipientInput.Focus();

    private void ChannelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecipientHint is not null)
            RecipientHint.Text = ChannelInput.SelectedValue is Channel.Sms ? "Например, +79991234567" : "Например, example@domain.ru";
    }

    private void ScheduleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalPanel is null || ScheduleInput.SelectedValue is not ScheduleKind kind)
            return;

        TimePanel.Visibility = VisibleWhen(kind != ScheduleKind.Interval);
        DatePanel.Visibility = VisibleWhen(kind == ScheduleKind.OneTime);
        DaysPanel.Visibility = VisibleWhen(kind == ScheduleKind.Weekly);
        IntervalPanel.Visibility = VisibleWhen(kind == ScheduleKind.Interval);
    }

    private TaskDefinition ReadForm()
    {
        var channel = (Channel)ChannelInput.SelectedValue;
        var kind = (ScheduleKind)ScheduleInput.SelectedValue;
        var recipient = RecipientInput.Text.Trim();
        if (!(channel == Channel.Email ? RecipientValidation.IsValidEmail(recipient) : RecipientValidation.IsValidPhone(recipient)))
            throw Invalid(RecipientInput, channel == Channel.Email ? "Проверьте email-адрес." : "Проверьте номер телефона.");
        if (string.IsNullOrWhiteSpace(MessageInput.Text))
            throw Invalid(MessageInput, "Введите текст уведомления.");
        if (TimeZoneInput.SelectedValue is not string zone)
            throw Invalid(TimeZoneInput, "Выберите часовой пояс.");

        TimeOnly time = default;
        DateOnly? date = null;
        var interval = 0;
        ImmutableArray<DayOfWeek> days = [];
        if (kind == ScheduleKind.Interval)
        {
            if (!int.TryParse(IntervalInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out interval) ||
                interval is < 1 or > 525600)
                throw Invalid(IntervalInput, "Укажите интервал от 1 до 525 600 минут.");
        }
        else if (!TimeOnly.TryParseExact(TimeInput.Text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out time))
        {
            throw Invalid(TimeInput, "Укажите время в формате 09:30.");
        }

        if (kind == ScheduleKind.OneTime)
        {
            if (DateInput.SelectedDate is not { } localDate)
                throw Invalid(DateInput, "Укажите дату отправки.");
            date = DateOnly.FromDateTime(localDate);
        }
        if (kind == ScheduleKind.Weekly)
        {
            days = _days.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToImmutableArray();
            if (days.IsEmpty)
                throw Invalid(_days[DayOfWeek.Monday], "Выберите хотя бы один день недели.");
        }

        return new TaskDefinition(recipient, MessageInput.Text.Trim(), channel, new ScheduleOptions(kind, zone, time, date, days, interval));
    }

    private static ArgumentException Invalid(Control field, string message)
    {
        field.Focus();
        return new ArgumentException(message);
    }

    private async void SaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var definition = ReadForm();
            _saving = true;
            SaveButton.IsEnabled = CancelButton.IsEnabled = false;
            EditorError.Visibility = Visibility.Collapsed;
            await _save(definition);
            _saving = false;
            DialogResult = true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException
                   or UnauthorizedAccessException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            EditorError.Text = error.Message;
            EditorError.Visibility = Visibility.Visible;
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = CancelButton.IsEnabled = true;
        }
    }

    private void EditorClosing(object? sender, CancelEventArgs e) => e.Cancel = _saving;

    private static Visibility VisibleWhen(bool condition) => condition ? Visibility.Visible : Visibility.Collapsed;
}
