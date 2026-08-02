namespace ServiceLib.ViewModels;

public class StatusBarViewModel : MyReactiveObject
{
    public Interaction<string, Unit> SetClipboardDataInteraction { get; } = new();
    public Interaction<Unit, string?> PasswordInputInteraction { get; } = new();
    public Interaction<Unit, Unit> DispatcherRefreshIconInteraction { get; } = new();
    public EventChannel<bool> SubscriptionsUpdateRequested { get; } = new();
    public EventChannel<bool?> ShowHideWindowRequested { get; } = new();

    private static readonly Lazy<StatusBarViewModel> _instance = new(() => new());
    public static StatusBarViewModel Instance => _instance.Value;

    public EventChannel<string> SetDefaultServerRequested { get; } = new();
    public EventChannel<Unit> ReloadRequested { get; } = new();
    public EventChannel<Unit> AddServerViaScanRequested { get; } = new();
    public EventChannel<Unit> AddServerViaClipboardRequested { get; } = new();

    #region ObservableCollection

    public IObservableCollection<RoutingItem> RoutingItems { get; } = new ObservableCollectionExtended<RoutingItem>();

    public IObservableCollection<ComboItem> Servers { get; } = new ObservableCollectionExtended<ComboItem>();

    [Reactive]
    public RoutingItem SelectedRouting { get; set; }

    [Reactive]
    public ComboItem SelectedServer { get; set; }

    [Reactive]
    public bool BlServers { get; set; }

    #endregion ObservableCollection

    public ReactiveCommand<Unit, Unit> AddServerViaClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> AddServerViaScanCmd { get; }
    public ReactiveCommand<Unit, Unit> SubUpdateCmd { get; }
    public ReactiveCommand<Unit, Unit> SubUpdateViaProxyCmd { get; }
    public ReactiveCommand<Unit, Unit> CopyProxyCmdToClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> NotifyLeftClickCmd { get; }
    public ReactiveCommand<Unit, Unit> ShowWindowCmd { get; }
    public ReactiveCommand<Unit, Unit> HideWindowCmd { get; }

    // departament (A6): user explicitly opts into elevation to restore the requested all-traffic TUN
    // mode when it was unavailable (unelevated). Re-drives the normal TUN enable path (RebootAsAdmin on
    // Windows / sudo prompt on Linux/macOS) rather than leaving traffic silently on system-proxy.
    public ReactiveCommand<Unit, Unit> RequestTunElevationCmd { get; }

    #region System Proxy

    [Reactive]
    public bool BlSystemProxyClear { get; set; }

    [Reactive]
    public bool BlSystemProxySet { get; set; }

    [Reactive]
    public bool BlSystemProxyNothing { get; set; }

    [Reactive]
    public bool BlSystemProxyPac { get; set; }

    public ReactiveCommand<Unit, Unit> SystemProxyClearCmd { get; }
    public ReactiveCommand<Unit, Unit> SystemProxySetCmd { get; }
    public ReactiveCommand<Unit, Unit> SystemProxyNothingCmd { get; }
    public ReactiveCommand<Unit, Unit> SystemProxyPacCmd { get; }

    [Reactive]
    public bool BlRouting { get; set; }

    [Reactive]
    public int SystemProxySelected { get; set; }

    [Reactive]
    public bool BlSystemProxyPacVisible { get; set; }

    #endregion System Proxy

    #region UI

    [Reactive]
    public string InboundDisplay { get; set; }

    [Reactive]
    public string InboundLanDisplay { get; set; }

    [Reactive]
    public string RunningServerDisplay { get; set; }

    [Reactive]
    public string RunningServerToolTipText { get; set; }

    [Reactive]
    public string RunningInfoDisplay { get; set; }

    [Reactive]
    public string SpeedProxyDisplay { get; set; }

    [Reactive]
    public string SpeedDirectDisplay { get; set; }

    [Reactive]
    public bool EnableTun { get; set; }

    [Reactive]
    public bool BlIsNonWindows { get; set; }

    // departament (A6): the ACTUAL routing mode in effect, surfaced so a TUN request is never silently
    // downgraded to system-proxy behind the user's back. "Весь трафик · TUN" when the OS-level tunnel
    // is active; "Через системный прокси" otherwise. Bindable to a status line (UI hookup is Wave 2).
    [Reactive]
    public string RoutingModeDisplay { get; set; }

