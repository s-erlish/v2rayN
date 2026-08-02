using Avalonia.Media;
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

    /// <summary>
    /// The single runtime instance (set by the runtime ctor; null in design mode). Consumed by
    /// <c>AccountSyncView</c>, a static overlay in the MainWindow shell that has no inherited DataContext
    /// path to this VM, so it can bind the live sync stage line and invoke the retry / re-login commands
    /// without requiring a MainWindow change.
    /// </summary>
    public static AccountViewModel? Shared { get; private set; }

    // Which sync phase is live — kept so the caption can be re-derived on a language switch mid-sync.
    private SyncPhase _syncPhase = SyncPhase.Account;

    private enum SyncPhase { Account, Subs, Servers }

    // Cache of the last subscription fetch so we can re-merge the synthesized root when the profile
    // (which supplies the root's auto-renew flag + remnawave uuid) arrives after the sub list.
    private PrimarySubscriptionDto? _lastPrimary;
    private List<SubInfoDto> _lastAll = new();
    private bool _hasSubData;

    // True until the FIRST real load result lands. Gates the loading skeleton so a genuinely-empty
    // account resolves to the empty state rather than spinning forever.
    private bool _pendingFirstLoad = true;

    private CancellationTokenSource? _telegramCts;

    // Bounded poll after an email+password registration that requires verification (poll login until the
    // emailed link is clicked). Also owns the in-flight magic-link / password-reset requests. Cancelled on
    // logout, on «другой способ входа», and whenever a new start-page auth flow begins.
    private CancellationTokenSource? _registerCts;

    // Bounded post-top-up profile re-poll (a Platega top-up completes in the external browser with no
    // in-app return callback). Cancelled on logout or a subsequent top-up.
    private CancellationTokenSource? _topUpRefreshCts;

    // The Platega method code (2..13) chosen in the top-up flyout; null until PublicConfig lands / user picks.
    private int? _selectedTopUpMethod;

    // Bounded poll after a scoped CARD renewal (webhook-confirmed in the browser) — mirrors BuyViewModel.
    private CancellationTokenSource? _renewPollCts;
    private AccountSubCard? _renewPollCard;

    // Bounded poll of /me after a Telegram-link code is issued (until telegramLinked flips true).
    private CancellationTokenSource? _linkPollCts;

    // Bounded poll of GET /client/payments after a CARD device-top-up / upgrade opens in the browser
    // (webhook-confirmed there, no in-app return). Cancelled on logout or a newer card action.
    private CancellationTokenSource? _cardActionPollCts;
    private AccountSubCard? _cardActionCard;

    // The Telegram bot handle to reopen from «Открыть бота» while a link is pending.
    private string _linkBotUsername = string.Empty;

    /// <summary>Payment statuses that count as webhook-confirmed while re-polling GET /client/payments.</summary>
    private static readonly HashSet<string> PaidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "paid", "success", "succeeded", "completed", "confirmed", "done",
    };

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

    /// <summary>
    /// [Phase-3 signal] Live post-login sync stage caption — «Проверяем аккаунт» → «Загружаем подписки…»
    /// → «Обновляем серверы» — advanced as each real phase await begins (<see cref="RunSyncPhases"/>).
    /// AccountSyncView binds this as its single (ellipsis, non-reflowing) stage line. Empty when no sync
    /// is running; re-derived on a live language switch.
    /// </summary>
    [Reactive] public string SyncStageText { get; set; } = string.Empty;

    /// <summary>
    /// [Phase-3 signal] True when a post-login (<see cref="OnAuthenticated"/>) or cold-start
    /// (<see cref="StartupLoad"/>) sync phase THREW. On failure the raising gate flag
    /// (<see cref="IsImportingAccount"/> / <see cref="IsStartupLoading"/>) is deliberately left TRUE so the
    /// MainWindow shell keeps the sync overlay up (no MainWindow change needed), and AccountSyncView swaps
    /// in place to the retry surface (alert glyph + «Повторить» / «Войти заново»). Cleared by
    /// <see cref="SyncRetry"/> (re-run) and <see cref="SyncReLogin"/> (clear session). This is what turns a
    /// failed import into an actionable state instead of an eternal spinner or a false hand-off to Home.
    /// </summary>
    [Reactive] public bool SyncFailed { get; set; }

    [Reactive] public PublicConfigDto? PublicConfig { get; set; }

    #endregion reactive state (raw)

    #region reactive state (derived display)

    [Reactive] public string Username { get; set; } = string.Empty;
    [Reactive] public string AvatarInitial { get; set; } = string.Empty;
    [Reactive] public string BalanceText { get; set; } = string.Empty;

    /// <summary>Balance typeset as money: the amount alone (e.g. "1 490"), so the ₽ can be a stepped-down trailing run.</summary>
    [Reactive] public string BalanceAmountText { get; set; } = string.Empty;

    /// <summary>The currency symbol shown as a subordinate trailing glyph next to <see cref="BalanceAmountText"/> (e.g. "₽").</summary>
    [Reactive] public string BalanceCurrencyText { get; set; } = string.Empty;
    [Reactive] public bool HasBalance { get; set; }
    [Reactive] public string ReferralText { get; set; } = string.Empty;

    /// <summary>Raw referral code (e.g. "REF-97F7CBFB") — what the referral row copies to the clipboard.</summary>
    [Reactive] public string ReferralCode { get; set; } = string.Empty;
    [Reactive] public bool HasReferral { get; set; }
    [Reactive] public bool HasProfile { get; set; }

    /// <summary>Sentence-case tariff signal shown on the identity line under the username ("Тариф · Base" / "Пробный период").</summary>
    [Reactive] public string TariffCaptionText { get; set; } = string.Empty;
    [Reactive] public bool HasTariffCaption { get; set; }

    /// <summary>Every subscription as a health-rich carousel card (active first). Desktop used to render only the first.</summary>
    [Reactive] public List<AccountSubCard> SubCards { get; set; } = new();

    /// <summary>True when more than one subscription exists — gates the carousel dots / drag / arrow paging.</summary>
    [Reactive] public bool HasMultipleSubs { get; set; }

    /// <summary>The active carousel page (0-based). Driven by drag/dots/arrow keys in the view; only read for the pager.</summary>
    [Reactive] public int CarouselIndex { get; set; }

    /// <summary>Fixed pixel width every carousel card takes — computed by the view from the carousel viewport (peek-aware).</summary>
    [Reactive] public double CardWidth { get; set; } = 320;

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

    /// <summary>Есть ли что сказать об отказе. Нужен слоту ошибки, который держит своё место
    /// ВСЕГДА: без зарезервированной высоты появление сообщения сдвигало бы кнопки под курсором.</summary>
    [Reactive] public bool HasError { get; set; }

    // The four mutually-exclusive hero states (skeleton / active / empty / error).
    [Reactive] public bool ShowSkeleton { get; set; }
    [Reactive] public bool ShowActiveSub { get; set; }
    [Reactive] public bool ShowEmpty { get; set; }
    [Reactive] public bool ShowError { get; set; }

    // Logged-out CTA (nav-gating is deferred, so the tab shows a Telegram login prompt itself).
    [Reactive] public bool ShowLoginCta { get; set; }

    // ── Linking block («Способы входа») ──
    [Reactive] public bool ShowLinking { get; set; }
    [Reactive] public bool TelegramLinked { get; set; }
    [Reactive] public string TelegramLinkedId { get; set; } = string.Empty;
    [Reactive] public bool GoogleLinked { get; set; }
    [Reactive] public string GoogleLinkedId { get; set; } = string.Empty;
    [Reactive] public bool EmailLinked { get; set; }
    [Reactive] public string EmailLinkedId { get; set; } = string.Empty;

    /// <summary>True while the Telegram-link code is shown and we poll /me for the linked flag to flip.</summary>
    [Reactive] public bool TelegramLinkPending { get; set; }
    [Reactive] public string TelegramLinkCodeText { get; set; } = string.Empty;

    /// <summary>Show the «Привязать» action only when Telegram is neither linked nor pending.</summary>
    [Reactive] public bool TelegramCanLink { get; set; }

    /// <summary>The email the user types in the «Привязать почту» flyout.</summary>
    [Reactive] public string LinkEmailInput { get; set; } = string.Empty;

    #endregion reactive state (derived display)

    #region login state / inputs

    [Reactive] public LoginState CurrentLoginState { get; set; } = new LoginState.Idle();
    [Reactive] public string LoginEmail { get; set; } = string.Empty;
    [Reactive] public string LoginPassword { get; set; } = string.Empty;

    /// <summary>The «повторите пароль» field of the start-page registration form (must equal <see cref="LoginPassword"/>).</summary>
    [Reactive] public string RegisterConfirmPassword { get; set; } = string.Empty;

    [Reactive] public string TwoFaCode { get; set; } = string.Empty;

    /// <summary>The one-time handoff code the user can paste in the «войти по коду» fallback field
    /// (for platforms/edge cases where the <c>departamentvpn://auth</c> scheme callback doesn't fire).</summary>
    [Reactive] public string HandoffCodeInput { get; set; } = string.Empty;

    /// <summary>The balance top-up amount (₽) the user typed in the «Пополнить» flyout.</summary>
    [Reactive] public string TopUpAmount { get; set; } = string.Empty;

    /// <summary>Selectable Platega methods for the top-up flyout (mirrors the working Buy checkout). Empty until config loads.</summary>
    [Reactive] public List<TopUpMethodItem> TopUpMethods { get; set; } = new();

    /// <summary>Show the inline method chooser only when there is a real CHOICE (2+ methods); one method shows a caption instead.</summary>
    [Reactive] public bool ShowTopUpMethods { get; set; }

    /// <summary>«Оплата · СБП» caption shown when exactly one method exists (no chooser needed, but the method is still named).</summary>
    [Reactive] public string TopUpMethodCaption { get; set; } = string.Empty;
    [Reactive] public bool HasTopUpMethodCaption { get; set; }

    /// <summary>True only for a valid positive amount — gates the «Продолжить» button so it never reads as a dead action.</summary>
    [Reactive] public bool CanTopUp { get; set; }

    /// <summary>Inline validation error under the amount field (so an invalid amount fails visibly, not silently).</summary>
    [Reactive] public string TopUpError { get; set; } = string.Empty;
    [Reactive] public bool HasTopUpError { get; set; }

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

    /// <summary>«Войти через сайт» — opens the site's <c>/app-login</c> page in the system browser. A
    /// logged-in web session mints a one-time code there and returns to the app via the
    /// <c>departamentvpn://auth?code=…</c> scheme (redeemed by <see cref="CompleteAppHandoff"/>).</summary>
    public ReactiveCommand<Unit, Unit> LoginBrowserCmd { get; }

    /// <summary>«Войти по коду» — redeems a manually pasted handoff code (fallback when the scheme
    /// callback doesn't fire) via the same <see cref="CompleteAppHandoff"/> path.</summary>
    public ReactiveCommand<Unit, Unit> LoginByCodeCmd { get; }

    /// <summary>Start-page email+password registration (also the «отправить снова» action on the verify screen).</summary>
    public ReactiveCommand<Unit, Unit> RegisterCmd { get; }

    /// <summary>«Войти по ссылке» — request a passwordless magic sign-in link for <see cref="LoginEmail"/>.</summary>
    public ReactiveCommand<Unit, Unit> MagicLinkCmd { get; }

    /// <summary>«Забыли пароль?» — request a password-reset link for <see cref="LoginEmail"/>.</summary>
    public ReactiveCommand<Unit, Unit> PasswordResetCmd { get; }

    public ReactiveCommand<Unit, Unit> Submit2FaCmd { get; }
    public ReactiveCommand<Unit, Unit> LogoutCmd { get; }
    public ReactiveCommand<Unit, Unit> RetryCmd { get; }

    /// <summary>Sync-error «Повторить» — re-runs the post-login phase sequence (distinct from <see cref="RetryCmd"/>).</summary>
    public ReactiveCommand<Unit, Unit> SyncRetryCmd { get; }

    /// <summary>Sync-error «Войти заново» — clears the session so the shell returns to login/onboarding.</summary>
    public ReactiveCommand<Unit, Unit> SyncReLoginCmd { get; }

    /// <summary>Balance top-up: opens a Platega checkout for <see cref="TopUpAmount"/>.</summary>
    public ReactiveCommand<Unit, Unit> TopUpCmd { get; }

    // ── Linking actions ──
    public ReactiveCommand<Unit, Unit> LinkTelegramCmd { get; }
    public ReactiveCommand<Unit, Unit> OpenLinkBotCmd { get; }
    public ReactiveCommand<Unit, Unit> LinkEmailCmd { get; }
    public ReactiveCommand<Unit, Unit> OpenWebCabinetCmd { get; }

    #endregion commands

    /// <summary>Runtime constructor: seeds from the persisted session and loads real data when logged in.</summary>
    public AccountViewModel()
    {
        // Note: no AppManager access here — this VM is constructed during MainWindow field-init;
        // the engine (AppManager.Config) is only touched later, on user action, by the sync manager.
        _repo = new AccountRepository();
        _authManager = new AuthManager();

        // The single runtime instance. AccountSyncView (a static overlay in the MainWindow shell with no
        // inherited access to this VM) resolves it here to bind the live stage line and wire the retry /
        // re-login commands — self-wiring, so no MainWindow change is needed. Stays null in design mode.
        Shared = this;

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

        // Команда отдаёт Unit: свежий профиль нужен только пуллеру привязки, который зовёт метод напрямую.
        RefreshProfileCmd = ReactiveCommand.CreateFromTask(async () => { await RefreshProfile(); });
        LoadSubscriptionsCmd = ReactiveCommand.CreateFromTask(LoadSubscriptions);
        LoadTariffsCmd = ReactiveCommand.CreateFromTask(LoadTariffs);
        LoadPaymentsCmd = ReactiveCommand.CreateFromTask(LoadPayments);
        LoadDevicesCmd = ReactiveCommand.CreateFromTask(LoadActiveDevices);
        LoginTelegramCmd = ReactiveCommand.CreateFromTask(StartTelegramLogin);
        LoginSiteCmd = ReactiveCommand.CreateFromTask(LoginSite);
        LoginBrowserCmd = ReactiveCommand.Create(OpenSiteLoginBrowser);
        LoginByCodeCmd = ReactiveCommand.CreateFromTask(() => CompleteAppHandoff(HandoffCodeInput));
        RegisterCmd = ReactiveCommand.CreateFromTask(Register);
        MagicLinkCmd = ReactiveCommand.CreateFromTask(RequestMagicLink);
        PasswordResetCmd = ReactiveCommand.CreateFromTask(RequestPasswordReset);
        Submit2FaCmd = ReactiveCommand.CreateFromTask(Submit2Fa);
        LogoutCmd = ReactiveCommand.CreateFromTask(Logout);
        RetryCmd = ReactiveCommand.CreateFromTask(Retry);
        SyncRetryCmd = ReactiveCommand.CreateFromTask(SyncRetry);
        SyncReLoginCmd = ReactiveCommand.CreateFromTask(SyncReLogin);
        TopUpCmd = ReactiveCommand.CreateFromTask(TopUp);
        LinkTelegramCmd = ReactiveCommand.CreateFromTask(StartLinkTelegram);
        OpenLinkBotCmd = ReactiveCommand.Create(OpenLinkBot);
        LinkEmailCmd = ReactiveCommand.CreateFromTask(SubmitLinkEmail);
        OpenWebCabinetCmd = ReactiveCommand.CreateFromTask(OpenWebCabinet);

        // Safety net: a stray command exception surfaces as the error state instead of crashing.
        Observable.Merge(
                RefreshProfileCmd.ThrownExceptions,
                LoadSubscriptionsCmd.ThrownExceptions,
                LoadTariffsCmd.ThrownExceptions,
                LoadPaymentsCmd.ThrownExceptions,
                LoadDevicesCmd.ThrownExceptions,
                LoginTelegramCmd.ThrownExceptions,
                LoginSiteCmd.ThrownExceptions,
                LoginBrowserCmd.ThrownExceptions,
                LoginByCodeCmd.ThrownExceptions,
                RegisterCmd.ThrownExceptions,
                MagicLinkCmd.ThrownExceptions,
                PasswordResetCmd.ThrownExceptions,
                Submit2FaCmd.ThrownExceptions,
                LogoutCmd.ThrownExceptions,
                RetryCmd.ThrownExceptions,
                SyncRetryCmd.ThrownExceptions,
                SyncReLoginCmd.ThrownExceptions,
                TopUpCmd.ThrownExceptions,
                LinkTelegramCmd.ThrownExceptions,
                OpenLinkBotCmd.ThrownExceptions,
                LinkEmailCmd.ThrownExceptions,
                OpenWebCabinetCmd.ThrownExceptions)
            .Subscribe(ex => RunOnUi(() =>
            {
                Report(ex as ApiError ?? new ApiError.NetworkError(ex));
                // Defensive stuck-overlay guard: if a sync overlay is currently up (IsImportingAccount /
                // IsStartupLoading), any unhandled command exception — including one thrown by a sync
                // command's pre-phase setup that runs OUTSIDE RunSyncPhases' own try — must surface the
                // actionable error column (Повторить / Войти заново), never freeze on the spinner.
                if (IsImportingAccount || IsStartupLoading)
                {
                    IsLoading = false;
                    SyncFailed = true;
                }
                Recompute();
            }));

        // Live-validate the top-up amount as the user types: enables «Продолжить» only for a valid
        // positive amount and clears a stale inline error once the input becomes valid.
        this.WhenAnyValue(x => x.TopUpAmount).Subscribe(_ => ValidateTopUpAmount());

        AccountSession.StateChanged += OnSessionStateChanged;

        // Live language switch: re-derive every display string (balance caption, «Действует до …»,
        // device counts, referral line, error text) so open bindings pick up the new language. Also
        // re-apply the sync stage caption if a sync is mid-flight, so the loading line follows the switch.
        L.Instance.LanguageChanged += (_, _) => RunOnUi(() =>
        {
            if (IsImportingAccount || IsStartupLoading)
            {
                SetSyncStage(_syncPhase);
            }
            Recompute();
        });

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

    /// <summary>
    /// Hard ceiling on how long the COLD-START gate may hold the shell. Every individual request is
    /// already capped by the HTTP client (25s), but the restore is a SEQUENCE (account → subscriptions →
    /// server download), so nothing bounded the whole of it — a single stuck phase could keep the launch
    /// spinner up forever. Deliberately longer than one request timeout, so an ordinary slow call still
    /// resolves through its own error path and this only catches a genuinely stuck sequence.
    /// </summary>
    private static readonly TimeSpan StartupGateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Returning-user startup: auto-import subscriptions (→ refresh Home) then load the tab.</summary>
    private async Task StartupLoad()
    {
        // Same phase sequence as a fresh login MINUS the extra subscription fetch (parity with the original
        // returning-user path: import + load only). On success clear the cold-start gate so the loading
        // surface hands directly to the populated Home/Account instead of the empty login gate.
        var restore = RunSyncPhases(includeSubFetch: false);

        // Bounded gate (see StartupGateTimeout): if the restore has not resolved by the deadline we stop
        // holding the window. With servers already on disk the gate simply comes down — the restore keeps
        // running and refreshes Home when it lands. With nothing to fall back on we swap the spinner for
        // the actionable retry surface instead of spinning indefinitely; a late success clears it below.
        if (await Task.WhenAny(restore, Task.Delay(StartupGateTimeout)) != restore)
        {
            var hasLocalServers = await AppManager.Instance.HasStoredProfilesAsync() == true;
            RunOnUi(() =>
            {
                if (!IsStartupLoading)
                {
                    return;
                }
                if (hasLocalServers)
                {
                    IsStartupLoading = false;
                }
                else
                {
                    SyncFailed = true;
                }
                Recompute();
            });
        }

        if (await restore)
        {
            RunOnUi(() =>
            {
                IsStartupLoading = false;
                SyncFailed = false;   // a late success retracts anything the watchdog above put up
                Recompute();
            });
            return;
        }

        // The restore FAILED (offline, expired token, backend down). Failing to reach the ACCOUNT is not
        // a reason to hide the servers this user already has on disk: when the local store holds servers
        // the gate comes down and the app opens on them, with the failure surfaced non-blockingly on the
        // Account tab (RunSyncPhases reported it). Only when there is nothing local do we keep the
        // blocking retry surface up — there, retry/re-login really is the only meaningful next action.
        if (await AppManager.Instance.HasStoredProfilesAsync() == true)
        {
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

    #region carousel card navigation intents

    /// <summary>
    /// Raised when a carousel card's «Продлить» CTA is tapped. The view forwards this to its own
    /// <c>BuyRequested</c> event (the host opens Buy) — so card CTAs reuse the shipped navigation path
    /// without a MainWindow change and without the cards needing a view reference.
    /// </summary>
    public event EventHandler? BuyIntentRequested;

    /// <summary>Raised when a carousel card's «Устройства» link is tapped; the view forwards to <c>DevicesRequested</c>.</summary>
    public event EventHandler? DevicesIntentRequested;

    /// <summary>Raised only when a top-up checkout URL was successfully opened, so the view hides the flyout on SUCCESS (a
    /// validation failure keeps it open to show the inline error).</summary>
    public event EventHandler? TopUpCheckoutOpened;

    /// <summary>Card CTA hook: request the Buy screen (renew this subscription).</summary>
    public void RequestBuy() => BuyIntentRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Card CTA hook: request the Devices screen.</summary>
    public void RequestDevices() => DevicesIntentRequested?.Invoke(this, EventArgs.Empty);

    #endregion carousel card navigation intents

    #region loads

    private async Task LoadAll()
    {
        await RefreshProfile();
        await LoadSubscriptions();
        await LoadPublicConfig();
        await LoadTariffs();
        await LoadPayments();
    }

    /// <summary>
    /// Перечитывает профиль и обновляет вью-модель. ВОЗВРАЩАЕТ свежий профиль (или <c>null</c> при
    /// отказе), потому что применение идёт через <see cref="RunOnUi"/>, а он с фонового потока
    /// ПОСТИТ — то есть после <c>await RefreshProfile()</c> поле <see cref="Profile"/> ещё старое.
    /// Опрос привязки Telegram читал именно его и отставал на целую итерацию, а подтверждение,
    /// пришедшее в последнем окне опроса, терял совсем. Пуллеры обязаны читать возвращённое значение.
    /// </summary>
    private async Task<UserProfileDto?> RefreshProfile()
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
        return result.GetOrNull();
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
        RunOnUi(() => result
            .OnSuccess(c =>
            {
                PublicConfig = c;
                BuildTopUpMethods();
            })
            .OnFailure(Report));
    }

    /// <summary>
    /// Builds the top-up flyout's payment-method chips from <see cref="PublicConfigDto.PlategaMethods"/>
    /// (same source the working Buy checkout uses). Selects the first by default. With a single method the
    /// chooser is hidden and a «Оплата · …» caption names it instead; the id feeds <see cref="TopUp"/>'s
    /// <c>PaymentMethod</c> so the checkout actually opens.
    /// </summary>
    private void BuildTopUpMethods()
    {
        var methods = PublicConfig?.PlategaMethods ?? new List<PlategaMethodDto>();
        var items = methods
            .Select(m => new TopUpMethodItem(this, int.TryParse(m.Id, out var code) ? code : (int?)null, m.Label))
            .ToList();
        for (var i = 0; i < items.Count; i++)
        {
            items[i].IsSelected = i == 0;
        }
        TopUpMethods = items;
        ShowTopUpMethods = items.Count > 1;
        _selectedTopUpMethod = items.FirstOrDefault()?.MethodCode;
        if (items.Count == 1)
        {
            HasTopUpMethodCaption = true;
            TopUpMethodCaption = L.F("Account_TopUpVia", items[0].Label);
        }
        else
        {
            HasTopUpMethodCaption = false;
            TopUpMethodCaption = string.Empty;
        }
        // Methods can arrive AFTER the user has typed an amount (config loads async), so re-derive the
        // gate now — otherwise «Продолжить» would stay disabled until the next keystroke.
        ValidateTopUpAmount();
    }

    /// <summary>Selects one top-up method chip (single-select) and remembers its code for the next checkout.</summary>
    public void SelectTopUpMethod(TopUpMethodItem item)
    {
        _selectedTopUpMethod = item.MethodCode;
        foreach (var it in TopUpMethods)
        {
            it.IsSelected = ReferenceEquals(it, item);
        }
    }

    /// <summary>Parses a ₽ amount in either invariant or the current culture (handles "1490" and "1 490,00").</summary>
    private static bool TryParseAmount(string? raw, out double amount)
    {
        amount = 0;
        var t = raw?.Trim();
        if (t.IsNullOrEmpty())
        {
            return false;
        }
        return double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out amount)
            || double.TryParse(t, NumberStyles.Any, CultureInfo.CurrentCulture, out amount);
    }

    /// <summary>Re-derives <see cref="CanTopUp"/> from the typed amount AND the availability of a usable
    /// payment method, then clears a stale inline error once valid. Gating on the method (not just the
    /// amount) is what stops «Продолжить» from firing with a null method and dead-ending on the same
    /// "couldn't open payment" error the working Buy flow already guards against.</summary>
    private void ValidateTopUpAmount()
    {
        CanTopUp = TryParseAmount(TopUpAmount, out var amount) && amount > 0 && HasUsableTopUpMethod();
        if (CanTopUp && HasTopUpError)
        {
            HasTopUpError = false;
            TopUpError = string.Empty;
        }
    }

    /// <summary>True when at least one loaded top-up method carries a real (numeric) provider code.</summary>
    private bool HasUsableTopUpMethod()
        => TopUpMethods.Any(m => m.MethodCode is not null);

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

        // Enter the awaiting-confirmation state SYNCHRONOUSLY — before the first LoginView render and
        // before the CreateTelegramLoginToken network round-trip below — so the Telegram login path never
        // paints the method-select/credentials block. The MainWindow Telegram entry runs OpenLogin()
        // (creates + shows LoginView) and this command back-to-back with no dispatcher yield; because the
        // view observes CurrentLoginState inline on the UI thread, the awaiting state set here lands before
        // the sub-page's first layout, so LoginView shows the AwaitingBlock (spinner + «Ожидаем
        // подтверждения…») from the very first frame — never the MethodBlock. Previously this set
        // LoginState.Idle here, so the method block stayed on screen for the whole ~round-trip until the
        // real AwaitingTelegram arrived, which is the flash the owner reported.
        //
        // Placeholder = Polling with an EMPTY deep link: it maps to the same awaiting UI as AwaitingTelegram
        // (LoginView treats AwaitingTelegram and Polling identically) but carries no link, so no browser tab
        // opens before the real token/deep link is ready. We also clear TelegramDeepLink so the now-visible
        // «Открыть Telegram» button cannot reopen a stale link from a previous attempt until the fresh link
        // lands (the real AwaitingTelegram → ApplyLoginState sets TelegramDeepLink and opens the browser once).
        TelegramDeepLink = null;
        CurrentLoginState = new LoginState.Polling(string.Empty);

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
        // Also stop any start-page register/verify poll or in-flight magic-link/reset request, so leaving
        // an email flow (back / «другой способ входа») never leaves a background poll running.
        _registerCts?.Cancel();
        _registerCts = null;
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

    /// <summary>Base URL of the site's app-login handoff page (see departament-site AppLoginPage).</summary>
    private const string SiteLoginUrl = "https://departament.site/app-login";

    /// <summary>Custom URL scheme the site returns to (matches its allowlist <c>^departament[a-z0-9]*$</c>).</summary>
    public const string AppScheme = "departamentvpn";

    /// <summary>
    /// «Войти через сайт»: opens the site's <c>/app-login</c> page in the system browser, passing the
    /// app's custom-scheme return URL. When the web session is already signed in the site mints a one-time
    /// handoff code and redirects the browser to <c>departamentvpn://auth?code=…</c>, which the OS routes
    /// back to the app (single-instance IPC → <see cref="CompleteAppHandoff"/>). No backend/site change is
    /// needed: the site already honours a <c>departament…://</c> return in its safe-return allowlist.
    /// </summary>
    private void OpenSiteLoginBrowser()
    {
        RunOnUi(() =>
        {
            if (CurrentLoginState is LoginState.Error)
            {
                CurrentLoginState = new LoginState.Idle();
            }
        });
        var url = $"{SiteLoginUrl}?return={AppScheme}://auth";
        try
        {
            ProcUtils.ProcessStart(url);
        }
        catch
        {
            RunOnUi(() => AppEvents.SendSnackMsgRequested.Publish(L.T("Common_SomethingWrong")));
        }
    }

    /// <summary>
    /// Redeems a one-time app-handoff code (delivered by the <c>departamentvpn://auth</c> scheme callback
    /// or pasted into the «войти по коду» fallback) for a session. Enters
    /// <see cref="LoginState.SiteHandoffLoading"/> while the code is exchanged, then runs the SAME
    /// <see cref="OnAuthenticated"/> success path as email/Telegram login. An expired/invalid code surfaces
    /// as <see cref="LoginState.Error"/>. Safe to call from any thread.
    /// </summary>
    public async Task CompleteAppHandoff(string code)
    {
        var trimmed = code?.Trim() ?? string.Empty;
        if (trimmed.IsNullOrEmpty())
        {
            return;
        }
        _telegramCts?.Cancel();
        _registerCts?.Cancel();
        RunOnUi(() =>
        {
            TwoFaTempToken = null;
            CurrentLoginState = new LoginState.SiteHandoffLoading();
        });
        try
        {
            var profile = await _authManager.ConsumeAppHandoff(trimmed);
            RunOnUi(() => HandoffCodeInput = string.Empty);
            await OnAuthenticated(profile);
        }
        catch (ApiError e)
        {
            RunOnUi(() => CurrentLoginState = new LoginState.Error(e));
        }
    }

    /// <summary>
    /// Start-page email+password registration. Enters <see cref="LoginState.RegisterLoading"/>, then hands
    /// off to <see cref="AuthManager.BeginRegister"/> which emits either a straight
    /// <see cref="LoginState.Success"/> (verification off — same path as email login) or
    /// <see cref="LoginState.AwaitingEmailVerification"/> plus a bounded login poll. Every emit routes
    /// through <see cref="ApplyLoginState"/> on the UI thread. Doubles as the verify-screen «отправить
    /// снова» action: a fresh call cancels the previous poll, re-sends the email, and restarts polling.
    /// </summary>
    private async Task Register()
    {
        _telegramCts?.Cancel();
        _registerCts?.Cancel();
        var cts = new CancellationTokenSource();
        _registerCts = cts;
        TwoFaTempToken = null;
        CurrentLoginState = new LoginState.RegisterLoading();
        await _authManager.BeginRegister(
            LoginEmail?.Trim() ?? string.Empty,
            LoginPassword ?? string.Empty,
            state => RunOnUi(() => ApplyLoginState(state)),
            cts.Token);
    }

    /// <summary>«Войти по ссылке»: requests a magic sign-in link and moves to the «ссылка отправлена» state.</summary>
    private async Task RequestMagicLink()
    {
        var email = LoginEmail?.Trim() ?? string.Empty;
        if (email.IsNullOrEmpty() || !email.Contains('@'))
        {
            RunOnUi(() => CurrentLoginState = new LoginState.Error(new ApiError.Unauthorized()));
            return;
        }
        _registerCts?.Cancel();
        // «Отправить снова» приходит уже с экрана «ссылка отправлена» — НЕ перекидываем на форму
        // (SiteLoading увёл бы с pending-экрана); при первом запросе с формы показываем спиннер.
        if (CurrentLoginState is not LoginState.MagicLinkSent)
        {
            CurrentLoginState = new LoginState.SiteLoading();
        }
        await _authManager.BeginMagicLink(email, state => RunOnUi(() => ApplyLoginState(state)));
    }

    /// <summary>«Забыли пароль?»: requests a password-reset link and moves to the «письмо отправлено» state.</summary>
    private async Task RequestPasswordReset()
    {
        var email = LoginEmail?.Trim() ?? string.Empty;
        if (email.IsNullOrEmpty() || !email.Contains('@'))
        {
            RunOnUi(() => CurrentLoginState = new LoginState.Error(new ApiError.Unauthorized()));
            return;
        }
        _registerCts?.Cancel();
        // Как и magic-link: повторная отправка не должна уводить с экрана «письмо отправлено».
        if (CurrentLoginState is not LoginState.PasswordResetSent)
        {
            CurrentLoginState = new LoginState.SiteLoading();
        }
        await _authManager.BeginPasswordReset(email, state => RunOnUi(() => ApplyLoginState(state)));
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
            SyncFailed = false;
            SetSyncStage(SyncPhase.Account);
            Profile = profile;
            _pendingFirstLoad = true;
            IsLoading = true;
            CurrentLoginState = new LoginState.Success(profile);
            Recompute();
        });

        // Fresh-login sync phases (import → subscription fetch → account/Home load). On success clear the
        // sync signal AFTER all phases resolve, so the overlay hands directly to the populated Home. On
        // FAILURE RunSyncPhases raises SyncFailed and leaves IsImportingAccount TRUE — the overlay stays up
        // on the retry surface instead of resolving into a false success over a half-empty frame.
        if (await RunSyncPhases(includeSubFetch: true))
        {
            RunOnUi(() =>
            {
                IsLoading = false;
                IsImportingAccount = false;
                Recompute();
            });
        }
    }

    /// <summary>
    /// Runs the post-login sync phases — account import (+ Home server refresh) → [subscription fetch] →
    /// account/Home load — advancing the live <see cref="SyncStageText"/> before each real await. Returns
    /// true when every phase completes; on ANY exception it raises <see cref="SyncFailed"/> (so
    /// AccountSyncView swaps in place to the retry surface) and returns false WITHOUT clearing the caller's
    /// overlay gate — so a failed import never resolves into a false success and never strands an eternal
    /// spinner. <paramref name="includeSubFetch"/> mirrors the two existing call sites: fresh login runs the
    /// extra <see cref="FetchAndApplySubscriptions"/>, returning-user cold start does not.
    /// </summary>
    private async Task<bool> RunSyncPhases(bool includeSubFetch)
    {
        try
        {
            RunOnUi(() => SetSyncStage(SyncPhase.Account));
            await AutoImportAndRefreshHome();   // import + RefreshServers (flips Home IsEmpty=false)
            if (includeSubFetch)
            {
                RunOnUi(() => SetSyncStage(SyncPhase.Subs));
                await FetchAndApplySubscriptions();
            }
            RunOnUi(() => SetSyncStage(SyncPhase.Servers));
            await LoadAll();
            return true;
        }
        catch (Exception ex)
        {
            // Surface the failure WITHOUT clearing the gate flag: the caller keeps IsImportingAccount /
            // IsStartupLoading true, so the shell keeps the overlay up and AccountSyncView shows the retry
            // surface. No stuck-invisible state, no false hand-off to a half-empty Home.
            Logging.SaveLog("AccountSync", ex);
            RunOnUi(() =>
            {
                IsLoading = false;
                SyncFailed = true;
                // Also record it as a normal API error: when the caller decides the gate must come down
                // anyway (cold start with servers already on disk), the Account tab is where the failure
                // has to remain visible — with its own «Повторить» — instead of vanishing silently.
                Report(ex as ApiError ?? new ApiError.NetworkError(ex));
                Recompute();
            });
            return false;
        }
    }

    /// <summary>Advances the live sync stage + its localized caption (re-derivable on a language switch).</summary>
    private void SetSyncStage(SyncPhase phase)
    {
        _syncPhase = phase;
        SyncStageText = phase switch
        {
            SyncPhase.Account => L.T("Account_SyncStageAccount"),
            SyncPhase.Subs => L.T("Account_SyncSubtitle"),
            SyncPhase.Servers => L.T("Account_SyncStageServers"),
            _ => L.T("Account_SyncSubtitle"),
        };
    }

    /// <summary>
    /// Sync-error «Повторить»: re-runs the post-login phase sequence in place. Clears
    /// <see cref="SyncFailed"/> (AccountSyncView crossfades back to the loading column) and keeps the
    /// sync overlay up, then re-runs the phases; on success clears BOTH sync gates so the overlay hands to
    /// Home (whichever gate — fresh or cold-start — raised it), on failure RunSyncPhases re-raises
    /// SyncFailed and the overlay keeps the retry surface. Distinct from the Account-tab <see cref="Retry"/>.
    /// </summary>
    private async Task SyncRetry()
    {
        if (!IsLoggedIn)
        {
            // The session was wiped externally (e.g. a background 401) while the error surface was up —
            // don't leave an inert button. Drop the sync gates so the shell returns to login/onboarding,
            // exactly like «Войти заново».
            RunOnUi(() =>
            {
                SyncFailed = false;
                IsImportingAccount = false;
                IsStartupLoading = false;
                Recompute();
            });
            return;
        }
        RunOnUi(() =>
        {
            SyncFailed = false;
            IsImportingAccount = true;   // keep the overlay up during the retry (idempotent if already up)
            _pendingFirstLoad = true;
            IsLoading = true;
            SetSyncStage(SyncPhase.Account);
            Recompute();
        });

        if (await RunSyncPhases(includeSubFetch: true))
        {
            RunOnUi(() =>
            {
                IsLoading = false;
                IsImportingAccount = false;
                IsStartupLoading = false;
                Recompute();
            });
        }
    }

    /// <summary>
    /// Sync-error «Войти заново»: clears the failed session and returns the shell to the login/onboarding
    /// gate. Runs the full logout teardown FIRST (wipes the persisted session → IsLoggedIn=false) while the
    /// overlay is still up (IsImportingAccount stays true, so no Home flash), THEN drops the sync gates so
    /// the shell crossfades straight to onboarding/login.
    /// </summary>
    private async Task SyncReLogin()
    {
        await Logout();
        RunOnUi(() =>
        {
            SyncFailed = false;
            IsImportingAccount = false;
            IsStartupLoading = false;
            Recompute();
        });
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
        _registerCts?.Cancel();
        _topUpRefreshCts?.Cancel();
        _renewPollCts?.Cancel();
        _cardActionPollCts?.Cancel();
        _linkPollCts?.Cancel();
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
            RegisterConfirmPassword = string.Empty;
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
        // Invalid amount → INLINE error (kept visible in the flyout) + snack, and NO checkout: the flyout
        // stays open so the failure is never silent. (The button is also gated on CanTopUp.)
        if (!TryParseAmount(TopUpAmount, out var amount) || amount <= 0)
        {
            HasTopUpError = true;
            TopUpError = L.T("Account_AmountGtZero");
            CanTopUp = false;
            AppEvents.SendSnackMsgRequested.Publish(L.T("Account_AmountGtZero"));
            return;
        }

        // Mirror the WORKING Buy checkout: a Platega top-up REQUIRES a PaymentMethod, or the provider
        // returns no checkout URL (the "nothing happens" bug). Use the selected chip, else the first method.
        var methodCode = _selectedTopUpMethod ?? TopUpMethods.FirstOrDefault(m => m.MethodCode is not null)?.MethodCode;
        if (methodCode is null)
        {
            // No payment method loaded (config failed / not yet loaded) — surface it inline instead of
            // dead-ending on the generic "couldn't open payment" like the old code did.
            HasTopUpError = true;
            TopUpError = L.T("Buy_NoPaymentMethods");
            AppEvents.SendSnackMsgRequested.Publish(L.T("Buy_NoPaymentMethods"));
            return;
        }
        var result = await _repo.Buy(new PaymentRequestDto { Amount = amount, Currency = "RUB", PaymentMethod = methodCode });
        RunOnUi(() =>
        {
            result
                .OnSuccess(init =>
                {
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
                        TopUpAmount = string.Empty;
                        HasTopUpError = false;
                        TopUpError = string.Empty;
                        // Hide the flyout only now (success): a validation return above kept it open.
                        TopUpCheckoutOpened?.Invoke(this, EventArgs.Empty);
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

    #region renew / auto-renew / linking actions

    private static PaymentRequestDto BuildRenewRequest(AccountSubCard card) => new()
    {
        TariffId = card.TariffId,
        TariffPriceOptionId = card.PriceOptionId,
        Scope = card.Scope,
        SubscriptionId = card.SubscriptionId,
        Currency = card.Currency,
    };

    /// <summary>Scoped renewal from the wallet (POST /payments/balance) — settles immediately, then reloads.</summary>
    public async Task RenewWithBalance(AccountSubCard card)
    {
        if (!card.CanRenew || card.IsRenewing)
        {
            return;
        }
        RunOnUi(() => card.IsRenewing = true);
        var result = await _repo.PayWithBalance(BuildRenewRequest(card));
        RunOnUi(() =>
        {
            card.IsRenewing = false;
            result
                .OnSuccess(ok =>
                {
                    AppEvents.SendSnackMsgRequested.Publish(L.T("Account_RenewDone"));
                    _ = Retry();
                })
                .OnFailure(err => AppEvents.SendSnackMsgRequested.Publish(MessageFor(err)));
        });
    }

    /// <summary>
    /// Scoped card renewal (POST /payments/tariff/platega): opens the provider checkout in the browser,
    /// then re-polls GET /client/payments until the webhook confirms — mirrors BuyViewModel's poll.
    /// </summary>
    public async Task RenewWithCard(AccountSubCard card)
    {
        if (!card.CanRenew || card.IsRenewing)
        {
            return;
        }
        RunOnUi(() => card.IsRenewing = true);
        var result = await _repo.RenewTariffCard(BuildRenewRequest(card));
        RunOnUi(() =>
        {
            result
                .OnSuccess(init =>
                {
                    var url = init.PaymentUrl;
                    if (url.IsNullOrEmpty())
                    {
                        card.IsRenewing = false;
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CouldntOpenPayment"));
                        return;
                    }
                    try
                    {
                        ProcUtils.ProcessStart(url);
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CompletePaymentInBrowser"));
                        ScheduleRenewPoll(card, init);
                    }
                    catch
                    {
                        card.IsRenewing = false;
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CouldntOpenPayment"));
                    }
                })
                .OnFailure(err =>
                {
                    card.IsRenewing = false;
                    AppEvents.SendSnackMsgRequested.Publish(MessageFor(err));
                });
        });
    }

    /// <summary>Re-poll payment history a few times after a card renewal opens in the browser (no in-app return).</summary>
    private void ScheduleRenewPoll(AccountSubCard card, PaymentInitDto init)
    {
        _renewPollCts?.Cancel();
        // The cancelled task lands in its catch and returns; it can't reliably clear its own spinner
        // without racing a re-poll of the SAME card. So drop the spinner of the superseded card here —
        // but only when it's a DIFFERENT card (re-renewing the same card keeps spinning).
        if (_renewPollCard != null && !ReferenceEquals(_renewPollCard, card))
        {
            _renewPollCard.IsRenewing = false;
        }
        _renewPollCard = card;
        var cts = new CancellationTokenSource();
        _renewPollCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 5 && !cts.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
                    var payments = await _repo.GetPayments();
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    var items = payments.GetOrNull()?.Items ?? new List<PaymentDto>();
                    var confirmed = items.Any(p =>
                        PaidStatuses.Contains(p.Status.Trim()) &&
                        ((init.OrderId.IsNotEmpty() && p.OrderId == init.OrderId) ||
                         (init.PaymentId.IsNotEmpty() && p.Id == init.PaymentId)));
                    if (confirmed)
                    {
                        RunOnUi(() =>
                        {
                            card.IsRenewing = false;
                            AppEvents.SendSnackMsgRequested.Publish(L.T("Account_RenewDone"));
                            _ = Retry();
                        });
                        return;
                    }
                }
                RunOnUi(() => card.IsRenewing = false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer renewal or logout.
            }
        });
    }

    #region overflow: add-devices + upgrade

    /// <summary>Opens the device-top-up panel of a card's overflow flyout (resets to +1 and re-estimates).</summary>
    public void OpenDevicePicker(AccountSubCard card)
    {
        card.ExtraDevices = 1;
        RecomputeDevicePrice(card);
        card.SetPanel(AccountSubCard.PanelMode.Devices);
    }

    /// <summary>Opens the upgrade panel (list of eligible target tariffs).</summary>
    public void OpenUpgradePicker(AccountSubCard card) => card.SetPanel(AccountSubCard.PanelMode.Upgrade);

    /// <summary>Steps the extra-device count within [1 .. maxExtraDevices] and re-estimates the price.</summary>
    public void StepDevices(AccountSubCard card, int delta)
    {
        var max = card.MaxExtraDevices > 0 ? card.MaxExtraDevices : 1;
        card.ExtraDevices = Math.Clamp(card.ExtraDevices + delta, 1, max);
        RecomputeDevicePrice(card);
    }

    private void RecomputeDevicePrice(AccountSubCard card)
    {
        card.DevicePriceText = L.F("Account_DeviceEstimate", FormatMoney(EstimateDevicePrice(card), card.Currency));
    }

    /// <summary>
    /// Buys extra devices for a card's subscription. "balance" settles immediately (refresh the card);
    /// "platega" returns a checkout URL → open the browser + poll GET /client/payments (webhook-confirmed).
    /// </summary>
    public async Task BuyDevices(AccountSubCard card, string method)
    {
        if (card.IsDeviceBusy || !card.CanBuyDevices || card.SubscriptionId.IsNullOrEmpty())
        {
            return;
        }
        RunOnUi(() => card.IsDeviceBusy = true);
        var result = await _repo.PurchaseDevices(card.Scope, card.SubscriptionId!, card.ExtraDevices, method);
        RunOnUi(() =>
        {
            result
                .OnSuccess(dto =>
                {
                    if (dto.RequiresCheckout())
                    {
                        OpenCheckoutAndPoll(card, dto.PaymentUrl, dto.OrderId, dto.PaymentId,
                            () => card.IsDeviceBusy = false, "Account_DevicesAdded");
                    }
                    else if (dto.Ok == false)
                    {
                        // Balance settle that returned 200 but ok:false (e.g. insufficient funds) — do NOT
                        // claim success.
                        card.IsDeviceBusy = false;
                        AppEvents.SendSnackMsgRequested.Publish(MessageFor(new ApiError.Server(200)));
                    }
                    else
                    {
                        card.IsDeviceBusy = false;
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Account_DevicesAdded"));
                        _ = Retry();
                    }
                })
                .OnFailure(err =>
                {
                    card.IsDeviceBusy = false;
                    AppEvents.SendSnackMsgRequested.Publish(MessageFor(err));
                });
        });
    }

    /// <summary>Fetches a live upgrade quote for the chosen target and swaps the flyout to the confirm panel.</summary>
    public async Task SelectUpgradeTarget(AccountSubCard card, string targetTariffId, string targetName)
    {
        if (card.IsUpgradeBusy || targetTariffId.IsNullOrEmpty())
        {
            return;
        }
        RunOnUi(() => card.IsUpgradeBusy = true);
        var result = await _repo.UpgradeQuote(targetTariffId);
        RunOnUi(() =>
        {
            card.IsUpgradeBusy = false;
            result
                .OnSuccess(quote =>
                {
                    card.SelectedUpgradeTariffId = targetTariffId;
                    card.UpgradeConfirmTitle = L.F("Account_UpgradeTo", targetName);
                    var cur = quote.Currency.IsNotEmpty() ? quote.Currency : card.Currency;
                    card.UpgradeConfirmDetail = L.F("Account_UpgradeQuote", FormatMoney(quote.Amount, cur), quote.EffectiveDays);
                    card.SetPanel(AccountSubCard.PanelMode.UpgradeConfirm);
                })
                .OnFailure(err => AppEvents.SendSnackMsgRequested.Publish(MessageFor(err)));
        });
    }

    /// <summary>
    /// Confirms the upgrade to the selected target. "balance" settles immediately; "platega" returns a
    /// checkout URL → open the browser + poll GET /client/payments.
    /// </summary>
    public async Task ConfirmUpgrade(AccountSubCard card, string method)
    {
        if (card.IsUpgradeBusy || card.SelectedUpgradeTariffId.IsNullOrEmpty())
        {
            return;
        }
        RunOnUi(() => card.IsUpgradeBusy = true);
        var result = await _repo.Upgrade(card.SelectedUpgradeTariffId!, method, card.RemnawaveUuidValue ?? string.Empty);
        RunOnUi(() =>
        {
            result
                .OnSuccess(init =>
                {
                    // Balance upgrade settles immediately (no checkout URL); card upgrade returns a URL.
                    if (init.PaymentUrl.IsNotEmpty())
                    {
                        OpenCheckoutAndPoll(card, init.PaymentUrl, init.OrderId, init.PaymentId,
                            () => card.IsUpgradeBusy = false, "Account_UpgradeDone");
                    }
                    else
                    {
                        card.IsUpgradeBusy = false;
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Account_UpgradeDone"));
                        _ = Retry();
                    }
                })
                .OnFailure(err =>
                {
                    card.IsUpgradeBusy = false;
                    AppEvents.SendSnackMsgRequested.Publish(MessageFor(err));
                });
        });
    }

    /// <summary>
    /// Opens a provider checkout URL in the browser and re-polls GET /client/payments until the webhook
    /// confirms the order (mirrors <see cref="ScheduleRenewPoll"/>). On confirm: clear busy + reload the
    /// tab; on give-up/timeout: just clear busy. Shared by the card device-top-up and upgrade card paths.
    /// </summary>
    private void OpenCheckoutAndPoll(AccountSubCard card, string url, string? orderId, string? paymentId, Action clearBusy, string doneKey)
    {
        if (url.IsNullOrEmpty())
        {
            clearBusy();
            AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CouldntOpenPayment"));
            return;
        }
        try
        {
            ProcUtils.ProcessStart(url);
            AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CompletePaymentInBrowser"));
        }
        catch
        {
            clearBusy();
            AppEvents.SendSnackMsgRequested.Publish(L.T("Common_CouldntOpenPayment"));
            return;
        }

        _cardActionPollCts?.Cancel();
        // Drop the spinner of a superseded DIFFERENT card so it doesn't hang; same card keeps spinning.
        if (_cardActionCard != null && !ReferenceEquals(_cardActionCard, card))
        {
            _cardActionCard.IsDeviceBusy = false;
            _cardActionCard.IsUpgradeBusy = false;
        }
        _cardActionCard = card;
        var cts = new CancellationTokenSource();
        _cardActionPollCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 0; attempt < 5 && !cts.IsCancellationRequested; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
                    var payments = await _repo.GetPayments();
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    var items = payments.GetOrNull()?.Items ?? new List<PaymentDto>();
                    var confirmed = items.Any(p =>
                        PaidStatuses.Contains(p.Status.Trim()) &&
                        ((orderId.IsNotEmpty() && p.OrderId == orderId) ||
                         (paymentId.IsNotEmpty() && p.Id == paymentId)));
                    if (confirmed)
                    {
                        RunOnUi(() =>
                        {
                            clearBusy();
                            AppEvents.SendSnackMsgRequested.Publish(L.T(doneKey));
                            _ = Retry();
                        });
                        return;
                    }
                }
                RunOnUi(clearBusy);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer card action or logout.
            }
        });
    }

    #endregion overflow: add-devices + upgrade

    /// <summary>
    /// Flips a subscription's auto-renew and persists it (PATCH /client/auto-renew for the root, or the
    /// secondary endpoint by id). On failure the toggle is reverted so the UI never lies.
    /// </summary>
    public async Task SetAutoRenew(AccountSubCard card, bool enabled)
    {
        var result = card.Scope == "root"
            ? await _repo.TogglePrimaryAutoRenew(enabled)
            : await _repo.ToggleAutoRenew(card.SubscriptionId ?? string.Empty, enabled);
        RunOnUi(() =>
        {
            result
                .OnSuccess(_ =>
                {
                    card.AutoRenewCaption = enabled ? L.T("Account_AutoRenewOn") : L.T("Account_AutoRenewOff");
                    // Persist the flag on EVERY in-memory representation a later rebuild could read from,
                    // so neither a re-merge (from Profile / _lastAll) nor a plain Recompute (which reuses
                    // the existing Subscriptions list) flips the toggle back. The root is keyed by Type
                    // (its SubInfoDto id may be blank); a secondary by its id — which is exactly the gap
                    // that made secondaries revert within seconds of the next poll.
                    if (card.Scope == "root")
                    {
                        if (Profile != null)
                        {
                            Profile.AutoRenewEnabled = enabled;
                        }
                        ApplyAutoRenewFlag(s => string.Equals(s.Type, "root", StringComparison.OrdinalIgnoreCase), enabled);
                    }
                    else
                    {
                        var id = card.SubscriptionId;
                        ApplyAutoRenewFlag(s => id.IsNotEmpty() && s.Id == id, enabled);
                    }
                })
                .OnFailure(err =>
                {
                    card.SetAutoRenewSilently(!enabled);
                    AppEvents.SendSnackMsgRequested.Publish(MessageFor(err));
                });
        });
    }

    /// <summary>Writes the auto-renew flag onto every matching in-memory <see cref="SubInfoDto"/> (both the
    /// live <see cref="Subscriptions"/> list and the cached <c>_lastAll</c> a re-merge reads from), so a
    /// rebuild reflects the persisted state instead of reverting the toggle.</summary>
    private void ApplyAutoRenewFlag(Func<SubInfoDto, bool> match, bool enabled)
    {
        foreach (var s in Subscriptions)
        {
            if (match(s))
            {
                s.AutoRenewEnabled = enabled;
            }
        }
        foreach (var s in _lastAll)
        {
            if (match(s))
            {
                s.AutoRenewEnabled = enabled;
            }
        }
    }

    /// <summary>Requests a Telegram-link code, opens the bot, and polls /me until telegramLinked flips.</summary>
    private async Task StartLinkTelegram()
    {
        if (TelegramLinked)
        {
            return;
        }
        var result = await _repo.RequestLinkTelegram();
        RunOnUi(() =>
        {
            result
                .OnSuccess(dto =>
                {
                    _linkBotUsername = FirstNonBlank(dto.BotUsername, PublicConfig?.TelegramBotUsername, BackendConfig.BotUsername);
                    TelegramLinkCodeText = L.F("Account_TgLinkCode", dto.Code);
                    TelegramLinkPending = true;
                    TelegramCanLink = false;
                    OpenLinkBot();
                    ScheduleLinkPoll();
                    // Пользователю сказали, ЧТО делать. Раньше открывался чат с ботом, а код лежал в
                    // пилюле рядом с кнопкой на другом экране — связи между двумя фактами не было ни
                    // одной, и «привязка не работает» означало «я не понял, что от меня хотят».
                    Notify.Show(L.F("Account_TgLinkSend", dto.Code));
                })
                .OnFailure(err => Notify.Show(MessageFor(err), L.T("Common_Retry"), () => _ = StartLinkTelegram()));
        });
    }

    private void OpenLinkBot()
    {
        if (_linkBotUsername.IsNullOrEmpty())
        {
            return;
        }
        try
        {
            ProcUtils.ProcessStart($"https://t.me/{_linkBotUsername.TrimStart('@')}");
        }
        catch
        {
            // Opening the browser failed — the code is still visible for a manual send.
        }
    }

    /// <summary>
    /// Ждёт подтверждения привязки в Telegram. Такт и потолок — те же, что у входа
    /// (<c>AuthManager.BeginTelegramLogin</c>: опрос раз в ~2 с, потолок ~3 мин), чтобы два
    /// одинаковых на вид ожидания не вели себя по-разному.
    ///
    /// ЗАВЕРШАЕТСЯ ВСЕГДА, и это главное здесь. Раньше цикл, не дождавшись, просто заканчивался:
    /// <see cref="TelegramLinkPending"/> оставался <c>true</c> НАВСЕГДА, а
    /// <see cref="TelegramCanLink"/> вычисляется как <c>!TelegramLinked &amp;&amp; !TelegramLinkPending</c> —
    /// значит кнопка «Привязать» не возвращалась до перезапуска приложения. Одна неудачная попытка
    /// делала привязку невозможной — ровно то, о чём владелец сообщил как «не подвязывается».
    /// </summary>
    private void ScheduleLinkPoll()
    {
        _linkPollCts?.Cancel();
        var cts = new CancellationTokenSource();
        _linkPollCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                    // Читаем ВОЗВРАЩЁННЫЙ профиль, а не поле: применение идёт через Dispatcher.Post,
                    // поэтому поле здесь ещё старое (см. RefreshProfile).
                    var fresh = await RefreshProfile();
                    if (fresh?.TelegramLinked == true)
                    {
                        RunOnUi(() =>
                        {
                            TelegramLinkPending = false;
                            TelegramLinkCodeText = string.Empty;
                            Notify.Show(L.T("Account_LinkDone"));
                        });
                        return;
                    }
                }

                if (!cts.IsCancellationRequested)
                {
                    // Истекло. Возвращаем кнопку и говорим, почему — молчащее ожидание без выхода
                    // неотличимо от сломанной функции.
                    RunOnUi(() =>
                    {
                        TelegramLinkPending = false;
                        TelegramLinkCodeText = string.Empty;
                        TelegramCanLink = !TelegramLinked;
                        Notify.Show(L.T("Account_LinkExpired"), L.T("Common_Retry"), () => _ = StartLinkTelegram());
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Отменено выходом из аккаунта или более свежей попыткой — состояние ставит она.
            }
        });
    }

    /// <summary>Requests an email-link confirmation for the typed address (anti-enumeration: same reply either way).</summary>
    private async Task SubmitLinkEmail()
    {
        var email = LinkEmailInput?.Trim() ?? string.Empty;
        if (email.IsNullOrEmpty() || !email.Contains('@'))
        {
            AppEvents.SendSnackMsgRequested.Publish(L.T("Login_EmailInvalid"));
            return;
        }
        var result = await _repo.RequestLinkEmail(email);
        RunOnUi(() =>
        {
            result
                .OnSuccess(_ =>
                {
                    AppEvents.SendSnackMsgRequested.Publish(L.F("Account_EmailSent", email));
                    LinkEmailInput = string.Empty;
                })
                .OnFailure(err => AppEvents.SendSnackMsgRequested.Publish(MessageFor(err)));
        });
    }

    /// <summary>Opens the web cabinet already signed in via an app→site SSO handoff code.</summary>
    private async Task OpenWebCabinet()
    {
        var result = await _repo.CreateAppHandoff();
        RunOnUi(() =>
        {
            result
                .OnSuccess(dto =>
                {
                    var site = FirstNonBlank(PublicConfig?.SiteUrl, PublicConfig?.PublicAppUrl).TrimEnd('/');
                    var url = site.IsNotEmpty() ? $"{site}/tg-login?code={dto.Code}" : string.Empty;
                    if (url.IsNullOrEmpty())
                    {
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_SomethingWrong"));
                        return;
                    }
                    try
                    {
                        ProcUtils.ProcessStart(url);
                    }
                    catch
                    {
                        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_SomethingWrong"));
                    }
                })
                .OnFailure(err => AppEvents.SendSnackMsgRequested.Publish(MessageFor(err)));
        });
    }

    #endregion renew / auto-renew / linking actions

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

    /// <summary>Routes a stray command exception (from a command not in the VM-level ThrownExceptions
    /// merge, e.g. an <see cref="UpgradeTargetOption"/>'s SelectCmd) to the error surface on the UI thread,
    /// mirroring the merge so it never reaches the Rx default handler.</summary>
    public void ReportCommandException(Exception ex)
        => RunOnUi(() => Report(ex as ApiError ?? new ApiError.NetworkError(ex)));

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
            BalanceAmountText = string.Empty;
            BalanceCurrencyText = string.Empty;
            HasBalance = false;
            ReferralText = string.Empty;
            ReferralCode = string.Empty;
            HasReferral = false;
            ShowLinking = false;
            TelegramLinked = GoogleLinked = EmailLinked = false;
            TelegramLinkedId = GoogleLinkedId = EmailLinkedId = string.Empty;
            TelegramLinkPending = false;
            TelegramCanLink = false;
        }
        else
        {
            // Same precedence the Home chip renders (telegramUsername → «@…» → telegramName → email),
            // shared via AccountSession so the two identity surfaces never drift.
            Username = AccountSession.DisplayNameFor(profile);
            AvatarInitial = Monogram(Username);
            BalanceText = FormatMoney(profile.Balance, profile.Currency);
            // Typeset as money: amount and the currency symbol split, so the view can step the ₽ down.
            BalanceAmountText = FormatMoneyAmount(profile.Balance);
            BalanceCurrencyText = CurrencySymbol(profile.Currency);
            HasBalance = true;
            HasReferral = profile.ReferralCode.IsNotEmpty();
            ReferralCode = HasReferral ? profile.ReferralCode : string.Empty;
            ReferralText = HasReferral ? L.F("Account_ReferralCode", profile.ReferralCode) : string.Empty;

            // Linking block: how this account is reachable / recoverable. Each row reads linked (green
            // chip + muted identifier) or offers an accent action. A Telegram link stays "pending" until
            // /me flips telegramLinked (the poll below), so the code stays on screen across a Recompute.
            ShowLinking = true;
            TelegramLinked = profile.TelegramLinked;
            TelegramLinkedId = TelegramLinked
                ? (profile.TelegramUsername.IsNotEmpty() ? "@" + profile.TelegramUsername!.TrimStart('@') : string.Empty)
                : string.Empty;
            if (TelegramLinked)
            {
                TelegramLinkPending = false;
            }
            TelegramCanLink = !TelegramLinked && !TelegramLinkPending;
            GoogleLinked = profile.GoogleLinked;
            GoogleLinkedId = GoogleLinked ? profile.Email : string.Empty;
            EmailLinked = profile.HasPassword && profile.Email.IsNotEmpty();
            EmailLinkedId = EmailLinked ? profile.Email : string.Empty;
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

            // Hero expiry line agrees with the card: a far-future sentinel reads «Бессрочно», not a fake
            // 04.06.2099. A genuinely blank expiry stays hidden (unknown), not «Бессрочно».
            if (sub.ExpireAtIso.IsNotEmpty() && IsEffectivelyPerpetual(sub.ExpireAtIso))
            {
                HasSubExpiry = true;
                SubExpiry = L.T("Account_Perpetual");
            }
            else
            {
                HasSubExpiry = sub.ExpireAtIso.IsNotEmpty();
                SubExpiry = HasSubExpiry ? L.F("Account_ValidUntil", FormatIsoDate(sub.ExpireAtIso)) : string.Empty;
            }

            var unlimited = sub.Subscription?.Raw()?.IsUnlimitedDevices() == true;
            var totalStr = unlimited ? "∞" : sub.TotalDevices.ToString();
            var used = DeviceCount ?? 0;
            SubDevicesText = L.F("Account_DevicesCount", used, totalStr);
            DevicesRowValue = $"{used} / {totalStr}";
            HasDevicesRowValue = true;
        }

        // Identity-line tariff signal: the tariff name when known, else the trial marker, else nothing.
        if (HasTariffBadge)
        {
            TariffCaptionText = L.F("Account_TariffCaption", TariffBadge);
            HasTariffCaption = true;
        }
        else if (sub?.IsTrial == true)
        {
            TariffCaptionText = L.T("Account_TrialPeriod");
            HasTariffCaption = true;
        }
        else
        {
            TariffCaptionText = string.Empty;
            HasTariffCaption = false;
        }

        // Carousel cards: EVERY subscription (active/root first), each with health + expiry urgency + a
        // device-usage bar. The connected-device count is only known for the active sub (index 0), so
        // only that card shows the honest used/total bar; secondaries show their total device slots.
        var cards = new List<AccountSubCard>();
        for (var i = 0; i < Subscriptions.Count; i++)
        {
            cards.Add(BuildCard(Subscriptions[i], i));
        }
        // Keep the same fixed width the view last measured so a rebuild doesn't reset cards to a stale size.
        foreach (var c in cards)
        {
            c.CardWidth = CardWidth;
        }
        SubCards = cards;
        HasMultipleSubs = cards.Count > 1;
        if (CarouselIndex > cards.Count - 1)
        {
            CarouselIndex = cards.Count > 0 ? cards.Count - 1 : 0;
        }

        // History row trailing value (latest payment date)
        var latestIso = Payments.Count > 0 ? Payments.MaxBy(p => p.CreatedAt)?.CreatedAt : null;
        var date = FormatIsoDate(latestIso);
        HasHistoryRowValue = date.IsNotEmpty();
        HistoryRowValue = date;

        // Error text
        ErrorText = Error != null ? MessageFor(Error) : string.Empty;
        HasError = ErrorText.IsNotEmpty();

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
        else if (Error != null && !_hasSubData)
        {
            // «Подписок нет» — вывод, который можно делать ТОЛЬКО из успешно загруженного списка
            // (_hasSubData). Если список так и не приехал, а ошибка есть — это состояние ошибки с
            // кнопкой «Повторить», а не пустой аккаунт. Кэшированный профиль (он подставляется из
            // хранилища при холодном старте, ещё до сети) больше не маскирует эту разницу: раньше
            // условие требовало Profile == null, и вернувшийся пользователь без сети видел
            // «у вас нет подписки» вместо честного «не удалось загрузить».
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

    /// <summary>Health state of a subscription, derived purely from its expiry date. Colour-blind-safe: paired with copy.</summary>
    private enum SubHealth { Active, Expiring, Expired }

    /// <summary>
    /// Builds one carousel card of a subscription. The card is now a self-complete object: state-led
    /// title («Ваша подписка» / «Подписка N»), health chip, THREE grouped meters (expiry · traffic ·
    /// devices), a real scoped renewal + an auto-renew toggle with its next-charge line. The tariff word
    /// is deliberately absent — it lives once, on the hero identity line.
    /// </summary>
    private AccountSubCard BuildCard(SubInfoDto sub, int index)
    {
        // Title: the user's rename if set, else «Ваша подписка» (root/first), else «Подписка N».
        var name = sub.DisplayName.IsNotEmpty()
            ? sub.DisplayName!
            : (index == 0 ? L.T("Account_YourSubscription") : L.F("Account_SubscriptionN", index + 1));

        var (health, expiryText, urgent) = ResolveHealth(sub);
        var healthLabel = health switch
        {
            SubHealth.Expired => L.T("Account_HealthExpired"),
            SubHealth.Expiring => L.T("Account_HealthExpiring"),
            _ => L.T("Account_HealthActive"),
        };

        var raw = sub.Subscription?.Raw();

        // Traffic meter (NEW — reuses Home's renderer): used vs limit; unlimited → empty track + label.
        var showTrafficPill = raw != null;
        var trafficUnlimited = raw?.IsUnlimitedTraffic() != false;
        long trafficUsed = raw?.TrafficUsed ?? raw?.UserTraffic?.UsedTrafficBytes ?? 0;
        var trafficLimit = raw?.TrafficLimitBytes ?? 0;
        string trafficText;
        double trafficWidth = 0;
        if (trafficUnlimited)
        {
            trafficText = L.F("Account_TrafficUnlimited", FormatBytes(trafficUsed));
        }
        else
        {
            trafficText = $"{FormatBytes(trafficUsed)} / {FormatBytes(trafficLimit)}";
            // Guard a non-null limit of 0 (backend normally uses null for unlimited): avoids 0/0 = NaN
            // width (an unfilled, but harmless, bar) and keeps the fill honest.
            trafficWidth = trafficLimit > 0
                ? TrafficPillWidth * Math.Clamp((double)trafficUsed / trafficLimit, 0.0, 1.0)
                : 0.0;
        }

        // Device meter (kept, honestly labelled — no more "0 из ∞"). Only the active/root card knows the
        // live connected count; secondaries advertise their total device slots.
        var unlimitedDevices = raw?.IsUnlimitedDevices() == true || sub.TotalDevices <= 0;
        var total = sub.TotalDevices;
        var usedDevices = index == 0 ? (DeviceCount ?? 0) : 0;
        var showDeviceBar = index == 0 && !unlimitedDevices;
        string devicesText;
        double deviceWidth = 0;
        if (unlimitedDevices)
        {
            devicesText = L.T("Account_DevicesUnlimited");
        }
        else if (index == 0)
        {
            devicesText = L.F("Account_DevicesShort", usedDevices, total);
            deviceWidth = TrafficPillWidth * Math.Clamp((double)usedDevices / total, 0.0, 1.0);
        }
        else
        {
            devicesText = L.F("Account_DevicesTotal", total);
        }

        // Scoped renewal target. scope "root"|"secondary"; subscriptionId = client (account) id for root,
        // else the secondary sub id. Renew is a re-buy of the same tariff, so it needs the tariff id.
        var scope = string.Equals(sub.Type, "root", StringComparison.OrdinalIgnoreCase) ? "root" : "secondary";
        var subscriptionId = scope == "root" ? (Profile?.Id ?? string.Empty) : sub.Id;
        var tariffId = sub.TariffId;
        var canRenew = tariffId.IsNotEmpty();
        var currency = sub.TariffCurrency.IsNotEmpty() ? sub.TariffCurrency! : "RUB";
        var balanceLabel = Profile != null
            ? L.F("Account_RenewFromBalance", FormatMoney(Profile.Balance, Profile.Currency))
            : string.Empty;

        // Auto-renew line. The toggle targets the primary endpoint for the root sub (no id) or the
        // secondary endpoint by id; the next-charge line is only known for the root (from the primary
        // payload). Expiring + off → a gentle nudge instead of the plain "off".
        var autoRenewOn = sub.AutoRenewEnabled;
        var showAutoRenew = scope == "root" || sub.Id.IsNotEmpty();
        string autoRenewCaption;
        if (autoRenewOn)
        {
            if (index == 0 && _lastPrimary?.AutoRenewNextChargeAt.IsNotEmpty() == true && _lastPrimary.AutoRenewNextChargeAmount is { } amt)
            {
                var when = FormatIsoShort(_lastPrimary.AutoRenewNextChargeAt);
                var cur = _lastPrimary.AutoRenewCurrency.IsNotEmpty() ? _lastPrimary.AutoRenewCurrency! : currency;
                autoRenewCaption = L.F("Account_AutoRenewNext", when, FormatMoney(amt, cur));
            }
            else if (sub.ExpireAtIso.IsNotEmpty() && !IsEffectivelyPerpetual(sub.ExpireAtIso))
            {
                // No explicit next-charge (secondary sub, or the primary payload lacked it): still lead
                // with the same «Продлится …» voice using the known expiry date, so ON never flips between
                // «Продлится 03.06 — спишем 150 ₽» and the bare «Автопродление включено».
                autoRenewCaption = L.F("Account_AutoRenewOnDate", FormatIsoShort(sub.ExpireAtIso));
            }
            else
            {
                autoRenewCaption = L.T("Account_AutoRenewOn");
            }
        }
        else
        {
            autoRenewCaption = health == SubHealth.Expiring ? L.T("Account_AutoRenewNudge") : L.T("Account_AutoRenewOff");
        }

        // Overflow «Ещё»: device top-up + upgrade eligibility (both keyed off the loaded catalog). The
        // active (root, index 0) card is the only one whose used-device count we know; either card can
        // still top up / upgrade. Trials cannot be upgraded (backend rule); a top-up needs remaining time.
        var tariff = Tariffs.SelectMany(g => g.Tariffs).FirstOrDefault(t => t.Id == tariffId);
        var remainingDays = ComputeRemainingDays(sub.ExpireAtIso);
        var maxExtra = tariff?.MaxExtraDevices ?? 0;
        var pricePerExtra = tariff?.PricePerExtraDevice ?? 0;
        var remnawaveUuid = sub.RemnawaveUuid;
        var canBuyDevices = !sub.IsTrial
            && maxExtra > 0
            && pricePerExtra > 0
            && remainingDays > 0
            && subscriptionId.IsNotEmpty();

        var card = new AccountSubCard(this)
        {
            Name = name,
            HealthLabel = healthLabel,
            IsHealthActive = health == SubHealth.Active,
            IsHealthExpiring = health == SubHealth.Expiring,
            IsHealthExpired = health == SubHealth.Expired,
            ExpiryText = expiryText,
            ExpiryUrgent = urgent && health == SubHealth.Expiring,
            ExpiryExpired = health == SubHealth.Expired,
            MetersDim = health == SubHealth.Expired,

            ShowTrafficPill = showTrafficPill,
            TrafficText = trafficText,
            TrafficBrush = TrafficFillBrush,
            TrafficFillWidth = trafficWidth,

            DevicesText = devicesText,
            ShowUsageBar = showDeviceBar,
            UsageWidth = deviceWidth,

            RenewPrimary = health != SubHealth.Active,
            CanRenew = canRenew,
            BalanceMethodLabel = balanceLabel,
            TariffId = tariffId,
            PriceOptionId = sub.TariffPriceOptionId,
            Scope = scope,
            SubscriptionId = subscriptionId,
            Currency = currency,

            ShowAutoRenew = showAutoRenew,
            AutoRenewCaption = autoRenewCaption,

            MaxExtraDevices = maxExtra,
            PricePerExtraDevice = pricePerExtra,
            RemainingDays = remainingDays,
            CanBuyDevices = canBuyDevices,
            RemnawaveUuidValue = remnawaveUuid,
        };

        // Eligible upgrades: catalog tariffs strictly above the current one (by price), excluding trials
        // and the current tariff. Built after the card so each option can call back into it.
        var currentPrice = tariff?.Price ?? sub.TariffPrice ?? 0;
        // Only offer upgrades when we actually know the current price (> 0). If the active tariff isn't
        // in the loaded catalog AND its price is unknown, currentPrice would be 0 and EVERY paid tariff —
        // including cheaper/lateral ones — would falsely qualify as an "upgrade".
        if (!sub.IsTrial && remnawaveUuid.IsNotEmpty() && currentPrice > 0)
        {
            var targets = new List<UpgradeTargetOption>();
            foreach (var t in Tariffs.SelectMany(g => g.Tariffs))
            {
                if (t.Id == tariffId || t.Id.IsNullOrEmpty() || t.Price <= currentPrice)
                {
                    continue;
                }
                targets.Add(new UpgradeTargetOption(this, card, t.Id, t.Name,
                    FormatMoney(t.Price, t.Currency.IsNotEmpty() ? t.Currency : currency)));
            }
            card.UpgradeTargets = targets;
            card.HasUpgradeTargets = targets.Count > 0;
        }

        card.ShowMore = canBuyDevices || card.HasUpgradeTargets;
        if (canBuyDevices)
        {
            card.ExtraDevices = 1;
            card.DevicePriceText = L.F("Account_DeviceEstimate", FormatMoney(EstimateDevicePrice(card), currency));
        }

        card.SetAutoRenewSilently(autoRenewOn);
        card.Arm();
        return card;
    }

    /// <summary>Whole days remaining until an ISO expiry (0 if past / unparseable / perpetual).</summary>
    private static int ComputeRemainingDays(string? iso)
    {
        if (iso.IsNullOrEmpty())
        {
            return 0;
        }
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expire))
        {
            var days = (int)Math.Ceiling((expire - DateTimeOffset.UtcNow).TotalDays);
            return Math.Max(0, days);
        }
        return 0;
    }

    /// <summary>
    /// CLIENT-SIDE device-top-up estimate (there is no device price-quote endpoint):
    /// pricePerExtraDevice × extras × (100 − tierDiscount)/100 × remainingDays/30. The volume
    /// <c>deviceDiscountTiers</c> are NOT exposed on the tariff DTO the client already holds (that DTO is
    /// owned by the API layer and out of scope here), so the tier discount is treated as 0 — the estimate
    /// is therefore an upper bound; the backend applies any real tier discount at purchase (hence the «≈»).
    /// </summary>
    private static double EstimateDevicePrice(AccountSubCard card)
    {
        const double tierDiscount = 0.0;
        var remainingFactor = card.RemainingDays / 30.0;
        var price = card.PricePerExtraDevice * card.ExtraDevices * ((100.0 - tierDiscount) / 100.0) * remainingFactor;
        return Math.Max(0, price);
    }

    /// <summary>The traffic-pill track width (px), mirrored from <c>Size.TrafficPill</c>, used to size the usage-bar fill.</summary>
    private const double TrafficPillWidth = 160.0;

    /// <summary>
    /// The light→accent traffic-fill gradient (the ONE sanctioned gradient — it encodes a value), built
    /// once and shared by every card. Tuned for the pure-dark Incy surface (start ≈ near-white), matching
    /// Home's <c>SubscriptionMetaView.BuildTrafficBrush</c> so the two surfaces never drift.
    /// </summary>
    private static readonly IBrush TrafficFillBrush = BuildTrafficFillBrush();

    private static IBrush BuildTrafficFillBrush()
    {
        var accent = Color.Parse("#4C8DFF");
        var start = BlendToWhite(accent, 0.82);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0d, 0.5d, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1d, 0.5d, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(start, 0d),
                new GradientStop(accent, 1d),
            },
        };
    }

    private static Color BlendToWhite(Color a, double t)
    {
        byte Mix(byte x) => (byte)Math.Round(x + (255 - x) * t);
        return Color.FromArgb(0xFF, Mix(a.R), Mix(a.G), Mix(a.B));
    }

    /// <summary>
    /// True when an ISO expiry is a far-future "forever" sentinel — a concrete backend date of
    /// year ≥ 2099 (the sample sends 2099-06-04) or more than ~10 years out — so it renders «Бессрочно»
    /// instead of a specific, fake-looking calendar date. A blank/absent expiry is ALSO perpetual; an
    /// unparseable value is not (shown verbatim as active). Used by BOTH the card health and the hero line.
    /// </summary>
    private static bool IsEffectivelyPerpetual(string? iso)
    {
        if (iso.IsNullOrEmpty())
        {
            return true;
        }
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expire))
        {
            return expire.Year >= 2099 || (expire - DateTimeOffset.UtcNow).TotalDays > 3650;
        }
        return false;
    }

    /// <summary>Resolves a subscription's health + urgency copy from its expiry date (no/far-future expiry ⇒ perpetual/active).</summary>
    private static (SubHealth health, string expiryText, bool urgent) ResolveHealth(SubInfoDto sub)
    {
        var iso = sub.ExpireAtIso;
        if (IsEffectivelyPerpetual(iso))
        {
            return (SubHealth.Active, L.T("Account_Perpetual"), false);
        }
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expire))
        {
            var days = (expire - DateTimeOffset.UtcNow).TotalDays;
            if (days < 0)
            {
                return (SubHealth.Expired, L.F("Account_ExpiredOnDate", FormatIsoDate(iso)), true);
            }
            if (days <= 7)
            {
                var n = Math.Max(1, (int)Math.Ceiling(days));
                return (SubHealth.Expiring, L.F("Account_ExpiresInDays", n), true);
            }
            return (SubHealth.Active, L.F("Account_ActiveUntil", FormatIsoDate(iso)), false);
        }
        // Unparseable date — show it verbatim and treat as active rather than inventing urgency.
        return (SubHealth.Active, L.F("Account_ActiveUntil", FormatIsoDate(iso)), false);
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
        return $"{FormatMoneyAmount(amount)} {CurrencySymbol(currency)}";
    }

    /// <summary>The bare money amount (whole amounts drop the decimals) — the currency symbol is typeset separately.</summary>
    private static string FormatMoneyAmount(double amount) => amount % 1.0 == 0.0
        ? ((long)amount).ToString(CultureInfo.InvariantCulture)
        : amount.ToString("0.00", CultureInfo.InvariantCulture);

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

    /// <summary>Short "dd.MM" for the auto-renew next-charge line (year is redundant next to «спишем»).</summary>
    private static string FormatIsoShort(string? iso)
    {
        if (iso.IsNullOrEmpty())
        {
            return string.Empty;
        }
        var parts = iso!.Split('T')[0].Split('-');
        return parts.Length == 3 ? $"{parts[2]}.{parts[1]}" : FormatIsoDate(iso);
    }

    /// <summary>
    /// Localized byte formatter — ported 1:1 from Home's <c>SubscriptionMetaView.FormatBytes</c> so the
    /// account traffic pill reads identically to Home's («18,4 ГБ» in RU, «18.4 GB» in EN). Base 1024.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return L.T("Common_ZeroBytes");
        }
        var units = L.T("Common_ByteUnits").Split(',');
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var culture = CultureInfo.GetCultureInfo(L.Instance.CurrentLang == "en" ? "en-US" : "ru-RU");
        var digits = unit == 0 ? 0 : 1;
        var text = value.ToString("N" + digits, culture);
        var trailingZero = culture.NumberFormat.NumberDecimalSeparator + "0";
        if (digits == 1 && text.EndsWith(trailingZero, StringComparison.Ordinal))
        {
            text = text[..^trailingZero.Length];
        }
        return $"{text} {units[unit]}";
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

