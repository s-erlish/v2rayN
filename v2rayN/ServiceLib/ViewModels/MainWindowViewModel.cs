using System.Reactive.Concurrency;

namespace ServiceLib.ViewModels;

public class MainWindowViewModel : MyReactiveObject
{
    private static readonly string _tag = "MainWindowViewModel";

    public Interaction<Unit, string?> ReadTextFromClipboardInteraction { get; } = new();
    public Interaction<Unit, byte[]?> ScanScreenInteraction { get; } = new();
    public Interaction<Unit, string?> BrowseImageFileInteraction { get; } = new();
    public Interaction<bool?, Unit> ShowHideWindowInteraction { get; } = new();

    public bool DesignMode { get; set; }

    public ProfilesViewModel ProfilesViewModel { get; } = new();
    public MsgViewModel MsgViewModel { get; } = new();
    public ClashProxiesViewModel ClashProxiesViewModel { get; } = new();
    public ClashConnectionsViewModel ClashConnectionsViewModel { get; } = new();
    public CheckUpdateViewModel CheckUpdateViewModel { get; } = new();
    public BackupAndRestoreViewModel BackupAndRestoreViewModel { get; } = new();
    public StatusBarViewModel StatusBarViewModel { get; } = StatusBarViewModel.Instance;

    #region Menu

    //servers
    public ReactiveCommand<Unit, Unit> AddVmessServerCmd { get; }

    public ReactiveCommand<Unit, Unit> AddVlessServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddShadowsocksServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddSocksServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddHttpServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddTrojanServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddHysteria2ServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddTuicServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddWireguardServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddAnytlsServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddNaiveServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddCustomServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddPolicyGroupServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddProxyChainServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddServerViaClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> AddServerViaScanCmd { get; }
    public ReactiveCommand<Unit, Unit> AddServerViaImageCmd { get; }

    //Subscription
    public ReactiveCommand<Unit, Unit> SubSettingCmd { get; }

    public ReactiveCommand<Unit, Unit> SubUpdateCmd { get; }
    public ReactiveCommand<Unit, Unit> SubUpdateViaProxyCmd { get; }
    public ReactiveCommand<Unit, Unit> SubGroupUpdateCmd { get; }
    public ReactiveCommand<Unit, Unit> SubGroupUpdateViaProxyCmd { get; }

    //Setting
    public ReactiveCommand<Unit, Unit> OptionSettingCmd { get; }

    public ReactiveCommand<Unit, Unit> RoutingSettingCmd { get; }
    public ReactiveCommand<Unit, Unit> DNSSettingCmd { get; }
    public ReactiveCommand<Unit, Unit> FullConfigTemplateCmd { get; }
    public ReactiveCommand<Unit, Unit> GlobalHotkeySettingCmd { get; }
    public ReactiveCommand<Unit, Unit> RebootAsAdminCmd { get; }
    public ReactiveCommand<Unit, Unit> ClearServerStatisticsCmd { get; }
    public ReactiveCommand<Unit, Unit> OpenTheFileLocationCmd { get; }

    //Presets
    public ReactiveCommand<Unit, Unit> RegionalPresetDefaultCmd { get; }

    public ReactiveCommand<Unit, Unit> RegionalPresetRussiaCmd { get; }

    public ReactiveCommand<Unit, Unit> RegionalPresetIranCmd { get; }

    public ReactiveCommand<Unit, Unit> ReloadCmd { get; }

    [Reactive]
    public bool BlReloadEnabled { get; set; }

    [Reactive]
    public bool ShowClashUI { get; set; }

    [Reactive]
    public int TabMainSelectedIndex { get; set; }

    [Reactive] public bool BlIsWindows { get; set; }

    [Reactive] public bool BlNewUpdate { get; set; }

    [Reactive] public EGirdOrientation MainGirdOrientation { get; set; }

    #endregion Menu

    #region Init

