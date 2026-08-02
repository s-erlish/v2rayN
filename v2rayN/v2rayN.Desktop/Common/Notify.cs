namespace v2rayN.Desktop.Common;

/// <summary>
/// Ответ приложения пользователю — ОДНА строка, и при необходимости ОДНО действие рядом с ней.
///
/// Зачем отдельный канал рядом с <c>AppEvents.SendSnackMsgRequested</c>: тот несёт только
/// <see cref="string"/>, а правило G2 требует, чтобы отказ предлагал восстановление ТАМ ЖЕ, где о
/// себе сообщил («Повторить»), а выбор сервера при живом туннеле — «Переподключиться». Канал
/// десктопный по определению: он несёт готовый текст и делегат, поэтому ему нечего делать в
/// <c>ServiceLib</c>, которая общая с WPF-клиентом и обязана оставаться безъязыкой.
///
/// Единственный подписчик — оболочка (<c>MainWindow</c>), которая рисует тост. Событие статическое,
/// потому что источники (вью-модели вкладок, суб-страницы) окна не знают и знать не должны;
/// подписка снимается в <c>MainWindow.OnClosed</c>, поэтому закрытое окно не удерживается.
/// </summary>
public static class Notify
{
    /// <param name="Text">Что произошло. Одна фраза, активный глагол, sentence-case.</param>
    /// <param name="ActionLabel">Подпись действия или <c>null</c>, если выхода не предлагаем.</param>
    /// <param name="Action">Что сделать по нажатию. Выполняется на UI-потоке.</param>
    public sealed record Message(string Text, string? ActionLabel = null, Action? Action = null);

    public static event Action<Message>? Requested;

    /// <summary>Сообщить об исходе без предложения действия.</summary>
    public static void Show(string? text)
    {
        if (text.IsNotEmpty())
        {
            Requested?.Invoke(new Message(text!));
        }
    }

    /// <summary>Сообщить об исходе и предложить выход в том же месте (G2).</summary>
    public static void Show(string? text, string? actionLabel, Action action)
    {
        if (text.IsNotEmpty())
        {
            Requested?.Invoke(new Message(text!, actionLabel, action));
        }
    }
}
