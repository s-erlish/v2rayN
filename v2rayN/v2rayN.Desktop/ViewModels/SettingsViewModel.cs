namespace v2rayN.Desktop.ViewModels;

/// <summary>
/// Desktop ViewModel backing the Incy <c>SettingsView</c>. It does NOT duplicate any engine logic —
/// it reads/writes the REAL <see cref="Config"/> (via <see cref="ConfigHandler.SaveConfig"/>) and
/// reuses the shared engine view-models, mirroring the reference <c>OptionSettingViewModel</c> /
/// <c>StatusBarViewModel</c> / <c>ThemeSettingViewModel</c>:
///
///   • TUN mode        → inline segment sets <c>TunModeItem.EnableTun</c> (passive — see <see cref="SetTunMode"/>);
///   • bypass-LAN      → <c>Inbound[0].AllowLANConn</c>          (== OptionSettingViewModel.AllowLANConn);
///   • IPv6            → <c>TunModeItem.EnableIPv6Address</c>    (== OptionSettingViewModel.TunEnableIPv6Address);
///   • Mux             → <c>Mux4SboxItem.Protocol</c> on/off     (== OptionSettingViewModel.Mux4SboxProtocol);
///   • Mux count       → <c>Mux4SboxItem.MaxConnections</c>      (cycled on tap, visible only when Mux on);
///   • Fragment        → <c>CoreBasicItem.EnableFragment</c>     (== OptionSettingViewModel.EnableFragment);
///   • local proxy     → <c>Inbound[0].LocalPort / User / Pass</c> (== OptionSettingViewModel local proxy fields);
///   • autostart       → <c>GuiItem.AutoRun</c> + <see cref="AutoStartupHandler.UpdateTask"/>;
///   • sub auto-update → <c>GuiItem.AutoUpdateInterval</c>       (cycled on tap);
///   • language        → <c>UiItem.CurrentLanguage</c>          (cycled on tap, reboot to fully apply);
///   • lite mode       → <c>UiItem.LiteMode</c>                (shared persisted reduced-motion flag);
///   • DNS / Routing   → push Incy in-app sub-pages (DnsSubView / RoutingSubView) onto the shell stack.
///
/// Every toggle/value shown reflects the current persisted config (never hardcoded); flipping a toggle
/// writes back immediately and, when the core is already running, re-applies it live via the engine's
/// <see cref="StatusBarViewModel.ReloadRequested"/> channel (consumer-VPN OFF model: it never
/// auto-connects a disconnected app — no settings row starts the core).
/// </summary>
public class SettingsViewModel : MyReactiveObject
{
    private readonly bool _designMode;

    // Protocol written to Mux4SboxItem when the Mux switch is turned on (matches Global.SingboxMuxs).
    private const string DefaultMuxProtocol = "h2mux";

    // Cycle-on-tap option sets (no picker sub-screen exists yet → advance through real values in place).
    // Auto-update interval is stored in MINUTES: 60/360/720/1440 == 1/6/12/24 ч. (default 60 = 1 ч.).
    private static readonly int[] AutoUpdateOptions = [60, 360, 720, 1440];
    private static readonly int[] MuxConcurrencyOptions = [4, 8, 16, 32, 64, 128];

    // «Масштаб интерфейса» пресеты (in-app zoom, доля). Тап по строке продвигает к следующему пресету
    // строго больше текущего (обрабатывая и промежуточные значения с горячих клавиш Ctrl +/−), с оборотом
    // на минимум. Диапазон совпадает с UiScaleState.Min..Max (0.8..2.0).
    private static readonly double[] UiScaleOptions = [0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0];

    #region Toggle-backed settings (two-way from the iOS switches → real config)

    [Reactive] public bool BypassLan { get; set; }
    [Reactive] public bool EnableIpv6 { get; set; }
    [Reactive] public bool MuxEnabled { get; set; }
    [Reactive] public bool FragmentEnabled { get; set; }
    [Reactive] public bool AutoStart { get; set; }

    /// <summary>Owner-custom «Облегчённый режим». Backed by the SHARED persisted
    /// <see cref="UIItem.LiteMode"/> flag — survives restart and is the same field the desktop
    /// animation layer reads (App/MainWindow/ConnectHeroView) to suppress motion.</summary>
    [Reactive] public bool LiteMode { get; set; }

