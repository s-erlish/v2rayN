using System.Text.Json;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Именованный ОБРАТИМЫЙ набор для «Прокси по приложениям»: список имён процессов Windows, который
/// одним тумблером уходит мимо туннеля и тем же тумблером возвращается обратно.
/// </summary>
/// <param name="Key">Устойчивый идентификатор — по нему помнится, что набор добавил. Не показывается.</param>
/// <param name="TitleKey">Ключ локализации для заголовка строки.</param>
/// <param name="HintKey">Ключ локализации для подписи строки.</param>
/// <param name="Processes">Имена исполняемых файлов ровно так, как их сообщает Windows.</param>
public sealed record AppPreset(
    string Key,
    string TitleKey,
    string HintKey,
    IReadOnlyList<string> Processes)
{
    /// <summary>Запасной текст заголовка (ru, en) — см. <see cref="AppPresets.Text"/>.</summary>
    public required (string Ru, string En) TitleFallback { get; init; }

    /// <summary>Запасной текст подписи (ru, en) — см. <see cref="AppPresets.Text"/>.</summary>
    public required (string Ru, string En) HintFallback { get; init; }

    public string Title => AppPresets.Text(TitleKey, TitleFallback);

    public string Hint => AppPresets.Text(HintKey, HintFallback);
}

/// <summary>
/// Готовые наборы для «Прокси по приложениям» и запись о том, что каждый из них на самом деле добавил.
///
/// Перенос ПОВЕДЕНИЯ андроидного <c>handler/RussianAppsPreset.kt</c>, а не его состава: там набор про
/// российские приложения, которым не нравится зарубежный адрес выхода, здесь — про задержку. Три
/// свойства несущие, и все три оттуда:
///
/// 1. <b>Это набор, а не скрытый список.</b> У него есть имя, один тумблер применяет и снимает его,
///    и каждый процесс из набора виден в списке, на который набор действует, и снимается поштучно.
///    Зашитый список, который молча правит маршрутизацию, неотличим от бага.
/// 2. <b>Снятие возвращает ровно то, что применение взяло.</b> <see cref="Apply"/> запоминает то, что
///    ДЕЙСТВИТЕЛЬНО ДОБАВИЛ, — не весь набор, — и <see cref="Owned"/> — это то, что вернёт
///    <see cref="Release"/>. Процесс, отмеченный человеком до применения набора, переживёт его снятие,
///    потому что набор никогда его не присваивал.
/// 3. <b>Он сам по себе не перезапускает туннель.</b> Применение правит только выбор В ПАМЯТИ страницы;
///    записывает и перезагружает общий путь выхода, и только если что-то изменилось. Включить набор и
///    выключить обратно — ноль записей и ноль перезапусков.
///
/// <b>ВЫКЛЮЧЕНЫ ПО УМОЛЧАНИЮ</b>, в отличие от набора российских приложений: никто не просил выпускать
/// игры из туннеля из коробки, и молча сделать это значит удивить того, кто не играет.
///
/// <para>ОБ ИМЕНАХ ПРОЦЕССОВ. Каждое ниже сверено с источником — страницами поддержки самих издателей,
/// рекомендациями по фаерволу для магазинов и античитов и базами процессов, где записан канонический
/// исполняемый файл игры. Неверное имя МОЛЧИТ: оно не совпадает ни с одним процессом и просто не
/// маршрутизирует, поэтому угадывать хуже, чем не добавлять. Процесс, который никогда не запущен, не
/// стоит ничего — правило маршрутизации просто не срабатывает.</para>
///
/// <para>ЧЕГО ЗДЕСЬ НЕТ НАМЕРЕННО. Никаких общих рантаймов: Minecraft — это <c>javaw.exe</c>, у Warface
/// собственный бинарник называется буквально <c>Game.exe</c>, апдейтер Blizzard — <c>Agent.exe</c>;
/// вывести любой из них мимо туннеля значит увести туда постороннее ПО — единственная ошибка, которой
/// у этого файла быть не должно. Сервисы античитов (<c>vgc.exe</c>/<c>vgm.exe</c>, <c>BEService.exe</c>,
/// <c>EasyAntiCheat.exe</c>) тоже отсутствуют: это драйверы и службы, чей трафик — лицензия и
/// телеметрия, а не тот самый поток игры, и разводить маршрут античита с маршрутом его игры —
/// различие, которое лучше не создавать.</para>
/// </summary>
public static class AppPresets
{
    /// <summary>
    /// ИГРЫ. Соревновательные тайтлы, где задержка и есть продукт, плюс <c>RiotClientServices.exe</c> —
    /// это не витрина, а жёсткая зависимость: VALORANT и League без него не стартуют.
    /// </summary>
    public static readonly AppPreset Games = new(
        "games",
        "PerApp_PresetGames",
        "PerApp_PresetGamesHint",
        [
            "cs2.exe",                          // Counter-Strike 2
            "dota2.exe",                        // Dota 2
            "VALORANT-Win64-Shipping.exe",      // VALORANT (сама игра)
            "VALORANT.exe",                     // VALORANT (обёртка Riot)
            "RiotClientServices.exe",           // Клиент Riot — нужен VALORANT и League
            "League of Legends.exe",            // League of Legends (в имени действительно есть пробелы)
            "LeagueClient.exe",                 // Клиент League
            "LeagueClientUx.exe",               // Хост интерфейса клиента League
            "FortniteClient-Win64-Shipping.exe",// Fortnite
            "r5apex.exe",                       // Apex Legends
            "EscapeFromTarkov.exe",             // Escape from Tarkov
            "GTA5.exe",                         // GTA V / GTA Online
            "TslGame.exe",                      // PUBG: BATTLEGROUNDS
            "RustClient.exe",                   // Rust (имя процесса игры от Facepunch)
            "Overwatch.exe",                    // Overwatch 2
            "WorldOfTanks.exe",                 // World of Tanks
            "RainbowSix.exe",                   // Rainbow Six Siege
            "destiny2.exe",                     // Destiny 2
            "BF2042.exe",                       // Battlefield 2042
            "cod.exe",                          // Call of Duty (общий лаунчер современных частей)
        ])
    {
        TitleFallback = ("Игры", "Games"),
        HintFallback = (
            "Соревновательные игры идут мимо VPN — задержка ниже",
            "Competitive games skip the VPN — lower latency"),
    };

