using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Nofi.Core;

public sealed class TransientException(string message) : Exception(message);
public sealed class PermanentException(string message) : Exception(message);

public sealed class RetryPolicy(RetryOptions options)
{
    public bool CanRetry(int attemptNumber, Exception error) =>
        error is TransientException && attemptNumber < options.MaxAttempts;

    public TimeSpan GetDelay(int attemptNumber)
    {
        var seconds = options.InitialDelaySeconds * Math.Pow(options.Multiplier, attemptNumber - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(365).TotalSeconds));
    }
}

public sealed record NotificationMessage(
    string Recipient, string Text, int Attempt, FailureSimulationOptions Simulation);

public interface INotificationSender
{
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}

public sealed class EmailSenderMock : INotificationSender
{
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        if (!RecipientValidation.IsValidEmail(message.Recipient))
            throw new PermanentException("Некорректный email-адрес.");
        return Simulation.SendAsync(message, cancellationToken);
    }
}

public sealed class SmsSenderMock : INotificationSender
{
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        if (!RecipientValidation.IsValidPhone(message.Recipient))
            throw new PermanentException("Некорректный номер телефона.");
        return Simulation.SendAsync(message, cancellationToken);
    }
}

public static partial class RecipientValidation
{
    public static bool IsValidEmail(string value) =>
        MailAddress.TryCreate(value, out var address) &&
        string.Equals(value, address.Address, StringComparison.OrdinalIgnoreCase) &&
        address.Host.Contains('.');

    public static bool IsValidPhone(string value) =>
        PhoneCharacters().IsMatch(value) && value.Count(char.IsAsciiDigit) is >= 7 and <= 15;

    [GeneratedRegex(@"^\+?[0-9][0-9 ()\-]*$")]
    private static partial Regex PhoneCharacters();
}

internal static class Simulation
{
    public static async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var options = message.Simulation;
        await Task.Delay(options.DelayMilliseconds, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.Equals(message.Recipient, options.ForcedRecipient, StringComparison.OrdinalIgnoreCase))
        {
            if (options.ForcedFailureIsPermanent)
                throw new PermanentException("Принудительный постоянный сбой для получателя.");
            throw new TransientException("Принудительный временный сбой для получателя.");
        }

        if (message.Attempt <= options.FailFirstAttempts)
            throw new TransientException("Сбой в начале серии отправки.");
        if (Random.Shared.Next(100) < options.FailurePercent)
            throw new TransientException("Симуляция временной недоступности канала.");
    }
}

public sealed record DeliveryResult(JournalEntry Entry, Exception? Error, DateTimeOffset FinishedUtc);

public sealed class NotificationDispatcher(
    IDateTimeProvider clock, INotificationSender email, INotificationSender sms)
{
    public async Task<DeliveryResult> DispatchAsync(
        NotificationTask task, FailureSimulationOptions simulation, CancellationToken cancellationToken)
    {
        var definition = task.Definition;
        var message = new NotificationMessage(definition.Recipient, definition.Text, task.Attempts + 1, simulation);
        var started = clock.UtcNow;
        Exception? error = null;
        var outcome = DeliveryOutcome.Success;

        try
        {
            var sender = definition.Channel == Channel.Email ? email : sms;
            await sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Неизвестный сбой считаем временным, чтобы применить ограниченный retry.
            error = exception is PermanentException or TransientException
                ? exception
                : new TransientException($"Сбой канала: {exception.Message}");
            outcome = error is PermanentException ? DeliveryOutcome.PermanentError : DeliveryOutcome.TransientError;
        }

        var entry = new JournalEntry(
            Guid.NewGuid(), task.Id, started, task.ScheduledUtc!.Value, definition.Channel,
            message.Recipient, message.Text, message.Attempt, outcome, error?.Message, task.Delayed);
        return new DeliveryResult(entry, error, clock.UtcNow);
    }

    public static NotificationTask Complete(NotificationTask task, DeliveryResult result, RetryOptions options)
    {
        var attempt = result.Entry.Attempt;
        task = task with { LastRunUtc = result.Entry.TimeUtc };
        var policy = new RetryPolicy(options);
        if (result.Error is not null && policy.CanRetry(attempt, result.Error))
        {
            return task with
            {
                Status = TaskStatus.WaitingForRetry,
                Attempts = attempt,
                RetryUtc = result.FinishedUtc + policy.GetDelay(attempt)
            };
        }
        return FinishOccurrence(task, result.FinishedUtc, result.Error is null);
    }

    public static NotificationTask FinishOccurrence(NotificationTask task, DateTimeOffset now, bool delivered)
    {
        var schedule = task.Definition.Schedule;
        var previous = task.ScheduledUtc!.Value;
        var after = now > previous ? now : previous;
        DateTimeOffset? next = schedule.Kind == ScheduleKind.OneTime
            ? null : ScheduleCalculator.NextRun(schedule, after, previous);

        return task with
        {
            Status = delivered ? (next is null ? TaskStatus.Sent : TaskStatus.Waiting) : TaskStatus.Error,
            ScheduledUtc = next,
            RetryUtc = null,
            Attempts = 0,
            Delayed = false
        };
    }
}
