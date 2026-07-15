namespace v2rayN.Desktop.ViewModels;

/// <summary>
/// Desktop ViewModel backing the Incy <c>SettingsView</c>. It does NOT duplicate any engine logic —
/// it reads/writes the REAL <see cref="Config"/> (via <see cref="ConfigHandler.SaveConfig"/>) and
/// reuses the shared engine view-models, mirroring the reference <c>OptionSettingViewModel</c> /
/// <c>StatusBarViewModel</c> / <c>ThemeSettingViewModel</c>:
///
///   • TUN mode        → row tap toggles <c>TunModeItem.EnableTun</c> (passive — see <see cref="ToggleTun"/>);
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
    private static readonly int[] AutoUpdateOptions = [0, 6, 12, 24, 48];
    private static readonly int[] MuxConcurrencyOptions = [4, 8, 16, 32, 64, 128];

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
    [Reactive] public string PerAppText { get; set; } = string.Empty;
    [Reactive] public string DnsText { get; set; } = string.Empty;
    [Reactive] public string PingMethodText { get; set; } = string.Empty;
    [Reactive] public string MuxConcurrencyText { get; set; } = string.Empty;
    [Reactive] public string AppearanceText { get; set; } = string.Empty;
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

        // Mode row reflects the shared TUN state (single source of truth). Its tap flips the config
        // directly (see ToggleTun) — passively, never routing through the reload/UAC path.
        StatusBarViewModel.Instance
            .WhenAnyValue(x => x.EnableTun)
            .Subscribe(tun => ModeText = tun ? "TUN" : "Прокси");
    }

    /// <summary>Design-time constructor — sample strings only, never touches AppManager/config.</summary>
    private SettingsViewModel(bool design)
    {
        _designMode = true;
        ModeText = "TUN";
        PerAppText = "Выкл";
        DnsText = "Cloudflare";
        PingMethodText = "Реальная задержка (через ядро)";
        MuxConcurrencyText = "8";
        AppearanceText = "Тёмная";
        LanguageText = "Русский";
        SubAutoUpdateText = "24 ч.";
        AboutText = Utils.GetVersionInfo();
        BypassLan = true;
        LocalPortText = "10808";
        ProxyUser = "incy_6b7e970f";
        ProxyPass = "••••••••";
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
        AutoStart = _config.GuiItem.AutoRun;
        LiteMode = _config.UiItem.LiteMode;
        BlackTheme = _config.UiItem.BlackTheme;

        LocalPortText = (inbound?.LocalPort ?? 0).ToString();
        ProxyUser = inbound?.User ?? string.Empty;
        ProxyPass = inbound?.Pass ?? string.Empty;

        ModeText = StatusBarViewModel.Instance.EnableTun ? "TUN" : "Прокси";
        PerAppText = ResolvePerAppText();
        DnsText = ResolveDnsText();
        // Ping is a fixed real-delay probe through the core (no persisted method enum to fake).
        PingMethodText = "Реальная задержка (через ядро)";
        MuxConcurrencyText = ResolveMuxConcurrencyText();
        AppearanceText = ResolveThemeText();
        LanguageText = ResolveLanguageText();
        SubAutoUpdateText = ResolveAutoUpdateText();
        AboutText = Utils.GetVersionInfo();
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
        if (_designMode || _config.GuiItem.AutoRun == v)
        {
            return;
        }
        _config.GuiItem.AutoRun = v;
        await ConfigHandler.SaveConfig(_config);
        await AutoStartupHandler.UpdateTask(_config);
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
    /// Режим row: flip TUN ↔ Прокси as a PASSIVE setting. Consumer-VPN OFF model — a settings tap must
    /// never start the core or relaunch the app. So we do NOT route through
    /// <see cref="StatusBarViewModel.EnableTun"/> (whose <c>DoEnableTun</c> unconditionally reloads and,
    /// on non-admin Windows, calls <c>RebootAsAdmin()</c> with a UAC prompt). Instead we write the real
    /// config directly, persist it, and re-apply live ONLY when the core is already running. If TUN needs
    /// admin rights, that escalation belongs to the connect action, not this row.
    /// </summary>
    public async Task ToggleTun()
    {
        if (_designMode)
        {
            return;
        }

        var enable = !_config.TunModeItem.EnableTun;
        _config.TunModeItem.EnableTun = enable;
        await ConfigHandler.SaveConfig(_config);

        // Keep the shared status-bar VM in sync WITHOUT triggering its reload/UAC path: DoEnableTun
        // early-returns because _config.TunModeItem.EnableTun already equals the value we assign here.
        StatusBarViewModel.Instance.EnableTun = enable;
        ModeText = enable ? "TUN" : "Прокси";

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
        Thread.CurrentThread.CurrentUICulture = new CultureInfo(next);
        await ConfigHandler.SaveConfig(_config);
        LanguageText = ResolveLanguageText();
        NoticeManager.Instance.Enqueue(ResUI.NeedRebootTips);
    }

    /// <summary>Оформление row: toggle Тёмная ↔ Светлая base variant. Persists <c>UiItem.CurrentTheme</c>
    /// and applies it live through <c>App.ApplyTheme</c> — which also re-composits the separate
    /// «Чёрная (AMOLED)» overlay if it is on, so switching base never drops the black overlay. Both
    /// base variants carry full Incy light/dark tokens (GlobalResources ThemeDictionaries).</summary>
    public async Task CycleAppearanceAsync()
    {
        if (_designMode)
        {
            return;
        }

        var next = _config.UiItem.CurrentTheme == nameof(ETheme.Light) ? nameof(ETheme.Dark) : nameof(ETheme.Light);
        _config.UiItem.CurrentTheme = next;
        await ConfigHandler.SaveConfig(_config);
        v2rayN.Desktop.App.ApplyTheme(next, _config.UiItem.BlackTheme);
        AppearanceText = ResolveThemeText();
    }

    /// <summary>Re-read values that a sub-screen (per-app / ping / provider / etc.) may have changed,
    /// so the row value labels stay truthful after the dialog closes.</summary>
    public void RefreshDisplayValues()
    {
        if (_designMode)
        {
            return;
        }
        PerAppText = ResolvePerAppText();
        DnsText = ResolveDnsText();
        SubAutoUpdateText = ResolveAutoUpdateText();
        AppearanceText = ResolveThemeText();
        LanguageText = ResolveLanguageText();
    }

    #endregion Row actions

    #region Display resolvers

    private string ResolveDnsText()
    {
        var remote = _config.SimpleDNSItem?.RemoteDNS;
        return remote.IsNullOrEmpty() ? "По умолчанию" : remote!;
    }

    private string ResolveMuxConcurrencyText() =>
        _config.Mux4SboxItem.MaxConnections > 0 ? _config.Mux4SboxItem.MaxConnections.ToString() : "8";

    private string ResolvePerAppText()
    {
        if (!_config.UiItem.PerAppProxyEnabled)
        {
            return "Выкл";
        }
        var n = _config.UiItem.PerAppProxyList?.Count ?? 0;
        var mode = _config.UiItem.PerAppProxyBypass ? "кроме" : "только";
        return n > 0 ? $"{mode} {n}" : "Вкл";
    }

    private string ResolveThemeText() => _config.UiItem.CurrentTheme switch
    {
        nameof(ETheme.Light) => "Светлая",
        nameof(ETheme.Dark) => "Тёмная",
        null or "" => "Тёмная",
        _ => _config.UiItem.CurrentTheme!,
    };

    private string ResolveLanguageText() => _config.UiItem.CurrentLanguage switch
    {
        "ru" => "Русский",
        "en" => "English",
        "zh-Hans" => "简体中文",
        "zh-Hant" => "繁體中文",
        "fa" => "فارسی",
        "fr" => "Français",
        "hu" => "Magyar",
        "id" => "Bahasa Indonesia",
        null or "" => "Русский",
        _ => _config.UiItem.CurrentLanguage,
    };

    private string ResolveAutoUpdateText()
    {
        var n = _config.GuiItem.AutoUpdateInterval;
        return n > 0 ? $"{n} ч." : "Выкл";
    }

    #endregion Display resolvers
}