/// <summary>
/// One subscription rendered as a self-complete carousel card: a state-led title («Ваша подписка» /
/// «Подписка N»), a health chip, THREE grouped meters (expiry · traffic pill · device pill), a real
/// scoped renewal (wallet or card), and an auto-renew toggle with its next-charge line. The tariff is
/// intentionally NOT on the card — it lives once, on the hero. Built by
/// <see cref="AccountViewModel.BuildCard"/>; actions call back into the shared VM, so a card never needs
/// a view reference and MainWindow stays untouched.
/// </summary>
public sealed class AccountSubCard : ReactiveObject
{
    private readonly AccountViewModel _owner;

    // Guards the auto-renew network call: the toggle's initial value is set (SetAutoRenewSilently) BEFORE
    // Arm(), so building a card never fires a spurious PATCH; only a genuine user flip does.
    private bool _armed;

    public string Name { get; init; } = string.Empty;

    public string HealthLabel { get; init; } = string.Empty;
    public bool IsHealthActive { get; init; }
    public bool IsHealthExpiring { get; init; }
    public bool IsHealthExpired { get; init; }

    public string ExpiryText { get; init; } = string.Empty;

    /// <summary>Expiring (≤7d) — the expiry line reads in the warning tone.</summary>
    public bool ExpiryUrgent { get; init; }

