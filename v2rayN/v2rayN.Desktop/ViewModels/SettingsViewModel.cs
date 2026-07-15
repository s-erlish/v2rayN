namespace v2rayN.Desktop.ViewModels;

/// <summary>
/// Desktop ViewModel backing the Incy <c>SettingsView</c>. It does NOT duplicate any engine logic —
/// it reads/writes the REAL <see cref="Config"/> (via <see cref="ConfigHandler.SaveConfig"/>) and
/// reuses the shared engine view-models, mirroring the reference <c>OptionSettingViewModel</c> /
/// <c>StatusBarViewModel</c> / <c>ThemeSettingViewModel</c>:
///
///   • TUN mode        → the singleton <see cref="StatusBarViewModel.EnableTun"/> (its own
///                       persist + UAC-escalation + reload path is reused verbatim);
///   • bypass-LAN      → <c>Inbound[0].AllowLANConn</c>          (== OptionSettingViewModel.AllowLANConn);
///   • IPv6            → <c>TunModeItem.EnableIPv6Address</c>    (== OptionSettingViewModel.TunEnableIPv6Address);
///   • Mux             → <c>Mux4SboxItem.Protocol</c> on/off     (== OptionSettingViewModel.Mux4SboxProtocol);
///   • Fragment        → <c>CoreBasicItem.EnableFragment</c>     (== OptionSettingViewModel.EnableFragment);
///   • autostart       → <c>GuiItem.AutoRun</c> + <see cref="AutoStartupHandler.UpdateTask"/>;
///   • DNS / Routing   → open the existing modal sub-screens (DNSSettingViewModel / RoutingSettingViewModel).
///
/// Every toggle/value shown reflects the current persisted config (never hardcoded); flipping a toggle
/// writes back immediately and, when the core is already running, re-applies it live via the engine's
/// <see cref="StatusBarViewModel.ReloadRequested"/> channel (consumer-VPN OFF model: it never
/// auto-connects a disconnected app).
/// </summary>
public class SettingsViewModel : MyReactiveObject
{
    private readonly bool _designMode;

    // Protocol written to Mux4SboxItem when the Mux switch is turned on (matches Global.SingboxMuxs).
    private const string DefaultMuxProtocol = "h2mux";

    #region Toggle-backed settings (two-way from the iOS switches → real config)

    [Reactive] public bool BypassLan { get; set; }
    [Reactive] public bool EnableIpv6 { get; set; }
    [Reactive] public bool MuxEnabled { get; set; }
    [Reactive] public bool FragmentEnabled { get; set; }
    [Reactive] public bool AutoStart { get; set; }

    /// <summary>Owner-custom performance flag. No engine field yet (Ф-D8) — kept in-memory for the
    /// session so the switch is real UI state, not a hardcoded value; wired to a real setting later.</summary>
    [Reactive] public bool LiteMode { get; set; }

    #endregion Toggle-backed settings

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

        // TUN mode is owned by the shared StatusBarViewModel (single source of truth). Mirror it into
        // ModeText; the row's tap flips StatusBarViewModel.EnableTun which persists + reloads itself.
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
    }

    /// <summary>Design-only instance referenced from <c>Design.DataContext</c> in the axaml.</summary>
    public static SettingsViewModel Design { get; } = new(true);

    #region Load

    private void LoadFromConfig()
    {
        BypassLan = _config.Inbound.FirstOrDefault()?.AllowLANConn ?? false;
        EnableIpv6 = _config.TunModeItem.EnableIPv6Address;
        MuxEnabled = _config.Mux4SboxItem.Protocol.IsNotEmpty();
        FragmentEnabled = _config.CoreBasicItem.EnableFragment;
        AutoStart = _config.GuiItem.AutoRun;

        ModeText = StatusBarViewModel.Instance.EnableTun ? "TUN" : "Прокси";
        // per-app split-tunnel isn't available on the desktop yet (Ф-D8) → truthfully disabled.
        PerAppText = "Выкл";
        DnsText = ResolveDnsText();
        // Ping is a fixed real-delay probe through the core (no persisted method enum to fake).
        PingMethodText = "Реальная задержка (через ядро)";
        MuxConcurrencyText = _config.Mux4SboxItem.MaxConnections > 0
            ? _config.Mux4SboxItem.MaxConnections.ToString()
            : "8";
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

    /// <summary>Режим row: flip TUN ↔ Proxy. Reuses StatusBarViewModel.DoEnableTun (persist + UAC + reload).</summary>
    public void ToggleTun()
    {
        if (_designMode)
        {
            return;
        }
        StatusBarViewModel.Instance.EnableTun = !StatusBarViewModel.Instance.EnableTun;
    }

    /// <summary>DNS row: open the real DNS settings sub-screen, then refresh the shown value / reload.</summary>
    public async Task OpenDnsAsync()
    {
        if (_designMode)
        {
            return;
        }
        var vm = new DNSSettingViewModel();
        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(vm) == true)
        {
            DnsText = ResolveDnsText();
            if (IsCoreRunning())
            {
                StatusBarViewModel.Instance.ReloadRequested.Publish();
            }
        }
    }

    /// <summary>Маршрутизация row: open the real routing sub-screen, then rebuild routings / reload.</summary>
    public async Task OpenRoutingAsync()
    {
        if (_designMode)
        {
            return;
        }
        var vm = new RoutingSettingViewModel();
        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(vm) == true)
        {
            await ConfigHandler.InitBuiltinRouting(_config);
            await StatusBarViewModel.Instance.RefreshRoutingsMenu();
            if (IsCoreRunning())
            {
                StatusBarViewModel.Instance.ReloadRequested.Publish();
            }
        }
    }

    #endregion Row actions

    #region Display resolvers

    private string ResolveDnsText()
    {
        var remote = _config.SimpleDNSItem?.RemoteDNS;
        return remote.IsNullOrEmpty() ? "По умолчанию" : remote!;
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