    // departament (A6): true when TUN (all-traffic) can actually be enabled in THIS session — the
    // process is elevated (Windows admin) / has a sudo password (Linux/macOS). When false, TUN would
    // silently fall back to system-proxy, so the UI should offer elevation instead of leaking.
    [Reactive]
    public bool TunAvailable { get; set; }

    // departament (A6): true when the persisted config / user toggle REQUESTED TUN but it is
    // unavailable (not elevated) — traffic is really going through the system proxy, NOT the tunnel.
    // The UI shows a clear notice + a "grant elevation" affordance (RequestTunElevationCmd) instead of
    // silently routing only a subset of traffic. Cleared once TUN becomes available or the user
    // accepts system-proxy (turns the toggle off).
    [Reactive]
    public bool TunRequestedButUnavailable { get; set; }

    #endregion UI

    // departament (A6): the requested TUN intent. It is NOT tracked separately any more — it IS
    // `_config.TunModeItem.EnableTun`, the persisted intent, which the session downgrade can no longer
    // touch (TunUnavailable is [JsonIgnore] and carries the capability instead). A private duplicate is
    // exactly what let the two TUN surfaces disagree: only this view model updated it, so a change made
    // from Settings left the honest banner reporting the previous intent until the next launch.
    private bool TunRequested => _config.TunModeItem.EnableTun;

