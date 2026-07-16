using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ServiceLib.Handler.SysProxy;

namespace v2rayN.Desktop.ViewModels;

/// <summary>
/// Aggregator ViewModel for the Home screen. It WRAPS the real engine — it does not duplicate any
/// engine logic. It reuses the existing <see cref="ProfilesViewModel"/> and singleton
/// <see cref="StatusBarViewModel"/> held by <see cref="MainWindowViewModel"/>, and exposes for the
/// two-panel Home:
///   • the grouped server list (<see cref="ServerGroups"/>) projected from the real
///     <c>ProfilesViewModel.ProfileItems</c>, grouped by subscription;
///   • the empty / onboarding flags (<see cref="HasServers"/> / <see cref="IsEmpty"/>);
///   • the connect state (<see cref="IsConnected"/> / <see cref="IsConnecting"/>) derived from the
///     actually-running core;
///   • live up/down speed and a session uptime;
///   • commands: connect toggle (start via <c>MainWindowViewModel.Reload</c>, stop via
///     <c>CoreManager.CoreStop</c>), select server (<c>SetDefaultServer</c>), import (clipboard/QR),
///     refresh subscription.
///
/// Consumer-VPN (Happ) model: the app starts DISCONNECTED. The core is only started when the user
/// taps connect or picks a server — never on startup (the startup auto-connect is gated in
/// <see cref="MainWindowViewModel.Init"/>).
/// </summary>
public class HomeViewModel : MyReactiveObject, IDisposable
{
    private readonly MainWindowViewModel? _main;

    /// <summary>Real engine list VM (source of ProfileItems / SelectedProfile / ServerFilter / ping).</summary>
    public ProfilesViewModel? Profiles { get; }

    /// <summary>Real engine status VM (singleton) — running server / speed / TUN.</summary>
    public StatusBarViewModel? StatusBar { get; }

    /// <summary>Servers grouped by subscription, projected live from <c>Profiles.ProfileItems</c>.</summary>
    public ObservableCollection<HomeServerGroup> ServerGroups { get; } = new();

    private readonly Dictionary<string, bool> _groupExpanded = new();
    // Event-driven core state (B1/B3): no permanent 1s poll. The uptime tick below exists ONLY while
    // connected. Both are torn down in Dispose.
    private readonly IDisposable? _coreStateSub;
    private readonly IDisposable? _statsSub;
    private DispatcherTimer? _uptimeTimer;
    private DateTime? _connectedSince;
    private DateTime? _connectingUntil;
    private ServerSpeedItem? _lastSpeed;

    #region Reactive state

    [Reactive] public bool IsConnected { get; set; }

    [Reactive] public bool IsConnecting { get; set; }

    /// <summary>
    /// True after a TRUTHFUL connect failure (core failed to start / reload returned failed / the
    /// connect deadline elapsed) — the attempt collapsed back to disconnected. Sticky until the next
    /// connect attempt (<see cref="BeginConnecting"/>) or a successful connect clears it. Drives the
    /// hero's Error shield state (A4) via HomeHeroPresenter; NOT set for a plain user-initiated
    /// disconnect or an invalid server pick.
    /// </summary>
    [Reactive] public bool ConnectFailed { get; set; }

    [Reactive] public bool HasServers { get; set; }

    [Reactive] public bool IsEmpty { get; set; } = true;

    [Reactive] public string Subtitle { get; set; } = string.Empty;

    [Reactive] public string UpSpeed { get; set; } = "0 KB/s";

    [Reactive] public string DownSpeed { get; set; } = "0 KB/s";

    [Reactive] public string Uptime { get; set; } = "00:00:00";

    #endregion Reactive state

    #region Commands

    public ReactiveCommand<Unit, Unit> AddViaClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> AddViaQrCmd { get; }
    public ReactiveCommand<Unit, Unit> RefreshSubscriptionCmd { get; }

    #endregion Commands

