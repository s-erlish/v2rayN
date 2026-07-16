using v2rayN.Desktop.Account;
using v2rayN.Desktop.Account.Dto;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.ViewModels;

/// <summary>
/// Backs the Account tab. Port of V2rayNG viewmodel/AccountViewModel.kt (StateFlow → ReactiveUI):
/// holds the observable account/subscription/tariff/payment data, delegates every action to
/// <see cref="AccountRepository"/> / <see cref="AuthManager"/>, and derives the display strings the
/// (compiled-binding) view binds to. DATA-DRIVEN: nothing is invented — everything stays blank/empty
/// until the real API returns.
/// </summary>
public class AccountViewModel : MyReactiveObject
{
    private readonly AccountRepository _repo;
    private readonly AuthManager _authManager;

    // Cache of the last subscription fetch so we can re-merge the synthesized root when the profile
    // (which supplies the root's auto-renew flag + remnawave uuid) arrives after the sub list.
    private PrimarySubscriptionDto? _lastPrimary;
    private List<SubInfoDto> _lastAll = new();
    private bool _hasSubData;

    // True until the FIRST real load result lands. Gates the loading skeleton so a genuinely-empty
    // account resolves to the empty state rather than spinning forever.
    private bool _pendingFirstLoad = true;

    private CancellationTokenSource? _telegramCts;

    // Bounded post-top-up profile re-poll (a Platega top-up completes in the external browser with no
    // in-app return callback). Cancelled on logout or a subsequent top-up.
    private CancellationTokenSource? _topUpRefreshCts;

    #region reactive state (raw)

    [Reactive] public UserProfileDto? Profile { get; set; }
    [Reactive] public List<SubInfoDto> Subscriptions { get; set; } = new();
    [Reactive] public List<TariffGroupDto> Tariffs { get; set; } = new();
    [Reactive] public List<PaymentDto> Payments { get; set; } = new();
    [Reactive] public int? DeviceCount { get; set; }
    [Reactive] public bool IsLoading { get; set; }
    [Reactive] public ApiError? Error { get; set; }
    [Reactive] public bool IsLoggedIn { get; set; }

    /// <summary>
    /// [Wave 2a signal] True from the instant a login succeeds — set in the SAME UI tick as
    /// <see cref="IsLoggedIn"/> flips true, BEFORE the first import await — and cleared only after the
    /// post-login account import + subscription fetch + Home server refresh have ALL completed
    /// (success or failure). MainWindow's shell gate (Wave 2a) shows the account-sync overlay while this
    /// is true so no empty onboarding frame flashes between the login page closing and the imported
    /// servers landing on Home. Only wraps the fresh-login (<see cref="OnAuthenticated"/>) path — the
    /// returning-user cold-start path is intentionally NOT gated, so an already-populated Home is never
    /// hidden behind the sync overlay on launch.
    /// </summary>
    [Reactive] public bool IsImportingAccount { get; set; }

    /// <summary>
    /// [Cold-start gate] True from the very first synchronous line of the constructor when a persisted
    /// session/token exists — raised BEFORE <see cref="IsLoggedIn"/> is first assigned and before any
    /// await — and cleared in the <see cref="StartupLoad"/> finally, only AFTER the returning-user
    /// account import + subscription fetch + Home server refresh have ALL completed (success or failure).
    /// The MainWindow shell consumes this to keep the loading/sync surface up on launch instead of
    /// flashing the logged-out onboarding/login gate during the ~2s cold-start restore. Distinct from
    /// <see cref="IsImportingAccount"/> (which gates only the FRESH post-login import): a returning user
    /// never triggers IsImportingAccount, so without this flag their launch would briefly render the
    /// empty login gate. A genuinely logged-out user (no persisted session) leaves this false and sees
    /// the login gate immediately — no loading state.
    /// </summary>
    [Reactive] public bool IsStartupLoading { get; set; }

    [Reactive] public PublicConfigDto? PublicConfig { get; set; }

    #endregion reactive state (raw)

    #region reactive state (derived display)

    [Reactive] public string Username { get; set; } = string.Empty;
    [Reactive] public string AvatarInitial { get; set; } = string.Empty;
    [Reactive] public string BalanceText { get; set; } = string.Empty;
    [Reactive] public bool HasBalance { get; set; }
    [Reactive] public string ReferralText { get; set; } = string.Empty;

    /// <summary>Raw referral code (e.g. "REF-97F7CBFB") — what the referral row copies to the clipboard.</summary>
    [Reactive] public string ReferralCode { get; set; } = string.Empty;
    [Reactive] public bool HasReferral { get; set; }
    [Reactive] public bool HasProfile { get; set; }