    /// <summary>Expired — the expiry line reads in the destructive tone.</summary>
    public bool ExpiryExpired { get; init; }

    /// <summary>Expired ⇒ dim the (inactive) meters to 0.5 so the eye goes to «Продлить».</summary>
    public bool MetersDim { get; init; }

    // Traffic meter (NEW — reuses Home's fill gradient + byte formatter).
    public bool ShowTrafficPill { get; init; }
    public string TrafficText { get; init; } = string.Empty;
    public IBrush? TrafficBrush { get; init; }
    public double TrafficFillWidth { get; init; }

    // Device meter (kept, honestly labelled).
    public string DevicesText { get; init; } = string.Empty;
    public bool ShowUsageBar { get; init; }
    public double UsageWidth { get; init; }

    /// <summary>Expiring/expired ⇒ the «Продлить» CTA is promoted to Primary; active ⇒ it stays quiet (Tonal).</summary>
    public bool RenewPrimary { get; init; }

    /// <summary>True when the sub carries a tariff id to re-buy; false ⇒ the CTA falls back to the Buy screen.</summary>
    public bool CanRenew { get; init; }

    /// <summary>«С баланса · 1 490 ₽» label for the wallet renewal option.</summary>
    public string BalanceMethodLabel { get; init; } = string.Empty;