    /// <summary>Runtime constructor: wires the real engine instances (no duplication).</summary>
    public HomeViewModel(MainWindowViewModel main)
    {
        _config = AppManager.Instance.Config;
        _main = main;
        Profiles = main.ProfilesViewModel;
        StatusBar = main.StatusBarViewModel;

        AddViaClipboardCmd = ReactiveCommand.CreateFromTask(AddViaClipboard);
        AddViaQrCmd = ReactiveCommand.CreateFromTask(AddViaQr);
        RefreshSubscriptionCmd = ReactiveCommand.CreateFromTask(RefreshSubscription);

        // Rebuild grouped list + counters whenever the real server collection changes.
        Profiles.ProfileItems.CollectionChanged += OnProfileItemsChanged;
        RebuildGroups();

        // Live speed from the same statistics event the status bar consumes.
        _statsSub = AppEvents.DispatcherStatisticsRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(update => _lastSpeed = update);

        // Core state is now event-driven (B1/B3): CoreRunningStateChanged fires ONLY on a true/false
        // transition and ON A BACKGROUND THREAD, so we marshal to the UI thread before touching
        // reactive state. This replaces the old permanent 1s poller — nothing wakes at idle. The
        // per-second UPTIME clock still needs a tick while connected: it is started on the "true"
        // transition and stopped+disposed on "false" (see UpdateStateTick).
        _coreStateSub = AppEvents.CoreRunningStateChanged
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnCoreRunningStateChanged);