    [Reactive] public string SubName { get; set; } = string.Empty;
    [Reactive] public string TariffBadge { get; set; } = string.Empty;
    [Reactive] public bool HasTariffBadge { get; set; }
    [Reactive] public string SubExpiry { get; set; } = string.Empty;
    [Reactive] public bool HasSubExpiry { get; set; }
    [Reactive] public string SubDevicesText { get; set; } = string.Empty;

    [Reactive] public string DevicesRowValue { get; set; } = string.Empty;
    [Reactive] public bool HasDevicesRowValue { get; set; }
    [Reactive] public string HistoryRowValue { get; set; } = string.Empty;
    [Reactive] public bool HasHistoryRowValue { get; set; }

    [Reactive] public string ErrorText { get; set; } = string.Empty;

    // The four mutually-exclusive hero states (skeleton / active / empty / error).
    [Reactive] public bool ShowSkeleton { get; set; }
    [Reactive] public bool ShowActiveSub { get; set; }
    [Reactive] public bool ShowEmpty { get; set; }
    [Reactive] public bool ShowError { get; set; }

    // Logged-out CTA (nav-gating is deferred, so the tab shows a Telegram login prompt itself).
    [Reactive] public bool ShowLoginCta { get; set; }

    #endregion reactive state (derived display)

    #region login state / inputs

    [Reactive] public LoginState CurrentLoginState { get; set; } = new LoginState.Idle();
    [Reactive] public string LoginEmail { get; set; } = string.Empty;
    [Reactive] public string LoginPassword { get; set; } = string.Empty;
    [Reactive] public string TwoFaCode { get; set; } = string.Empty;

    /// <summary>The balance top-up amount (₽) the user typed in the «Пополнить» flyout.</summary>
    [Reactive] public string TopUpAmount { get; set; } = string.Empty;

    /// <summary>Non-null tempToken when the last site login requires a 2FA code; null otherwise.</summary>
    [Reactive] public string? TwoFaTempToken { get; set; }

    /// <summary>The Telegram deep link to open, when a Telegram login is awaiting confirmation.</summary>
    [Reactive] public string? TelegramDeepLink { get; set; }

    #endregion login state / inputs

    #region commands

    public ReactiveCommand<Unit, Unit> RefreshProfileCmd { get; }
    public ReactiveCommand<Unit, Unit> LoadSubscriptionsCmd { get; }
    public ReactiveCommand<Unit, Unit> LoadTariffsCmd { get; }
    public ReactiveCommand<Unit, Unit> LoadPaymentsCmd { get; }
    public ReactiveCommand<Unit, Unit> LoadDevicesCmd { get; }
    public ReactiveCommand<Unit, Unit> LoginTelegramCmd { get; }
    public ReactiveCommand<Unit, Unit> LoginSiteCmd { get; }
    public ReactiveCommand<Unit, Unit> Submit2FaCmd { get; }
    public ReactiveCommand<Unit, Unit> LogoutCmd { get; }
    public ReactiveCommand<Unit, Unit> RetryCmd { get; }

    /// <summary>Balance top-up: opens a Platega checkout for <see cref="TopUpAmount"/>.</summary>
    public ReactiveCommand<Unit, Unit> TopUpCmd { get; }

    #endregion commands