    /// <summary>«Чёрная (AMOLED)» — a SEPARATE appearance toggle that composes ON TOP of the
    /// Тёмная/Светлая base (mirrors Android's Mono overlay over day/night). Backed by the persisted
    /// <see cref="UIItem.BlackTheme"/> flag; flipping it applies a true-black overlay live via
    /// <c>App.ApplyTheme</c> and survives restart. Independent of <see cref="AppearanceText"/>.</summary>
    [Reactive] public bool BlackTheme { get; set; }

    #endregion Toggle-backed settings

    #region Local-proxy editable fields (Inbound[0]; committed from the inline sub-panel)

    [Reactive] public string LocalPortText { get; set; } = string.Empty;
    [Reactive] public string ProxyUser { get; set; } = string.Empty;
    [Reactive] public string ProxyPass { get; set; } = string.Empty;

    #endregion Local-proxy editable fields

    #region One-way display values (read from the real config)

    [Reactive] public string ModeText { get; set; } = string.Empty;

    /// <summary>Инлайн-сегмент «Режим»: true = TUN, false = Прокси. Держится в синхроне с общим
    /// <c>StatusBarViewModel.EnableTun</c> (подписка ниже) — сегмент отражает и внешние смены TUN.</summary>
    [Reactive] public bool IsTunMode { get; set; }

    /// <summary>Инлайн-сегмент «Оформление»: true = Светлая, false = Тёмная базовая тема.
    /// Независим от отдельного монохром-оверлея (<see cref="BlackTheme"/>).</summary>
    [Reactive] public bool IsLightTheme { get; set; }

    [Reactive] public string PerAppText { get; set; } = string.Empty;
    [Reactive] public string DnsText { get; set; } = string.Empty;
    [Reactive] public string PingMethodText { get; set; } = string.Empty;
    [Reactive] public string MuxConcurrencyText { get; set; } = string.Empty;
    [Reactive] public string AppearanceText { get; set; } = string.Empty;

    /// <summary>«Масштаб интерфейса» row value — текущий in-app zoom как «NN%». Держится в синхроне с
    /// оболочкой (MainWindow горячие клавиши Ctrl +/−/0) через <c>UiScaleState.Changed</c>.</summary>
    [Reactive] public string UiScaleText { get; set; } = string.Empty;

    [Reactive] public string LanguageText { get; set; } = string.Empty;
    [Reactive] public string SubAutoUpdateText { get; set; } = string.Empty;
    [Reactive] public string AboutText { get; set; } = string.Empty;

    #endregion One-way display values

    /// <summary>Runtime constructor — binds to the live config and the shared status-bar VM.</summary>
    public SettingsViewModel()
    {
        _config = AppManager.Instance.Config;

        LoadFromConfig();
        WirePersistence();

        // Mode row reflects the shared TUN state (single source of truth). Its segment sets the config
        // directly (see SetTunMode) — passively, never routing through the reload/UAC path.
        StatusBarViewModel.Instance
            .WhenAnyValue(x => x.EnableTun)
            .Subscribe(tun =>
            {
                ModeText = tun ? "TUN" : Common.L.T("Settings_ModeProxy");
                IsTunMode = tun; // keep the inline Режим segment in sync with external TUN changes
            });

        // Строка «Масштаб интерфейса» держится в синхроне с оболочкой: когда пользователь меняет zoom
        // горячими клавишами (Ctrl +/Ctrl −/Ctrl 0 в MainWindow), UiScaleState.Changed обновляет подпись
        // здесь. Единственный рантайм-экземпляр (keep-alive SettingsView) живёт всё приложение, поэтому
        // отписка не нужна (тот же паттерн, что MotionState в MainWindow).
        v2rayN.Desktop.Common.UiScaleState.Changed += OnUiScaleStateChanged;
    }

    private void OnUiScaleStateChanged(object? sender, double scale) => UiScaleText = FormatUiScale(scale);

