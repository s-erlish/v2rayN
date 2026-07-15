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
public class HomeViewModel : MyReactiveObject
{
    private readonly MainWindowViewModel? _main;

    /// <summary>Real engine list VM (source of ProfileItems / SelectedProfile / ServerFilter / ping).</summary>
    public ProfilesViewModel? Profiles { get; }

    /// <summary>Real engine status VM (singleton) — running server / speed / TUN.</summary>
    public StatusBarViewModel? StatusBar { get; }

    /// <summary>Servers grouped by subscription, projected live from <c>Profiles.ProfileItems</c>.</summary>
    public ObservableCollection<HomeServerGroup> ServerGroups { get; } = new();

    private readonly Dictionary<string, bool> _groupExpanded = new();
    private readonly DispatcherTimer? _stateTimer;
    private DateTime? _connectedSince;
    private DateTime? _connectingUntil;
    private ServerSpeedItem? _lastSpeed;

    #region Reactive state

    [Reactive] public bool IsConnected { get; set; }

    [Reactive] public bool IsConnecting { get; set; }

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
        AppEvents.DispatcherStatisticsRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(update => _lastSpeed = update);

        // 1s state sync keeps the shield / uptime / speed honest regardless of who started or
        // stopped the core (shield tap, server-row click, or the core dying).
        _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _stateTimer.Tick += (_, _) => SyncState();
        _stateTimer.Start();
        SyncState();
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
            AppEvents.SendSnackMsgRequested.Publish("Не удалось подключиться");
        }
        SyncState();
    }

    private async Task Disconnect()
    {
        IsConnecting = false;
        _connectingUntil = null;
        await CoreManager.Instance.CoreStop();
        // Clear the Windows system proxy so the user keeps internet after disconnecting. Without
        // this the OS keeps routing through the now-dead 127.0.0.1:port and every browser breaks
        // until reconnect/reboot. forceDisable=true mirrors AppManager.AppExitAsync.
        await SysProxyHandler.UpdateSysProxy(_config, true);
        _connectedSince = null;
        SyncState();
    }

    /// <summary>Server-row click: make it the default server (engine reloads → connects).</summary>
    public async Task SelectServer(string? indexId)
    {
        if (Profiles == null || indexId.IsNullOrEmpty())
        {
            return;
        }
        // SetDefaultServer only reloads when the pick actually changes the active server.
        if (indexId != _config?.IndexId && !IsConnected)
        {
            BeginConnecting();
        }
        await Profiles.SetDefaultServer(indexId);
        SyncState();
    }

    private void BeginConnecting()
    {
        IsConnecting = true;
        // Safety deadline so a failed connect can't leave the shield spinning forever.
        _connectingUntil = DateTime.Now.AddSeconds(12);
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
                IsConnecting = false;
                _connectingUntil = null;
            }
        }
    }

    private static string FormatUptime(TimeSpan t) =>
        $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

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

            foreach (var g in grouped)
            {
                var key = $"{g.Key.Key}|{g.Key.Name}";
                var expanded = !_groupExpanded.TryGetValue(key, out var ex) || ex;
                ServerGroups.Add(new HomeServerGroup(key, g.Key.Name, g.ToList(), expanded, OnGroupExpandedChanged));
            }
        }

        Subtitle = $"{count} серверов · {providers} провайдеров";
    }

    private void OnGroupExpandedChanged(string key, bool expanded) => _groupExpanded[key] = expanded;

    #endregion Grouped list projection

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

    public HomeServerGroup(string key, string name, IList<ProfileItemModel> servers, bool isExpanded, Action<string, bool>? onExpandedChanged = null)
    {
        Key = key;
        Name = name;
        Servers = servers;
        _isExpanded = isExpanded;
        _onExpandedChanged = onExpandedChanged;
    }

    public string Key { get; }
    public string Name { get; }
    public IList<ProfileItemModel> Servers { get; }

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
