namespace Nofi.Core;

public static class SettingsValidation
{
    public static void Validate(AppSettings settings)
    {
        if (settings.Retry is not { } retry || settings.Simulation is not { } simulation)
            throw new ArgumentException("Политика повтора и параметры имитации обязательны.");
        if (retry.MaxAttempts is < 1 or > 50)
            throw new ArgumentException("Лимит должен быть от 1 до 50 попыток.");
        if (retry.InitialDelaySeconds is < 1 or > 86400)
            throw new ArgumentException("Базовая задержка должна быть от 1 до 86 400 секунд.");
        if (!double.IsFinite(retry.Multiplier) || retry.Multiplier is < 1 or > 10)
            throw new ArgumentException("Коэффициент роста должен быть от 1 до 10.");
        if (simulation.FailurePercent is < 0 or > 100)
            throw new ArgumentException("Вероятность сбоя должна быть от 0 до 100%.");
        if (simulation.DelayMilliseconds is < 0 or > 5000)
            throw new ArgumentException("Задержка имитации должна быть от 0 до 5000 мс.");
        if (simulation.FailFirstAttempts is < 0 or > 50)
            throw new ArgumentException("Число первых сбоев должно быть от 0 до 50.");
        if (simulation.ForcedRecipient is null || simulation.ForcedRecipient.Length > 254)
            throw new ArgumentException("Укажите получателя для принудительного сбоя длиной до 254 символов.");
        if (settings.PollMilliseconds is < 100 or > 60_000)
            throw new ArgumentException("Интервал опроса должен быть от 100 до 60 000 мс.");
        if (!Enum.IsDefined(settings.MissedRuns))
            throw new ArgumentException("Выберите режим обработки пропущенных запусков.");
    }
}