    public MainWindowViewModel()
    {
        _config = AppManager.Instance.Config;
        BlIsWindows = Utils.IsWindows();
        MainGirdOrientation = _config.UiItem.MainGirdOrientation;

        #region WhenAnyValue && ReactiveCommand

        //servers
        AddVmessServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.VMess);
        });
        AddVlessServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.VLESS);
        });
        AddShadowsocksServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Shadowsocks);
        });
        AddSocksServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.SOCKS);
        });
        AddHttpServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.HTTP);
        });
        AddTrojanServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Trojan);
        });
        AddHysteria2ServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Hysteria2);
        });
        AddTuicServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.TUIC);
        });
        AddWireguardServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.WireGuard);
        });
        AddAnytlsServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Anytls);
        });
        AddNaiveServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Naive);
        });
        AddCustomServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Custom);
        });
        AddPolicyGroupServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.PolicyGroup);
        });
        AddProxyChainServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.ProxyChain);
        });
        AddServerViaClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaClipboardAsync(null);
        });
        AddServerViaScanCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaScanAsync();
        });
        AddServerViaImageCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaImageAsync();
        });

        //Subscription
        SubSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SubSettingAsync();
        });

        SubUpdateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess("", false);
        });
        SubUpdateViaProxyCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess("", true);
        });
        SubGroupUpdateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(_config.SubIndexId, false);
        });
        SubGroupUpdateViaProxyCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(_config.SubIndexId, true);
        });

        //Setting
        OptionSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await OptionSettingAsync();
        });
        RoutingSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingSettingAsync();
        });
        DNSSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await DNSSettingAsync();
        });
        FullConfigTemplateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await FullConfigTemplateAsync();
        });
        GlobalHotkeySettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var globalHotkeySettingViewModel = new GlobalHotkeySettingViewModel();
            if (await AppManager.Instance.WindowDialog.ShowDialogAsync(globalHotkeySettingViewModel) == true)
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            }
        });
        RebootAsAdminCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AppManager.Instance.RebootAsAdmin();
        });
        ClearServerStatisticsCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ClearServerStatistics();
        });
        OpenTheFileLocationCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await OpenTheFileLocation();
        });

        ReloadCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await Reload();
        });

        RegionalPresetDefaultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Default);
        });

        RegionalPresetRussiaCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Russia);
        });

        RegionalPresetIranCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Iran);
        });

        #endregion WhenAnyValue && ReactiveCommand

        #region AppEvents

        AppEvents.AddServerViaClipboardRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await SafeHandler(nameof(AddServerViaClipboardAsync), () => AddServerViaClipboardAsync(null)));

        AppEvents.HasUpdateNotified
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async bl => BlNewUpdate = bl);

        #endregion AppEvents

        var vmReloadRequestedList = new List<IObservable<Unit>>
        {
            ProfilesViewModel.ReloadRequested.AsObservable(),
            StatusBarViewModel.ReloadRequested.AsObservable(),
            CheckUpdateViewModel.ReloadRequested.AsObservable(),
        };

        foreach (var reloadRequested in vmReloadRequestedList)
        {
            reloadRequested
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(async _ => await SafeHandler(nameof(Reload), () => Reload()));
        }

        // Seamless server switch: a live server change routes here instead of Reload() so the tunnel
        // does not visibly drop (make-before-break; see MainWindowViewModel.SwitchServer).
        ProfilesViewModel.SwitchRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await SafeHandler(nameof(SwitchServer), SwitchServer));

        StatusBarViewModel.AddServerViaScanRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await SafeHandler(nameof(AddServerViaScanAsync), AddServerViaScanAsync));

        StatusBarViewModel.AddServerViaClipboardRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await SafeHandler(nameof(AddServerViaClipboardAsync), () => AddServerViaClipboardAsync(null)));

        StatusBarViewModel.ShowHideWindowRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async blShow => await SafeHandler(nameof(ShowHideWindowInteraction), async () => await ShowHideWindowInteraction.Handle(blShow)));

        StatusBarViewModel.SetDefaultServerRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async indexId => await SafeHandler(nameof(ProfilesViewModel.SetDefaultServer), () => ProfilesViewModel.SetDefaultServer(indexId)));

        StatusBarViewModel.SubscriptionsUpdateRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async blProxy => await SafeHandler(nameof(UpdateSubscriptionProcess), () => UpdateSubscriptionProcess("", blProxy)));

        _ = Init();
    }

    private async Task Init()
    {
        AppManager.Instance.ShowInTaskbar = true;

        if (DesignMode)
        {
            return;
        }

        // The whole body is guarded and SetReloadEnabled(true) lives in the finally. This task is
        // discarded by its caller (`_ = Init();`), so a throw from any single initializer used to be
        // unobserved AND skip the enable — leaving BlReloadEnabled false for the rest of the session,
        // i.e. a permanently dead Connect affordance with nothing on screen to explain it.
        try
        {
            //await ConfigHandler.InitBuiltinRouting(_config);
            await ConfigHandler.InitBuiltinDNS(_config);
            await ConfigHandler.InitBuiltinFullConfigTemplate(_config);
            await ProfileExManager.Instance.Init();
            await CoreManager.Instance.Init(_config, UpdateHandler);
            await CertPemManager.Instance.Init(_config);
            TaskManager.Instance.RegUpdateTask(_config, UpdateTaskHandler);

            if (_config.GuiItem.EnableStatistics || _config.GuiItem.DisplayRealTimeSpeed)
            {
                await StatisticsManager.Instance.Init(_config, UpdateStatisticsHandler);
            }
            await RefreshServersDispatcherAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            NoticeManager.Instance.SendMessage(ex.Message);
        }
        finally
        {
            // Consumer-VPN (Happ) model: the app starts DISCONNECTED. Do NOT auto-connect the core on
            // startup. The core is started only on an explicit user action — tapping the Home shield
            // (HomeViewModel.ConnectToggle → Reload) or picking a server (SetDefaultServer → Reload).
            // The connect/disconnect paths (Reload / CoreManager.CoreStop) remain fully intact.
            SetReloadEnabled(true);
        }
    }

    /// <summary>
    /// Runs an <c>Rx Subscribe(async …)</c> handler body. Those lambdas bind as <c>Action&lt;T&gt;</c>,
    /// so Rx discards the returned Task and the handler is effectively <c>async void</c>: a throw is
    /// neither reported nor logged, and the tap that caused it simply does nothing at all. Everything
    /// funnelled through here at least reaches the log and the message panel.
    /// </summary>
    private async Task SafeHandler(string what, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}.{what}", ex);
            NoticeManager.Instance.SendMessage(ex.Message);
        }
    }

    #endregion Init

    #region Actions

    private async Task UpdateHandler(bool notify, string msg)
    {
        NoticeManager.Instance.SendMessage(msg);
        if (notify)
        {
            NoticeManager.Instance.Enqueue(msg);
        }
        await Task.CompletedTask;
    }

    private async Task UpdateTaskHandler(bool success, string msg)
    {
        NoticeManager.Instance.SendMessageEx(msg);
        if (success)
        {
            var indexIdOld = _config.IndexId;
            await RefreshServersDispatcherAsync();

            // OFF-model guard (A2): a subscription refresh/update must NEVER auto-connect the VPN.
            // The servers/profiles are already refreshed above; only reload (which restarts the
            // core with the updated config) when a core was ALREADY running before the refresh, so
            // an active tunnel picks up the new server list. When disconnected we stop here — no
            // Reload, no core start. Without this guard the reload below fired on every Home refresh
            // (SubIndexId is empty on the Home shell), silently connecting a disconnected user.
            var wasRunning = AppManager.Instance.IsRunningCore(ECoreType.Xray)
                || AppManager.Instance.IsRunningCore(ECoreType.sing_box);
            if (wasRunning)
            {
                // If indexId changed or subIndexId is empty, directly reload.
                if (indexIdOld != _config.IndexId || _config.SubIndexId.IsNullOrEmpty())
                {
                    await Reload();
                }
                else
                {
                    // The activity config belongs to the current group.
                    var profile = await AppManager.Instance.GetProfileItem(_config.IndexId);
                    if (profile != null && profile.Subid == _config.SubIndexId)
                    {
                        await Reload();
                    }
                }
            }

            if (_config.UiItem.EnableAutoAdjustMainLvColWidth)
            {
                await ProfilesViewModel.AdjustMainLvColWidth();
            }
        }
    }

    private async Task UpdateStatisticsHandler(ServerSpeedItem update)
    {
        // Idle guard (B5): don't publish stats when the UI can't display them. IsUiHidden covers
        // both hidden-to-tray (ShowInTaskbar == false) AND minimized, so the whole 3-subscriber
        // fan-out is skipped for a window the user cannot see (previously only paused when tray-hidden).
        if (AppManager.Instance.IsUiHidden)
        {
            return;
        }
        AppEvents.DispatcherStatisticsRequested.Publish(update);
        await Task.CompletedTask;
    }

    #endregion Actions

    #region Servers && Groups

    private async Task RefreshServers()
    {
        await ProfilesViewModel.RefreshServers();
        await StatusBarViewModel.RefreshServers();

        // await Task.Delay(200);
    }

    // StartAsync, NOT Start: the async lambda binds to Start<TResult>(Func<TResult>) with TResult =
    // Task, so the awaited observable yields the INNER task and discards it — the await completed as
    // soon as RefreshServers hit its first incomplete await, not when the list was actually refreshed.
    // Every caller here reads the refreshed state immediately afterwards (Init enables Connect,
    // UpdateTaskHandler decides whether to Reload, the clipboard/scan/subscription imports repaint).
    private async Task RefreshServersDispatcherAsync()
    {
        await Observable.StartAsync(RefreshServers, RxSchedulers.MainThreadScheduler);
    }

    private async Task RefreshSubscriptions()
    {
        await Observable.StartAsync(ProfilesViewModel.RefreshSubscriptions, RxSchedulers.MainThreadScheduler);
    }

    #endregion Servers && Groups

    #region Add Servers

    public async Task AddServerAsync(EConfigType eConfigType)
    {
        ProfileItem item = new()
        {
            Subid = _config.SubIndexId,
            ConfigType = eConfigType,
            IsSub = false,
        };

        bool? ret = false;
        if (eConfigType == EConfigType.Custom)
        {
            var addServer2ViewModel = new AddServer2ViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addServer2ViewModel);
        }
        else if (eConfigType.IsGroupType())
        {
            var addGroupServerViewModel = new AddGroupServerViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addGroupServerViewModel);
        }
        else
        {
            var addServerViewModel = new AddServerViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addServerViewModel);
        }
        if (ret == true)
        {
            await RefreshServersDispatcherAsync();
            if (item.IndexId == _config.IndexId)
            {
                await Reload();
            }
        }
    }

    public async Task AddServerViaClipboardAsync(string? clipboardData)
    {
        // Bug 8: every add outcome must be OBSERVABLE. The bottom snack (Enqueue) is a no-op sink on
        // this build, and a subscription-URL add deliberately raises no snack at all, so each branch
        // below ALSO writes a concise inline status line to the message panel (SendMessageEx) — the
        // user always sees that the tap did something. Exceptions are caught + logged, never swallowed.
        try
        {
            var stringData = clipboardData;
            if (clipboardData == null)
            {
                var result = await ReadTextFromClipboardInteraction.Handle(Unit.Default);
                if (result.IsNullOrEmpty())
                {
                    // Empty/unavailable clipboard — surface it instead of a silent no-op.
                    NoticeManager.Instance.SendMessageEx("Нет данных для добавления");
                    NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                    return;
                }
                stringData = result;
            }

            // Detect a subscription-URL paste and whether that URL is already stored, so a duplicate
            // re-paste surfaces "подписка уже добавлена" instead of silently re-fetching. (AddSubItem
            // returns 0 for an existing URL too, so ret alone cannot tell new from duplicate.)
            var isSubUrl = ContainsSubscriptionUrl(stringData);
            var alreadyExists = isSubUrl && await SubscriptionUrlAlreadyExistsAsync(stringData);

            var ret = await ConfigHandler.AddBatchServers(_config, stringData, _config.SubIndexId, false);
            if (ret > 0)
            {
                await RefreshSubscriptions();
                await RefreshServersDispatcherAsync();
                if (isSubUrl)
                {
                    // Subscription add: no bottom notification (owner request) — its download progress
                    // streams into the message panel below; here we mark the add itself.
                    NoticeManager.Instance.SendMessageEx(alreadyExists
                        ? "Подписка уже добавлена, обновляю данные"
                        : "Подписка добавлена, загружаю серверы");
                }
                else
                {
                    // Direct server-link import — keep the snack AND mirror it inline.
                    var msg = string.Format(ResUI.SuccessfullyImportedServerViaClipboard, ret);
                    NoticeManager.Instance.Enqueue(msg);
                    NoticeManager.Instance.SendMessageEx(msg);
                }
                // A pasted http(s) URL only creates a SubItem — no servers were fetched yet. Download
                // them now so ProfileItems populates and onboarding is replaced (Android does this
                // immediately after import). Never starts the core (OFF-model).
                await DownloadImportedSubscriptionAsync(stringData);
            }
            else
            {
                // Nothing recognised in the pasted data (invalid / unsupported / already-present with
                // nothing new). Surface it inline as well as via the snack sink.
                NoticeManager.Instance.SendMessageEx("Нет данных для добавления");
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            }
        }
        catch (Exception ex)
        {
            // Never let the import path throw into an unobserved fire-and-forget task.
            Logging.SaveLog("AddServerViaClipboardAsync", ex);
            NoticeManager.Instance.SendMessageEx(ResUI.OperationFailed);
        }
    }

    public async Task AddServerViaScanAsync()
    {
        var result = await ScanScreenInteraction.Handle(Unit.Default);
        await ScanScreenResult(result);
    }

    public async Task ScanScreenResult(byte[]? bytes)
    {
        var result = QRCodeUtils.ParseBarcode(bytes);
        await AddScanResultAsync(result);
    }

    public async Task AddServerViaImageAsync()
    {
        var imageFileName = await BrowseImageFileInteraction.Handle(Unit.Default);
        await AddScanResultAsync(imageFileName);
    }

    public async Task ScanImageResult(string fileName)
    {
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        var result = QRCodeUtils.ParseBarcode(fileName);
        await AddScanResultAsync(result);
    }

    private async Task AddScanResultAsync(string? result)
    {
        // Bug 8: mirror the clipboard path — every outcome is surfaced inline (message panel) so a
        // scan is never a silent no-op, and the whole flow is wrapped so nothing throws unobserved.
        try
        {
            if (result.IsNullOrEmpty())
            {
                NoticeManager.Instance.SendMessageEx(ResUI.NoValidQRcodeFound);
                NoticeManager.Instance.Enqueue(ResUI.NoValidQRcodeFound);
                return;
            }

            var isSubUrl = ContainsSubscriptionUrl(result);
            var alreadyExists = isSubUrl && await SubscriptionUrlAlreadyExistsAsync(result);

            var ret = await ConfigHandler.AddBatchServers(_config, result, _config.SubIndexId, false);
            if (ret > 0)
            {
                await RefreshSubscriptions();
                await RefreshServersDispatcherAsync();
                if (isSubUrl)
                {
                    // Subscription add: no bottom notification (owner request) — mark it inline instead.
                    NoticeManager.Instance.SendMessageEx(alreadyExists
                        ? "Подписка уже добавлена, обновляю данные"
                        : "Подписка добавлена, загружаю серверы");
                }
                else
                {
                    // Direct server-link scan — keep the snack AND mirror it inline.
                    NoticeManager.Instance.Enqueue(ResUI.SuccessfullyImportedServerViaScan);
                    NoticeManager.Instance.SendMessageEx(ResUI.SuccessfullyImportedServerViaScan);
                }
                // A scanned http(s) URL only creates a SubItem — fetch its servers now (OFF-model:
                // never starts the core) so the list populates and onboarding is replaced.
                await DownloadImportedSubscriptionAsync(result);
            }
            else
            {
                NoticeManager.Instance.SendMessageEx("Нет данных для добавления");
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AddScanResultAsync", ex);
            NoticeManager.Instance.SendMessageEx(ResUI.OperationFailed);
        }
    }

    /// <summary>
    /// When freshly-imported clipboard/QR content contains an http(s) subscription URL, download its
    /// servers right away (the import step only created a SubItem). This mirrors Android, which
    /// downloads immediately after import so the first-run onboarding flow does not dead-lock with an
    /// empty server list. OFF-model contract: this path NEVER starts the core — the dedicated
    /// log-only handler refreshes the list/meta without a Reload(), unlike the scheduled-update path.
    /// </summary>
    private async Task DownloadImportedSubscriptionAsync(string? importedData)
    {
        if (!ContainsSubscriptionUrl(importedData))
        {
            return;
        }

        // No bottom toast on subscription add — the engine progress already streams into the message
        // panel via SubscriptionImportLogHandler. (Owner request: adding a subscription must raise no
        // bottom notifications.)
        await Task.Run(async () => await SubscriptionHandler.UpdateProcess(_config, "", false, SubscriptionImportLogHandler));

        await RefreshSubscriptions();
        await RefreshServersDispatcherAsync();
    }

    private static bool ContainsSubscriptionUrl(string? data)
    {
        if (data.IsNullOrEmpty())
        {
            return false;
        }
        return data.Split('\n', '\r')
            .Any(line => line.Trim().StartsWith(Global.HttpsProtocol) || line.Trim().StartsWith(Global.HttpProtocol));
    }

    /// <summary>
    /// True when every http(s) subscription URL contained in <paramref name="data"/> is already
    /// stored as a <c>SubItem</c> — i.e. a re-paste/re-scan of a subscription that was added before.
    /// <see cref="ConfigHandler.AddSubItem(Config, string)"/> returns 0 for an already-existing URL as
    /// well as for a freshly-added one, so the add counter cannot distinguish them; this pre-check
    /// lets the add path surface "подписка уже добавлена" instead of silently re-fetching.
    /// </summary>
    private static async Task<bool> SubscriptionUrlAlreadyExistsAsync(string? data)
    {
        if (data.IsNullOrEmpty())
        {
            return false;
        }
        var urls = data.Split('\n', '\r')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(Global.HttpsProtocol) || line.StartsWith(Global.HttpProtocol))
            .ToList();
        if (urls.Count == 0)
        {
            return false;
        }
        var subItems = await AppManager.Instance.SubItems();
        if (subItems is null || subItems.Count == 0)
        {
            return false;
        }
        var existing = subItems.Select(s => s.Url).Where(u => u.IsNotEmpty()).ToHashSet();
        return urls.All(existing.Contains);
    }

    /// <summary>
    /// Log-only completion handler for an import-triggered subscription download. Routes engine
    /// progress to the message panel (no toast spam) exactly like <c>UpdateTaskHandler</c>, but
    /// deliberately omits its <c>Reload()</c> so importing a subscription on a disconnected app does
    /// not silently connect it (OFF-model). The list/meta refresh is done by the caller once.
    /// </summary>
    private static async Task SubscriptionImportLogHandler(bool success, string msg)
    {
        NoticeManager.Instance.SendMessageEx(msg);
        await Task.CompletedTask;
    }

    #endregion Add Servers

    #region Subscription

    private async Task SubSettingAsync()
    {
        var subSettingViewModel = new SubSettingViewModel();
        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(subSettingViewModel) == true)
        {
            await RefreshSubscriptions();
        }
    }

    public async Task UpdateSubscriptionProcess(string subId, bool blProxy)
    {
        await Task.Run(async () => await SubscriptionHandler.UpdateProcess(_config, subId, blProxy, UpdateTaskHandler));
    }

    #endregion Subscription

    #region Setting

    private async Task OptionSettingAsync()
    {
        var settingViewModel = new OptionSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(settingViewModel);
        if (ret == true)
        {
            MainGirdOrientation = _config.UiItem.MainGirdOrientation;
            RxSchedulers.MainThreadScheduler.Schedule(async () =>
            {
                await StatusBarViewModel.InboundDisplayStatus();
            });
            await Reload();
        }
    }

    private async Task RoutingSettingAsync()
    {
        var routingSettingViewModel = new RoutingSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(routingSettingViewModel);
        if (ret == true)
        {
            await ConfigHandler.InitBuiltinRouting(_config);
            RxSchedulers.MainThreadScheduler.Schedule(async () =>
            {
                await StatusBarViewModel.RefreshRoutingsMenu();
            });
            await Reload();
        }
    }

    private async Task DNSSettingAsync()
    {
        var dnsSettingViewModel = new DNSSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(dnsSettingViewModel);
        if (ret == true)
        {
            await Reload();
        }
    }

    private async Task FullConfigTemplateAsync()
    {
        var fullConfigTemplateViewModel = new FullConfigTemplateViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(fullConfigTemplateViewModel);
        if (ret == true)
        {
            await Reload();
        }
    }

    private async Task ClearServerStatistics()
    {
        await StatisticsManager.Instance.ClearAllServerStatistics();
        await RefreshServersDispatcherAsync();
    }

    private async Task OpenTheFileLocation()
    {
        var path = Utils.StartupPath();
        if (Utils.IsWindows())
        {
            ProcUtils.ProcessStart(path);
        }
        else if (Utils.IsLinux())
        {
            ProcUtils.ProcessStart("xdg-open", path);
        }
        else if (Utils.IsMacOS())
        {
            ProcUtils.ProcessStart("open", path);
        }
        await Task.CompletedTask;
    }

    #endregion Setting

    #region core job

    // Pending follow-up job for whoever owns _reloadSemaphore. Ordered by STRENGTH: a full reload
    // subsumes a seamless switch, never the other way round. The old protocol was a single non-volatile
    // bool, which destroyed the job's KIND at defer time — so every deferred SwitchServer replayed as a
    // Reload (CoreStopInternal → CoreRunningStateChanged(false) → a visible disconnect and, on Windows,
    // a TUN adapter flap) instead of the make-before-break swap the feature exists to provide.
    private const int JobNone = 0;
    private const int JobSwitch = 1;
    private const int JobReload = 2;

    // int + Interlocked, not a plain bool: this is written from the UI scheduler (a user picking a
    // server) and read from the TaskManager pool thread (the hourly subscription auto-update reload),
    // so it needs both atomicity and the fences the Interlocked ops provide.
    private int _pendingJob = JobNone;
    private readonly SemaphoreSlim _reloadSemaphore = new(1, 1);

    /// <summary>
    /// Publishes a follow-up job, keeping whichever of the pending/new job is stronger. Always executes
    /// at least one <see cref="Interlocked.CompareExchange(ref int, int, int)"/>, so the publish is a
    /// full fence — which the correctness argument in <see cref="PublishPendingJob"/> relies on.
    /// </summary>
    private void PublishJobKind(int job)
    {
        int seen, next;
        do
        {
            seen = Volatile.Read(ref _pendingJob);
            next = seen > job ? seen : job;
        }
        while (Interlocked.CompareExchange(ref _pendingJob, next, seen) != seen);
    }

    /// <summary>
    /// Defers <paramref name="job"/> to the current gate owner AND closes the lost-wakeup window that
    /// used to strand it. Returns true only when this call actually ran the job itself.
    ///
    /// The window it closes: a requester whose <c>WaitAsync(0)</c> failed could be preempted before its
    /// write, the owner could then release and run its post-release check (seeing nothing), and the
    /// write would land afterwards with nobody left to consume it — the user's server switch was simply
    /// never performed, while the list already painted the new row active and the shield settled to
    /// "connected" on its 12 s deadline.
    ///
    /// Why the re-probe below makes that impossible rather than merely rarer. After the publish (a full
    /// fence) exactly two cases exist, and both consume the marker:
    ///   * the probe FAILS -> some owner holds the gate right now. Its release happens after our probe,
    ///     which happens after our publish, so its finally-block drain is guaranteed to observe the
    ///     marker (or to lose the Interlocked.Exchange race to us, which is the same thing).
    ///   * the probe SUCCEEDS -> no owner holds the gate, so no future drain is scheduled by anyone
    ///     else; we drain it here. If we are racing an owner that has released but not yet drained,
    ///     the single Interlocked.Exchange in DrainPendingJob decides which of us runs it — exactly
    ///     one does, never zero and never both.
    /// The recursion (drain -> Reload/SwitchServer -> possibly defer again) needs a fresh owner to
    /// appear between our Release and the inner WaitAsync each time, and the marker survives every
    /// round, so it terminates and cannot lose work.
    /// </summary>
    private async Task<bool> PublishPendingJob(int job)
    {
        PublishJobKind(job);

        if (!await _reloadSemaphore.WaitAsync(0))
        {
            return false;
        }

        _reloadSemaphore.Release();
        return await DrainPendingJob();
    }

    /// <summary>
    /// Atomically claims the pending job and runs it in its ORIGINAL form. Exactly one caller can claim
    /// a given marker, so a drain racing a publisher's re-probe never double-runs it.
    /// </summary>
    private async Task<bool> DrainPendingJob()
    {
        var job = Interlocked.Exchange(ref _pendingJob, JobNone);
        switch (job)
        {
            case JobReload:
                return await Reload();

            case JobSwitch:
                await SwitchServer();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Returns true when THIS call actually ran the core-load attempt to completion (whatever its
    /// outcome), false ONLY when it deferred to an already-in-flight reload (semaphore contended). The
    /// connect UI uses this so a deferred call is NOT mistaken for a connect failure: the in-flight
    /// owner's follow-up job (_hasNextReloadJob) will still bring the core up, so judging "not connected"
    /// the instant this deferred call returns would paint a false error though a connect is still coming.
    /// </summary>
    public async Task<bool> Reload()
    {
        //If there are unfinished reload job, marked with next job.
        if (!await _reloadSemaphore.WaitAsync(0))
        {
            return await PublishPendingJob(JobReload);
        }

        if (DesignMode)
        {
            _reloadSemaphore.Release();
            await DrainPendingJob();
            return true;
        }

        try
        {
            SetReloadEnabled(false);

            var profileItem = await ConfigHandler.GetDefaultServer(_config);
            if (profileItem == null)
            {
                NoticeManager.Instance.Enqueue(ResUI.CheckServerSettings);
                return true;
            }
            var allResult = await CoreConfigContextBuilder.BuildAll(_config, profileItem);
            if (NoticeManager.Instance.NotifyValidatorResult(allResult.CombinedValidatorResult) && !allResult.Success)
            {
                return true;
            }

            await Task.Run(async () =>
            {
                await LoadCore(allResult.MainResult.Context, allResult.PreSocksResult?.Context);
                await SysProxyHandler.UpdateSysProxy(_config, false);
                await Task.Delay(1000);
            });
            RxSchedulers.MainThreadScheduler.Schedule(async () =>
            {
                await StatusBarViewModel.TestServerAvailability();
            });

            var showClashUI = AppManager.Instance.IsRunningCore(ECoreType.sing_box);
            if (showClashUI)
            {
                //await Observable.Start(async () =>
                //{
                //    await ClashProxiesViewModel.ProxiesReload();
                //}, RxSchedulers.MainThreadScheduler);
                RxSchedulers.MainThreadScheduler.Schedule(async () =>
                {
                    await ClashProxiesViewModel.ProxiesReload();
                });
            }

            ReloadResult(showClashUI);
        }
        finally
        {
            SetReloadEnabled(true);
            _reloadSemaphore.Release();
            //If there is a next job, execute it IN ITS ORIGINAL FORM (switch stays a switch).
            await DrainPendingJob();
        }

        return true;
    }

    /// <summary>
    /// Seamless live server switch. Mirrors <see cref="Reload"/> but routes to
    /// <see cref="CoreManager.SwitchServer"/> (make-before-break: hot-swap → Xray-only restart → full
    /// restart) instead of <see cref="LoadCore"/>, and drops the unconditional 1 s settle delay — a
    /// switch keeps the same deterministic ports/TUN, so there is nothing to wait for. The core path
    /// never calls CoreStop nor resets RunningCoreType, so the Home shield/tray/status bar keep reading
    /// "connected" (the shield's own Connecting spin masks the swap) and never flash disconnected.
    /// Shares the reload semaphore so a switch and a reload can never run concurrently.
    /// </summary>
    public async Task SwitchServer()
    {
        if (!await _reloadSemaphore.WaitAsync(0))
        {
            // A reload/switch is already in flight; mark a follow-up so the newest target still lands —
            // as a SWITCH, so the seamless path is not silently downgraded to a full stop/start.
            await PublishPendingJob(JobSwitch);
            return;
        }

        if (DesignMode)
        {
            _reloadSemaphore.Release();
            await DrainPendingJob();
            return;
        }

        try
        {
            SetReloadEnabled(false);

            var profileItem = await ConfigHandler.GetDefaultServer(_config);
            if (profileItem == null)
            {
                NoticeManager.Instance.Enqueue(ResUI.CheckServerSettings);
                return;
            }
            var allResult = await CoreConfigContextBuilder.BuildAll(_config, profileItem);
            if (NoticeManager.Instance.NotifyValidatorResult(allResult.CombinedValidatorResult) && !allResult.Success)
            {
                return;
            }

            await Task.Run(async () =>
            {
                // SwitchServer internally falls back to a full LoadCore when a seamless tier is not
                // possible or fails, so the user is never left disconnected.
                await CoreManager.Instance.SwitchServer(allResult.MainResult.Context, allResult.PreSocksResult?.Context);
                // Ports are unchanged across a switch, so re-asserting the system proxy is idempotent;
                // keep it so direct/system-proxy mode stays correct. No Task.Delay here (unlike Reload).
                await SysProxyHandler.UpdateSysProxy(_config, false);
            });
            RxSchedulers.MainThreadScheduler.Schedule(async () =>
            {
                await StatusBarViewModel.TestServerAvailability();
            });

            var showClashUI = AppManager.Instance.IsRunningCore(ECoreType.sing_box);
            if (showClashUI)
            {
                RxSchedulers.MainThreadScheduler.Schedule(async () =>
                {
                    await ClashProxiesViewModel.ProxiesReload();
                });
            }

            ReloadResult(showClashUI);
        }
        finally
        {
            SetReloadEnabled(true);
            _reloadSemaphore.Release();
            await DrainPendingJob();
        }
    }

    private void ReloadResult(bool showClashUI)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            ShowClashUI = showClashUI;
            TabMainSelectedIndex = showClashUI ? TabMainSelectedIndex : 0;
        });
    }

    private void SetReloadEnabled(bool enabled)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() => BlReloadEnabled = enabled);
    }

    private async Task LoadCore(CoreConfigContext? mainContext, CoreConfigContext? preContext)
    {
        await CoreManager.Instance.LoadCore(mainContext, preContext);
    }

    #endregion core job

    #region Presets

    public async Task ApplyRegionalPreset(EPresetType type)
    {
        await ConfigHandler.ApplyRegionalPreset(_config, type);
        await ConfigHandler.InitRouting(_config);
        RxSchedulers.MainThreadScheduler.Schedule(async () =>
        {
            await StatusBarViewModel.RefreshRoutingsMenu();
        });

        await ConfigHandler.SaveConfig(_config);
        await new UpdateService(_config, UpdateTaskHandler).UpdateGeoFileAll();
        await Reload();
    }

    #endregion Presets
}