    /// <summary>Runtime constructor: seeds from the persisted session and loads real data when logged in.</summary>
    public AccountViewModel()
    {
        // Note: no AppManager access here — this VM is constructed during MainWindow field-init;
        // the engine (AppManager.Config) is only touched later, on user action, by the sync manager.
        _repo = new AccountRepository();
        _authManager = new AuthManager();

        // Evaluate the persisted session ONCE. When a session exists, raise the cold-start gate BEFORE
        // IsLoggedIn is first assigned (and before any await), so the shell never gets a chance to paint
        // the logged-out onboarding/login gate between construction and the cold-start restore landing.
        // A genuinely logged-out user (no persisted session) keeps IsStartupLoading false and sees the
        // login gate immediately.
        var hasSession = AccountSession.IsLoggedIn();
        IsStartupLoading = hasSession;
        IsLoggedIn = hasSession;
        if (hasSession)
        {
            Profile = AuthTokenStore.GetUser();
        }

        RefreshProfileCmd = ReactiveCommand.CreateFromTask(RefreshProfile);
        LoadSubscriptionsCmd = ReactiveCommand.CreateFromTask(LoadSubscriptions);
        LoadTariffsCmd = ReactiveCommand.CreateFromTask(LoadTariffs);
        LoadPaymentsCmd = ReactiveCommand.CreateFromTask(LoadPayments);
        LoadDevicesCmd = ReactiveCommand.CreateFromTask(LoadActiveDevices);
        LoginTelegramCmd = ReactiveCommand.CreateFromTask(StartTelegramLogin);
        LoginSiteCmd = ReactiveCommand.CreateFromTask(LoginSite);
        Submit2FaCmd = ReactiveCommand.CreateFromTask(Submit2Fa);
        LogoutCmd = ReactiveCommand.CreateFromTask(Logout);
        RetryCmd = ReactiveCommand.CreateFromTask(Retry);
        TopUpCmd = ReactiveCommand.CreateFromTask(TopUp);

        // Safety net: a stray command exception surfaces as the error state instead of crashing.
        Observable.Merge(
                RefreshProfileCmd.ThrownExceptions,
                LoadSubscriptionsCmd.ThrownExceptions,
                LoadTariffsCmd.ThrownExceptions,
                LoadPaymentsCmd.ThrownExceptions,
                LoadDevicesCmd.ThrownExceptions,
                LoginTelegramCmd.ThrownExceptions,
                LoginSiteCmd.ThrownExceptions,
                Submit2FaCmd.ThrownExceptions,
                LogoutCmd.ThrownExceptions,
                RetryCmd.ThrownExceptions,
                TopUpCmd.ThrownExceptions)
            .Subscribe(ex => RunOnUi(() =>
            {
                Report(ex as ApiError ?? new ApiError.NetworkError(ex));
                Recompute();
            }));

        AccountSession.StateChanged += OnSessionStateChanged;

        // Live language switch: re-derive every display string (balance caption, «Действует до …»,
        // device counts, referral line, error text) so open bindings pick up the new language.
        L.Instance.LanguageChanged += (_, _) => RunOnUi(Recompute);

        Recompute();

        if (IsLoggedIn)
        {
            // Returning user: re-import the account subscriptions (parity with the Android startup path
            // in MainActivity) so a sub bought/changed on another device shows up, then load the tab.
            // Run the whole restore on the thread pool (Task.Run) so NONE of its synchronous prefix —
            // token-store reads, subscription import, server download, account fetch — executes on the
            // UI-thread launch path: the window paints immediately and every UI mutation marshals back
            // via RunOnUi. (Previously the fire-and-forget `_ = StartupLoad()` ran its synchronous
            // prefix + first network round-trips on the UI thread, delaying first paint.)
            _ = Task.Run(StartupLoad);
        }
    }

    /// <summary>Returning-user startup: auto-import subscriptions (→ refresh Home) then load the tab.</summary>
    private async Task StartupLoad()
    {
        try
        {
            await AutoImportAndRefreshHome();
            await LoadAll();
        }
        finally
        {
            // Clear the cold-start gate only AFTER import + Home refresh + account/subscription load
            // resolve (success OR failure), so the loading surface hands directly to the populated
            // Home/Account instead of the empty login gate.
            RunOnUi(() =>
            {
                IsStartupLoading = false;
                Recompute();
            });
        }
    }

    /// <summary>Design-time constructor: sample logged-in active state so the previewer renders.</summary>
    private AccountViewModel(bool design)
    {
        _repo = null!;
        _authManager = null!;
        IsLoggedIn = true;
        Profile = new UserProfileDto
        {
            Email = "user@example.com",
            TelegramUsername = "serumfx",
            Balance = 0.0,
            Currency = "RUB",
            ReferralCode = "REF-888F7211",
        };
        Subscriptions = new List<SubInfoDto>
        {
            new()
            {
                Type = "root",
                DisplayName = "departament vpn",
                TariffDisplayName = "Base",
                ExpireAtIso = "2099-06-04T00:00:00Z",
                TotalDevices = 0,
                Subscription = new SubResponseWrapper { Response = new RawSubDto { HwidDeviceLimit = 0 } },
            },
        };
        DeviceCount = 23;
        Payments = new List<PaymentDto> { new() { CreatedAt = "2026-07-10T12:00:00Z" } };
        _pendingFirstLoad = false;
        Recompute();
    }

    public static AccountViewModel CreateDesign() => new(true);

    #region loads

    private async Task LoadAll()
    {
        await RefreshProfile();
        await LoadSubscriptions();
        await LoadPublicConfig();
        await LoadTariffs();
        await LoadPayments();
    }

    private async Task RefreshProfile()
    {
        var result = await _repo.RefreshProfile();
        RunOnUi(() =>
        {
            result
                .OnSuccess(p =>
                {
                    Profile = p;
                    MarkLoaded();
                    if (_hasSubData)
                    {
                        Subscriptions = MergeSubscriptions(_lastPrimary, _lastAll, p);
                    }
                })
                .OnFailure(Report);
            Recompute();
        });
    }

    private async Task LoadSubscriptions()
    {
        await FetchAndApplySubscriptions();
    }