        // Reflect whatever the core is doing right now (it may already be running when this VM builds).
        SyncState();
        UpdateStateTick();
    }

    /// <summary>Design-time constructor: sample groups only, no engine, no timers.</summary>
    private HomeViewModel()
    {
    }

    #region Connect / disconnect (wraps engine — does NOT auto-connect on startup)

    /// <summary>Shield tap: connect if idle, disconnect if running.</summary>
    public void ConnectToggle()
    {
        if (IsConnected)
        {
            _ = Disconnect();
        }
        else
        {
            _ = Connect();
        }
    }

    private async Task Connect()
    {
        if (_main == null)
        {
            return;
        }
        BeginConnecting();
        // Reload builds the config and starts the core with the current default server.
        await _main.Reload();

        // Truthful failure: Reload has fully awaited the core start, so if the core is not actually
        // running the attempt failed (wrong exe / bad config / blocked). Do NOT leave the shield
        // spinning up to 12s and never read as connected — clear the connecting state now and
        // surface the failure. Verbatim Android string (values-ru/strings.xml toast_status_failed).
        if (!IsCoreRunning())
        {
            IsConnecting = false;
            _connectingUntil = null;
            // A4: surface a distinct FAILURE state instead of collapsing silently to Idle. Sticky
            // until the next attempt / a successful connect; drives the hero's Error shield.
            ConnectFailed = true;
            AppEvents.SendSnackMsgRequested.Publish("Не удалось подключиться");
        }
        SyncState();
    }

    private async Task Disconnect()
    {
        IsConnecting = false;
        _connectingUntil = null;
        // A deliberate user disconnect is not a failure — clear any lingering error shield.
        ConnectFailed = false;
        await CoreManager.Instance.CoreStop();
        // Clear the Windows system proxy so the user keeps internet after disconnecting. Without
        // this the OS keeps routing through the now-dead 127.0.0.1:port and every browser breaks
        // until reconnect/reboot. forceDisable=true mirrors AppManager.AppExitAsync.
        await SysProxyHandler.UpdateSysProxy(_config, true);
        _connectedSince = null;
        SyncState();
    }

    /// <summary>Server-row click: make it the default server, then connect per the W1d contract.</summary>
    public async Task SelectServer(string? indexId)
    {
        if (Profiles == null || indexId.IsNullOrEmpty())
        {
            return;
        }

        // Capture BEFORE the call (SetDefaultServer mutates _config.IndexId):
        //   changed      — does this pick move the active default? (its Reload connects)
        //   wasConnected — was a core already running? (an in-place switch, no spinner)
        // W1d SetDefaultServer contract: returns true = ready to connect; when the pick is ALREADY
        // the default it returns true WITHOUT reloading (the caller must Connect); when the pick
        // CHANGES it persists + Reload()s (which connects, incl. while disconnected); false = do not
        // connect. So: any server tap while disconnected is a connect intent — spin the shield now.
        var changed = indexId != _config?.IndexId;
        var wasConnected = IsConnected;
        if (!wasConnected)
        {
            BeginConnecting();
        }

        if (!await Profiles.SetDefaultServer(indexId))
        {
            // Invalid / failed pick — abort the spinner, do not connect.
            IsConnecting = false;
            _connectingUntil = null;
            SyncState();
            return;
        }

        // Re-tapping the ALREADY-active server while disconnected does not reload (nothing changed),
        // so connect explicitly — this is the A5 fix (that tap used to be dead). When the pick changed,
        // SetDefaultServer's Reload already connects, so a second Connect() here would double-connect.
        if (!changed && !wasConnected)
        {
            await Connect();
        }
        else
        {
            SyncState();
        }
    }

    private void BeginConnecting()
    {
        IsConnecting = true;
        // A new attempt clears the previous failure (A4): the hero leaves Error for Connecting.
        ConnectFailed = false;
        // Safety deadline so a failed connect can't leave the shield spinning forever.
        _connectingUntil = DateTime.Now.AddSeconds(12);
        // Run the transient tick while pending so the deadline is actually evaluated even when the
        // connect came from a fire-and-forget Reload (server switch) that raises no failure event.
        UpdateStateTick();
    }

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);

    private void SyncState()
    {
        var running = IsCoreRunning();
        if (running)
        {
            _connectedSince ??= DateTime.Now;
            IsConnected = true;
            IsConnecting = false;
            _connectingUntil = null;
            // A successful connect clears any error shield (A4).
            ConnectFailed = false;
            Uptime = FormatUptime(DateTime.Now - _connectedSince.Value);

            var s = _lastSpeed;
            if (s != null)
            {
                UpSpeed = $"{Utils.HumanFy(s.ProxyUp)}/s";
                DownSpeed = $"{Utils.HumanFy(s.ProxyDown)}/s";
            }
        }
        else
        {
            _connectedSince = null;
            IsConnected = false;
            Uptime = "00:00:00";
            UpSpeed = "0 KB/s";
            DownSpeed = "0 KB/s";

            if (IsConnecting && _connectingUntil is { } until && DateTime.Now > until)
            {
                // The connect deadline elapsed with no running core — a truthful timeout failure (A4).
                IsConnecting = false;
                _connectingUntil = null;
                ConnectFailed = true;
                // HomeViewModel is now the SOLE publisher of connect-transition snacks (StatusBar's
                // duplicate was removed). The direct Connect() path publishes on immediate failure;
                // publish here too so a fire-and-forget/server-switch connect that only fails via the
                // deadline still surfaces the same inline snack instead of silently failing.
                AppEvents.SendSnackMsgRequested.Publish("Не удалось подключиться");
            }
        }
    }

    private static string FormatUptime(TimeSpan t) =>
        $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

    /// <summary>
    /// Core start/stop transition (already marshalled to the UI thread). Sync the shield/uptime once
    /// and (re)evaluate the transient 1s tick.
    /// </summary>
    private void OnCoreRunningStateChanged(bool running)
    {
        SyncState();
        UpdateStateTick();
    }

    /// <summary>
    /// The transient 1s tick exists ONLY while connected (to advance the uptime clock) OR while a
    /// connect is pending (to enforce the connect deadline now that the permanent 1s poll is gone —
    /// the disconnected-idle app has no timer at all). It stops itself the moment neither holds.
    /// </summary>
    private void UpdateStateTick()
    {
        if (IsCoreRunning() || IsConnecting)
        {
            if (_uptimeTimer == null)
            {
                _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _uptimeTimer.Tick += OnUptimeTick;
            }
            _uptimeTimer.Start();
        }
        else
        {
            StopUptimeTick();
        }
    }

    private void OnUptimeTick(object? sender, EventArgs e)
    {
        SyncState();
        // Stop once neither connected nor still attempting — covers a normal disconnect, a
        // self-healed silent core crash, and a connect that timed out (SyncState flips the flags).
        UpdateStateTick();
    }

    private void StopUptimeTick()
    {
        if (_uptimeTimer != null)
        {
            _uptimeTimer.Stop();
            _uptimeTimer.Tick -= OnUptimeTick;
            _uptimeTimer = null;
        }
    }

    #endregion Connect / disconnect

    #region Import / refresh (wraps MainWindowViewModel)

    public async Task AddViaClipboard()
    {
        if (_main != null)
        {
            await _main.AddServerViaClipboardAsync(null);
        }
    }

    public async Task AddViaQr()
    {
        if (_main != null)
        {
            await _main.AddServerViaScanAsync();
        }
    }

    public async Task RefreshSubscription()
    {
        if (_main != null)
        {
            await _main.UpdateSubscriptionProcess("", false);
        }
    }

    #endregion Import / refresh

    #region Grouped list projection

    private void OnProfileItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildGroups();

    private void RebuildGroups()
    {
        var items = Profiles?.ProfileItems;
        var count = items?.Count ?? 0;
        HasServers = count > 0;
        IsEmpty = !HasServers;

        ServerGroups.Clear();

        var providers = 0;
        if (items != null && count > 0)
        {
            var grouped = items
                .GroupBy(i => new
                {
                    Key = i.Subid ?? string.Empty,
                    Name = string.IsNullOrEmpty(i.SubRemarks) ? "Мои серверы" : i.SubRemarks,
                })
                .ToList();

            providers = grouped.Count(g => !string.IsNullOrEmpty(g.Key.Key));
            if (providers == 0)
            {
                providers = grouped.Count;
            }

            // A9: pinned subscriptions float to the top. SubItem.Pinned is read from the in-memory
            // sub cache (Profiles.SubItems, keyed by Subid == the group key). OrderByDescending is a
            // stable sort, so unpinned groups keep their existing order underneath the pinned ones.
            var ordered = grouped
                .Select(g => new
                {
                    Group = g,
                    Pinned = Profiles?.SubItems.FirstOrDefault(s => s.Id == g.Key.Key)?.Pinned ?? false,
                })
                .OrderByDescending(x => x.Pinned)
                .ToList();

            foreach (var x in ordered)
            {
                var g = x.Group;
                var key = $"{g.Key.Key}|{g.Key.Name}";
                var expanded = !_groupExpanded.TryGetValue(key, out var ex) || ex;
                ServerGroups.Add(new HomeServerGroup(key, g.Key.Name, g.ToList(), expanded, x.Pinned, OnGroupExpandedChanged));
            }
        }

        Subtitle = $"{count} серверов · {providers} провайдеров";
    }

    private void OnGroupExpandedChanged(string key, bool expanded) => _groupExpanded[key] = expanded;

    #endregion Grouped list projection

    #region Teardown

    /// <summary>
    /// Tear down the event-driven core-state subscription, the live-speed subscription, the
    /// per-second uptime tick, and the ProfileItems change hook (B3). HomeViewModel is a plain
    /// app-lifetime VM (no view-activation lifecycle), so disposal runs when the shell disposes it.
    /// </summary>
    public void Dispose()
    {
        if (Profiles != null)
        {
            Profiles.ProfileItems.CollectionChanged -= OnProfileItemsChanged;
        }
        StopUptimeTick();
        _coreStateSub?.Dispose();
        _statsSub?.Dispose();
    }

    #endregion Teardown

    #region Design-time

    /// <summary>Design-only factory — sample rows so the previewer renders (runtime stays real/empty).</summary>
    public static HomeViewModel CreateDesign()
    {
        var vm = new HomeViewModel();
        var servers = new List<ProfileItemModel>
        {
            new() { Remarks = "Germany", ConfigType = EConfigType.VLESS, Network = "tcp", StreamSecurity = "reality", IsActive = true, IndexId = "d1" },
            new() { Remarks = "Finland", ConfigType = EConfigType.VLESS, Network = "tcp", StreamSecurity = "reality", IndexId = "d2" },
            new() { Remarks = "Netherlands", ConfigType = EConfigType.VLESS, Network = "grpc", StreamSecurity = "reality", IndexId = "d3" },
            new() { Remarks = "France", ConfigType = EConfigType.Trojan, Network = "tcp", StreamSecurity = "tls", IndexId = "d4" },
            new() { Remarks = "Japan", ConfigType = EConfigType.Shadowsocks, Network = "tcp", StreamSecurity = "", IndexId = "d5" },
        };
        vm.ServerGroups.Add(new HomeServerGroup("sub|import sub", "import sub", servers, true));
        vm.HasServers = true;
        vm.IsEmpty = false;
        vm.Subtitle = "5 серверов · 1 провайдеров";
        return vm;
    }

    #endregion Design-time
}

/// <summary>
/// One subscription group in the Home server list. <see cref="Servers"/> are real
/// <see cref="ProfileItemModel"/>s; the header is collapsible (default expanded).
/// </summary>
public sealed class HomeServerGroup : INotifyPropertyChanged
{
    private readonly Action<string, bool>? _onExpandedChanged;
    private bool _isExpanded;

    public HomeServerGroup(string key, string name, IList<ProfileItemModel> servers, bool isExpanded, bool pinned = false, Action<string, bool>? onExpandedChanged = null)
    {
        Key = key;
        Name = name;
        Servers = servers;
        _isExpanded = isExpanded;
        Pinned = pinned;
        _onExpandedChanged = onExpandedChanged;
    }

    public string Key { get; }
    public string Name { get; }
    public IList<ProfileItemModel> Servers { get; }

    /// <summary>True when this subscription is pinned — pinned groups are ordered first (A9).</summary>
    public bool Pinned { get; }

    public int Count => Servers.Count;
    public string CountText => Count.ToString();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }
            _isExpanded = value;
            _onExpandedChanged?.Invoke(Key, value);
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