    /// <summary>
    /// ВИТРИНЫ — ОТДЕЛЬНЫМ ТУМБЛЕРОМ, и это разделение и есть смысл.
    ///
    /// Вывести <c>steam.exe</c> мимо туннеля — значит вывести загрузки и магазин. Загрузки обычно того и
    /// стоят: это самая большая передача, которую делает машина, и туннель её только замедляет. Но
    /// МАГАЗИН после этого видит настоящее подключение, а значит его регион, цены и доступность идут за
    /// реальным адресом, а не за адресом выхода. Это решение про деньги и каталог, а не про задержку, и
    /// ему нечего делать на тумблере, который чинит пинг, — поэтому тумблер второй, и строка говорит,
    /// что он делает.
    /// </summary>
    public static readonly AppPreset Launchers = new(
        "launchers",
        "PerApp_PresetLaunchers",
        "PerApp_PresetLaunchersHint",
        [
            "steam.exe",                // Steam
            "Battle.net.exe",           // Battle.net
            "EpicGamesLauncher.exe",    // Epic Games Launcher
            "EADesktop.exe",            // Приложение EA
            "upc.exe",                  // Ubisoft Connect
        ])
    {
        TitleFallback = ("Игровые лаунчеры", "Game launchers"),
        HintFallback = (
            "Загрузки быстрее, но магазин увидит ваш настоящий регион",
            "Faster downloads, but the store sees your real region"),
    };

    public static readonly IReadOnlyList<AppPreset> All = [Games, Launchers];

    #region локализация