    /// <summary>
    /// Fetches /subscription/all plus the authoritative primary subscription and publishes the merged
    /// list, so the active/primary sub always renders (never the raw un-merged /all list). Port of
    /// AccountViewModel.fetchAndApplySubscriptions.
    /// </summary>
    private async Task FetchAndApplySubscriptions()
    {
        var allResult = await _repo.LoadSubscriptions();
        var primaryResult = await _repo.LoadPrimarySubscription();

        RunOnUi(() =>
        {
            var all = allResult.GetOrNull()?.Items ?? new List<SubInfoDto>();
            var primary = primaryResult.GetOrNull();
            var merged = MergeSubscriptions(primary, all, Profile);

            if (merged.Count > 0 || allResult.IsSuccess)
            {
                _lastPrimary = primary;
                _lastAll = all;
                _hasSubData = true;
                Subscriptions = merged;
                if (merged.Count > 0)
                {
                    MarkLoaded();
                }
                // Fetch the REAL connected-device count for the active (first/root) sub.
                var uuid = merged.FirstOrDefault()?.RemnawaveUuid;
                if (uuid.IsNotEmpty())
                {
                    _ = LoadDevices(uuid!);
                }
            }
            else
            {
                var err = allResult.ExceptionOrNull() ?? primaryResult.ExceptionOrNull();
                if (err != null)
                {
                    Report(err);
                }
            }
            Recompute();
        });
    }

    /// <summary>
    /// Builds the list the Account screen consumes: the active/root subscription first (enriched from
    /// the primary payload when present), then the secondaries from /all. Port of mergeSubscriptions.
    /// </summary>
    private static List<SubInfoDto> MergeSubscriptions(PrimarySubscriptionDto? primary, List<SubInfoDto> all, UserProfileDto? profile)
    {
        var rootFromAll = all.FirstOrDefault(it => string.Equals(it.Type, "root", StringComparison.OrdinalIgnoreCase));
        var secondaries = all.Where(it => !string.Equals(it.Type, "root", StringComparison.OrdinalIgnoreCase)).ToList();

        SubInfoDto? activeRoot;
        if (primary?.HasActiveSubscription() == true)
        {
            activeRoot = BuildRootSub(primary, rootFromAll, profile);
        }
        else
        {
            activeRoot = rootFromAll;
        }

        var ordered = new List<SubInfoDto>();
        if (activeRoot != null)
        {
            ordered.Add(activeRoot);
        }
        ordered.AddRange(secondaries);

        // Dedup by non-blank id (a synthesized root can have a blank id and must be kept).
        var seen = new HashSet<string>();
        return ordered.Where(it => it.Id.IsNullOrEmpty() || seen.Add(it.Id)).ToList();
    }

    /// <summary>Synthesizes/enriches the root sub from the primary payload. Port of buildRootSub.</summary>
    private static SubInfoDto BuildRootSub(PrimarySubscriptionDto primary, SubInfoDto? rootFromAll, UserProfileDto? profile)
    {
        var raw = primary.Raw();
        return new SubInfoDto
        {
            Type = "root",
            Id = rootFromAll?.Id ?? string.Empty,
            RemnawaveUuid = FirstNonBlank(profile?.RemnawaveUuid, rootFromAll?.RemnawaveUuid),
            Subscription = primary.Subscription,
            TariffDisplayName = primary.TariffDisplayName.IsNotEmpty() ? primary.TariffDisplayName : rootFromAll?.TariffDisplayName,
            DisplayName = rootFromAll?.DisplayName,
            DefaultLabel = rootFromAll?.DefaultLabel,
            SubscriptionIndex = rootFromAll?.SubscriptionIndex,
            TariffId = rootFromAll?.TariffId.IsNotEmpty() == true ? rootFromAll.TariffId : primary.ActiveTariffId(),
            TariffPriceOptionId = rootFromAll?.TariffPriceOptionId,
            DeviceCount = rootFromAll?.DeviceCount ?? 0,
            TotalDevices = rootFromAll?.TotalDevices ?? (raw?.HwidDeviceLimit > 0 ? raw.HwidDeviceLimit : 0),
            ConnectedDevices = rootFromAll?.ConnectedDevices ?? 0,
            AutoRenewEnabled = profile?.AutoRenewEnabled ?? rootFromAll?.AutoRenewEnabled ?? false,
            ExpireAtIso = raw?.ExpireAt.IsNotEmpty() == true ? raw.ExpireAt : rootFromAll?.ExpireAtIso,
            IsTrial = rootFromAll?.IsTrial ?? false,
            TariffPrice = rootFromAll?.TariffPrice,
            TariffCurrency = primary.AutoRenewCurrency.IsNotEmpty() ? primary.AutoRenewCurrency : rootFromAll?.TariffCurrency,
            RenewalPrice = primary.AutoRenewNextChargeAmount ?? rootFromAll?.RenewalPrice,
        };
    }

    private async Task LoadTariffs()
    {
        var result = await _repo.LoadCatalog();
        RunOnUi(() =>
        {
            result.OnSuccess(c => Tariffs = c.Items).OnFailure(Report);
            Recompute();
        });
    }