    /// <summary>Design-time constructor — sample strings only, never touches AppManager/config.</summary>
    private SettingsViewModel(bool design)
    {
        _designMode = true;
        ModeText = "TUN";
        IsTunMode = true;
        IsLightTheme = false;
        PerAppText = "Выкл";
        DnsText = "Cloudflare";
        PingMethodText = "Реальная";
        MuxConcurrencyText = "8";
        AppearanceText = "Тёмная";
        UiScaleText = "100%";
        LanguageText = "Русский";
        SubAutoUpdateText = "24 ч.";
        AboutText = Utils.GetVersionInfo();
        BypassLan = true;
        LocalPortText = "10808";
        // Plain v2rayN default: local SOCKS5 has no auth out of the box (empty login/password).
        ProxyUser = string.Empty;
        ProxyPass = string.Empty;
    }

    /// <summary>Design-only instance referenced from <c>Design.DataContext</c> in the axaml.</summary>
    public static SettingsViewModel Design { get; } = new(true);

    #region Load

    private void LoadFromConfig()
    {
        var inbound = _config.Inbound.FirstOrDefault();

        BypassLan = inbound?.AllowLANConn ?? false;
        EnableIpv6 = _config.TunModeItem.EnableIPv6Address;
        MuxEnabled = _config.Mux4SboxItem.Protocol.IsNotEmpty();
        FragmentEnabled = _config.CoreBasicItem.EnableFragment;
        // Показываем ФАКТ, а не намерение. На Windows автозапуск живёт в реестре, и он мог разойтись
        // с сохранённым флагом (другая реализация, задача планировщика вместо Run-значения, отключение
        // в «Диспетчере задач», восстановление конфига из бэкапа). Раньше переключатель читал только
        // конфиг и уверенно показывал «включено» у приложения, которое не стартовало, — и починить это
        // из интерфейса было нельзя, потому что обработчик выходит сразу, если значение не изменилось.
        // Reconcile приводит реестр к намерению и возвращает то, что получилось на самом деле.
        AutoStart = _designMode
            ? _config.GuiItem.AutoRun
            : v2rayN.Desktop.Common.AutostartHelper.Reconcile(_config.GuiItem.AutoRun);
        LiteMode = _config.UiItem.LiteMode;
        BlackTheme = _config.UiItem.BlackTheme;

        LocalPortText = (inbound?.LocalPort ?? 0).ToString();
        ProxyUser = inbound?.User ?? string.Empty;
        ProxyPass = inbound?.Pass ?? string.Empty;

        ModeText = StatusBarViewModel.Instance.EnableTun ? "TUN" : Common.L.T("Settings_ModeProxy");
        IsTunMode = StatusBarViewModel.Instance.EnableTun;
        IsLightTheme = _config.UiItem.CurrentTheme == nameof(ETheme.Light);
        PerAppText = ResolvePerAppText();
        DnsText = ResolveDnsText();
        PingMethodText = ResolvePingMethodText();
        MuxConcurrencyText = ResolveMuxConcurrencyText();
        AppearanceText = ResolveThemeText();
        LanguageText = ResolveLanguageText();
        SubAutoUpdateText = ResolveAutoUpdateText();
        AboutText = Utils.GetVersionInfo();

        // Читаем фактор ПРЯМО из конфига (клампя), а не из UiScaleState.Current: этот VM конструируется в
        // field-init MainWindow ДО того, как MainWindow засеет UiScaleState из конфига, поэтому Current тут
        // ещё дефолтный. Дальше подпись ведёт подписка OnUiScaleStateChanged.
        UiScaleText = FormatUiScale(v2rayN.Desktop.Common.UiScaleState.Clamp(_config.UiItem.UiScale));
    }

    #endregion Load

    #region Persistence (write-back on change → SaveConfig, reload only when the core is running)

    private void WirePersistence()
    {
        // Each subscription emits the loaded value once on subscribe; the equality guards below make
        // that initial emission a no-op, so only genuine user changes are persisted.
        this.WhenAnyValue(x => x.BypassLan).Subscribe(async v => await OnBypassLanChanged(v));
        this.WhenAnyValue(x => x.EnableIpv6).Subscribe(async v => await OnIpv6Changed(v));
        this.WhenAnyValue(x => x.MuxEnabled).Subscribe(async v => await OnMuxChanged(v));
        this.WhenAnyValue(x => x.FragmentEnabled).Subscribe(async v => await OnFragmentChanged(v));
        this.WhenAnyValue(x => x.AutoStart).Subscribe(async v => await OnAutoStartChanged(v));
        this.WhenAnyValue(x => x.LiteMode).Subscribe(async v => await OnLiteModeChanged(v));
        this.WhenAnyValue(x => x.BlackTheme).Subscribe(async v => await OnBlackThemeChanged(v));
    }