    /// <summary>True while a renewal is settling / the browser poll runs — the CTA shows an in-slot spinner.</summary>
    [Reactive] public bool IsRenewing { get; set; }

    /// <summary>
    /// The renew choice (баланс / карта) is revealed INLINE inside the card (not a flyover popup) when
    /// «Продлить» is tapped on a renewable card — removing the whole flyout-placement class of bug (the
    /// popup that flipped over the card + meters). A card with no tariff to re-buy skips this and goes
    /// straight to the Buy screen.
    /// </summary>
    private bool _renewExpanded;
    public bool RenewExpanded
    {
        get => _renewExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _renewExpanded, value);
            this.RaisePropertyChanged(nameof(RenewTopPrimary));
        }
    }

    /// <summary>The top «Продлить» button is the ONE primary only while the inline pay-choice is collapsed;
    /// once expanded, the inner «Оплатить картой» owns the single accent, so the top demotes to Tonal
    /// (never two filled primaries stacked in the expiring/expired state).</summary>
    public bool RenewTopPrimary => RenewPrimary && !RenewExpanded;

    // Auto-renew.
    public bool ShowAutoRenew { get; init; }
    [Reactive] public bool AutoRenewOn { get; set; }
    [Reactive] public string AutoRenewCaption { get; set; } = string.Empty;

    // Scoped renewal target (see AccountViewModel.BuildCard).
    internal string? TariffId { get; init; }
    internal string? PriceOptionId { get; init; }
    internal string Scope { get; init; } = "root";
    internal string? SubscriptionId { get; init; }
    internal string Currency { get; init; } = "RUB";

    /// <summary>Fixed card width (px) the carousel assigns from its viewport; reactive so a resize reflows every card.</summary>
    [Reactive] public double CardWidth { get; set; } = 320;

    // ── Overflow «Ещё» (докупка устройств / апгрейд тарифа) ─────────────────────
    // The card header carries a kebab that opens a single flyout with four mutually-exclusive panels
    // (menu → devices | upgrade → upgrade-confirm), toggled by these bools. Secondary actions: they stay
    // out of the card body so «Продлить» remains the one primary CTA.

    /// <summary>True when the overflow «Ещё» affordance is offered (device top-up and/or an upgrade is possible).</summary>
    public bool ShowMore { get; set; }

    [Reactive] public bool MenuMode { get; set; } = true;
    [Reactive] public bool DeviceMode { get; set; }
    [Reactive] public bool UpgradeMode { get; set; }
    [Reactive] public bool UpgradeConfirmMode { get; set; }

    // Device top-up. Estimate = pricePerExtraDevice × extras × (100−tierDiscount)/100 × remainingDays/30.
    internal int MaxExtraDevices { get; init; }
    internal double PricePerExtraDevice { get; init; }
    internal int RemainingDays { get; init; }

    /// <summary>True when the tariff exposes a positive per-extra-device price and a headroom (max &gt; 0).</summary>
    public bool CanBuyDevices { get; init; }
    [Reactive] public int ExtraDevices { get; set; } = 1;
    [Reactive] public string DevicePriceText { get; set; } = string.Empty;
    [Reactive] public bool IsDeviceBusy { get; set; }

    // Upgrade.
    internal string? RemnawaveUuidValue { get; init; }
    public List<UpgradeTargetOption> UpgradeTargets { get; set; } = new();
    public bool HasUpgradeTargets { get; set; }
    [Reactive] public bool IsUpgradeBusy { get; set; }
    [Reactive] public string UpgradeConfirmTitle { get; set; } = string.Empty;
    [Reactive] public string UpgradeConfirmDetail { get; set; } = string.Empty;
    internal string? SelectedUpgradeTariffId { get; set; }

    public ReactiveCommand<Unit, Unit> RenewBalanceCmd { get; }
    public ReactiveCommand<Unit, Unit> RenewCardCmd { get; }
    public ReactiveCommand<Unit, Unit> OpenBuyCmd { get; }
    public ReactiveCommand<Unit, Unit> DevicesCmd { get; }

    /// <summary>The single «Продлить» CTA action: on a renewable card it toggles the inline pay-method choice;
    /// on a card with no tariff to re-buy it goes straight to the Buy screen (never a one-button popup).</summary>
    public ReactiveCommand<Unit, Unit> PrimaryRenewCmd { get; }

    // Overflow picker navigation + actions.
    public ReactiveCommand<Unit, Unit> OpenDevicePickerCmd { get; }
    public ReactiveCommand<Unit, Unit> OpenUpgradePickerCmd { get; }
    public ReactiveCommand<Unit, Unit> BackToMenuCmd { get; }
    public ReactiveCommand<Unit, Unit> IncDevicesCmd { get; }
    public ReactiveCommand<Unit, Unit> DecDevicesCmd { get; }
    public ReactiveCommand<Unit, Unit> BuyDevicesBalanceCmd { get; }
    public ReactiveCommand<Unit, Unit> BuyDevicesCardCmd { get; }
    public ReactiveCommand<Unit, Unit> UpgradeBalanceCmd { get; }
    public ReactiveCommand<Unit, Unit> UpgradeCardCmd { get; }

    public AccountSubCard(AccountViewModel owner)
    {
        _owner = owner;
        RenewBalanceCmd = ReactiveCommand.CreateFromTask(() => owner.RenewWithBalance(this));
        RenewCardCmd = ReactiveCommand.CreateFromTask(() => owner.RenewWithCard(this));
        OpenBuyCmd = ReactiveCommand.Create(owner.RequestBuy);
        DevicesCmd = ReactiveCommand.Create(owner.RequestDevices);
        PrimaryRenewCmd = ReactiveCommand.Create(() =>
        {
            if (CanRenew)
            {
                RenewExpanded = !RenewExpanded;
            }
            else
            {
                owner.RequestBuy();
            }
        });

        OpenDevicePickerCmd = ReactiveCommand.Create(() => owner.OpenDevicePicker(this));
        OpenUpgradePickerCmd = ReactiveCommand.Create(() => owner.OpenUpgradePicker(this));
        BackToMenuCmd = ReactiveCommand.Create(() => SetPanel(PanelMode.Menu));
        IncDevicesCmd = ReactiveCommand.Create(() => owner.StepDevices(this, +1));
        DecDevicesCmd = ReactiveCommand.Create(() => owner.StepDevices(this, -1));
        BuyDevicesBalanceCmd = ReactiveCommand.CreateFromTask(() => owner.BuyDevices(this, "balance"));
        BuyDevicesCardCmd = ReactiveCommand.CreateFromTask(() => owner.BuyDevices(this, "platega"));
        UpgradeBalanceCmd = ReactiveCommand.CreateFromTask(() => owner.ConfirmUpgrade(this, "balance"));
        UpgradeCardCmd = ReactiveCommand.CreateFromTask(() => owner.ConfirmUpgrade(this, "platega"));
        this.WhenAnyValue(x => x.AutoRenewOn).Subscribe(v =>
        {
            if (_armed)
            {
                _ = owner.SetAutoRenew(this, v);
            }
        });

        // Safety net: a stray card-action exception surfaces as a snack instead of crashing the app.
        Observable.Merge(
                RenewBalanceCmd.ThrownExceptions,
                RenewCardCmd.ThrownExceptions,
                BuyDevicesBalanceCmd.ThrownExceptions,
                BuyDevicesCardCmd.ThrownExceptions,
                UpgradeBalanceCmd.ThrownExceptions,
                UpgradeCardCmd.ThrownExceptions,
                OpenDevicePickerCmd.ThrownExceptions,
                OpenUpgradePickerCmd.ThrownExceptions)
            .Subscribe(_ =>
            {
                IsDeviceBusy = false;
                IsUpgradeBusy = false;
                IsRenewing = false;
                AppEvents.SendSnackMsgRequested.Publish(L.T("Common_SomethingWrong"));
            });
    }

    /// <summary>The four mutually-exclusive overflow-flyout panels.</summary>
    public enum PanelMode
    {
        Menu,
        Devices,
        Upgrade,
        UpgradeConfirm,
    }

    /// <summary>Switches the overflow flyout to one panel (drives the four IsVisible bools).</summary>
    public void SetPanel(PanelMode mode)
    {
        MenuMode = mode == PanelMode.Menu;
        DeviceMode = mode == PanelMode.Devices;
        UpgradeMode = mode == PanelMode.Upgrade;
        UpgradeConfirmMode = mode == PanelMode.UpgradeConfirm;
    }

    /// <summary>Sets the toggle without triggering the network call (initial build + failure revert).</summary>
    public void SetAutoRenewSilently(bool value)
    {
        var wasArmed = _armed;
        _armed = false;
        AutoRenewOn = value;
        _armed = wasArmed;
    }

    /// <summary>Arms the toggle so subsequent user flips persist to the backend.</summary>
    public void Arm() => _armed = true;
}