    public StatusBarViewModel()
    {
        _config = AppManager.Instance.Config;
        SelectedRouting = new();
        SelectedServer = new();
        RunningServerToolTipText = GetRunningServerToolTipText("-");
        BlSystemProxyPacVisible = Utils.IsWindows();
        BlIsNonWindows = Utils.IsNonWindows();

        // Downgrade the EFFECTIVE routing to false when this process cannot create a tunnel (so the
        // generated core config never requests one it cannot build) WITHOUT touching the persisted
        // intent. TunUnavailable is [JsonIgnore], so neither the 20-minute autosave nor the exit save
        // can write the downgrade to disk any more — previously one unelevated Windows run, or ANY
        // Linux/macOS launch (LinuxSudoPwd is in-memory only and is necessarily empty this early),
        // erased the user's TUN choice permanently and, from the second launch on, also erased the
        // "TUN requested but unavailable" banner that was supposed to report it.
        _config.TunModeItem.TunUnavailable = !AllowEnableTun();
        EnableTun = _config.TunModeItem.EnableTunEffective;
        UpdateRoutingModeStatus();

        #region WhenAnyValue && ReactiveCommand

        this.WhenAnyValue(
                x => x.SelectedRouting,
                y => y != null && !y.Remarks.IsNullOrEmpty())
            .Subscribe(async c => await RoutingSelectedChangedAsync(c));

        this.WhenAnyValue(
                x => x.SelectedServer,
                y => y != null && !y.Text.IsNullOrEmpty())
            .Subscribe(ServerSelectedChanged);

        SystemProxySelected = (int)_config.SystemProxyItem.SysProxyType;
        this.WhenAnyValue(
                x => x.SystemProxySelected,
                y => y >= 0)
            .Subscribe(async c => await DoSystemProxySelected(c));

        // Skip(1): WhenAnyValue replays the CURRENT value on subscribe, and the current value is the
        // one the constructor just seeded from the config a few lines above. Handling that replay as a
        // user toggle is what would make a downgraded session write EnableTun=false back to disk on
        // every launch, erasing the intent the [JsonIgnore] capability flag exists to protect. Only
        // real changes — the toggle, the tray, the elevation command, Settings — reach DoEnableTun.
        this.WhenAnyValue(
                x => x.EnableTun,
                y => y == true)
            .Skip(1)
            .Subscribe(async c => await DoEnableTun(c));

        CopyProxyCmdToClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await CopyProxyCmdToClipboard();
        });

        NotifyLeftClickCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowHideWindowRequested.Publish(null);
            await Task.CompletedTask;
        });
        ShowWindowCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowHideWindowRequested.Publish(true);
            await Task.CompletedTask;
        });
        HideWindowCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowHideWindowRequested.Publish(false);
            await Task.CompletedTask;
        });

        RequestTunElevationCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            // A6: honour an explicit opt-in to elevation. Record the intent and drive the same TUN
            // enable path the toggle uses. If the toggle is already on (config off = unavailable),
            // call DoEnableTun directly since WhenAnyValue won't re-fire on an unchanged value.
            _config.TunModeItem.EnableTun = true;
            if (EnableTun)
            {
                await DoEnableTun(true);
            }
            else
            {
                EnableTun = true;
            }
        });

        AddServerViaClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
            {
                await AddServerViaClipboard();
            });
        AddServerViaScanCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaScan();
        });
        SubUpdateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(false);
        });
        SubUpdateViaProxyCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(true);
        });

        //System proxy
        SystemProxyClearCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetListenerType(ESysProxyType.ForcedClear);
        });
        SystemProxySetCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetListenerType(ESysProxyType.ForcedChange);
        });
        SystemProxyNothingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetListenerType(ESysProxyType.Unchanged);
        });
        SystemProxyPacCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetListenerType(ESysProxyType.Pac);
        });

        #endregion WhenAnyValue && ReactiveCommand

        #region AppEvents

        AppEvents.DispatcherStatisticsRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async result => await UpdateStatistics(result));

        AppEvents.SysProxyChangeRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async result => await SetListenerType(result));

        #endregion AppEvents

        _ = Init();
    }

    private async Task Init()
    {
        await ConfigHandler.InitBuiltinRouting(_config);
        await RefreshRoutingsMenu();
        await InboundDisplayStatus();
        // Startup must not point the OS at a local proxy port nothing is listening on. This client
        // starts DISCONNECTED, so applying a stored "set system proxy" here breaks browsing before the
        // user has ever pressed connect — and a setting a crashed previous run left behind has to go
        // either way. So: reflect the stored choice in the UI, clear the OS setting while no core is
        // running (Unchanged is respected — UpdateSysProxy leaves it alone), and let the connect path
        // re-assert it once the core is actually up (MainWindowViewModel.Reload does exactly that).
        if (IsAnyCoreRunning())
        {
            await ChangeSystemProxyAsync(_config.SystemProxyItem.SysProxyType, true);
        }
        else
        {
            await SysProxyHandler.UpdateSysProxy(_config, true);
            await RefreshSystemProxyStatus(_config.SystemProxyItem.SysProxyType);
        }

        BlRouting = true;
    }

    private static bool IsAnyCoreRunning()
        => AppManager.Instance.IsRunningCore(ECoreType.Xray)
        || AppManager.Instance.IsRunningCore(ECoreType.sing_box);

    private async Task CopyProxyCmdToClipboard()
    {
        var cmd = Utils.IsWindows() ? "set" : "export";
        var address = $"{Global.Loopback}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}";

        var sb = new StringBuilder();
        sb.AppendLine($"{cmd} http_proxy={Global.HttpProtocol}{address}");
        sb.AppendLine($"{cmd} https_proxy={Global.HttpProtocol}{address}");
        sb.AppendLine($"{cmd} all_proxy={Global.Socks5Protocol}{address}");
        sb.AppendLine("");
        sb.AppendLine($"{cmd} HTTP_PROXY={Global.HttpProtocol}{address}");
        sb.AppendLine($"{cmd} HTTPS_PROXY={Global.HttpProtocol}{address}");
        sb.AppendLine($"{cmd} ALL_PROXY={Global.Socks5Protocol}{address}");

        await SetClipboardDataInteraction.Handle(sb.ToString());
    }

    private async Task AddServerViaClipboard()
    {
        AddServerViaClipboardRequested.Publish();
        await Task.Delay(1000);
    }

    private async Task AddServerViaScan()
    {
        AddServerViaScanRequested.Publish();
        await Task.Delay(1000);
    }

    private async Task UpdateSubscriptionProcess(bool blProxy)
    {
        SubscriptionsUpdateRequested.Publish(blProxy);
        await Task.Delay(1000);
    }

    public async Task RefreshServers()
    {
        await RefreshServersBiz();
    }

    private async Task RefreshServersBiz()
    {
        await RefreshServersMenu();

        //display running server
        var running = await ConfigHandler.GetDefaultServer(_config);
        if (running != null)
        {
            RunningServerDisplay = running.GetSummary();
            RunningServerToolTipText = GetRunningServerToolTipText(RunningServerDisplay);
        }
        else
        {
            RunningServerDisplay = ResUI.CheckServerSettings;
            RunningServerToolTipText = GetRunningServerToolTipText(RunningServerDisplay);
        }
    }

    private string GetRunningServerToolTipText(string serverInfo)
    {
        return Utils.IsLinux() ? Global.AppName : serverInfo;
    }

    private async Task RefreshServersMenu()
    {
        var lstModel = await AppManager.Instance.ProfileModels(_config.SubIndexId, "");

        if (lstModel?.Count > _config.GuiItem.TrayMenuServersLimit)
        {
            BlServers = false;
            return;
        }

        var models = new List<ComboItem>();
        BlServers = true;
        foreach (var it in lstModel)
        {
            var name = it.GetSummary();

            var item = new ComboItem() { ID = it.IndexId, Text = name };
            models.Add(item);
            if (_config.IndexId == it.IndexId)
            {
                SelectedServer = item;
            }
        }
        Servers.Clear();
        Servers.AddRange(models);
    }

    private void ServerSelectedChanged(bool c)
    {
        if (!c)
        {
            return;
        }
        if (SelectedServer == null)
        {
            return;
        }
        if (SelectedServer.ID.IsNullOrEmpty())
        {
            return;
        }
        SetDefaultServerRequested.Publish(SelectedServer.ID);
    }

    public async Task TestServerAvailability()
    {
        var item = await ConfigHandler.GetDefaultServer(_config);
        if (item == null)
        {
            return;
        }

        await TestServerAvailabilitySub(ResUI.Speedtesting);

        var msg = await Task.Run(ConnectionHandler.RunAvailabilityCheck);

        NoticeManager.Instance.SendMessageEx(msg);
        await TestServerAvailabilitySub(msg);
    }

    private async Task TestServerAvailabilitySub(string msg)
    {
        RxSchedulers.MainThreadScheduler.Schedule(msg, (scheduler, msg) =>
        {
            _ = TestServerAvailabilityResult(msg);
            return Disposable.Empty;
        });
        await Task.CompletedTask;
    }

    public async Task TestServerAvailabilityResult(string msg)
    {
        RunningInfoDisplay = msg;
        await Task.CompletedTask;
    }

    #region System proxy and Routings

    private async Task SetListenerType(ESysProxyType type)
    {
        if (_config.SystemProxyItem.SysProxyType == type)
        {
            return;
        }
        _config.SystemProxyItem.SysProxyType = type;
        await ChangeSystemProxyAsync(type, true);
        NoticeManager.Instance.SendMessageEx($"{ResUI.TipChangeSystemProxy} - {_config.SystemProxyItem.SysProxyType}");

        SystemProxySelected = (int)_config.SystemProxyItem.SysProxyType;
        await ConfigHandler.SaveConfig(_config);
    }

    public async Task ChangeSystemProxyAsync(ESysProxyType type, bool blChange)
    {
        await SysProxyHandler.UpdateSysProxy(_config, false);
        await RefreshSystemProxyStatus(type, blChange);
    }

    /// <summary>
    /// Bring the surfaced system-proxy state (the mode flags and the tray icon) in line with
    /// <paramref name="type"/>, without touching the OS setting itself.
    /// </summary>
    private async Task RefreshSystemProxyStatus(ESysProxyType type, bool blChange = true)
    {
        BlSystemProxyClear = type == ESysProxyType.ForcedClear;
        BlSystemProxySet = type == ESysProxyType.ForcedChange;
        BlSystemProxyNothing = type == ESysProxyType.Unchanged;
        BlSystemProxyPac = type == ESysProxyType.Pac;

        if (blChange)
        {
            try
            {
                await DispatcherRefreshIconInteraction.Handle(Unit.Default);
            }
            catch (UnhandledInteractionException<Unit, Unit>)
            {
                // Ignore
            }
        }
    }

    public async Task RefreshRoutingsMenu()
    {
        var routings = await AppManager.Instance.RoutingItems();

        RoutingItems.Clear();
        RoutingItems.AddRange(routings);

        SelectedRouting = routings.FirstOrDefault(t => t.IsActive == true);
    }

    private async Task RoutingSelectedChangedAsync(bool c)
    {
        if (!c)
        {
            return;
        }

        if (SelectedRouting == null)
        {
            return;
        }

        var item = await AppManager.Instance.GetRoutingItem(SelectedRouting?.Id);
        if (item is null)
        {
            return;
        }

        if (await ConfigHandler.SetDefaultRouting(_config, item) == 0)
        {
            NoticeManager.Instance.SendMessageEx(ResUI.TipChangeRouting);
            ReloadRequested.Publish();
            await DispatcherRefreshIconInteraction.Handle(Unit.Default);
        }
    }

    private async Task DoSystemProxySelected(bool c)
    {
        if (!c)
        {
            return;
        }
        if (_config.SystemProxyItem.SysProxyType == (ESysProxyType)SystemProxySelected)
        {
            return;
        }
        await SetListenerType((ESysProxyType)SystemProxySelected);
    }

    private async Task DoEnableTun(bool c)
    {
        // Compare BOTH the intent and the capability, exactly as SettingsViewModel.SetTunMode does —
        // one setting, two surfaces, one guard. Comparing the effective value alone made the toggle
        // ONE-WAY in a downgraded session: switching TUN off evaluated (true && !true) == false against
        // an already-false toggle, returned early, and wrote nothing. The persisted intent stayed stuck
        // at true, the constructor re-read that stuck true on every later launch, and the honest banner
        // could never be dismissed.
        if (_config.TunModeItem.EnableTun == EnableTun
            && _config.TunModeItem.EnableTunEffective == EnableTun)
        {
            return;
        }

        // The toggle value is the user's routing intent. It is the ONLY thing persisted; the capability
        // below is session-scoped, so the UI can honestly report a downgrade to system-proxy without
        // the downgrade ever erasing the choice.
        _config.TunModeItem.EnableTun = EnableTun;
        _config.TunModeItem.TunUnavailable = EnableTun && !AllowEnableTun();

        if (EnableTun && AllowEnableTun() == false)
        {
            // When running as a non-administrator, reboot to administrator mode. The INTENT stays true
            // on disk — the whole point of relaunching elevated is to come back with the tunnel on;
            // writing false here (as this used to) meant the elevated relaunch reappeared with TUN off.
            if (Utils.IsWindows())
            {
                UpdateRoutingModeStatus();
                await ConfigHandler.SaveConfig(_config);
                await AppManager.Instance.RebootAsAdmin();
                return;
            }
            else
            {
                var password = await PasswordInputInteraction.Handle(Unit.Default);
                if (password.IsNullOrEmpty())
                {
                    // The intent is kept; only this session is downgraded, and the A6 banner says so.
                    UpdateRoutingModeStatus();
                    await ConfigHandler.SaveConfig(_config);
                    return;
                }
                // The sudo password is now held, so the session can create the tunnel after all.
                _config.TunModeItem.TunUnavailable = false;
            }
        }

        await ConfigHandler.SaveConfig(_config);
        UpdateRoutingModeStatus();
        ReloadRequested.Publish();
    }

    /// <summary>
    /// departament (A6): recompute the surfaced routing mode + the "TUN requested but unavailable"
    /// notice from the current state. Called from the constructor and after every TUN change made
    /// HERE. Public because the same setting has a second surface (the Settings mode row) which writes
    /// the config directly: without a way to re-derive, the honest banner reported the previous intent
    /// until the next launch — up forever after the user turned the mode off, absent after they turned
    /// it on in a session that cannot tunnel.
    /// </summary>
    public void RefreshRoutingModeStatus() => UpdateRoutingModeStatus();

    private void UpdateRoutingModeStatus()
    {
        var tunActive = _config.TunModeItem.EnableTunEffective && EnableTun;
        RoutingModeDisplay = tunActive ? "Весь трафик · TUN" : "Через системный прокси";
        TunAvailable = AllowEnableTun();
        TunRequestedButUnavailable = TunRequested && !TunAvailable;
    }

    /// <summary>
    /// Guarantee this session can actually carry traffic before a connect.
    ///
    /// The app routes the OS through the tunnel (TUN) or through its local proxy (system proxy). With
    /// neither, the core starts, every surface reports "connected", and not one byte leaves through it
    /// — the reported «подключается, но не работает», with nothing on screen to explain it. A fresh
    /// config ships <see cref="ESysProxyType.ForcedClear"/> (enum default 0) and nothing in the
    /// connect path ever promotes it, so any session that does not tunnel — a downgraded one, or one
    /// where the user picked proxy mode — connects to nothing at all.
    ///
    /// So when the session will not tunnel and the system proxy is set to FORCED CLEAR, set it. Clear
    /// means "actively wipe the OS proxy", which cannot coexist with a running core: it is this app's
    /// own disconnected state. <see cref="ESysProxyType.Unchanged"/> and Pac are deliberate user
    /// choices and are left alone.
    ///
    /// Only the stored choice is written when no core is running: pointing the OS at a local port
    /// nothing is listening on would break browsing for a user who is deliberately disconnected. The
    /// connect path re-asserts the system proxy right after the core comes up, so writing the value
    /// here is enough for it to take effect on this very connect.
    /// </summary>
    public async Task EnsureTrafficPathAsync()
    {
        if (_config.TunModeItem.EnableTunEffective
            || _config.SystemProxyItem.SysProxyType != ESysProxyType.ForcedClear)
        {
            return;
        }

        _config.SystemProxyItem.SysProxyType = ESysProxyType.ForcedChange;
        SystemProxySelected = (int)ESysProxyType.ForcedChange;
        await ConfigHandler.SaveConfig(_config);

        if (IsAnyCoreRunning())
        {
            await ChangeSystemProxyAsync(ESysProxyType.ForcedChange, true);
        }
        else
        {
            await RefreshSystemProxyStatus(ESysProxyType.ForcedChange);
        }
    }

    private bool AllowEnableTun()
    {
        if (Utils.IsWindows())
        {
            return Utils.IsAdministrator();
        }
        else if (Utils.IsLinux())
        {
            return AppManager.Instance.LinuxSudoPwd.IsNotEmpty();
        }
        else if (Utils.IsMacOS())
        {
            return AppManager.Instance.LinuxSudoPwd.IsNotEmpty();
        }
        return false;
    }

    #endregion System proxy and Routings

    #region UI

    public async Task InboundDisplayStatus()
    {
        StringBuilder sb = new();
        sb.Append($"[{EInboundProtocol.mixed}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}");
        if (_config.Inbound.First().SecondLocalPortEnabled)
        {
            sb.Append($",{AppManager.Instance.GetLocalPort(EInboundProtocol.socks2)}");
        }
        sb.Append(']');
        InboundDisplay = $"{ResUI.LabLocal}:{sb}";

        if (_config.Inbound.First().AllowLANConn)
        {
            var lan = _config.Inbound.First().NewPort4LAN
                ? $"[{EInboundProtocol.mixed}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks3)}]"
                : $"[{EInboundProtocol.mixed}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}]";
            InboundLanDisplay = $"{ResUI.LabLAN}:{lan}";
        }
        else
        {
            InboundLanDisplay = $"{ResUI.LabLAN}:{Global.None}";
        }
        await Task.CompletedTask;
    }

    public async Task UpdateStatistics(ServerSpeedItem update)
    {
        if (!_config.GuiItem.DisplayRealTimeSpeed)
        {
            return;
        }

        try
        {
            if (AppManager.Instance.IsRunningCore(ECoreType.sing_box))
            {
                SpeedProxyDisplay = string.Format(ResUI.SpeedDisplayText, EInboundProtocol.mixed, Utils.HumanFy(update.ProxyUp), Utils.HumanFy(update.ProxyDown));
                SpeedDirectDisplay = string.Empty;
            }
            else
            {
                SpeedProxyDisplay = string.Format(ResUI.SpeedDisplayText, Global.ProxyTag, Utils.HumanFy(update.ProxyUp), Utils.HumanFy(update.ProxyDown));
                SpeedDirectDisplay = string.Format(ResUI.SpeedDisplayText, Global.DirectTag, Utils.HumanFy(update.DirectUp), Utils.HumanFy(update.DirectDown));
            }
        }
        catch
        {
        }
        await Task.CompletedTask;
    }

    #endregion UI
}