    private async Task OnBypassLanChanged(bool v)
    {
        var inbound = _config.Inbound.FirstOrDefault();
        if (_designMode || inbound == null || inbound.AllowLANConn == v)
        {
            return;
        }
        inbound.AllowLANConn = v;
        await PersistAndMaybeReload();
    }

    private async Task OnIpv6Changed(bool v)
    {
        if (_designMode || _config.TunModeItem.EnableIPv6Address == v)
        {
            return;
        }
        _config.TunModeItem.EnableIPv6Address = v;
        await PersistAndMaybeReload();
    }

    private async Task OnMuxChanged(bool v)
    {
        if (_designMode || _config.Mux4SboxItem.Protocol.IsNotEmpty() == v)
        {
            return;
        }
        _config.Mux4SboxItem.Protocol = v ? DefaultMuxProtocol : string.Empty;
        await PersistAndMaybeReload();
    }

    private async Task OnFragmentChanged(bool v)
    {
        if (_designMode || _config.CoreBasicItem.EnableFragment == v)
        {
            return;
        }
        _config.CoreBasicItem.EnableFragment = v;
        await PersistAndMaybeReload();
    }

    private async Task OnAutoStartChanged(bool v)
    {
        if (_designMode)
        {
            return;
        }
        // Намеренно НЕ выходим, когда сохранённый флаг уже равен v: расходится с реальностью именно
        // реестр, и ранний выход означал бы, что пользователь видит «включено», автозапуска нет, а
        // переключатель бессилен. Запись в реестр идемпотентна, повторить её ничего не стоит.
        var changed = _config.GuiItem.AutoRun != v;
        _config.GuiItem.AutoRun = v;
        if (changed)
        {
            await ConfigHandler.SaveConfig(_config);
        }
        // Windows: write/remove the human-readable HKCU\...\Run value «departament» → exe.
        // Non-Windows: AutostartHelper is a no-op, so the shared handler owns autostart there.
        if (Utils.IsWindows())
        {
            v2rayN.Desktop.Common.AutostartHelper.Apply(v);
        }
        else
        {
            await AutoStartupHandler.UpdateTask(_config);
        }
    }

    /// <summary>«Облегчённый режим» — a pure UI/motion flag. Persist it so it survives restart AND so the
    /// animation layer can read it live; it never touches the core, so there is deliberately no reload.</summary>
    private async Task OnLiteModeChanged(bool v)
    {
        if (_designMode || _config.UiItem.LiteMode == v)
        {
            return;
        }
        _config.UiItem.LiteMode = v;
        await ConfigHandler.SaveConfig(_config);
        // Broadcast the change so the shell (MainWindow) + connect hero (ConnectHeroView) re-apply
        // their motion state IMMEDIATELY — no restart. This is what actually stops the shield spin,
        // the tab-transition and the page rise the instant lite is enabled (and revives them off).
        v2rayN.Desktop.Common.MotionState.SetLite(v);
    }

    /// <summary>«Чёрная (AMOLED)» — a pure appearance flag. Persist it (survives restart) and apply the
    /// true-black overlay live over the current base variant; never touches the core, so no reload.</summary>
    private async Task OnBlackThemeChanged(bool v)
    {
        if (_designMode || _config.UiItem.BlackTheme == v)
        {
            return;
        }
        _config.UiItem.BlackTheme = v;
        await ConfigHandler.SaveConfig(_config);
        v2rayN.Desktop.App.ApplyTheme(_config.UiItem.CurrentTheme, v);
    }