    private async Task LoadPayments()
    {
        var result = await _repo.GetPayments();
        RunOnUi(() =>
        {
            result
                .OnSuccess(p =>
                {
                    Payments = p.Items;
                    AccountCache.PutPayments(p.Items);
                })
                .OnFailure(Report);
            Recompute();
        });
    }

    private async Task LoadPublicConfig()
    {
        var result = await _repo.LoadPublicConfig();
        RunOnUi(() => result.OnSuccess(c => PublicConfig = c).OnFailure(Report));
    }

    private async Task LoadActiveDevices()
    {
        var uuid = Subscriptions.FirstOrDefault()?.RemnawaveUuid;
        if (uuid.IsNotEmpty())
        {
            await LoadDevices(uuid!);
        }
    }

    /// <summary>
    /// Resolves the ACTIVE subscription's connected-device count from GET /client/devices (cache-first).
    /// A device-fetch failure is swallowed on purpose — the count is secondary. Port of loadDevices.
    /// </summary>
    private async Task LoadDevices(string uuid)
    {
        if (uuid.IsNullOrEmpty())
        {
            return;
        }
        var cached = AccountCache.GetDevices(uuid);
        if (cached != null)
        {
            RunOnUi(() =>
            {
                DeviceCount = cached.Count;
                Recompute();
            });
            return;
        }
        var result = await _repo.GetDevices(uuid);
        RunOnUi(() =>
        {
            result.OnSuccess(d =>
            {
                AccountCache.PutDevices(uuid, d.Devices);
                DeviceCount = d.Devices.Count;
                Recompute();
            });
            // On failure: keep last known count, no error surfaced.
        });
    }

    #endregion loads

    #region login / logout actions

    private async Task StartTelegramLogin()
    {
        _telegramCts?.Cancel();
        var cts = new CancellationTokenSource();
        _telegramCts = cts;
        TwoFaTempToken = null;
        CurrentLoginState = new LoginState.Idle();

        await _authManager.BeginTelegramLogin(state => RunOnUi(() => ApplyLoginState(state)), cts.Token);
    }

    /// <summary>
    /// Cancels an in-flight Telegram login poll and resets the login flow to idle. The poll loop
    /// (<see cref="AuthManager.BeginTelegramLogin"/>) honours the token and returns immediately on
    /// cancel, so this stops the ≤3-minute background poll that otherwise keeps running after the login
    /// sub-page is dismissed. The host (LoginView back/close → MainWindow.PopSubPage) must call this on
    /// dismiss. Idempotent and safe to call when no login is in flight; never disturbs an already
    /// successful login (guarded by <see cref="IsLoggedIn"/>).
    /// </summary>
    public void CancelLogin()
    {
        _telegramCts?.Cancel();
        _telegramCts = null;
        RunOnUi(() =>
        {
            if (!IsLoggedIn)
            {
                CurrentLoginState = new LoginState.Idle();
            }
        });
    }

    private async Task LoginSite()
    {
        _telegramCts?.Cancel();
        TwoFaTempToken = null;
        CurrentLoginState = new LoginState.SiteLoading();
        try
        {
            var result = await _authManager.LoginSite(LoginEmail, LoginPassword);
            if (result is LoginResult.Success success)
            {
                await OnAuthenticated(success.Client);
            }
            else if (result is LoginResult.Requires2Fa twoFa)
            {
                RunOnUi(() =>
                {
                    TwoFaTempToken = twoFa.TempToken;
                    CurrentLoginState = new LoginState.Idle();
                });
            }
        }
        catch (ApiError e)
        {
            RunOnUi(() => CurrentLoginState = new LoginState.Error(e));
        }
    }

    private async Task Submit2Fa()
    {
        var tempToken = TwoFaTempToken;
        if (tempToken.IsNullOrEmpty())
        {
            return;
        }
        CurrentLoginState = new LoginState.SiteLoading();
        try
        {
            var profile = await _authManager.Submit2Fa(tempToken!, TwoFaCode);
            RunOnUi(() => TwoFaTempToken = null);
            await OnAuthenticated(profile);
        }
        catch (ApiError e)
        {
            RunOnUi(() => CurrentLoginState = new LoginState.Error(e));
        }
    }

    private void ApplyLoginState(LoginState state)
    {
        CurrentLoginState = state;
        switch (state)
        {
            case LoginState.AwaitingTelegram awaiting:
                TelegramDeepLink = awaiting.DeepLink;
                // Open the Telegram deep link in the default browser so the user can confirm.
                ProcUtils.ProcessStart(awaiting.DeepLink);
                break;
            case LoginState.Success success:
                _ = OnAuthenticated(success.Profile);
                break;
        }
    }