/// <summary>One eligible upgrade target in the card's «Улучшить тариф» list: a tariff above the current
/// one. Selecting it asks the VM for a live quote, then swaps the flyout to the confirm panel.</summary>
public sealed class UpgradeTargetOption : ReactiveObject
{
    public string TariffId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string PriceText { get; init; } = string.Empty;
    public ReactiveCommand<Unit, Unit> SelectCmd { get; }

    public UpgradeTargetOption(AccountViewModel owner, AccountSubCard card, string tariffId, string name, string priceText)
    {
        TariffId = tariffId;
        Name = name;
        PriceText = priceText;
        SelectCmd = ReactiveCommand.CreateFromTask(() => owner.SelectUpgradeTarget(card, tariffId, name));
        // Safety net: this command isn't part of the VM/card ThrownExceptions merge, so route its stray
        // exceptions to the same error surface instead of the Rx default handler (which could crash).
        SelectCmd.ThrownExceptions.Subscribe(owner.ReportCommandException);
    }
}

/// <summary>One selectable Platega method chip in the balance top-up flyout (mirrors the working Buy
/// checkout's method sheet). Tapping it sets the method used for the next top-up checkout.</summary>
public sealed class TopUpMethodItem : ReactiveObject
{
    /// <summary>The Platega method code (2..13) sent as <c>PaymentMethod</c>; null when the id isn't numeric.</summary>
    public int? MethodCode { get; }
    public string Label { get; }

    /// <summary>Single-select state — drives the chip's selected styling.</summary>
    [Reactive] public bool IsSelected { get; set; }

    public ReactiveCommand<Unit, Unit> SelectCmd { get; }

    public TopUpMethodItem(AccountViewModel owner, int? methodCode, string label)
    {
        MethodCode = methodCode;
        Label = label;
        SelectCmd = ReactiveCommand.Create(() => owner.SelectTopUpMethod(this));
    }
}
