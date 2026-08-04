namespace v2rayN.Desktop.Common;

/// <summary>
/// Кто имеет право занять тост.
///
/// ЗАЧЕМ ЭТО ЕСТЬ. <c>AppEvents.SendSnackMsgRequested</c> — общий с апстримом канал: в него пишут и
/// осознанные ответы departament (подписка добавлена, ссылка не разобрана, проверка узла не прошла),
/// и служебная трансляция состояния ядра. Пока у канала не было отрисовки, разницы не было видно;
/// как только тост появился (e594a2d), наружу полезло и второе — владелец увидел снизу
/// «[Custom] [Xray]ғı Finland» при подключении к подписке.
///
/// ГРАНИЦА (владелец, и она же записана над <c>DelegateSnackMsg</c>): тост НЕ комментирует состояние
/// подключения — за него отвечают щит на «Главной» и строка статуса. Тост отвечает на ЯВНОЕ действие
/// пользователя и на отказ, который иначе останется немым.
///
/// Поэтому фильтруется КАНАЛ, а не поверхность: строка по-прежнему уходит в журнал
/// (<c>NoticeManager.SendMessage</c>), причину отказа по-прежнему забирает «Главная»
/// (<c>HomeViewModel._lastNotice</c> → подпись под щитом), а на экран всплывает только то, что
/// написано для человека.
///
/// Живёт в Desktop, а не в ServiceLib: ServiceLib делится с WPF-клиентом апстрима, у которого своя
/// поверхность сообщений и своё право показывать всё подряд. Здесь сравниваются ЗНАЧЕНИЯ ресурсов
/// (<c>ResUI</c>), а не русские литералы, поэтому правило не зависит от языка.
/// </summary>
public static class NoticePolicy
{
    /// <summary>
    /// Строка — это «визитка» узла в формате апстрима <c>ProfileItem.GetSummary()</c>:
    /// <c>[Custom] [Xray]ғı Finland</c> или <c>[VLESS] Finland(***12:443)</c>. Это не предложение, а
    /// внутренняя отладочная подпись; человеку её показывать нельзя нигде.
    /// Опознаётся по форме — ведущий <c>[токен]</c>, где токен разбирается как <see cref="EConfigType"/>.
    /// </summary>
    public static bool IsNodeSummary(string? text)
    {
        if (text is null)
        {
            return false;
        }

        var s = text.AsSpan().TrimStart();
        if (s.Length == 0 || s[0] != '[')
        {
            return false;
        }

        var close = s.IndexOf(']');
        if (close <= 1)
        {
            return false;
        }

        var token = s[1..close];
        // Enum.TryParse принимает и ЧИСЛО («[2]» разобралось бы как Custom). Здесь опознаётся имя
        // типа узла, а не число: сообщение, начинающееся с «[10] …», визиткой узла не является.
        if (!char.IsLetter(token[0]))
        {
            return false;
        }

        return Enum.TryParse<EConfigType>(token.ToString(), false, out _);
    }

    /// <summary>
    /// Строка лишь пересказывает состояние подключения — то, что уже нарисовано щитом и строкой
    /// статуса: жизненный цикл ядра, отказ запуска, требование выбрать сервер, служебная информация
    /// о процессе. Показывать это тостом — дублировать экран, а иногда и противоречить ему.
    /// Причина отказа при этом не теряется: «Главная» читает тот же канал и подписывает ею щит.
    /// </summary>
    public static bool IsConnectionState(string? text)
    {
        if (text.IsNullOrEmpty())
        {
            return false;
        }

        var s = text!.Trim();
        if (Same(s, ResUI.FailedToRunCore)
            || Same(s, ResUI.CheckServerSettings)
            || Same(s, ResUI.PleaseSelectServer))
        {
            return true;
        }

        // «Start service: <дата>» и строка окружения (версия | пути | ОС) — журнал, не сообщение.
        // Сегодня они публикуются с notify:false и до тоста не доходят; правило страхует от того,
        // что апстрим когда-нибудь поднимет флаг.
        return StartsWithFormat(s, ResUI.StartService)
            || s == Utils.GetRuntimeInfo();
    }

    /// <summary>Единственный вопрос, который задаёт тост, прежде чем показаться.</summary>
    public static bool ShouldToast(string? text) => !IsNodeSummary(text) && !IsConnectionState(text);

    private static bool Same(string s, string? resource) =>
        resource.IsNotEmpty() && string.Equals(s, resource!.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Сравнение с ресурсом-шаблоном (<c>"Start service: {0}"</c>) по его неизменяемой голове:
    /// подставленное значение впереди не стоит, поэтому голова — достаточный признак.
    /// </summary>
    private static bool StartsWithFormat(string s, string? format)
    {
        if (format.IsNullOrEmpty())
        {
            return false;
        }

        var brace = format!.IndexOf('{');
        var head = (brace > 0 ? format[..brace] : format).Trim();
        return head.Length >= 4 && s.StartsWith(head, StringComparison.Ordinal);
    }
}
