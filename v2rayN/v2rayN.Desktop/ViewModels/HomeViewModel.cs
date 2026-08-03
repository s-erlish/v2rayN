using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ServiceLib.Handler.SysProxy;
using v2rayN.Desktop.Common;

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
    // Coalesces the Clear()+AddRange() CollectionChanged burst the engine emits on every
    // refresh/select into ONE deferred reconcile (see OnProfileItemsChanged / ScheduleReconcile).
    private bool _reconcilePending;
    // Event-driven core state (B1/B3): no permanent 1s poll. The uptime tick below exists ONLY while
    // connected. Both are torn down in Dispose.
    private readonly IDisposable? _coreStateSub;
    private readonly IDisposable? _switchSettledSub;
    private readonly IDisposable? _statsSub;
    private readonly IDisposable? _noticeSub;

    // Last message the engine published while THIS connect attempt was in flight, and when it landed.
    // Only a message from the current attempt may be shown as its reason (see ConnectFailReason).
    private string? _lastNotice;
    private DateTime _lastNoticeAt = DateTime.MinValue;
    private DateTime? _attemptStartedAt;
    // Per-item live-sync: latency/speed results are reported by MUTATING the ProfileItems instances in
    // place (ProfilesViewModel.SetSpeedTestResult sets Delay/DelayVal/SpeedVal/IpInfo) — a per-ITEM
    // property change that raises NO CollectionChanged, so the CollectionChanged-driven reconcile never
    // fires and the DISPLAYED rows (distinct retained instances) would never show the "Testing…" spinner
    // or the ms result. We subscribe to each source item's PropertyChanged and mirror the changed field
    // onto the matching displayed row. Re-synced on every reconcile (items are rebuilt wholesale).
    private readonly List<ProfileItemModel> _observedItems = new();
    private DispatcherTimer? _uptimeTimer;
    private DateTime? _connectedSince;
    private DateTime? _connectingUntil;
    // True ONLY during a server SWITCH from a live connection (A5: a changed default reloads the
    // core). The shield shows Connecting while the old core cycles DOWN→UP; this flag holds that
    // Connecting state until a real stop transition (CoreRunningStateChanged=false) is observed, so
    // the still-running OLD core can't snap the shield straight back to Connected before the switch
    // actually re-establishes. Cleared on the stop event, on the deadline, or on an aborted pick.
    private bool _awaitingCoreCycle;
    private ServerSpeedItem? _lastSpeed;

    // Launch-time snapshot of "does the local store hold any server?", read synchronously by the shell
    // BEFORE the first frame and handed down here so both answer the question identically. It is the
    // stand-in for the truth only until the engine's first load lands (Profiles.HasLoadedServers); after
    // that the live list is the truth. null = the store could not be read → UNKNOWN, which must never be
    // rendered as "empty". See ReconcileGroups.
    private readonly bool? _storedServersAtLaunch;

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

    /// <summary>
    /// Why the last attempt failed, in the engine's own words, or null when there is nothing to add.
    ///
    /// The engine already explains every connect failure — a bad config, a core that would not start,
    /// a routing rule pointing at a missing node — through <c>NoticeManager</c>. On this client those
    /// messages reach no surface at all: the bottom toast was deliberately removed and its handler
    /// re-routes into the inline message panel, which the Incy shell never places, so the whole channel
    /// ends nowhere. That is why a failed connect said «Не удалось подключиться» and nothing else, and
    /// why the owner's report is «не знаю в чем причина». Captured here per attempt and shown in the
    /// hero's existing Error-state hint line, in place of the generic retry hint.
    /// </summary>
    [Reactive] public string? ConnectFailReason { get; set; }

    [Reactive] public bool HasServers { get; set; }

    /// <summary>
    /// "We KNOW this user has nothing stored" — never merely "we have not loaded yet". Both this and
    /// <see cref="HasServers"/> stay false while the answer is unknown, so the onboarding gate and the
    /// list's empty state simply do not appear until one of them is a fact. Defaulting this to true is
    /// what used to show the welcome screen to returning users for the first ~second of every launch.
    /// </summary>
    [Reactive] public bool IsEmpty { get; set; }

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
    /// <param name="storedServersAtLaunch">
    /// The shell's synchronous launch-time answer to "does the local store hold any server?" (null =
    /// unknown). It carries the empty/onboarding decision until the engine's first load lands.
    /// </param>
    public HomeViewModel(MainWindowViewModel main, bool? storedServersAtLaunch = null)
    {
        _config = AppManager.Instance.Config;
        _main = main;
        _storedServersAtLaunch = storedServersAtLaunch;
        Profiles = main.ProfilesViewModel;
        StatusBar = main.StatusBarViewModel;

        AddViaClipboardCmd = ReactiveCommand.CreateFromTask(AddViaClipboard);
        AddViaQrCmd = ReactiveCommand.CreateFromTask(AddViaQr);
        RefreshSubscriptionCmd = ReactiveCommand.CreateFromTask(RefreshSubscription);

        // Reconcile grouped list + counters whenever the real server collection changes. The
        // reconcile mutates ServerGroups IN PLACE (never Clear()+rebuild) so a mere active-flag flip
        // on selection does not tear the whole list down — see ReconcileGroups (Bug 6).
        Profiles.ProfileItems.CollectionChanged += OnProfileItemsChanged;
        ReconcileGroups();

        // Live language switch: the fallback group name ("My servers") and the servers/providers
        // meta line are composed in code, so re-run the projection when the language changes.
        L.Instance.LanguageChanged += OnLanguageChanged;

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

        // Positive "seamless switch settled" signal (Tier 1/Tier 2 make-before-break). That path raises
        // NO CoreRunningStateChanged(false), so the mid-switch Connecting hold (_awaitingCoreCycle) would
        // otherwise linger up to the 12s deadline after an INSTANT switch. Same background-thread
        // contract → marshal to the UI thread, same disposable pattern as _coreStateSub.
        _switchSettledSub = AppEvents.CoreSwitchSettled
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => OnCoreSwitchSettled());

        // The engine's notify channel — everything CoreManager reports with notify:true, which is
        // exactly the set of connect outcomes (bad config, core would not start, no server selected,
        // routing rule pointing at a missing node). Remembered so a failed attempt can say WHY.
        _noticeSub = AppEvents.SendSnackMsgRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(msg =>
            {
                // «Визитка» узла («[Custom] [Xray]ғı Finland») — отладочная подпись апстрима, а не
                // причина. Подписать ею щит значило бы объяснить отказ строкой, которую человек не
                // читает; отказы (FailedToRunCore, ошибки сборки конфига) проходят как проходили.
                if (msg.IsNullOrEmpty() || NoticePolicy.IsNodeSummary(msg))
                {
                    return;
                }
                _lastNotice = msg;
                _lastNoticeAt = DateTime.Now;
            });

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
        // A tap during a transition is NOT a new command. Without this the disc was the opposite of a
        // toggle in exactly the two states where the user taps hardest:
        //  • during «Подключение…» every impatient tap re-armed the 12 s deadline and queued another
        //    Reload, so the attempt could be pushed out indefinitely;
        //  • during a disconnect the hero still says «Подключено» for the whole teardown (IsConnected
        //    only flips in SyncState at the end), so a second tap ran a SECOND CoreStop +
        //    UpdateSysProxy against a core already being torn down.
        if (IsConnecting || _disconnecting)
        {
            return;
        }

        if (IsConnected)
        {
            _ = Disconnect();
        }
        else
        {
            // Nothing to connect to: the shield is already painted as unavailable (0.38 outline,
            // «Выберите сервер»), and firing anyway produced 12 s of fake «Подключение…» ending in a red
            // «Не удалось подключиться» — a connection failure reported to someone with no server.
            if (!HasServers)
            {
                return;
            }
            _ = Connect();
        }
    }

    /// <summary>Teardown in flight. Guards the disc (see <see cref="ConnectToggle"/>) — the hero cannot
    /// paint a busy state for it, so the gate has to live here.</summary>
    private bool _disconnecting;

    private async Task Connect()
    {
        if (_main == null)
        {
            return;
        }
        BeginConnecting();
        // A connect that carries nothing is not a connect. When this session will not tunnel (no
        // elevation / no sudo, or the user picked proxy mode) the OS has to reach the app through its
        // local proxy, and a fresh config ships the system proxy set to "force clear" — so the core
        // came up, everything said «Подключено», and not a byte went through it, with nothing on
        // screen to say why. See StatusBarViewModel.EnsureTrafficPathAsync.
        if (StatusBar != null)
        {
            try
            {
                await StatusBar.EnsureTrafficPathAsync();
            }
            catch (Exception ex)
            {
                // Never let the routing-path guard block the connect itself.
                Logging.SaveLog("HomeConnect", ex);
            }
        }
        // Reload builds the config and starts the core with the current default server. Its return
        // value tells us whether it actually RAN the attempt or merely deferred to a reload already in
        // flight (semaphore contended — e.g. a rapid second tap, or a background reload).
        bool executed;
        try
        {
            executed = await _main.Reload();
        }
        catch (Exception ex)
        {
            // A throw out of the build/start path used to leave the shield spinning to its 12 s deadline
            // with no explanation. Report it as the failure it is, now.
            Logging.SaveLog("HomeConnect", ex);
            IsConnecting = false;
            _connectingUntil = null;
            FailConnect();
            SyncState();
            return;
        }

        // Truthful failure, but ONLY when Reload actually executed: it has then fully awaited the core
        // start, so a not-running core means the attempt failed (wrong exe / bad config / blocked) —
        // surface it now instead of spinning to the 12s deadline. If Reload merely DEFERRED (executed
        // == false), the in-flight owner's follow-up job will still bring the core up, so we must NOT
        // paint a failure here; we keep the Connecting spin and let the CoreRunningStateChanged event
        // resolve it (or the 12s deadline surface an honest timeout if it never comes). This closes the
        // "first tap does nothing, second tap connects" race. Verbatim Android string (toast_status_failed).
        if (executed && !IsCoreRunning())
        {
            IsConnecting = false;
            _connectingUntil = null;
            // A4: surface a distinct FAILURE state instead of collapsing silently to Idle. Sticky
            // until the next attempt / a successful connect; drives the hero's Error shield — which is
            // the ONLY surface for connect state now (no bottom snack: the owner doesn't want it), so
            // it also has to carry the reason the engine reported.
            FailConnect();
        }
        SyncState();
    }

    private async Task Disconnect()
    {
        IsConnecting = false;
        _connectingUntil = null;
        // A deliberate user disconnect ends any mid-switch hold and is not a failure.
        _awaitingCoreCycle = false;
        ConnectFailed = false;
        _disconnecting = true;
        try
        {
            // byUser:true records sticky user-stop intent and aborts any in-flight auto-restart so the
            // tunnel the user just tore down can never be silently re-established (C1).
            await CoreManager.Instance.CoreStop(byUser: true);
            // Clear the Windows system proxy so the user keeps internet after disconnecting. Without
            // this the OS keeps routing through the now-dead 127.0.0.1:port and every browser breaks
            // until reconnect/reboot. forceDisable=true mirrors AppManager.AppExitAsync.
            await SysProxyHandler.UpdateSysProxy(_config, true);
        }
        catch (Exception ex)
        {
            // UpdateSysProxy can throw (registry write, netsh, insufficient rights). Without this catch
            // the throw skipped SyncState(), so IsConnected stayed true over an already-stopped core —
            // a shield reading «Подключено» on a dead tunnel — and UpdateStateTick stops the timer in
            // exactly that state, so nothing ever self-healed it.
            Logging.SaveLog("HomeDisconnect", ex);
        }
        finally
        {
            _connectedSince = null;
            _disconnecting = false;
            SyncState();
        }
    }

    /// <summary>
    /// Клик по строке сервера: сделать его основным и подключиться по контракту W1d.
    ///
    /// ВЫБОР ≠ ПЕРЕКЛЮЧЕНИЕ ЖИВОГО ТУННЕЛЯ (владелец G1, порт с Android
    /// <c>MainActivity.setSelectServer</c> / <c>promptApplySelectedServer</c>). Пока соединения НЕТ,
    /// выбор подключает — как и раньше, это то поведение, которое на телефоне как раз считается
    /// недоделанным. Но когда туннель УЖЕ поднят, выбор его больше не роняет: он только запоминает
    /// сервер, а оболочка предлагает «Переподключиться», называя выбранный сервер. Отказ оставляет
    /// соединение ровно таким, каким оно было.
    /// </summary>
    /// <param name="applyToRunningTunnel">
    /// True — пользователь уже согласился перенести живое соединение (нажал «Переподключиться»).
    /// False (по умолчанию) — просто выбор; поднятый туннель не трогаем.
    /// </param>
    public async Task SelectServer(string? indexId, bool applyToRunningTunnel = false)
    {
        if (Profiles == null || indexId.IsNullOrEmpty())
        {
            return;
        }

        // Capture BEFORE the call (SetDefaultServer mutates _config.IndexId):
        //   changed      — does this pick move the active default? (its Reload connects/reconnects)
        //   wasConnected — was a core already running? (drives the switch-vs-fresh-connect handling)
        // W1d SetDefaultServer contract: returns true = ready to connect; when the pick is ALREADY
        // the default it returns true WITHOUT reloading (the caller must Connect); when the pick
        // CHANGES it persists + Reload()s (which connects, incl. while disconnected); false = do not
        // connect.
        var changed = indexId != _config?.IndexId;
        var wasConnected = IsConnected;

        // Drive the SAME Connecting spin a shield tap does whenever this pick will RELOAD or CONNECT
        // (A5). Two cases qualify: a CHANGED default (SetDefaultServer's Reload tears the core down
        // and restarts it — a genuine reconnect, even from a live connection), and ANY pick while
        // disconnected (it connects). The ONLY tap that spins nothing is re-selecting the already-
        // active server WHILE CONNECTED: it reloads nothing, so the shield stays Connected.
        // ...с ОДНОЙ поправкой (G1): смена основного сервера ПРИ ЖИВОМ туннеле больше не крутит
        // спиннер и ничего не перезапускает, пока пользователь не подтвердил это явно. Иначе выбор
        // и переключение — одно действие, а разорвать работающее соединение он не просил.
        var switchingLive = changed && wasConnected;
        var willConnect = !wasConnected || (changed && applyToRunningTunnel);
        if (willConnect)
        {
            // Same guarantee the shield tap makes: a pick that starts the core must leave the OS with
            // a route into it. SetDefaultServer's own Reload connects without passing through
            // Connect(), so the check has to happen here too.
            if (StatusBar != null)
            {
                try
                {
                    await StatusBar.EnsureTrafficPathAsync();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("HomeSelectServer", ex);
                }
            }
            BeginConnecting();
            if (wasConnected)
            {
                // Switching servers from a live connection: drop Connected NOW so the hero paints
                // the Connecting spin during the switch (the presenter renders Connected as long as
                // IsConnected is true). The still-running OLD core must not read as Connected until
                // it actually cycles — SyncState holds Connecting until the stop event (see
                // _awaitingCoreCycle), then the new core's start resolves to Connected.
                IsConnected = false;
                _awaitingCoreCycle = true;
            }
        }

        if (!await Profiles.SetDefaultServer(indexId, applyToRunningCore: !switchingLive || applyToRunningTunnel))
        {
            // Invalid / failed pick — abort the spinner, do not connect.
            IsConnecting = false;
            _connectingUntil = null;
            _awaitingCoreCycle = false;
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

        // Соединение живо, сервер выбран, но не применён — предлагаем перенести, называя сервер.
        // Ровно как Android: Snackbar с действием «Переподключиться» рядом со списком; отказ ничего
        // не делает, выбор остаётся на следующее подключение.
        if (switchingLive && !applyToRunningTunnel)
        {
            await OfferReconnect(indexId);
        }
    }

    /// <summary>
    /// Предложение «Переподключиться» к только что выбранному серверу при живом туннеле.
    /// Порт <c>promptApplySelectedServer</c>: та же формулировка, то же место (транзиентная
    /// поверхность внизу, рядом со списком), тот же исход при отказе — ничего.
    /// </summary>
    private async Task OfferReconnect(string indexId)
    {
        var name = string.Empty;
        try
        {
            var item = await AppManager.Instance.GetProfileItem(indexId);
            name = ProfileDisplay.StripLeadingFlag(item?.Remarks);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("HomeOfferReconnect", ex);
        }

        var msg = name.IsNotEmpty()
            ? L.F("Home_ServerSelectedReconnect", name)
            : L.T("Home_ServerSelectedReconnectGeneric");

        Notify.Show(msg, L.T("Home_ReconnectAction"), () => _ = ApplySelectionToRunningTunnel());
    }

    /// <summary>
    /// Переносит РАБОТАЮЩИЙ туннель на выбранный сервер. Порт
    /// <c>HomeFragment.applySelectionToRunningTunnel</c>: щит уходит в «Подключение…», дальше всё
    /// идёт через обычную машину состояний, поэтому застрявший перезапуск отчитается как любой
    /// другой неудавшийся старт, а не оставит герой на прежнем сервере.
    /// </summary>
    public async Task ApplySelectionToRunningTunnel()
    {
        if (Profiles == null || !IsConnected)
        {
            return;
        }

        if (StatusBar != null)
        {
            try
            {
                await StatusBar.EnsureTrafficPathAsync();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("HomeApplySelection", ex);
            }
        }

        BeginConnecting();
        IsConnected = false;
        _awaitingCoreCycle = true;
        Profiles.ApplySelectedServerToRunningCore();
        SyncState();
    }

    private void BeginConnecting()
    {
        IsConnecting = true;
        // A new attempt clears the previous failure (A4): the hero leaves Error for Connecting.
        ConnectFailed = false;
        ConnectFailReason = null;
        _attemptStartedAt = DateTime.Now;
        // Safety deadline so a failed connect can't leave the shield spinning forever.
        _connectingUntil = DateTime.Now.AddSeconds(12);
        // Run the transient tick while pending so the deadline is actually evaluated even when the
        // connect came from a fire-and-forget Reload (server switch) that raises no failure event.
        UpdateStateTick();
    }

    /// <summary>
    /// Paint the truthful failure state AND carry the engine's own explanation with it. Every failure
    /// path goes through here so the hero can never show «Не удалось подключиться» while the reason the
    /// engine already produced is sitting unread.
    /// </summary>
    private void FailConnect()
    {
        // Only a message published during THIS attempt explains it — anything older belongs to an
        // earlier action (a subscription update, a previous connect) and would be a misleading caption.
        ConnectFailReason = _attemptStartedAt is { } started
            && _lastNoticeAt >= started
            && _lastNotice.IsNotEmpty()
                ? _lastNotice
                : null;
        ConnectFailed = true;
    }

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);

    private void SyncState()
    {
        var running = IsCoreRunning();

        // Mid server-switch hold (A5): while _awaitingCoreCycle is set the OLD core is still up but
        // we are re-establishing on the newly-picked server, so a running core must NOT read as
        // Connected yet — keep the Connecting spin. The stop transition clears the flag
        // (OnCoreRunningStateChanged), after which the new core's start resolves to Connected. The
        // connect deadline bounds the hold: if it elapses with a core still up and no observed stop
        // (defensive — CoreStop always publishes the stop), give up and treat the core as Connected
        // rather than spin forever.
        if (running && _awaitingCoreCycle)
        {
            if (_connectingUntil is { } holdUntil && DateTime.Now <= holdUntil)
            {
                return;
            }
            _awaitingCoreCycle = false;
        }

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
            // Core is down — any mid-switch hold is over (the old core stopped). Belt-and-suspenders
            // to the stop-event clear, so a hold can never outlive an observed not-running state.
            _awaitingCoreCycle = false;
            _connectedSince = null;
            IsConnected = false;
            Uptime = "00:00:00";
            UpSpeed = "0 KB/s";
            DownSpeed = "0 KB/s";

            if (IsConnecting && _connectingUntil is { } until && DateTime.Now > until)
            {
                // The connect deadline elapsed with no running core — a truthful timeout failure (A4).
                // ConnectFailed alone drives the hero's Error shield; no bottom snack is published for
                // connect/disconnect transitions (the owner doesn't want them, and the shield already
                // conveys the state). Covers the fire-and-forget/server-switch connect that only fails
                // via the deadline.
                IsConnecting = false;
                _connectingUntil = null;
                FailConnect();
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
        // A real STOP transition ends any mid-switch hold: the old core went down, so the next
        // start is the NEW connection. Use the event's own flag rather than a live IsCoreRunning()
        // probe — this marshalled callback can arrive AFTER LoadCore's back-to-back stop→start has
        // already brought the new core up, which a probe would misread as "still running".
        if (!running)
        {
            _awaitingCoreCycle = false;
        }
        SyncState();
        UpdateStateTick();
    }

    /// <summary>
    /// A seamless server switch (Tier 1/Tier 2) completed successfully. The switch keeps the tunnel up
    /// and raises no stop event, so resolve the mid-switch Connecting hold NOW instead of waiting on the
    /// 12s deadline: clear the hold, snap to Connected, and re-sync the shield.
    /// </summary>
    private void OnCoreSwitchSettled()
    {
        _awaitingCoreCycle = false;
        _connectingUntil = null;
        IsConnecting = false;
        IsConnected = true;
        ConnectFailed = false;
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

    // Coalesce the transient Clear()+AddRange() burst the engine emits on EVERY refresh/select
    // (ProfilesViewModel.RefreshServersBiz rebuilds ProfileItems wholesale) into ONE reconcile on the
    // next dispatcher tick. Reconciling synchronously on the Clear() would observe a transient
    // count==0 and (a) latch IsEmpty=true for one frame — the MainWindow shell faithfully crossfades
    // to the onboarding surface and back, i.e. the "black flash" on select — and (b) tear every group
    // down only to rebuild it on the following AddRange, defeating the in-place diff. Deferring makes
    // the reconcile read the SETTLED list (post-AddRange): a pure selection then flips only IsActive,
    // while a GENUINE empty (logout / no subs) still latches onboarding because the settled count
    // really is zero. A single pending post absorbs any number of bursts before it fires.
    private void OnProfileItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleReconcile();

    private void ScheduleReconcile()
    {
        if (_reconcilePending)
        {
            return;
        }
        _reconcilePending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _reconcilePending = false;
                ReconcileGroups();
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Project <c>Profiles.ProfileItems</c> onto the grouped <see cref="ServerGroups"/> by RECONCILING
    /// the existing collection IN PLACE — never <c>Clear()</c>+rebuild (Bug 6). The engine rebuilds
    /// ProfileItems wholesale (Clear + AddRange of brand-new instances) on EVERY change, including a
    /// mere active-flag flip when a server is selected. A blind rebuild made the non-virtualizing
    /// ItemsControl destroy + recreate all group headers, rows and reveal containers (and re-run the
    /// SubscriptionMetaView async resolve) → the visible jerk/flash on every tap.
    ///
    /// Instead we diff:
    ///   • groups by <see cref="HomeServerGroup.Key"/> (<c>{subid}|{name}</c>) — add / remove / move
    ///     only when the grouping itself changes;
    ///   • rows within a matched group by <c>IndexId</c> — a persisting row keeps its container and
    ///     only its reactive fields (IsActive / Delay / …) are copied across, so the selected-pill
    ///     binding (<c>Classes.selected="{Binding IsActive}"</c>) updates smoothly with NO teardown.
    /// A pure selection therefore mutates nothing structural: only two rows' IsActive flip. A full
    /// container rebuild now happens ONLY for a genuine grouping change (sub added/removed/reordered,
    /// server added/removed/reordered, or a renamed row whose displayed non-reactive fields changed).
    /// </summary>
    private void ReconcileGroups()
    {
        var items = Profiles?.ProfileItems;
        var count = items?.Count ?? 0;

        // «Есть ли серверы» — это ФАКТ, а не значение по умолчанию. Пока движок не завершил первую
        // загрузку списка из БД (HasLoadedServers), пустая коллекция значит «ещё не загрузили», и
        // отвечать за пользователя нельзя: берём снимок хранилища, снятый синхронно при запуске. Иначе
        // у вернувшегося пользователя (подписка из буфера обмена, вход не выполнен) первый кадр — это
        // приветственный онбординг «добавьте подписку», хотя его серверы лежат в БД и приезжают через
        // мгновение. Неизвестность (снимок null) оставляет ОБА флага false — ни списка, ни пустого
        // состояния, — чтобы ничего не утверждать, пока не узнаем правду.
        var loaded = Profiles?.HasLoadedServers == true;
        HasServers = loaded ? count > 0 : _storedServersAtLaunch == true;
        IsEmpty = loaded ? count == 0 : _storedServersAtLaunch == false;

        var plan = BuildGroupPlan(items, out var providers);
        ReconcileServerGroups(plan);

        // Счётчики считаем только по РЕАЛЬНО загруженному списку: «0 серверов» до загрузки было бы ложью.
        Subtitle = loaded ? FormatServersProvidersMeta(count, providers) : string.Empty;

        // Point the per-item ping/speedtest live-sync at the current (possibly rebuilt) source items.
        ResyncItemSubscriptions();
    }

    /// <summary>(Re)attach the per-item PropertyChanged handler to the live ProfileItems so an in-place
    /// latency/speed update propagates to the displayed rows. Detaches the previous set first — the
    /// engine rebuilds ProfileItems wholesale, so stale instances must be dropped.</summary>
    private void ResyncItemSubscriptions()
    {
        foreach (var it in _observedItems)
        {
            it.PropertyChanged -= OnSourceItemChanged;
        }
        _observedItems.Clear();

        var items = Profiles?.ProfileItems;
        if (items == null)
        {
            return;
        }
        foreach (var it in items)
        {
            it.PropertyChanged += OnSourceItemChanged;
            _observedItems.Add(it);
        }
    }

    /// <summary>A source ProfileItems row changed a REACTIVE field in place (ping/speedtest result,
    /// selection). Mirror just that state onto the matching displayed row (by IndexId) so the ping
    /// spinner and result appear without a full reconcile. Runs on the UI thread (the engine marshals
    /// SetSpeedTestResult through the main scheduler).</summary>
    private void OnSourceItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ProfileItemModel src)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(ProfileItemModel.Delay):
            case nameof(ProfileItemModel.DelayVal):
            case nameof(ProfileItemModel.SpeedVal):
            case nameof(ProfileItemModel.IpInfo):
            case nameof(ProfileItemModel.IsActive):
                break;
            default:
                return;
        }

        var row = FindRowByIndexId(src.IndexId);
        if (row == null || ReferenceEquals(row, src))
        {
            return;   // no displayed copy, or the row IS the source (INPC already propagates)
        }
        row.Delay = src.Delay;
        row.DelayVal = src.DelayVal;
        row.SpeedVal = src.SpeedVal;
        row.IpInfo = src.IpInfo;
        row.IsActive = src.IsActive;
    }

    private ProfileItemModel? FindRowByIndexId(string? indexId)
    {
        if (string.IsNullOrEmpty(indexId))
        {
            return null;
        }
        foreach (var g in ServerGroups)
        {
            foreach (var r in g.Servers)
            {
                if (string.Equals(r.IndexId, indexId, StringComparison.Ordinal))
                {
                    return r;
                }
            }
        }
        return null;
    }

    /// <summary>The desired shape of one group for a reconcile pass (no view objects allocated yet).</summary>
    private readonly struct GroupPlan
    {
        public GroupPlan(string key, string name, bool pinned, bool expanded, List<ProfileItemModel> servers)
        {
            Key = key;
            Name = name;
            Pinned = pinned;
            Expanded = expanded;
            Servers = servers;
        }

        public string Key { get; }
        public string Name { get; }
        public bool Pinned { get; }
        public bool Expanded { get; }
        public List<ProfileItemModel> Servers { get; }
    }

    /// <summary>Compute the desired ordered grouping from the live ProfileItems (same rule as before:
    /// group by subscription, pinned subs first). Purely a data projection — it touches no view state.</summary>
    private List<GroupPlan> BuildGroupPlan(IList<ProfileItemModel>? items, out int providers)
    {
        providers = 0;
        var plan = new List<GroupPlan>();
        if (items == null || items.Count == 0)
        {
            return plan;
        }

        var grouped = items
            .GroupBy(i => new
            {
                Key = i.Subid ?? string.Empty,
                Name = string.IsNullOrEmpty(i.SubRemarks) ? L.T("Home_MyServers") : i.SubRemarks,
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
            plan.Add(new GroupPlan(key, g.Key.Name, x.Pinned, expanded, g.ToList()));
        }

        return plan;
    }

    /// <summary>Reconcile the live <see cref="ServerGroups"/> collection against the plan, mutating
    /// only what actually differs. Matched groups are updated in place (see <see cref="GroupPlan"/>).</summary>
    private void ReconcileServerGroups(List<GroupPlan> plan)
    {
        // 1. Drop groups that no longer exist (match by Key). Only their own containers are torn down.
        var planKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in plan)
        {
            planKeys.Add(p.Key);
        }
        for (var i = ServerGroups.Count - 1; i >= 0; i--)
        {
            if (!planKeys.Contains(ServerGroups[i].Key))
            {
                ServerGroups.RemoveAt(i);
            }
        }

        // 2. Walk the plan in order: insert new groups, move displaced ones, update matched ones in
        //    place. Everything left of i is already correct, so a match is found at index >= i.
        for (var i = 0; i < plan.Count; i++)
        {
            var p = plan[i];
            var existingIndex = IndexOfGroup(p.Key);
            if (existingIndex < 0)
            {
                // A genuinely new subscription group — only this one container is created.
                ServerGroups.Insert(i, new HomeServerGroup(p.Key, p.Name, p.Servers, p.Expanded, p.Pinned, OnGroupExpandedChanged));
                continue;
            }
            if (existingIndex != i)
            {
                ServerGroups.Move(existingIndex, i);
            }
            var group = ServerGroups[i];
            group.UpdateHeader(p.Name, p.Pinned);
            group.ReconcileServers(p.Servers);
        }
    }

    private int IndexOfGroup(string key)
    {
        for (var i = 0; i < ServerGroups.Count; i++)
        {
            if (string.Equals(ServerGroups[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Compose the "{n} servers · {n} providers" meta line from the locale-aware plural
    /// forms (Common_ServersPlural / Common_ProvidersPlural) and the Home_ServersProvidersMeta
    /// template. Shared by the live list and the design-time sample so neither hardcodes the words.</summary>
    private static string FormatServersProvidersMeta(int count, int providers) =>
        L.F("Home_ServersProvidersMeta", L.Plural("Common_ServersPlural", count), L.Plural("Common_ProvidersPlural", providers));

    private void OnGroupExpandedChanged(string key, bool expanded) => _groupExpanded[key] = expanded;

    private void OnLanguageChanged(object? sender, EventArgs e) => ReconcileGroups();

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
        foreach (var it in _observedItems)
        {
            it.PropertyChanged -= OnSourceItemChanged;
        }
        _observedItems.Clear();
        L.Instance.LanguageChanged -= OnLanguageChanged;
        StopUptimeTick();
        _coreStateSub?.Dispose();
        _switchSettledSub?.Dispose();
        _statsSub?.Dispose();
        _noticeSub?.Dispose();
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
        vm.Subtitle = FormatServersProvidersMeta(5, 1);
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
    private string _name;
    private bool _pinned;

    public HomeServerGroup(string key, string name, IEnumerable<ProfileItemModel> servers, bool isExpanded, bool pinned = false, Action<string, bool>? onExpandedChanged = null)
    {
        Key = key;
        _name = name;
        Servers = new ObservableCollection<ProfileItemModel>(servers);
        _isExpanded = isExpanded;
        _pinned = pinned;
        _onExpandedChanged = onExpandedChanged;
    }

    public string Key { get; }

    public string Name
    {
        get => _name;
        private set
        {
            if (string.Equals(_name, value, StringComparison.Ordinal))
            {
                return;
            }
            _name = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Live server rows for this group. An <see cref="ObservableCollection{T}"/> (not a static list)
    /// so it can be reconciled IN PLACE via <see cref="ReconcileServers"/> — a selection / refresh
    /// updates rows through their existing containers instead of tearing every row down (Bug 6).
    /// </summary>
    public ObservableCollection<ProfileItemModel> Servers { get; }

    /// <summary>True when this subscription is pinned — pinned groups are ordered first (A9).</summary>
    public bool Pinned
    {
        get => _pinned;
        private set
        {
            if (_pinned == value)
            {
                return;
            }
            _pinned = value;
            OnPropertyChanged();
        }
    }

    public int Count => Servers.Count;
    public string CountText => Count.ToString();

    /// <summary>Update header fields that can shift WITHOUT changing the group's identity (Key), so a
    /// pin toggle / re-projection keeps this exact group instance (its expand state, its hooked
    /// meta-bar and reveal container) rather than replacing it.</summary>
    internal void UpdateHeader(string name, bool pinned)
    {
        Name = name;
        Pinned = pinned;
    }

    /// <summary>
    /// Reconcile this group's rows against the latest engine projection IN PLACE, matching by
    /// <c>IndexId</c>. A row that persists keeps its existing container — only its reactive fields
    /// (IsActive / Delay / DelayVal / …) are copied across, so the selected-pill and ping bindings
    /// update with NO teardown/relayout (Bug 6). Rows are inserted / removed / reordered only for a
    /// genuine membership or order change; a row whose DISPLAYED (non-reactive) fields changed — e.g.
    /// a rename — is swapped so that one row re-renders.
    /// </summary>
    internal void ReconcileServers(IReadOnlyList<ProfileItemModel> desired)
    {
        var before = Servers.Count;

        // Remove rows that are gone (by IndexId).
        var desiredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in desired)
        {
            desiredIds.Add(d.IndexId ?? string.Empty);
        }
        for (var i = Servers.Count - 1; i >= 0; i--)
        {
            if (!desiredIds.Contains(Servers[i].IndexId ?? string.Empty))
            {
                Servers.RemoveAt(i);
            }
        }

        // Insert / move / update to match the desired order. Positions left of i are already correct.
        for (var i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            var existingIndex = IndexOfServer(d.IndexId);
            if (existingIndex < 0)
            {
                Servers.Insert(i, d);
                continue;
            }
            if (existingIndex != i)
            {
                Servers.Move(existingIndex, i);
            }
            var current = Servers[i];
            if (SameDisplay(current, d))
            {
                // Same row — refresh only the reactive state so its container is NOT rebuilt.
                CopyLiveState(current, d);
            }
            else
            {
                // A displayed but non-reactive field changed (rename / protocol shift) — swap the
                // instance so ONLY this one row re-renders.
                Servers[i] = d;
            }
        }

        if (Servers.Count != before)
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(CountText));
        }
    }

    private int IndexOfServer(string? indexId)
    {
        for (var i = 0; i < Servers.Count; i++)
        {
            if (string.Equals(Servers[i].IndexId, indexId, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The row's displayed, NON-reactive fields — when these all match, the two instances
    /// render identically and only their reactive state can differ, so an in-place copy suffices.</summary>
    private static bool SameDisplay(ProfileItemModel a, ProfileItemModel b) =>
        string.Equals(a.Remarks, b.Remarks, StringComparison.Ordinal)
        && a.ConfigType == b.ConfigType
        && string.Equals(a.ProtocolDisplay, b.ProtocolDisplay, StringComparison.Ordinal)
        && string.Equals(a.Network, b.Network, StringComparison.Ordinal)
        && string.Equals(a.StreamSecurity, b.StreamSecurity, StringComparison.Ordinal)
        && string.Equals(a.Subid, b.Subid, StringComparison.Ordinal)
        && string.Equals(a.SubRemarks, b.SubRemarks, StringComparison.Ordinal);

    /// <summary>Copy the [Reactive] fields the rows observe onto the retained instance. Each set is a
    /// no-op when unchanged (ReactiveObject raises only on real change), so a pure selection touches
    /// just IsActive — the selected pill flips, nothing else moves.</summary>
    private static void CopyLiveState(ProfileItemModel target, ProfileItemModel source)
    {
        target.IsActive = source.IsActive;
        target.Delay = source.Delay;
        target.DelayVal = source.DelayVal;
        target.SpeedVal = source.SpeedVal;
        target.IpInfo = source.IpInfo;
        target.TodayUp = source.TodayUp;
        target.TodayDown = source.TodayDown;
        target.TotalUp = source.TotalUp;
        target.TotalDown = source.TotalDown;
    }

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