    // Подписи — В ОДНУ СТРОКУ. В эталонном кадре у этих двух строк подписи нет вовсе, и рост строки с
    // 58 до 74 ломает ритм карточки; но у «Игровых лаунчеров» последствие не самоочевидно — магазин
    // после этого видит настоящий регион и цены, — и строка обязана его назвать. Одна короткая фраза
    // держит и ритм, и честность; развёрнутое обоснование живёт в комментариях к самим наборам.

    /// <summary>
    /// Подпись строки набора. Ключи <c>PerApp_Preset*</c> принадлежат общей таблице (Common/L.Settings.cs),
    /// и когда они там есть — побеждают они. Пока их там нет, <see cref="L"/> по контракту возвращает САМ
    /// КЛЮЧ, и строка показала бы пользователю «PerApp_PresetGames». Поэтому здесь лежит запасная пара
    /// ru/en: экран остаётся читаемым до того, как ключи доедут в таблицу, и ни строчки кода менять после
    /// этого не придётся.
    /// </summary>
    public static string Text(string key, (string Ru, string En) fallback)
    {
        var value = L.T(key);
        return value == key
            ? (L.Instance.CurrentLang == "en" ? fallback.En : fallback.Ru)
            : value;
    }

    #endregion локализация

    #region владение

    private const string FileName = "departament_presets.json";
    private const string Tag = "AppPresets";

    private static readonly object _lock = new();
    private static Dictionary<string, List<string>>? _owned;

    /// <summary>
    /// Что <see cref="Apply"/> добавил для этого набора и ещё не вернул. Пусто — набор не применён.
    /// </summary>
    public static IReadOnlyList<string> Owned(AppPreset preset)
    {
        lock (_lock)
        {
            return Load().TryGetValue(preset.Key, out var list) ? list : [];
        }
    }

    /// <summary>Набор сейчас владеет хотя бы одной записью — то есть его тумблер включён.</summary>
    public static bool IsApplied(AppPreset preset) => Owned(preset).Count > 0;

    /// <summary>
    /// Запоминает записи, которые набор только что добавил, чтобы <see cref="Release"/> вернул ровно их.
    /// </summary>
    /// <param name="preset">Набор, который включают.</param>
    /// <param name="current">Текущий выбор, без учёта регистра.</param>
    /// <returns>Записи, которые НАДО ДОБАВИТЬ — возможно, пусто, если всё уже было выбрано.</returns>
    public static IReadOnlyList<string> Apply(AppPreset preset, ISet<string> current)
    {
        var added = preset.Processes.Where(p => !current.Contains(p)).ToList();
        lock (_lock)
        {
            var owned = Load();
            owned[preset.Key] = added;
            Persist();
        }
        return added;
    }

    /// <summary>
    /// Записи, которые надо СНЯТЬ при выключении набора: только то, что добавил <see cref="Apply"/>.
    ///
    /// Процесс, выбранный человеком до применения набора, набору не принадлежит и остаётся — снятие
    /// набора не имеет права отнимать решение, которое человек принял сам.
    /// </summary>
    public static IReadOnlyList<string> Release(AppPreset preset)
    {
        lock (_lock)
        {
            var owned = Load();
            var list = owned.TryGetValue(preset.Key, out var v) ? v : [];
            owned.Remove(preset.Key);
            Persist();
            return list;
        }
    }

    private static Dictionary<string, List<string>> Load()
    {
        if (_owned is not null)
        {
            return _owned;
        }
        try
        {
            var path = Utils.GetConfigPath(FileName);
            if (File.Exists(path))
            {
                _owned = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path));
            }
        }
        catch (Exception ex)
        {
            // Нечитаемая запись читается как «набор не применён» — это безопасный ответ: тумблер
            // показывает «выключено», а включение заново запишет то, что добавит. Выбор пользователя
            // при этом не трогается в любом случае.
            Logging.SaveLog(Tag, ex);
        }
        return _owned ??= new Dictionary<string, List<string>>();
    }

    private static void Persist()
    {
        try
        {
            File.WriteAllText(Utils.GetConfigPath(FileName), JsonSerializer.Serialize(_owned ?? []));
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    #endregion владение
}