    private async Task PersistAndMaybeReload()
    {
        await ConfigHandler.SaveConfig(_config);
        // Re-apply live only if the core is already up; a disconnected app stays disconnected.
        if (IsCoreRunning())
        {
            StatusBarViewModel.Instance.ReloadRequested.Publish();
        }
    }

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);

    #endregion Persistence

    #region Row actions (invoked from the view code-behind on tap)

    /// <summary>
    /// Режим-сегмент: set TUN vs Прокси to a SPECIFIC value (the segment picks one, it does not blind-
    /// toggle). PASSIVE setting — consumer-VPN OFF model: a settings tap must never start the core or
    /// relaunch the app. So we do NOT route through <see cref="StatusBarViewModel.EnableTun"/>'s
    /// <c>DoEnableTun</c> (which unconditionally reloads and, on non-admin Windows, calls
    /// <c>RebootAsAdmin()</c> with a UAC prompt). Instead we write the real config directly FIRST, persist,
    /// then mirror the shared VM (DoEnableTun early-returns because config already equals the new value),
    /// and re-apply live ONLY when the core is already running. Idempotent: no-op if already at
    /// <paramref name="enable"/>. TUN admin escalation belongs to the connect action, not this row.
    /// </summary>
    public async Task SetTunMode(bool enable)
    {
        if (_designMode
            || (_config.TunModeItem.EnableTun == enable && _config.TunModeItem.EnableTunEffective == enable))
        {
            return;
        }

        // EnableTun is the persisted INTENT; TunUnavailable is this session's capability. Picking TUN in
        // a session that cannot create one records the intent and leaves the session downgraded (the A6
        // banner reports it) instead of erasing the choice on the next autosave.
        _config.TunModeItem.EnableTun = enable;
        _config.TunModeItem.TunUnavailable = enable && !StatusBarViewModel.Instance.TunAvailable;
        await ConfigHandler.SaveConfig(_config);

        // Keep the shared status-bar VM in sync WITHOUT triggering its reload/UAC path: DoEnableTun
        // early-returns because the effective config already equals the value we assign here.
        var effective = _config.TunModeItem.EnableTunEffective;
        StatusBarViewModel.Instance.EnableTun = effective;
        ModeText = effective ? "TUN" : Common.L.T("Settings_ModeProxy");
        IsTunMode = effective;

        // Re-apply live only if the core is already up; a disconnected app stays disconnected.
        if (IsCoreRunning())
        {
            StatusBarViewModel.Instance.ReloadRequested.Publish();
        }
    }

    // DNS и Маршрутизация больше НЕ открывают отдельные окна. Их строки в SettingsView кладут Incy
    // in-app суб-страницы (DnsSubView / RoutingSubView) на общий стек оболочки; те пишут в тот же
    // реальный конфиг и по возврату освежают значения строк через RefreshDisplayValues (см. DnsText).

    /// <summary>Локальный прокси: commit the inline-edited port / SOCKS5 credentials to <c>Inbound[0]</c>.
    /// Invalid port → revert the field to the persisted value (never write a broken port). Reloads live
    /// only if the core is already running.</summary>
    public async Task CommitLocalProxyAsync()
    {
        if (_designMode)
        {
            return;
        }

        var inbound = _config.Inbound.FirstOrDefault();
        if (inbound == null)
        {
            return;
        }

        var user = ProxyUser?.Trim() ?? string.Empty;
        var pass = ProxyPass ?? string.Empty;

        var portOk = int.TryParse(LocalPortText?.Trim(), out var port) && port > 0 && port < Global.MaxPort;
        if (!portOk)
        {
            // Reject silently and restore the real value so the UI never shows an un-persisted port.
            LocalPortText = inbound.LocalPort.ToString();
            port = inbound.LocalPort;
        }

        var changed = inbound.LocalPort != port || (inbound.User ?? string.Empty) != user || (inbound.Pass ?? string.Empty) != pass;
        if (!changed)
        {
            return;
        }

        inbound.LocalPort = port;
        inbound.User = user;
        inbound.Pass = pass;
        await PersistAndMaybeReload();
    }

    /// <summary>Число соединений Mux row: cycle through the real option set; persists + reloads live.</summary>
    public async Task CycleMuxConcurrencyAsync()
    {
        if (_designMode)
        {
            return;
        }

        var cur = _config.Mux4SboxItem.MaxConnections;
        var idx = Array.IndexOf(MuxConcurrencyOptions, cur);
        // Unknown/0 → start at 8 (index 1), otherwise advance one step.
        var next = MuxConcurrencyOptions[idx < 0 ? 1 : (idx + 1) % MuxConcurrencyOptions.Length];

        _config.Mux4SboxItem.MaxConnections = next;
        await PersistAndMaybeReload();
        MuxConcurrencyText = next.ToString();
    }

    /// <summary>Автообновление подписки row: cycle Выкл / 6 / 12 / 24 / 48 ч; persists the real interval.</summary>
    public async Task CycleAutoUpdateAsync()
    {
        if (_designMode)
        {
            return;
        }

        var cur = _config.GuiItem.AutoUpdateInterval;
        var idx = Array.IndexOf(AutoUpdateOptions, cur);
        var next = AutoUpdateOptions[idx < 0 ? 0 : (idx + 1) % AutoUpdateOptions.Length];

        _config.GuiItem.AutoUpdateInterval = next;
        await ConfigHandler.SaveConfig(_config);
        SubAutoUpdateText = ResolveAutoUpdateText();
    }

    /// <summary>Язык row: toggle Русский ↔ English; persists the real language + culture and prompts a
    /// restart to apply everywhere. Value updates immediately.</summary>
    public async Task CycleLanguageAsync()
    {
        if (_designMode)
        {
            return;
        }

        var next = _config.UiItem.CurrentLanguage == "en" ? "ru" : "en";
        _config.UiItem.CurrentLanguage = next;
        await ConfigHandler.SaveConfig(_config);

        // Живое переключение языка без перезапуска: L.SetLanguage синхронизирует CurrentUICulture
        // (движок/ResUI) и обновляет все открытые {loc:T} биндинги; RefreshDisplayValues
        // пере-вычисляет значения строк-резолверов. Уведомление о перезапуске больше не нужно.
        Common.L.Instance.SetLanguage(next);
        RefreshDisplayValues();
    }

    /// <summary>Оформление-сегмент: set a SPECIFIC base variant (Светлая when <paramref name="light"/>,
    /// иначе Тёмная). Persists <c>UiItem.CurrentTheme</c> and applies it live through <c>App.ApplyTheme</c>
    /// — which also re-composits the separate «Монохром» overlay if it is on, so switching base never drops
    /// the overlay. The theme-flood (radial reveal of the new theme from the tapped point, 520ms) IS the
    /// feedback; App.ApplyTheme routes through it. Idempotent: no-op if already at the target variant.
    /// Both base variants carry full Incy light/dark tokens (GlobalResources ThemeDictionaries).</summary>
    public async Task SetAppearance(bool light)
    {
        if (_designMode)
        {
            return;
        }

        var target = light ? nameof(ETheme.Light) : nameof(ETheme.Dark);
        if (_config.UiItem.CurrentTheme == target)
        {
            return;
        }

        _config.UiItem.CurrentTheme = target;
        await ConfigHandler.SaveConfig(_config);
        v2rayN.Desktop.App.ApplyTheme(target, _config.UiItem.BlackTheme);
        AppearanceText = ResolveThemeText();
        IsLightTheme = light;
    }

    /// <summary>Масштаб интерфейса row: цикл по пресетам 80…200% (следующий СТРОГО больше текущего, с
    /// оборотом на минимум — корректно и после произвольных значений с горячих клавиш Ctrl +/−). Толкает
    /// фактор в общий <c>UiScaleState</c> → оболочка (MainWindow) применяет zoom мгновенно (трансформ + мин-
    /// размер + брейкпоинт), и персистит в <c>UiItem.UiScale</c>. Подпись обновит подписка на Changed;
    /// выставляем и напрямую для мгновенной отзывчивости.</summary>
    public void CycleUiScale()
    {
        if (_designMode)
        {
            return;
        }

        var cur = v2rayN.Desktop.Common.UiScaleState.Current;
        // Первый пресет строго больше текущего; если текущий ≥ максимума — оборот на минимум.
        var next = UiScaleOptions.FirstOrDefault(o => o > cur + 0.001);
        if (next < UiScaleOptions[0])
        {
            next = UiScaleOptions[0];
        }

        v2rayN.Desktop.Common.UiScaleState.Set(next);
        _config.UiItem.UiScale = next;
        _ = ConfigHandler.SaveConfig(_config);
        UiScaleText = FormatUiScale(next);
    }

    private static string FormatUiScale(double scale) => $"{Math.Round(scale * 100)}%";

    /// <summary>Re-read values that a sub-screen (per-app / ping / provider / etc.) may have changed,
    /// so the row value labels stay truthful after the dialog closes.</summary>
    public void RefreshDisplayValues()
    {
        if (_designMode)
        {
            return;
        }
        ModeText = StatusBarViewModel.Instance.EnableTun ? "TUN" : Common.L.T("Settings_ModeProxy");
        IsTunMode = StatusBarViewModel.Instance.EnableTun;
        PerAppText = ResolvePerAppText();
        DnsText = ResolveDnsText();
        PingMethodText = ResolvePingMethodText();
        SubAutoUpdateText = ResolveAutoUpdateText();
        AppearanceText = ResolveThemeText();
        IsLightTheme = _config.UiItem.CurrentTheme == nameof(ETheme.Light);
        LanguageText = ResolveLanguageText();
    }

    #endregion Row actions

    #region Display resolvers

    /// <summary>Maps the stored remote-DNS value to its friendly preset name (never the raw DoH URL).
    /// Preset URLs mirror <see cref="DnsSubView"/> / <c>Global.DomainRemoteDNSAddress</c>; an empty
    /// value is the built-in resolver («По умолчанию»), any other non-empty value is a custom entry
    /// («Свой»).</summary>
    private string ResolveDnsText()
    {
        var remote = _config.SimpleDNSItem?.RemoteDNS?.Trim();
        if (remote.IsNullOrEmpty())
        {
            return Common.L.T("Common_Default");
        }
        return remote switch
        {
            "https://cloudflare-dns.com/dns-query" => "Cloudflare",
            "https://dns.google/dns-query" => "Google",
            "https://dns.adguard-dns.com/dns-query" => "AdGuard",
            _ => Common.L.T("Common_Custom"),
        };
    }

    /// <summary>Maps the persisted ping-method key (<c>SpeedTestItem.PingMethod</c>) to its SHORT
    /// Russian row label — «Реальная» / «TCP» / «HTTP» / «ICMP» (the long "…через ядро" phrasing
    /// overflowed the row value).</summary>
    private string ResolvePingMethodText() => _config.SpeedTestItem?.PingMethod switch
    {
        "Tcping" => "TCP",
        "Httping" => "HTTP",
        "Icmping" => "ICMP",
        _ => Common.L.T("Ping_Real"),
    };

    private string ResolveMuxConcurrencyText() =>
        _config.Mux4SboxItem.MaxConnections > 0 ? _config.Mux4SboxItem.MaxConnections.ToString() : "8";

    private string ResolvePerAppText()
    {
        if (!_config.UiItem.PerAppProxyEnabled)
        {
            return Common.L.T("Common_Off");
        }
        var n = _config.UiItem.PerAppProxyList?.Count ?? 0;
        var mode = _config.UiItem.PerAppProxyBypass ? Common.L.T("Settings_PerAppExcept") : Common.L.T("Settings_PerAppOnly");
        return n > 0 ? $"{mode} {n}" : Common.L.T("Common_On");
    }

    private string ResolveThemeText() => _config.UiItem.CurrentTheme switch
    {
        nameof(ETheme.Light) => Common.L.T("Settings_ThemeLight"),
        nameof(ETheme.Dark) => Common.L.T("Settings_ThemeDark"),
        null or "" => Common.L.T("Settings_ThemeDark"),
        _ => _config.UiItem.CurrentTheme!,
    };

    private string ResolveLanguageText() => _config.UiItem.CurrentLanguage switch
    {
        "ru" => Common.L.T("Settings_LangRussian"),
        "en" => "English",
        "zh-Hans" => "简体中文",
        "zh-Hant" => "繁體中文",
        "fa" => "فارسی",
        "fr" => "Français",
        "hu" => "Magyar",
        "id" => "Bahasa Indonesia",
        null or "" => Common.L.T("Settings_LangRussian"),
        _ => _config.UiItem.CurrentLanguage,
    };

    private string ResolveAutoUpdateText()
    {
        // Stored in minutes; the row shows whole hours (60 → «1 ч.»). 0 disables.
        var n = _config.GuiItem.AutoUpdateInterval;
        return n > 0 ? Common.L.F("Common_HoursShort", n / 60) : Common.L.T("Common_Off");
    }

    #endregion Display resolvers
}