    /// <summary>On a successful auth: flip to logged-in, auto-import subscriptions, load real data.</summary>
    private async Task OnAuthenticated(UserProfileDto profile)
    {
        RunOnUi(() =>
        {
            IsLoggedIn = true;
            // Raise the sync signal in the SAME tick as IsLoggedIn (before the first await below), so the
            // Wave 2a overlay is already up when LoginView closes on IsLoggedIn — no empty onboarding flash.
            IsImportingAccount = true;
            Profile = profile;
            _pendingFirstLoad = true;
            IsLoading = true;
            CurrentLoginState = new LoginState.Success(profile);
            Recompute();
        });

        try
        {
            await AutoImportAndRefreshHome();   // import + RefreshServers (flips Home IsEmpty=false)
            await FetchAndApplySubscriptions();
            await LoadAll();
        }
        finally
        {
            // Clear the sync signal only AFTER import + fetch + Home refresh resolve (success OR failure),
            // so the overlay hands directly to the populated Home instead of a half-empty frame.
            RunOnUi(() =>
            {
                IsLoading = false;
                IsImportingAccount = false;
                Recompute();
            });
        }
    }

    /// <summary>
    /// Runs the account auto-import (login → GET account subscriptions → persist + download servers)
    /// and then refreshes the engine's server list so the imported servers appear on Home and its
    /// empty/onboarding state flips off. A transient import failure is surfaced but never blocks the
    /// rest of the load — <see cref="FetchAndApplySubscriptions"/> re-reports real errors.
    /// </summary>
    private async Task AutoImportAndRefreshHome()
    {
        var import = await _repo.AutoImportSubscriptions();
        RunOnUi(() => import.OnFailure(Report));
        RequestHomeServerRefresh();
    }

