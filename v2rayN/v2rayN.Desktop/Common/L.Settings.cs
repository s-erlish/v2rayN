namespace v2rayN.Desktop.Common;

// ─────────────────────────────────────────────────────────────────────────────
// OWNER: WP3 — Settings + sub-pages.
// Keys: Settings_*, Dns_*, Routing_*, PerApp_*, Ping_*, Geo_*, About_*, Backup_*,
//       UrlSchemes_*, Provider_* (+ Common_* references).
// Views: SettingsView(.axaml/.cs), SettingsViewModel, DnsSubView, RoutingSubView,
//        PerAppProxyPage, PingSettingsPage, GeoFilesPage, AboutPage, BackupPage,
//        UrlSchemesPage, ProviderSettingsPage, ThemeSettingViewModel.
// Inventory: LOCALIZATION_PLAN.md §2.3.
// NOTE (WP0 already done): the language-switch wiring in SettingsViewModel.CycleLanguageAsync
//       and ThemeSettingViewModel already calls L.Instance.SetLanguage(...) and the reboot
//       notice is dropped. WP3 only needs to convert the Resolve*Text() resolvers to
//       language-aware output. This is the ONLY L file WP3 edits.
// Locale-neutral tokens kept as-is in BOTH languages (never keyed here): TUN, DNS, IPv6,
//       FakeIP, Mux, SOCKS5, TCP/HTTP/ICMP, HWID, User-Agent, depv://, geoip.dat, geosite.dat,
//       Cloudflare/Google/AdGuard, protocol names, and language endonyms.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class L
{
    partial void RegisterSettings()
    {
        // ── SettingsView — section headers + rows ──
        Add("Settings_SecConnection", "Подключение", "Connection");
        Add("Settings_Mode", "Режим", "Mode");
        Add("Settings_ModeProxy", "Только прокси", "Proxy only");
        Add("Settings_PerApp", "Прокси по приложениям", "Per-app proxy");
        Add("Settings_BypassLan", "Обход локальной сети", "Bypass local network");
        Add("Settings_BypassLanHint", "Прямой доступ к устройствам в локальной сети", "Direct access to devices on the local network");
        Add("Settings_Ipv6Hint", "Включить IPv6-адресацию в туннеле", "Enable IPv6 addressing in the tunnel");
        Add("Settings_Ping", "Пинг", "Ping");
        Add("Settings_LocalProxy", "Локальный прокси", "Local proxy");
        Add("Settings_LocalProxyHint", "Порт и SOCKS5-авторизация", "Port and SOCKS5 authentication");
        Add("Settings_Port", "Порт", "Port");
        Add("Settings_Socks5Auth", "SOCKS5-авторизация", "SOCKS5 authentication");
        Add("Settings_Username", "Логин", "Username");
        Add("Settings_NotSet", "Не задан", "Not set");
        Add("Settings_Socks5Hint", "Адрес: 127.0.0.1. Пустые логин и пароль отключают SOCKS5-авторизацию.", "Address: 127.0.0.1. Empty username and password disable SOCKS5 authentication.");

        Add("Settings_SecBypass", "Обход блокировок", "Bypass censorship");
        Add("Settings_Mux", "Мультиплексирование (Mux)", "Multiplexing (Mux)");
        Add("Settings_MuxHint", "Объединяет запросы в один канал соединения", "Combines requests into a single connection channel");
        Add("Settings_MuxCount", "Число соединений Mux", "Mux connection count");
        Add("Settings_Fragment", "Фрагментация пакетов", "Packet fragmentation");
        Add("Settings_FragmentHint", "Разбивает TLS-рукопожатие против DPI", "Splits the TLS handshake to defeat DPI");

        Add("Settings_SecPerformance", "Производительность", "Performance");
        Add("Settings_LiteMode", "Облегчённый режим", "Lite mode");
        Add("Settings_LiteModeHint", "Отключает анимации и тени", "Disables animations and shadows");

        Add("Settings_SecInterface", "Интерфейс", "Interface");
        Add("Settings_Appearance", "Оформление", "Appearance");
        Add("Settings_Language", "Язык", "Language");
        Add("Settings_FontSize", "Размер шрифта", "Font size");
        Add("Settings_UiScale", "Масштаб интерфейса", "Interface scale");
        Add("Settings_Autostart", "Запуск с системой", "Launch with the system");
        Add("Settings_AutostartHint", "Открывать при входе в систему", "Open when you sign in");

        Add("Settings_SecSubscription", "Подписка", "Subscription");
        Add("Settings_SubAutoUpdate", "Автообновление подписки", "Auto-update subscription");
        Add("Settings_Routing", "Маршрутизация", "Routing");
        Add("Settings_RoutingHint", "Правила proxy, direct и block", "proxy, direct and block rules");
        Add("Settings_GeoFiles", "Файлы ресурсов", "Resource files");
        Add("Settings_GeoFilesHint", "Geo-базы для маршрутизации", "Geo databases for routing");

        Add("Settings_About", "О приложении", "About");
        Add("Settings_Log", "Журнал", "Log");
        Add("Settings_LogHint", "Лог ядра за текущий сеанс", "Core log for the current session");
        Add("Settings_CheckUpdate", "Проверить обновления", "Check for updates");
        Add("Settings_Backup", "Резервное копирование", "Backup");
        Add("Settings_BackupHint", "Сохранить и восстановить настройки", "Save and restore settings");
        Add("Settings_UrlSchemes", "Схемы URL-адресов", "URL schemes");
        Add("Settings_UrlSchemesHint", "Быстрые команды depv://", "Quick depv:// commands");

        // ── SettingsViewModel — display-value resolvers (language-aware) ──
        Add("Settings_PerAppExcept", "кроме", "except");
        Add("Settings_PerAppOnly", "только", "only");
        Add("Settings_ThemeLight", "Светлая", "Light");
        Add("Settings_ThemeDark", "Тёмная", "Dark");
        // Четыре РАВНЫХ пункта «Оформления» (screens.md). Чёрно-белая больше не надстройка над базой.
        Add("Settings_ThemeMono", "Чёрно-белая", "Monochrome");
        Add("Settings_ThemeSystem", "Как в системе", "Match system");
        Add("Settings_LangRussian", "Русский", "Russian");
        Add("Settings_LangSystem", "Системный", "System");

        // ── DnsSubView ──
        Add("Dns_Intro", "Через какой сервер приложение разрешает имена", "Which server the app uses to resolve names");
        Add("Dns_Provider", "Провайдер", "Provider");
        Add("Dns_CustomAddress", "Свой DNS-адрес", "Custom DNS address");
        Add("Dns_CustomHint", "DoH-адрес (https://…/dns-query), DoT или обычный IP: 1.1.1.1", "DoH address (https://…/dns-query), DoT, or a plain IP: 1.1.1.1");
        Add("Dns_Advanced", "Дополнительно", "Advanced");
        Add("Dns_AdvancedHint", "Ускоряет соединение, отвечая на DNS-запросы локально (sing-box)", "Speeds up connections by answering DNS queries locally (sing-box)");

        // ── RoutingSubView ──
        Add("Routing_Intro", "Правила proxy, direct и block", "The proxy, direct, and block rules");
        Add("Routing_RuleSets", "Наборы правил", "Rule sets");
        Add("Routing_RulesCount", "{0} правил", "{0} rules");
        Add("Routing_Active", "Активен", "Active");
        Add("Routing_DomainStrategy", "Стратегия доменов", "Domain strategy");
        Add("Routing_DomainResolution", "Разрешение доменов", "Domain resolution");
        Add("Routing_DomainHint", "Как ядро сопоставляет домены с правилами", "How the core matches domains against rules");
        Add("Routing_Maintenance", "Обслуживание", "Maintenance");
        Add("Routing_DefaultRules", "Стандартные правила", "Default rules");
        Add("Routing_DefaultRulesHint", "Пересоздать встроенные наборы правил", "Rebuild the built-in rule sets");
        Add("Routing_Reset", "Сбросить", "Reset");
        Add("Routing_DsAsIs", "Как есть", "As is");
        Add("Routing_DsIpIfNonMatch", "IP при несовпадении", "IP if no match");
        Add("Routing_DsIpOnDemand", "IP по запросу", "IP on demand");

        // ── PerAppProxyPage ──
        Add("PerApp_SplitTunnel", "Раздельное туннелирование", "Split tunneling");
        Add("PerApp_SplitTunnelHint", "Выберите, какие программы идут через VPN", "Choose which apps go through the VPN");
        Add("PerApp_BypassHint", "Кроме выбранных — идут напрямую, минуя VPN", "Except selected — they go direct, bypassing the VPN");
        Add("PerApp_OnlyHint", "Только выбранные — через VPN идут лишь они", "Only selected — just these go through the VPN");
        Add("PerApp_Apps", "Приложения", "Apps");
        Add("PerApp_AddExe", "Добавить .exe", "Add .exe");
        Add("PerApp_TunHint", "Работает в режиме TUN (sing-box). Правила применяются при следующем подключении.", "Works in TUN mode (sing-box). Rules apply on the next connection.");
        Add("PerApp_ProgramFileType", "Программа", "Program");

        // ── PingSettingsPage (only Real / TCP rows) ──
        Add("Ping_Intro", "Как измеряется задержка до серверов", "How latency to servers is measured");
        Add("Ping_RealTitle", "Реальная задержка", "Real latency");
        Add("Ping_RealHint", "Замер через туннель — точнее, но медленнее", "Measured through the tunnel — more accurate, but slower");
        Add("Ping_TcpHint", "tcping до адреса сервера", "tcping to the server address");
        Add("Ping_TestAddress", "Адрес проверки задержки", "Latency test address");
        Add("Ping_Timeout", "Тайм-аут проверки, сек", "Test timeout, sec");
        // Short row label used by SettingsViewModel.ResolvePingMethodText («Реальная»); TCP/HTTP/ICMP stay as tokens.
        Add("Ping_Real", "Реальная", "Real");

        // ── GeoFilesPage ──
        Add("Geo_Intro", "Geo-базы для маршрутизации", "Geo databases for routing");
        Add("Geo_UpdateNow", "Обновить сейчас", "Update now");
        Add("Geo_NotDownloaded", "Не загружен", "Not downloaded");
        Add("Geo_SizeUpdated", "{0} МБ · обновлён {1}", "{0} MB · updated {1}");
        Add("Geo_Updating", "Обновление…", "Updating…");
        Add("Geo_Downloading", "Загрузка баз…", "Downloading databases…");
        Add("Geo_Done", "Готово — базы обновлены.", "Done — databases updated.");
        Add("Geo_Failed", "Не удалось обновить: ", "Update failed: ");

        // ── AboutPage ──
        Add("About_Version", "Версия —", "Version —");
        Add("About_VersionValue", "Версия {0}", "Version {0}");
        Add("About_TitleVersion", "departament · Версия {0}", "departament · Version {0}");
        Add("About_OpenSite", "Открыть сайт", "Open website");
        Add("About_TelegramBot", "Telegram-бот", "Telegram bot");
        Add("About_Details", "Сведения", "Details");
        Add("About_CopyDetails", "Копировать сведения", "Copy details");
        Add("About_SystemInfo", "ОС: {0}\nАрхитектура: {1}\n.NET: {2}", "OS: {0}\nArchitecture: {1}\n.NET: {2}");

        // ── BackupPage ──
        Add("Backup_Intro", "Файл с серверами и настройками", "A file with your servers and settings");
        Add("Backup_Export", "Экспорт", "Export");
        Add("Backup_ExportHint", "Сохранить копию в файл", "Save a backup to a file");
        Add("Backup_Save", "Сохранить…", "Save…");
        Add("Backup_Import", "Импорт", "Import");
        Add("Backup_ImportHint", "Восстановить из файла — приложение перезапустится", "Restore from a file — the app will restart");
        Add("Backup_Restore", "Восстановить…", "Restore…");
        Add("Backup_Saving", "Сохранение…", "Saving…");
        Add("Backup_Saved", "Копия сохранена: {0}", "Backup saved: {0}");
        Add("Backup_SaveFailed", "Не удалось сохранить копию.", "Couldn't save the backup.");
        Add("Backup_ExportError", "Ошибка экспорта: ", "Export error: ");
        Add("Backup_Restoring", "Восстановление… Приложение перезапустится.", "Restoring… The app will restart.");
        Add("Backup_ImportError", "Ошибка импорта: ", "Import error: ");

        // ── UrlSchemesPage ──
        Add("UrlSchemes_Registration", "Регистрация схемы depv://", "depv:// scheme registration");
        Add("UrlSchemes_Register", "Зарегистрировать", "Register");
        Add("UrlSchemes_Remove", "Убрать", "Remove");
        Add("UrlSchemes_Hint", "Нажмите на схему, чтобы скопировать. Используйте их в ярлыках, скриптах или других приложениях.", "Tap a scheme to copy it. Use them in shortcuts, scripts, or other apps.");
        Add("UrlSchemes_StartTunnel", "Запустить туннель", "Start the tunnel");
        Add("UrlSchemes_OpenApp", "Открыть приложение", "Open the app");
        Add("UrlSchemes_Stop", "Остановить соединение", "Stop the connection");
        Add("UrlSchemes_Toggle", "Переключить соединение", "Toggle the connection");
        Add("UrlSchemes_Import", "Импорт (автоопределение типа)", "Import (auto-detect type)");
        Add("UrlSchemes_AddByUrl", "Добавить по URL", "Add by URL");
        Add("UrlSchemes_WindowsOnly", "Регистрация схемы доступна только на Windows.", "Scheme registration is available on Windows only.");
        Add("UrlSchemes_Registered", "Схема зарегистрирована — ссылки depv:// открывают departament.", "Scheme registered — depv:// links open departament.");
        Add("UrlSchemes_NotRegistered", "Схема не зарегистрирована.", "Scheme not registered.");
        Add("UrlSchemes_NoPath", "Не удалось определить путь к программе.", "Couldn't determine the app's path.");
        Add("UrlSchemes_RegisterFailed", "Не удалось зарегистрировать: ", "Registration failed: ");
        Add("UrlSchemes_RemovedOk", "Схема удалена.", "Scheme removed.");
        Add("UrlSchemes_RemoveFailed", "Не удалось удалить: ", "Removal failed: ");

        // ── ProviderSettingsPage ──
        Add("Provider_Title", "Настройки подписок", "Subscription settings");
        Add("Provider_SecUpdates", "Обновление", "Updates");
        Add("Provider_AutoUpdate", "Автообновление", "Auto-update");
        Add("Provider_AutoUpdateHint", "Автоматически обновлять подписки", "Update subscriptions automatically");
        Add("Provider_Interval", "Интервал обновления", "Update interval");
        Add("Provider_SecNetwork", "Сеть", "Network");
        Add("Provider_Hwid", "Идентификатор устройства (HWID)", "Device ID (HWID)");
        Add("Provider_UserAgentHint", "Отправляется ядром на исходящих соединениях.", "Sent by the core on outbound connections.");

        // ═════════════════════════════════════════════════════════════════════════════════════════
        // Подэкраны настроек по единому лекалу (screens.md «Подэкраны»). Строки — как в спецификации.
        // ═════════════════════════════════════════════════════════════════════════════════════════

        // ── DNS ──
        Add("Dns_Presets", "Пресеты", "Presets");
        Add("Dns_Custom", "Свой сервер", "Custom server");
        Add("Dns_CustomSub", "Указать вручную", "Enter manually");

        // ── Пинг ──
        Add("Ping_TcpTitle", "TCP-соединение", "TCP connection");
        Add("Ping_Params", "Параметры проверки", "Test parameters");
        Add("Ping_Unsupported", "HTTP-запрос и ICMP ядром не измеряются — доступны реальная задержка и TCP.", "HTTP request and ICMP aren't measured by the core — real latency and TCP are available.");

        // ── Прокси по приложениям ──
        Add("PerApp_Mode", "Режим", "Mode");
        Add("PerApp_ModeExcept", "Кроме выбранных", "Except selected");
        Add("PerApp_ModeOnly", "Только выбранные", "Only selected");
        Add("PerApp_Search", "Поиск по приложениям", "Search apps");
        Add("PerApp_Programs", "Программы", "Programs");
        Add("PerApp_EmptyTitle", "Программы не найдены", "No programs found");
        Add("PerApp_EmptyText", "Измените запрос или добавьте .exe вручную.", "Change the query or add an .exe manually.");
        Add("PerApp_Chosen", "Выбрано {0}", "{0} selected");

        // ── Локальный прокси ──
        Add("LocalProxy_Intro", "Доступ для программ на этом компьютере", "Access for programs on this computer");
        Add("LocalProxy_Params", "Параметры", "Parameters");
        Add("LocalProxy_Address", "Адрес", "Address");
        Add("LocalProxy_PortSocks", "Порт SOCKS5", "SOCKS5 port");
        Add("LocalProxy_PortHttp", "Порт HTTP", "HTTP port");
        Add("LocalProxy_PortHttpSame", "Тот же вход принимает и HTTP", "The same inbound also accepts HTTP");
        Add("LocalProxy_Access", "Доступ", "Access");
        Add("LocalProxy_AuthHint", "Логин и пароль при подключении", "Username and password when connecting");
        Add("LocalProxy_Login", "Логин", "Username");
        Add("LocalProxy_Password", "Пароль", "Password");
        Add("LocalProxy_BlockUdp", "Блокировать UDP", "Block UDP");
        Add("LocalProxy_BlockUdpHint", "Запретить UDP через прокси", "Disallow UDP through the proxy");
        Add("LocalProxy_Foot", "Порты меняются только при отключённом туннеле.", "Ports can only be changed while the tunnel is off.");
        Add("LocalProxy_FootLive", "Туннель включён — порт менять нельзя. Отключитесь, чтобы изменить его.", "The tunnel is on — the port can't be changed. Disconnect to change it.");

        // ── Маршрутизация ──
        Add("Routing_Rules", "Правила", "Rules");
        Add("Routing_AddRule", "Добавить правило", "Add a rule");
        Add("Routing_NoRules", "Правил пока нет", "No rules yet");
        Add("Routing_Resetting", "Пересоздаём…", "Rebuilding…");

        // ── Файлы ресурсов ──
        Add("Geo_Source", "Источник Geo-файлов", "Geo file source");
        Add("Geo_SourceRow", "Источник", "Source");
        Add("Geo_Files", "Файлы", "Files");
        Add("Geo_Update", "Обновить", "Update");

        // ── Журнал ──
        Add("Log_Title", "Журнал", "Log");
        Add("Log_Intro", "Лог ядра за текущий сеанс", "The core log for the current session");
        Add("Log_Search", "Поиск по журналу", "Search the log");
        Add("Log_EmptyTitle", "Записей пока нет", "No entries yet");
        Add("Log_EmptyText", "Журнал заполнится при следующем подключении.", "The log will fill up on the next connection.");
        Add("Log_NoMatchTitle", "Ничего не найдено", "Nothing found");
        Add("Log_NoMatchText", "По этому запросу записей нет. Попробуйте другой.", "No entries match this query. Try another one.");
        Add("Log_Copy", "Копировать", "Copy");
        Add("Log_Clear", "Очистить", "Clear");
        Add("Log_Copied", "Журнал скопирован", "Log copied");
        Add("Log_Lines", "{0} строк", "{0} lines");

        // ── Проверить обновление ──
        Add("Update_Title", "Проверить обновление", "Check for updates");
        Add("Update_PreRelease", "Искать предварительный выпуск", "Look for pre-releases");
        Add("Update_PreReleaseHint", "Ранние сборки с новыми функциями", "Early builds with new features");
        Add("Update_Check", "Проверить обновление", "Check for updates");
        Add("Update_Checking", "Проверяем…", "Checking…");
        Add("Update_Components", "Компоненты", "Components");
        Add("Update_Now", "Обновить сейчас", "Update now");
        Add("Update_App", "Приложение", "Application");
        Add("Update_GeoFiles", "Geo-базы", "Geo databases");
        Add("Update_Foot", "departament {0}", "departament {0}");

        // ── Резервное копирование ──
        Add("Backup_SecData", "Данные", "Data");
        Add("Backup_SaveCopy", "Сохранить копию", "Save a backup");
        Add("Backup_SaveCopyHint", "Все серверы и настройки в один файл", "All servers and settings in one file");
        Add("Backup_RestoreFile", "Восстановить из файла", "Restore from a file");
        Add("Backup_RestoreFileHint", "Заменит текущие серверы", "Replaces the current servers");
        Add("Backup_SecCloud", "Облако", "Cloud");
        Add("Backup_WebDav", "Настройки WebDAV", "WebDAV settings");
        Add("Backup_WebDavNotSet", "Не настроено", "Not configured");
        Add("Backup_WebDavUrl", "Адрес сервера", "Server address");
        Add("Backup_WebDavIntro", "Копия хранится на вашем сервере WebDAV", "The backup is kept on your own WebDAV server");
        Add("Backup_WebDavServer", "Сервер", "Server");
        Add("Backup_WebDavFolder", "Папка", "Folder");
        Add("Backup_SecActions", "Действия", "Actions");
        Add("Backup_WebDavCheck", "Проверить подключение", "Test the connection");
        Add("Backup_CloudUpload", "Выгрузить копию", "Upload a backup");
        Add("Backup_CloudRestore", "Восстановить из облака", "Restore from the cloud");
        Add("Backup_Working", "Выполняем…", "Working…");

        // ── Схемы URL-адресов ──
        Add("UrlSchemes_Intro", "Быстрые команды для запуска из браузера и ярлыков", "Quick commands to launch from the browser and shortcuts");
        Add("UrlSchemes_ConnectHint", "Подключиться к текущему серверу", "Connect to the current server");
        Add("UrlSchemes_DisconnectHint", "Отключиться", "Disconnect");
        Add("UrlSchemes_SubHint", "Добавить подписку по ссылке", "Add a subscription by link");
        Add("UrlSchemes_Copied", "Схема скопирована", "Scheme copied");
        Add("UrlSchemes_Commands", "Команды", "Commands");
        Add("UrlSchemes_RegisterRow", "Открывать ссылки depv://", "Open depv:// links");

        // ── О приложении ──
        Add("About_SecApp", "Приложение", "Application");
        Add("About_VersionRow", "Версия", "Version");
        Add("About_Identifier", "Идентификатор", "Identifier");
        Add("About_SecLinks", "Ссылки и документы", "Links and documents");
        Add("About_SourceCode", "Исходный код", "Source code");
        Add("About_Licenses", "Лицензии открытого ПО", "Open-source licenses");
        Add("About_Feedback", "Обратная связь", "Feedback");
        Add("About_TelegramChannel", "Канал в Telegram", "Telegram channel");
        Add("About_Privacy", "Политика конфиденциальности", "Privacy policy");
        Add("About_CheckUpdates", "Проверить обновления", "Check for updates");
        Add("About_Copied", "Скопировано", "Copied");
        Add("About_Site", "Сайт departament", "departament website");
        Add("About_System", "Система", "System");

        // ── Настройки подписок ──
        Add("Provider_Intro", "Как приложение обновляет подписки и представляется серверу", "How the app updates subscriptions and identifies itself to the server");
    }
}
