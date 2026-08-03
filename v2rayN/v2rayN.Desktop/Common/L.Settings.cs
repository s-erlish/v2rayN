namespace v2rayN.Desktop.Common;

// ─────────────────────────────────────────────────────────────────────────────
// OWNER: WP3, settings + sub-pages.
// Keys: Settings_*, Dns_*, Routing_*, PerApp_*, Ping_*, Geo_*, About_*, Backup_*,
//       UrlSchemes_*, Provider_* (+ Common_* references).
// Views: SettingsView(.axaml/.cs), SettingsViewModel, DnsSubView, RoutingSubView,
//        PerAppProxyPage, GeoFilesPage, AboutPage, BackupPage,
//        UrlSchemesPage, ProviderSettingsPage, ThemeSettingViewModel.
// Inventory: LOCALIZATION_PLAN.md §2.3.
// NOTE (WP0 already done): the language-switch wiring in SettingsViewModel.SetLanguageAsync
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
        // ── SettingsView: section headers + rows ──
        Add("Settings_SecConnection", "Подключение", "Connection");
        Add("Settings_Mode", "Режим", "Mode");
        Add("Settings_ModeProxy", "Прокси", "Proxy");
        Add("Settings_PerApp", "Прокси по приложениям", "Per-app proxy");
        Add("Settings_BypassLan", "Обход локальной сети", "Bypass local network");
        Add("Settings_BypassLanHint", "Прямой доступ к устройствам в локальной сети", "Direct access to devices on the local network");
        Add("Settings_Ipv6Hint", "Включить IPv6-адресацию в туннеле", "Enable IPv6 addressing in the tunnel");
        Add("Settings_Ping", "Пинг", "Ping");
        Add("Settings_LocalProxy", "Локальный прокси", "Local proxy");
        // «Имя пользователя», matching Android lp_socks_login, not a second word for the same field.
        Add("Settings_LocalProxyHint", "Порт, имя пользователя и пароль SOCKS5", "Port, username and password for SOCKS5");
        Add("Settings_Port", "Порт", "Port");
        Add("Settings_Socks5Auth", "SOCKS5-авторизация", "SOCKS5 authentication");
        Add("Settings_Username", "Имя пользователя", "Username");
        Add("Settings_NotSet", "Не задан", "Not set");
        Add("Settings_Socks5Hint", "Адрес: 127.0.0.1. Пустые имя пользователя и пароль отключают SOCKS5-авторизацию.", "Address: 127.0.0.1. Empty username and password disable SOCKS5 authentication.");

        Add("Settings_SecBypass", "Обход блокировок", "Bypass censorship");
        Add("Settings_Mux", "Мультиплексирование (Mux)", "Multiplexing (Mux)");
        Add("Settings_MuxHint", "Объединяет запросы в один канал", "Combines requests into a single channel");
        Add("Settings_MuxCount", "Число подключений Mux", "Mux connection count");
        Add("Settings_Fragment", "Фрагментация пакетов", "Packet fragmentation");
        Add("Settings_FragmentHint", "Разбивает TLS-рукопожатие против DPI", "Splits the TLS handshake to defeat DPI");

        Add("Settings_SecPerformance", "Производительность", "Performance");
        Add("Settings_LiteMode", "Облегчённый режим", "Lite mode");
        Add("Settings_LiteModeHint", "Отключает анимации, снижает нагрузку", "Disables animations, reduces load");

        Add("Settings_SecInterface", "Интерфейс", "Interface");
        Add("Settings_Appearance", "Оформление", "Appearance");
        // «Чёрно-белый режим» matches Android settings_theme_mono; «Монохром» was a second word
        // for the same setting.
        Add("Settings_Monochrome", "Чёрно-белый режим", "Black and white");
        Add("Settings_MonochromeHint", "Поверх тёмной или светлой темы", "Over the dark or light theme");
        Add("Settings_Language", "Язык", "Language");
        Add("Settings_Autostart", "Запуск при загрузке", "Launch at startup");
        Add("Settings_AutostartHint", "Открывать departament при входе в систему", "Open departament when you sign in");

        Add("Settings_SecSubscription", "Подписка", "Subscription");
        // ВЛАДЕЛЕЦ B1: «подписка», не «провайдер» — см. L.Common.cs.
        Add("Settings_SubAutoUpdate", "Автообновление подписок", "Auto-update subscriptions");
        Add("Settings_Routing", "Маршрутизация", "Routing");
        Add("Settings_GeoFiles", "Файлы ресурсов", "Resource files");

        Add("Settings_About", "О приложении", "About");
        Add("Settings_Backup", "Резервное копирование", "Backup");
        Add("Settings_UrlSchemes", "Схемы URL-адресов", "URL schemes");
        Add("Settings_UrlSchemesHint", "Быстрые команды depv://", "Quick depv:// commands");

        // ── SettingsViewModel: display-value resolvers (language-aware) ──
        Add("Settings_PerAppExcept", "кроме", "except");
        Add("Settings_PerAppOnly", "только", "only");
        Add("Settings_ThemeLight", "Светлая", "Light");
        Add("Settings_ThemeDark", "Тёмная", "Dark");
        Add("Settings_LangRussian", "Русский", "Russian");

        // ── DnsSubView ──
        Add("Dns_Intro", "DNS-сервер, через который приложение разрешает домены при подключении. По умолчанию используется встроенный резолвер.", "The DNS server the app uses to resolve domains when connecting. The built-in resolver is used by default.");
        Add("Dns_Provider", "Провайдер", "Provider");
        Add("Dns_CustomAddress", "Свой DNS-адрес", "Custom DNS address");
        Add("Dns_CustomHint", "DoH-адрес (https://…/dns-query), DoT или обычный IP: 1.1.1.1", "DoH address (https://…/dns-query), DoT, or a plain IP: 1.1.1.1");
        Add("Dns_Advanced", "Дополнительно", "Advanced");
        Add("Dns_AdvancedHint", "Ускоряет подключение, отвечая на DNS-запросы локально (sing-box)", "Speeds up connections by answering DNS queries locally (sing-box)");

        // ── RoutingSubView ──
        Add("Routing_Intro", "Наборы правил определяют, какой трафик идёт через VPN, а какой напрямую. Выберите активный набор.", "Rule sets decide which traffic goes through the VPN and which goes direct. Pick the active set.");
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
        Add("PerApp_BypassHint", "Выбранные идут напрямую, минуя VPN", "Selected apps go direct, bypassing the VPN");
        Add("PerApp_OnlyHint", "Только выбранные идут через VPN", "Only selected apps go through the VPN");
        Add("PerApp_Apps", "Приложения", "Apps");
        Add("PerApp_AddExe", "Добавить .exe", "Add .exe");
        Add("PerApp_TunHint", "Работает в режиме TUN (sing-box). Правила применяются при следующем подключении.", "Works in TUN mode (sing-box). Rules apply on the next connection.");
        Add("PerApp_ProgramFileType", "Программа", "Program");

        // ── Пинг: список выбора у строки настроек (SettingsView.ShowPingChoice) — only Real / TCP ──
        Add("Ping_RealTitle", "Реальная задержка", "Real latency");
        Add("Ping_RealHint", "Через ядро, как при подключении", "Through the core, as when connected");
        Add("Ping_TcpHint", "TCP-подключение к серверу", "TCP connection to the server");
        Add("Ping_TestAddress", "Адрес проверки задержки", "Latency test address");
        Add("Ping_Timeout", "Тайм-аут проверки, сек", "Test timeout, sec");
        // Short row label used by SettingsViewModel.ResolvePingMethodText («Реальная»); TCP/HTTP/ICMP stay as tokens.
        Add("Ping_Real", "Реальная", "Real");

        // ── GeoFilesPage ──
        Add("Geo_Intro", "Базы geoip и geosite нужны для маршрутизации по странам и доменам. Обновляются с GitHub.", "The geoip and geosite databases are used for routing by country and domain. Updated from GitHub.");
        Add("Geo_UpdateNow", "Обновить сейчас", "Update now");
        Add("Geo_NotDownloaded", "Не загружен", "Not downloaded");
        Add("Geo_SizeUpdated", "{0} МБ · обновлён {1}", "{0} MB · updated {1}");
        Add("Geo_Updating", "Обновление…", "Updating…");
        Add("Geo_Downloading", "Загрузка баз…", "Downloading databases…");
        Add("Geo_Done", "Базы обновлены.", "Databases updated.");
        // Trailing space: GeoFilesPage.axaml.cs:89 still appends ex.Message. The copy is a complete
        // 9.4 message so the view can drop that concatenation without a new string.
        Add("Geo_Failed", "Не удалось обновить базы. Проверьте сеть и повторите. ", "Couldn't update the databases. Check your network and try again. ");

        // ── AboutPage ──
        // Placeholder before AboutPage.axaml.cs:19 overwrites it with About_VersionValue.
        Add("About_Version", "Версия", "Version");
        Add("About_VersionValue", "Версия {0}", "Version {0}");
        Add("About_TitleVersion", "departament · Версия {0}", "departament · Version {0}");
        Add("About_OpenSite", "Открыть сайт", "Open website");
        Add("About_TelegramBot", "Telegram-бот", "Telegram bot");
        Add("About_Details", "Сведения", "Details");
        Add("About_CopyDetails", "Копировать сведения", "Copy details");
        Add("About_SystemInfo", "ОС: {0}\nАрхитектура: {1}\n.NET: {2}", "OS: {0}\nArchitecture: {1}\n.NET: {2}");

        // ── BackupPage ──
        Add("Backup_Intro", "Сохраните все настройки, подписки и серверы в один .zip-файл или восстановите их из ранее сохранённой копии.", "Save all settings, subscriptions, and servers to a single .zip file, or restore them from a previous backup.");
        Add("Backup_Export", "Экспорт", "Export");
        Add("Backup_ExportHint", "Сохранить копию в файл", "Save a backup to a file");
        Add("Backup_Save", "Сохранить…", "Save…");
        Add("Backup_Import", "Импорт", "Import");
        Add("Backup_ImportHint", "Восстановить из файла, приложение перезапустится", "Restore from a file, the app will restart");
        Add("Backup_Restore", "Восстановить…", "Restore…");
        Add("Backup_Saving", "Сохранение…", "Saving…");
        Add("Backup_Saved", "Копия сохранена: {0}", "Backup saved: {0}");
        Add("Backup_SaveFailed", "Не удалось сохранить копию. Выберите другую папку и повторите.", "Couldn't save the backup. Pick another folder and try again.");
        // Trailing space: BackupPage.axaml.cs:51 / :84 still append ex.Message (see Geo_Failed).
        Add("Backup_ExportError", "Не удалось сохранить копию. Выберите другую папку и повторите. ", "Couldn't save the backup. Pick another folder and try again. ");
        Add("Backup_Restoring", "Восстановление… Приложение перезапустится.", "Restoring… The app will restart.");
        Add("Backup_ImportError", "Не удалось восстановить из файла. Выберите другой файл и повторите. ", "Couldn't restore from that file. Pick another file and try again. ");

        // ── UrlSchemesPage ──
        Add("UrlSchemes_Registration", "Регистрация схемы depv://", "depv:// scheme registration");
        Add("UrlSchemes_Register", "Зарегистрировать", "Register");
        Add("UrlSchemes_Remove", "Убрать", "Remove");
        Add("UrlSchemes_Hint", "Нажмите на схему, чтобы скопировать. Используйте их в ярлыках, скриптах или других приложениях.", "Tap a scheme to copy it. Use them in shortcuts, scripts, or other apps.");
        Add("UrlSchemes_StartTunnel", "Запустить туннель", "Start the tunnel");
        Add("UrlSchemes_OpenApp", "Открыть приложение", "Open the app");
        // Terminology lock 9.3: the tunnel state is «подключение», never «соединение».
        Add("UrlSchemes_Stop", "Отключиться", "Disconnect");
        // depv://close closes the app; UrlSchemesPage.axaml.cs:34 currently labels it UrlSchemes_Stop.
        // This key is here so that view can label the two rows apart.
        Add("UrlSchemes_Close", "Закрыть приложение", "Close the app");
        Add("UrlSchemes_Toggle", "Переключить подключение", "Toggle the connection");
        Add("UrlSchemes_Import", "Импорт (автоопределение)", "Import (auto-detect)");
        Add("UrlSchemes_AddByUrl", "Добавить по URL", "Add by URL");
        Add("UrlSchemes_WindowsOnly", "Регистрация схемы доступна только на Windows.", "Scheme registration is available on Windows only.");
        Add("UrlSchemes_Registered", "Схема зарегистрирована. Ссылки depv:// открывают departament.", "Scheme registered. depv:// links open departament.");
        Add("UrlSchemes_NotRegistered", "Схема не зарегистрирована.", "Scheme not registered.");
        Add("UrlSchemes_NoPath", "Не удалось определить путь к программе. Переустановите departament и повторите.", "Couldn't determine the app's path. Reinstall departament and try again.");
        // Trailing space: UrlSchemesPage.axaml.cs:110 / :144 still append ex.Message (see Geo_Failed).
        Add("UrlSchemes_RegisterFailed", "Не удалось зарегистрировать схему. Запустите departament от имени администратора и повторите. ", "Couldn't register the scheme. Run departament as administrator and try again. ");
        Add("UrlSchemes_RemovedOk", "Схема удалена.", "Scheme removed.");
        Add("UrlSchemes_RemoveFailed", "Не удалось убрать схему. Запустите departament от имени администратора и повторите. ", "Couldn't remove the scheme. Run departament as administrator and try again. ");

        // ── ProviderSettingsPage ──
        Add("Provider_Title", "Настройки подписок", "Subscription settings");
        Add("Provider_SecUpdates", "Обновление", "Updates");
        Add("Provider_AutoUpdate", "Автообновление", "Auto-update");
        Add("Provider_AutoUpdateHint", "Автоматически обновлять серверы подписок", "Refresh subscription servers automatically");
        Add("Provider_Interval", "Интервал обновления", "Update interval");
        Add("Provider_SecNetwork", "Сеть", "Network");
        Add("Provider_Hwid", "Идентификатор устройства (HWID)", "Device ID (HWID)");
        Add("Provider_UserAgentHint", "Отправляется ядром на исходящих подключениях", "Sent by the core on outbound connections");
    }
}