    /// <summary>
    /// Repopulates the engine's server list (<c>ProfilesViewModel.ProfileItems</c>) after an account
    /// import so the Home screen picks up the freshly-imported servers (HomeViewModel reacts to the
    /// collection change and flips <c>IsEmpty</c> false). OFF-model: <c>RefreshServers</c> only reloads
    /// the list from the DB — it never starts the core. Reaches the running MainWindow's VM via
    /// ReactiveUI's <see cref="IViewFor{T}"/> (read-only; does not touch the Home-owned views).
    /// </summary>
    private static void RequestHomeServerRefresh()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                    && desktop.MainWindow is IViewFor<MainWindowViewModel> { ViewModel: { } main })
                {
                    await main.ProfilesViewModel.RefreshServers();
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AccountAutoImportRefresh", ex);
            }
        });
    }

    private async Task Logout()
    {
        _telegramCts?.Cancel();
        _topUpRefreshCts?.Cancel();
        // Wipe stops the VPN and DELETES the account-imported subscriptions + their servers (tracked by
        // AuthTokenStore.ManagedGuids — a user's OWN manually-added subs are never in that set, so they
        // survive logout). Refresh the Home server list afterwards so the removed servers disappear and
        // Home returns to its empty/onboarding state instead of showing the stale (e.g. «Base») group.
        await AccountSession.Wipe();
        AccountCache.InvalidateAll();
        RequestHomeServerRefresh();
        RunOnUi(() =>
        {
            IsLoggedIn = false;
            Profile = null;
            Subscriptions = new List<SubInfoDto>();
            _lastPrimary = null;
            _lastAll = new List<SubInfoDto>();
            _hasSubData = false;
            Payments = new List<PaymentDto>();
            DeviceCount = null;
            Tariffs = new List<TariffGroupDto>();
            TwoFaTempToken = null;
            TelegramDeepLink = null;
            CurrentLoginState = new LoginState.Idle();
            _pendingFirstLoad = false;
            Recompute();
        });
    }

    private async Task Retry()
    {
        _pendingFirstLoad = true;
        ClearError();
        Recompute();
        if (IsLoggedIn)
        {
            await LoadAll();
        }
    }

    /// <summary>
    /// Balance top-up (parity with Android «Пополнение баланса»): opens a Platega checkout for the
    /// entered ₽ amount in the external browser. A top-up ADDS to the balance, so it is deliberately a
    /// provider checkout (PayPlatega), never a balance payment (which would be circular). Data-driven:
    /// the amount comes straight from the user; nothing is fabricated.
    /// </summary>
    private async Task TopUp()
    {
        var raw = TopUpAmount?.Trim();
        if (raw.IsNullOrEmpty()
            || !(double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount)
                 || double.TryParse(raw, NumberStyles.Any, CultureInfo.CurrentCulture, out amount))
            || amount <= 0)
        {
            AppEvents.SendSnackMsgRequested.Publish(L.T("Account_AmountGtZero"));
            return;
        }

        var result = await _repo.Buy(new PaymentRequestDto { Amount = amount, Currency = "RUB" });
        RunOnUi(() =>
        {
            result
                .OnSuccess(init =>
                {
                    TopUpAmount = string.Empty;
                    var url = init.PaymentUrl;
                    if (url.IsNullOrEmpty())
                    {
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CouldntOpenPayment"));
                        return;
                    }
                    try
                    {
                        ProcUtils.ProcessStart(url);
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CompletePaymentInBrowser"));
                        // The top-up completes in the external browser with no in-app return callback, so
                        // re-poll the profile until the balance lands — BalanceText updates without a
                        // manual retry.
                        SchedulePostTopUpBalanceRefresh();
                    }
                    catch
                    {
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CouldntOpenPayment"));
                    }
                })
                .OnFailure(err => AppEvents.SendSnackMsgRequested.Publish(MessageFor(err)));
        });
    }

    /// <summary>
    /// After a balance top-up checkout is opened externally, the payment settles in the browser with no
    /// in-app return signal. Re-fetch the profile a few times (bailing as soon as the balance changes) so
    /// <see cref="BalanceText"/> refreshes on its own. Data-driven: every poll is a real /profile fetch —
    /// nothing is fabricated. Cancelled on logout or a subsequent top-up.
    /// </summary>
    private void SchedulePostTopUpBalanceRefresh()
    {
        _topUpRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _topUpRefreshCts = cts;
        var startingBalance = Profile?.Balance;

        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 12 && !cts.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
                    await RefreshProfile();
                    if (Profile?.Balance is { } current && current != startingBalance)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled by logout / a newer top-up — stop quietly.
            }
        });
    }

    #endregion login / logout actions

    #region tariff badge resolution (public helpers)

    /// <summary>Resolves a tariff's display name from its tariff id against the loaded catalog.</summary>
    public string? TariffNameFor(string? tariffId)
    {
        if (tariffId.IsNullOrEmpty())
        {
            return null;
        }
        return Tariffs.SelectMany(g => g.Tariffs).FirstOrDefault(t => t.Id == tariffId)?.Name.NullIfEmpty();
    }

    /// <summary>Resolves a tariff's display name from the price-option id the subscription renews on.</summary>
    public string? TariffNameForPriceOptionId(string? priceOptionId)
    {
        if (priceOptionId.IsNullOrEmpty())
        {
            return null;
        }
        return Tariffs
            .SelectMany(g => g.Tariffs)
            .FirstOrDefault(t => t.PriceOptions.Any(o => o.Id == priceOptionId))
            ?.Name.NullIfEmpty();
    }

    #endregion tariff badge resolution

    #region derive display + hero state

    public void ClearError()
    {
        Error = null;
    }

    private void Report(ApiError error)
    {
        Error = error;
        _pendingFirstLoad = false;
    }

    private void MarkLoaded()
    {
        _pendingFirstLoad = false;
    }

    /// <summary>Recomputes every derived display string + the mutually-exclusive hero state.</summary>
    private void Recompute()
    {
        // Profile block
        var profile = Profile;
        HasProfile = profile != null;
        if (profile == null)
        {
            Username = string.Empty;
            AvatarInitial = string.Empty;
            BalanceText = string.Empty;
            HasBalance = false;
            ReferralText = string.Empty;
            ReferralCode = string.Empty;
            HasReferral = false;
        }
        else
        {
            // Same precedence the Home chip renders (telegramUsername → «@…» → telegramName → email),
            // shared via AccountSession so the two identity surfaces never drift.
            Username = AccountSession.DisplayNameFor(profile);
            AvatarInitial = Monogram(Username);
            BalanceText = FormatMoney(profile.Balance, profile.Currency);
            HasBalance = true;
            HasReferral = profile.ReferralCode.IsNotEmpty();
            ReferralCode = HasReferral ? profile.ReferralCode : string.Empty;
            ReferralText = HasReferral ? L.F("Account_ReferralCode", profile.ReferralCode) : string.Empty;
        }

        // Active subscription block (first/root of the merged list)
        var sub = Subscriptions.FirstOrDefault();
        if (sub == null)
        {
            SubName = string.Empty;
            TariffBadge = string.Empty;
            HasTariffBadge = false;
            SubExpiry = string.Empty;
            HasSubExpiry = false;
            SubDevicesText = string.Empty;
            DevicesRowValue = string.Empty;
            HasDevicesRowValue = false;
        }
        else
        {
            SubName = FirstNonBlank(sub.DisplayName, sub.TariffDisplayName, sub.DefaultLabel, L.T("Account_MySubs"));

            var badge = TariffNameFor(sub.TariffId) ?? TariffNameForPriceOptionId(sub.TariffPriceOptionId) ?? sub.TariffBadgeName();
            HasTariffBadge = badge.IsNotEmpty();
            TariffBadge = badge ?? string.Empty;

            HasSubExpiry = sub.ExpireAtIso.IsNotEmpty();
            SubExpiry = HasSubExpiry ? L.F("Account_ValidUntil", FormatIsoDate(sub.ExpireAtIso)) : string.Empty;

            var unlimited = sub.Subscription?.Raw()?.IsUnlimitedDevices() == true;
            var totalStr = unlimited ? "∞" : sub.TotalDevices.ToString();
            var used = DeviceCount ?? 0;
            SubDevicesText = L.F("Account_DevicesCount", used, totalStr);
            DevicesRowValue = $"{used} / {totalStr}";
            HasDevicesRowValue = true;
        }

        // History row trailing value (latest payment date)
        var latestIso = Payments.Count > 0 ? Payments.MaxBy(p => p.CreatedAt)?.CreatedAt : null;
        var date = FormatIsoDate(latestIso);
        HasHistoryRowValue = date.IsNotEmpty();
        HistoryRowValue = date;

        // Error text
        ErrorText = Error != null ? MessageFor(Error) : string.Empty;

        // Logged-out: the profile card shows the Telegram login gate and the whole subscription hero is
        // hidden. Guarding here (not just on Profile == null) is what stops a signed-out user from
        // seeing a PERPETUALLY-PULSING skeleton — _pendingFirstLoad stays true until a real load runs,
        // and a logged-out user never runs one.
        if (!IsLoggedIn)
        {
            ShowSkeleton = false;
            ShowActiveSub = false;
            ShowEmpty = false;
            ShowError = false;
            ShowLoginCta = true;
            return;
        }
        ShowLoginCta = false;

        // Hero state machine (port of renderHeroState) — logged-in only. Skeleton shows ONLY while an
        // actual first load is in flight (cold-loading) and the profile has not landed yet.
        var coldLoading = _pendingFirstLoad || IsLoading;
        var subsNotEmpty = Subscriptions.Count > 0;
        bool skeleton = false, active = false, empty = false, error = false;
        if (subsNotEmpty)
        {
            active = true;
        }
        else if (coldLoading && Profile == null)
        {
            skeleton = true;
        }
        else if (Profile == null && Error != null)
        {
            error = true;
        }
        else
        {
            empty = true;
        }
        ShowSkeleton = skeleton;
        ShowActiveSub = active;
        ShowEmpty = empty;
        ShowError = error;
    }

    private void OnSessionStateChanged(AccountState state)
    {
        RunOnUi(() =>
        {
            IsLoggedIn = state is AccountState.LoggedIn;
            if (state is AccountState.LoggedIn loggedIn)
            {
                Profile = loggedIn.Profile;
            }
            else if (state is AccountState.LoggedOut)
            {
                Profile = null;
            }
            Recompute();
        });
    }

    #endregion derive display + hero state

    #region formatting helpers (ported 1:1)

    private static string FormatMoney(double amount, string currency)
    {
        var n = amount % 1.0 == 0.0
            ? ((long)amount).ToString(CultureInfo.InvariantCulture)
            : amount.ToString("0.00", CultureInfo.InvariantCulture);
        return $"{n} {CurrencySymbol(currency)}";
    }

    // RUB-only product: RUB/blank/USD/unknown all render as the ruble sign; only genuinely distinct
    // currencies keep their own symbol.
    private static string CurrencySymbol(string currency) => currency.Trim().ToUpperInvariant() switch
    {
        "EUR" => "€",
        "KZT" => "₸",
        "UAH" => "₴",
        _ => "₽",
    };

    private static string FormatIsoDate(string? iso)
    {
        if (iso.IsNullOrEmpty())
        {
            return string.Empty;
        }
        var datePart = iso!.Split('T')[0];
        var parts = datePart.Split('-');
        return parts.Length == 3 ? $"{parts[2]}.{parts[1]}.{parts[0]}" : datePart;
    }

    private static string Monogram(string primary)
    {
        var trimmed = primary.Trim().TrimStart('@');
        return trimmed.Length > 0 ? trimmed.Substring(0, 1).ToUpperInvariant() : string.Empty;
    }

    private static string MessageFor(ApiError error) => error switch
    {
        ApiError.ServiceUnavailable => L.T("Common_ServiceUnavailable"),
        ApiError.NetworkError => L.T("Common_NetworkError"),
        ApiError.Unauthorized => L.T("Common_SignInRequired"),
        ApiError.RateLimited => L.T("Common_TooManyRequests"),
        ApiError.TimeoutError => L.T("Common_Timeout"),
        _ => L.T("Common_SomethingWrong"),
    };

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var v in values)
        {
            if (v.IsNotEmpty())
            {
                return v!;
            }
        }
        return string.Empty;
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    #endregion formatting helpers
}
