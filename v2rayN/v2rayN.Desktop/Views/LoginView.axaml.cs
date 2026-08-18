using System.Reactive.Disposables;
using System.Text.RegularExpressions;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media.Transformation;
using v2rayN.Desktop.Account;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран «Вход» departament (порт V2rayNG ui/LoginActivity.kt + activity_login.xml, 1:1).
/// Две секции: Telegram (deep link + опрос подтверждения) и сайт (email + пароль, при
/// необходимости 6-значный код 2FA/TOTP).
///
/// Привязан к СУЩЕСТВУЮЩЕМУ <see cref="AccountViewModel"/> (login-члены): состояния
/// idle / awaiting-TG / loading-site / 2FA / success / error переключаются по
/// <see cref="AccountViewModel.CurrentLoginState"/> — но теперь С ДВИЖЕНИЕМ (кроссфейд MethodBlock↔
/// AwaitingBlock, reveal 2FA/ошибки, success-момент дуга→галочка), а НЕ мгновенным IsVisible.
/// Каждая анимация имеет lite-фолбэк (<see cref="IsReducedMotion"/> → мгновенно), петли гасятся
/// селектором :is(Window):not(.lite) и по видимости. При успешном входе success-момент играется
/// ДО отпускания <see cref="BackRequested"/> (оверлей IsImportingAccount перекрывает хэндофф).
/// </summary>
public partial class LoginView : UserControl
{
    /// <summary>Прагматичная проверка email (аналог Android Patterns.EMAIL_ADDRESS).</summary>
    private static readonly Regex _emailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$", RegexOptions.Compiled);

    // Моушен-трансформы: TransformOperations композируются чисто с TransformOperationsTransition
    // из стилей (приём ServerListView) — «сырой» ScaleTransform/TranslateTransform конфликтовал бы
    // с переходом RenderTransform. Масштаб центрируется по RenderTransformOrigin="50%,50%" блоков.
    private static readonly ITransform _scale1 = TransformOperations.Parse("scale(1)");
    private static readonly ITransform _scale098 = TransformOperations.Parse("scale(0.98)");
    private static readonly ITransform _scale090 = TransformOperations.Parse("scale(0.9)");
    private static readonly ITransform _rise0 = TransformOperations.Parse("translateY(0px)");
    private static readonly ITransform _rise8 = TransformOperations.Parse("translateY(8px)");
    private static readonly ITransform _riseNeg4 = TransformOperations.Parse("translateY(-4px)");

    /// <summary>Хост подписывается и закрывает суб-страницу (кнопка «назад» / успешный вход).</summary>
    public event EventHandler? BackRequested;

    private CompositeDisposable? _subscriptions;
    private AccountViewModel? _vm;

    // Запрос входа через сайт / 2FA в полёте (LoginState.SiteLoading).
    private bool _siteBusy;

    // Блок 2FA видим (TwoFaTempToken != null) — спиннер занятости идёт на «Подтвердить».
    private bool _twoFaVisible;

    // Форма в режиме регистрации (сегмент «Регистрация») — иначе режим входа.
    private bool _registerMode;

    // Запрос регистрации в полёте (LoginState.RegisterLoading) — спиннер на «Создать аккаунт».
    private bool _registerBusy;

    // Какой из трёх блоков колонки контента показан сейчас (управляет кроссфейдом/CTA/дугой).
    private ViewBlock _viewBlock = ViewBlock.Method;

    // Вид активного пред-состояния «письмо отправлено» (задаёт заголовок/подсказку/действие «отправить снова»).
    private PendingKind _pendingKind = PendingKind.Verify;

    private enum ViewBlock
    {
        Method,
        Awaiting,
        EmailPending,
    }

    private enum PendingKind
    {
        Verify,
        Magic,
        Reset,

        // Browser→app SSO handoff: a one-time code is being redeemed (departamentvpn://auth callback or a
        // pasted code). Transient, self-resolving — no resend/back actions; the ring spins while it redeems.
        Handoff,
    }

    // Ключ ошибки ИМЕННО логин-потока (LoginState.Error → auth_err_*); имеет приоритет над общим
    // AccountViewModel.ErrorText. Храним КЛЮЧ (не текст), чтобы строка переводилась вживую.
    private string _loginErrorKey = string.Empty;

    private bool _revealPassword;

    // ── Координация движения/состояния ──────────────────────────────────────
    // AwaitingBlock — показанный сейчас блок (управляет CTA/дугой/дыханием/кроссфейдом).
    private bool _awaiting;

    // Первая раскладка прошла. ДО неё смены состояния СНАПАЮТСЯ (Telegram-вход выставляет ожидание
    // синхронно до первого кадра — самоанимироваться на открытии нельзя).
    private bool _firstRenderDone;

    // Колонка метода пред-скрыта в ctor под entrance-стаггер — стаггер ещё не сыгран.
    private bool _entryPending;

    // Строка ошибки сейчас раскрыта (чтобы не переигрывать reveal при смене текста/языка).
    private bool _errorShown;

    // Гардит перекрывающиеся кроссфейды MethodBlock↔AwaitingBlock.
    private CancellationTokenSource? _blockCts;

    // Success-момент → хэндофф: BackRequested отпускается ТОЛЬКО после проигрыша success-момента.
    private bool _loggedIn;
    private bool _beatStarted;
    private bool _beatDone;
    private bool _handoffFired;
    private bool _detached;

    public LoginView()
    {
        InitializeComponent();

        if (Design.IsDesignMode)
        {
            DataContext = AccountViewModel.CreateDesign();
        }

        // Manual back claims the handoff: if the user taps back DURING the ~0.4s success beat, the
        // beat's finally→TryHandoff must not raise BackRequested a second time (a double pop). Setting
        // _handoffFired here makes the two mutually exclusive — whoever fires first wins.
        BackButton.Click += (_, _) =>
        {
            _handoffFired = true;
            BackRequested?.Invoke(this, EventArgs.Empty);
        };
        TelegramButton.Click += OnTelegramClick;
        RestartButton.Click += OnRestartClick;
        ChooseAnotherButton.Click += OnChooseAnotherClick;
        OpenTelegramButton.Click += OnOpenTelegramClick;
        TogglePasswordButton.Click += OnTogglePasswordClick;

        // Сегмент «Вход | Регистрация» — переключает режим формы (ApplyMode).
        SignInTab.Click += (_, _) => SetMode(false);
        RegisterTab.Click += (_, _) => SetMode(true);

        // Пред-состояния «письмо отправлено»: повторная отправка (по виду) и возврат ко входу.
        ResendButton.Click += OnResendClick;
        BackToSignInButton.Click += OnBackToSignInClick;

        // «Войти по коду» (§A1 #5): разворачивает поле ручного ввода handoff-кода (фолбэк, если callback-
        // схема departamentvpn://auth не сработала). Enter в поле = отправить код тем же путём, что кнопка.
        CodeEntryToggle.Click += OnToggleCodeEntry;
        HandoffCodeBox.KeyDown += OnHandoffCodeKeyDown;

        // Отправка с клавиатуры (паритет imeOptions actionNext/actionDone).
        EmailBox.KeyDown += OnEmailKeyDown;
        PasswordBox.KeyDown += OnPasswordKeyDown;
        ConfirmPasswordBox.KeyDown += OnConfirmPasswordKeyDown;
        CodeBox.KeyDown += OnCodeKeyDown;
        // Активная ячейка 2FA подсвечивается только в фокусе — перерисовываем при смене фокуса.
        CodeBox.GotFocus += (_, _) => RenderCodeCells();
        CodeBox.LostFocus += (_, _) => RenderCodeCells();

        // Email подрезается перед отправкой командой (VM использует значение как есть).
        SiteButton.Click += (_, _) => TrimEmail();

        // Idle-вход (§P2 9): пред-скрываем колонку метода, чтобы стаггер раскрыл её сверху вниз без
        // пред-вспышки (приём ConnectHeroView). Только при включённом движении; lite/preview — видно.
        if (!IsReducedMotion())
        {
            foreach (var child in MethodBlock.Children)
            {
                child.Opacity = 0;
            }
            _entryPending = true;
        }

        Loaded += OnFirstLoaded;

        DataContextChanged += (_, _) => Rebind();
        AttachedToVisualTree += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) =>
        {
            _detached = true;
            Unbind();
        };

        Rebind();
        RenderCodeCells();
        ApplyMode();
    }

    // ── Первая раскладка: entrance-стаггер / фиксация _firstRenderDone ────────
    private void OnFirstLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        _firstRenderDone = true;

        if (!_entryPending)
        {
            return;
        }
        _entryPending = false;

        // Открылись сразу в ожидание (Telegram-вход) или движение выключено — не стаггерим; просто
        // возвращаем колонку метода видимой (понадобится при возврате из ожидания: cancel/ошибка).
        if (IsReducedMotion() || _awaiting || !MethodBlock.IsVisible)
        {
            RestoreMethodChildren();
            return;
        }
        PlayEntryStagger();
    }

    // ── Привязка к VM ───────────────────────────────────────────────────────

    /// <summary>Пересобирает подписки на login-состояние VM (идемпотентно).</summary>
    private void Rebind()
    {
        Unbind();

        _vm = DataContext as AccountViewModel;
        if (_vm is null)
        {
            return;
        }

        var d = new CompositeDisposable();
        _subscriptions = d;

        // Машина состояний входа: idle / awaiting-TG / loading-site / success / error. Доставляем
        // СИНХРОННО, когда мы уже на UI-потоке (см. StartTelegramLogin: ожидание выставляется до первого
        // кадра — инлайн-применение гарантирует, что первый кадр уже «ожидание», а не MethodBlock).
        _vm.WhenAnyValue(x => x.CurrentLoginState)
            .Subscribe(state =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    ApplyLoginState(state);
                }
                else
                {
                    Dispatcher.UIThread.Post(() => ApplyLoginState(state));
                }
            })
            .DisposeWith(d);

        // Блок 2FA виден, пока бэкенд держит tempToken (паритет onTwoFactor).
        _vm.WhenAnyValue(x => x.TwoFaTempToken)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(Apply2Fa)
            .DisposeWith(d);

        // Живая валидация: submit активен только при валидном вводе (обе формы — вход и регистрация).
        _vm.WhenAnyValue(x => x.LoginEmail, x => x.LoginPassword)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                UpdateSiteGate();
                UpdateRegisterGate();
            })
            .DisposeWith(d);

        // Повтор пароля (регистрация): пере-считываем гейт «Создать аккаунт» + подсказку несовпадения.
        _vm.WhenAnyValue(x => x.RegisterConfirmPassword)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateRegisterGate())
            .DisposeWith(d);

        _vm.WhenAnyValue(x => x.TwoFaCode)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => Update2FaGate())
            .DisposeWith(d);

        // Общий ErrorText VM (реальный диагностик) — если нет ошибки логин-потока.
        _vm.WhenAnyValue(x => x.ErrorText)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateErrorLine())
            .DisposeWith(d);

        // Вход выполнен — но суб-страница закрывается ТОЛЬКО после success-момента (см. OnLoggedIn):
        // BackRequested гейтится дугой→галочкой, чтобы был кадр подтверждения (§3.4).
        _vm.WhenAnyValue(x => x.IsLoggedIn)
            .DistinctUntilChanged()
            .Where(loggedIn => loggedIn)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => OnLoggedIn())
            .DisposeWith(d);

        // Живой lite-тумблер → пере-оцениваем «дыхание» самолётика (аттачим только пока видно ожидание
        // и не lite — не тикаем петлю за кадром).
        void OnLiteChanged(object? s, bool lite) => RunOnUiLang(UpdateBreathe);
        MotionState.Changed += OnLiteChanged;
        Disposable.Create(() => MotionState.Changed -= OnLiteChanged).DisposeWith(d);

        // Живой перевод императивных строк (строка ошибки + подсказка глаза).
        void OnLanguageChanged(object? s, EventArgs e) => RunOnUiLang(ApplyLanguage);
        L.Instance.LanguageChanged += OnLanguageChanged;
        Disposable.Create(() => L.Instance.LanguageChanged -= OnLanguageChanged).DisposeWith(d);
    }

    /// <summary>Диспетчеризует на UI-поток (событие языка/lite может прийти не из UI).</summary>
    private static void RunOnUiLang(Action action)
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

    /// <summary>Пере-применяет императивные строки под текущий язык.</summary>
    private void ApplyLanguage()
    {
        UpdateErrorLine();
        ToolTip.SetTip(TogglePasswordButton, L.T(_revealPassword ? "Login_HidePassword" : "Login_ShowPassword"));
        // Re-derive the imperative mode labels (title/subtitle/toolbar/watermark) in the new language.
        ApplyMode();
        // Re-derive the pending-screen copy if it's up.
        if (_viewBlock == ViewBlock.EmailPending)
        {
            ConfigureEmailPending(_pendingKind, _vm?.LoginEmail?.Trim() ?? string.Empty);
        }
    }

    private void Unbind()
    {
        _subscriptions?.Dispose();
        _subscriptions = null;
    }

    // ── Машина состояний (паритет LoginActivity.render) ─────────────────────

    private void ApplyLoginState(LoginState state)
    {
        switch (state)
        {
            case LoginState.AwaitingTelegram:
            case LoginState.Polling:
                SetAwaiting(true);
                SetSiteBusy(false);
                SetLoginError(string.Empty);
                break;

            case LoginState.SiteLoading:
                SetAwaiting(false);
                SetSiteBusy(true);
                SetLoginError(string.Empty);
                break;

            case LoginState.SiteHandoffLoading:
                // Browser→app handoff code is being redeemed: focused «завершаем вход через сайт…» step
                // (reuses the pending block with the handoff kind — spinner, no resend/back), then Success
                // plays the badge beat over it exactly like an email/2FA login.
                SetSiteBusy(false);
                SetRegisterBusy(false);
                SetLoginError(string.Empty);
                ConfigureEmailPending(PendingKind.Handoff, string.Empty);
                ShowBlock(ViewBlock.EmailPending);
                break;

            case LoginState.RegisterLoading:
                SetLoginError(string.Empty);
                if (_viewBlock == ViewBlock.EmailPending)
                {
                    // «Отправить снова» с экрана verify-email — остаёмся на месте, крутим кольцо.
                    var spin = !IsReducedMotion();
                    SetSpinning(PendingSpinner, spin);
                    PendingSpinner.Opacity = spin ? 1 : 0;
                }
                else
                {
                    // Первичная регистрация из формы — спиннер на «Создать аккаунт».
                    SetSiteBusy(false);
                    SetRegisterBusy(true);
                }
                break;

            case LoginState.AwaitingEmailVerification verify:
                SetSiteBusy(false);
                SetRegisterBusy(false);
                SetLoginError(string.Empty);
                ConfigureEmailPending(PendingKind.Verify, verify.Email);
                ShowBlock(ViewBlock.EmailPending);
                break;

            case LoginState.MagicLinkSent magic:
                SetSiteBusy(false);
                SetRegisterBusy(false);
                SetLoginError(string.Empty);
                ConfigureEmailPending(PendingKind.Magic, magic.Email);
                ShowBlock(ViewBlock.EmailPending);
                break;

            case LoginState.PasswordResetSent reset:
                SetSiteBusy(false);
                SetRegisterBusy(false);
                SetLoginError(string.Empty);
                ConfigureEmailPending(PendingKind.Reset, reset.Email);
                ShowBlock(ViewBlock.EmailPending);
                break;

            case LoginState.Success:
                // НЕ возвращаемся в MethodBlock здесь — success-момент играется на том блоке, что виден
                // (кольцо ожидания → галочка, или транзиентная плашка над формой), затем отпускает
                // хэндофф. SetSiteBusy(false) гасит инлайн-спиннер кнопки под ним.
                SetSiteBusy(false);
                SetRegisterBusy(false);
                SetLoginError(string.Empty);
                PlaySuccessBeat();
                break;

            case LoginState.Error error:
                SetAwaiting(false);
                SetSiteBusy(false);
                SetRegisterBusy(false);
                SetLoginError(MessageKeyFor(error.ErrorValue));
                // Неверные учётные данные → вспышка рамок email+пароль в Red (без shake, §3.5).
                if (error.ErrorValue is ApiError.Unauthorized)
                {
                    FlashCredentialFields();
                }
                break;

            default: // Idle. Ошибку НЕ трогаем: Idle приходит и сразу после показа ошибки.
                SetAwaiting(false);
                SetSiteBusy(false);
                SetRegisterBusy(false);
                break;
        }
    }

    /// <summary>Совместимость: вход/выход из ожидания Telegram — тонкая обёртка над <see cref="ShowBlock"/>.</summary>
    private void SetAwaiting(bool awaiting) => ShowBlock(awaiting ? ViewBlock.Awaiting : ViewBlock.Method);

    /// <summary>
    /// Показывает один из трёх блоков колонки контента (форма / ожидание Telegram / «письмо отправлено»),
    /// ЗАМЕНЯЯ текущий КРОССФЕЙДОМ (220мс Ease.Standard, микро-scale), кроме первого кадра/lite — там
    /// мгновенный снап. Управляет активностью CTA Telegram, вращением дуги ожидания и «дыханием» самолётика.
    /// </summary>
    private void ShowBlock(ViewBlock target)
    {
        var changed = target != _viewBlock;
        var prev = _viewBlock;
        _viewBlock = target;
        _awaiting = target == ViewBlock.Awaiting;

        // CTA Telegram неактивен ТОЛЬКО пока идёт опрос (паритет showAwaiting).
        TelegramButton.IsEnabled = !_awaiting;
        // Дуга крутится только пока видно ожидание (класс + селектор :not(.lite)).
        SetSpinning(AwaitingSpinner, _awaiting);
        // «Дыхание» самолётика — только пока видно ожидание и не lite.
        UpdateBreathe();
        // Кольцо пред-состояния крутится только внутри EmailPending (verify-email); гасим при выходе.
        if (target != ViewBlock.EmailPending)
        {
            SetSpinning(PendingSpinner, false);
        }

        if (!_firstRenderDone || IsReducedMotion())
        {
            SnapBlocks(target);
            return;
        }
        if (!changed)
        {
            return;
        }
        CrossfadeBlocks(BlockControl(target), BlockControl(prev));
    }

    private Control BlockControl(ViewBlock block) => block switch
    {
        ViewBlock.Awaiting => AwaitingBlock,
        ViewBlock.EmailPending => EmailPendingBlock,
        _ => MethodBlock,
    };

    /// <summary>Мгновенно ставит нужный блок (первый кадр / lite / reduced-motion).</summary>
    private void SnapBlocks(ViewBlock target)
    {
        _blockCts?.Cancel();
        _blockCts = null;
        void Set(Control c, bool vis)
        {
            c.IsVisible = vis;
            c.Opacity = 1;
            c.RenderTransform = null;
        }
        Set(MethodBlock, target == ViewBlock.Method);
        Set(AwaitingBlock, target == ViewBlock.Awaiting);
        Set(EmailPendingBlock, target == ViewBlock.EmailPending);
    }

    /// <summary>Кроссфейд между блоками (оба видны в перекрытии → нет пустого кадра).</summary>
    private async void CrossfadeBlocks(Control incoming, Control outgoing)
    {
        if (ReferenceEquals(incoming, outgoing))
        {
            return;
        }

        _blockCts?.Cancel();
        var cts = new CancellationTokenSource();
        _blockCts = cts;

        incoming.Opacity = 0;
        incoming.RenderTransform = _scale098;
        incoming.IsVisible = true;
        outgoing.IsVisible = true;

        try
        {
            await Task.WhenAll(
                PlayMotion(incoming, 0d, 1d, _scale098, _scale1, Motion.Dur.State, Motion.Ease.Standard, token: cts.Token),
                PlayMotion(outgoing, 1d, 0d, _scale1, _scale098, Motion.Dur.State, Motion.Ease.Standard, token: cts.Token, keepTransform: true));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Движение — не причина потерять экран входа. Ниже кадр всё равно доводится до финального
            // состояния, поэтому пользователь видит нужный блок даже когда переход не сыграл.
            Logging.SaveLog("LoginView.CrossfadeBlocks", ex);
        }

        if (cts.IsCancellationRequested)
        {
            return; // новее переход владеет финальным состоянием
        }

        outgoing.IsVisible = false;
        outgoing.Opacity = 1;
        outgoing.RenderTransform = null;
        incoming.Opacity = 1;
        incoming.RenderTransform = null;
        _blockCts = null;
    }

    /// <summary>Аттачит/снимает «дыхание» самолётика (петля) по видимости ожидания + lite.</summary>
    private void UpdateBreathe()
    {
        var on = _awaiting && !IsReducedMotion();
        if (on)
        {
            if (!AwaitingPlane.Classes.Contains("breathing"))
            {
                AwaitingPlane.Classes.Add("breathing");
            }
        }
        else
        {
            AwaitingPlane.Classes.Remove("breathing");
        }
    }

    /// <summary>
    /// Занятость входа через сайт / 2FA: спиннер на инициировавшей кнопке, submit-кнопки
    /// заблокированы (паритет setSiteBusy). CTA Telegram НЕ трогаем (им владеет SetAwaiting).
    /// </summary>
    private void SetSiteBusy(bool busy)
    {
        _siteBusy = busy;

        var onSite = busy && !_twoFaVisible;
        var on2Fa = busy && _twoFaVisible;

        // Under reduced-motion/lite the inline arc can't rotate (its keyframe is gated off by
        // :is(Window):not(.lite)), so a hidden label + a frozen dashed ring reads as broken. Keep the
        // label and skip the spinner entirely — the button is already disabled (dimmed) via the gates
        // below, which conveys "busy" without any motion.
        var lite = IsReducedMotion();
        var showSiteSpin = onSite && !lite;
        var show2FaSpin = on2Fa && !lite;

        SiteSpinner.IsVisible = showSiteSpin;
        SetSpinning(SiteSpinner, showSiteSpin);
        SiteButtonLabel.IsVisible = !showSiteSpin;

        ConfirmSpinner.IsVisible = show2FaSpin;
        SetSpinning(ConfirmSpinner, show2FaSpin);
        ConfirmButtonLabel.IsVisible = !show2FaSpin;

        UpdateSiteGate();
        Update2FaGate();
    }

    /// <summary>Занятость регистрации: инлайн-спиннер на «Создать аккаунт», submit заблокирован.</summary>
    private void SetRegisterBusy(bool busy)
    {
        _registerBusy = busy;
        // Как и site-спиннер: под reduced-motion/lite дугу не крутим (её keyframe выключен селектором),
        // просто держим лейбл и заблокированную (притушенную) кнопку.
        var showSpin = busy && !IsReducedMotion();
        RegisterSpinner.IsVisible = showSpin;
        SetSpinning(RegisterSpinner, showSpin);
        RegisterButtonLabel.IsVisible = !showSpin;
        UpdateRegisterGate();
    }

    /// <summary>Переключает режим формы (вход ⇄ регистрация): сбрасывает ошибку и пере-применяет вид.</summary>
    private void SetMode(bool register)
    {
        if (_registerMode == register)
        {
            return;
        }
        _registerMode = register;
        SetLoginError(string.Empty);
        ApplyMode();
    }

    /// <summary>
    /// Применяет режим формы к видимости/лейблам: сегмент, заголовок/подзаголовок/тулбар, watermark
    /// пароля, поля регистрации (повтор пароля + подсказка + «Создать аккаунт») против sign-in-элементов
    /// (кнопка сайта + пассворлесс-ссылки + блок 2FA). Пере-считывает гейты валидации под новый режим.
    /// </summary>
    private void ApplyMode()
    {
        SetClass(SignInTab, "segActive", !_registerMode);
        SetClass(RegisterTab, "segActive", _registerMode);

        ToolbarTitle.Text = L.T(_registerMode ? "Login_TabRegister" : "Login_SignIn");
        TitleText.Text = L.T(_registerMode ? "Login_TitleRegister" : "Login_Title");
        SubtitleText.Text = L.T(_registerMode ? "Login_SubtitleRegister" : "Login_Subtitle");
        PasswordBox.Watermark = L.T(_registerMode ? "Login_PasswordRegister" : "Login_Password");

        UpdateFormVisibility();
        UpdateSiteGate();
        UpdateRegisterGate();
    }

    /// <summary>
    /// Gates the mutually-exclusive form regions: register fields + «Создать аккаунт» (register mode),
    /// email submit «Войти» + passwordless links + the demoted alternates block (sign-in mode), and the
    /// 2FA block (sign-in + tempToken). While 2FA is up the sign-in submit + alternates are hidden so the
    /// focused code step is the ONLY filled accent (its «Подтвердить»). Called from both <see cref="ApplyMode"/>
    /// and <see cref="Apply2Fa"/> so the two toggles never disagree.
    /// </summary>
    private void UpdateFormVisibility()
    {
        var signInForm = !_registerMode && !_twoFaVisible;

        // Только регистрация.
        RegisterPasswordHint.IsVisible = _registerMode;
        ConfirmPasswordBox.IsVisible = _registerMode;
        RegisterButtonHost.IsVisible = _registerMode && !_twoFaVisible;

        // Только вход (и не во время 2FA).
        SiteButtonHost.IsVisible = signInForm;
        PasswordlessLinks.IsVisible = signInForm;
        AltMethodsBlock.IsVisible = signInForm;

        // Блок 2FA осмыслен только во входе; в регистрации всегда скрыт (иначе — по tempToken).
        TwoFaBlock.IsVisible = !_registerMode && _twoFaVisible;
    }

    /// <summary>Настраивает пред-экран «письмо отправлено» под вид состояния (verify / magic / reset).</summary>
    private void ConfigureEmailPending(PendingKind kind, string email)
    {
        _pendingKind = kind;
        var (titleKey, hintKey) = kind switch
        {
            PendingKind.Magic => ("Login_MagicSentTitle", "Login_MagicSentHint"),
            PendingKind.Reset => ("Login_ResetSentTitle", "Login_ResetSentHint"),
            PendingKind.Handoff => ("Login_SiteHandoff", string.Empty),
            _ => ("Login_VerifyTitle", "Login_VerifyHint"),
        };
        PendingTitle.Text = L.T(titleKey);
        PendingHint.Text = hintKey.IsNotEmpty() ? L.F(hintKey, email) : string.Empty;
        PendingHint.IsVisible = hintKey.IsNotEmpty();

        // The handoff step is transient + self-resolving — its resend/back actions would be meaningless
        // (there's nothing to re-send and the redeem finishes on its own), so hide them for that kind only.
        var transient = kind == PendingKind.Handoff;
        ResendButton.IsVisible = !transient;
        BackToSignInButton.IsVisible = !transient;

        // verify-email polls login and the handoff redeems a code → spin the ring; magic/reset are a calm
        // static «отправлено» (arc hidden, track + envelope remain). Under lite the arc is not shown at all.
        var spin = (kind == PendingKind.Verify || kind == PendingKind.Handoff) && !IsReducedMotion();
        SetSpinning(PendingSpinner, spin);
        PendingSpinner.Opacity = spin ? 1 : 0;
    }

    /// <summary>Показывает/прячет блок 2FA по tempToken (паритет onTwoFactor) с reveal + автофокусом.</summary>
    private void Apply2Fa(string? tempToken)
    {
        var visible = tempToken != null;
        var appeared = visible && !_twoFaVisible;
        _twoFaVisible = visible;

        if (appeared)
        {
            UpdateFormVisibility();
            SetLoginError(string.Empty);
            RenderCodeCells();
            Reveal2Fa();
            if (!Design.IsDesignMode)
            {
                CodeBox.Focus();
            }
        }
        else if (!visible)
        {
            UpdateFormVisibility();
        }
        Update2FaGate();
    }

    /// <summary>Reveal блока 2FA (opacity 0→1 + translateY 8→0, 300мс OutQuint); lite — мгновенно.</summary>
    private void Reveal2Fa()
    {
        if (!_firstRenderDone || IsReducedMotion())
        {
            TwoFaBlock.Opacity = 1;
            TwoFaBlock.RenderTransform = null;
            return;
        }
        _ = RevealFrom(TwoFaBlock, _rise8, Motion.Dur.Reveal, Motion.Ease.OutQuint);
    }

    // ── Валидация ввода ─────────────────────────────────────────────────────

    /// <summary>Кнопка «Войти через сайт» активна только при валидном email + пароле. Пассворлесс-ссылки
    /// («Войти по ссылке» / «Забыли пароль?») активны при валидном email (пароль не нужен).</summary>
    private void UpdateSiteGate()
    {
        var email = _vm?.LoginEmail?.Trim() ?? string.Empty;
        var password = _vm?.LoginPassword ?? string.Empty;

        EmailError.IsVisible = email.Length > 0 && !IsEmail(email);
        SiteButton.IsEnabled = !_siteBusy && IsEmail(email) && password.Length > 0;

        var emailOk = IsEmail(email);
        MagicLinkButton.IsEnabled = emailOk;
        ForgotPasswordButton.IsEnabled = emailOk;
    }

    /// <summary>«Создать аккаунт» активна при валидном email, пароле ≥8 и совпадающем повторе.</summary>
    private void UpdateRegisterGate()
    {
        var email = _vm?.LoginEmail?.Trim() ?? string.Empty;
        var password = _vm?.LoginPassword ?? string.Empty;
        var confirm = _vm?.RegisterConfirmPassword ?? string.Empty;

        // Валидность email отражаем в общей строке EmailError (обе формы делят поле).
        EmailError.IsVisible = email.Length > 0 && !IsEmail(email);
        ConfirmPasswordError.IsVisible = _registerMode && confirm.Length > 0 && confirm != password;

        RegisterSubmitButton.IsEnabled = !_registerBusy
            && IsEmail(email)
            && password.Length >= 8
            && confirm.Length > 0
            && confirm == password;
    }

    /// <summary>
    /// «Подтвердить» активна только при 6 цифрах; нецифровые символы отбрасываются. Также перерисовывает
    /// сегментные ячейки под текущий код.
    /// </summary>
    private void Update2FaGate()
    {
        var code = _vm?.TwoFaCode ?? string.Empty;

        var digits = new string(code.Where(char.IsDigit).ToArray());
        if (digits != code && _vm != null)
        {
            _vm.TwoFaCode = digits; // повторное уведомление доведёт состояние ниже
            return;
        }

        CodeError.IsVisible = code.Length > 0 && !IsSixDigits(code);
        ConfirmButton.IsEnabled = !_siteBusy && IsSixDigits(code);
        RenderCodeCells();
    }

    /// <summary>
    /// Отражает код 2FA в 6 сегментных ячейках. Источник истины — реальный CodeBox (ввод/вставка/
    /// клавиатура/автофокус нетронуты): вставка 6-значного кода наполняет все ячейки. filled — цифра
    /// введена; active — следующая к вводу ячейка (только в фокусе, пока код неполон).
    /// </summary>
    private void RenderCodeCells()
    {
        var code = _vm?.TwoFaCode ?? string.Empty;
        var focused = CodeBox.IsFocused;
        var cells = CodeCells.Children;
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i] is not Border border)
            {
                continue;
            }
            if (border.Child is TextBlock tb)
            {
                tb.Text = i < code.Length ? code[i].ToString() : string.Empty;
            }
            var active = focused && code.Length < 6 && i == code.Length;
            SetClass(border, "active", active);
            SetClass(border, "filled", i < code.Length && !active);
        }
    }

    private static void SetClass(StyledElement el, string name, bool on)
    {
        if (on)
        {
            if (!el.Classes.Contains(name))
            {
                el.Classes.Add(name);
            }
        }
        else
        {
            el.Classes.Remove(name);
        }
    }

    private static bool IsEmail(string value) => value.Length > 0 && _emailRegex.IsMatch(value);

    private static bool IsSixDigits(string value) => value.Length == 6 && value.All(char.IsDigit);

    /// <summary>VM отправляет LoginEmail как есть — подрезаем через привязку (паритет trim()).</summary>
    private void TrimEmail()
    {
        if (_vm is null)
        {
            return;
        }
        var trimmed = _vm.LoginEmail?.Trim() ?? string.Empty;
        if (trimmed != _vm.LoginEmail)
        {
            _vm.LoginEmail = trimmed;
        }
    }

    // ── Строка ошибки (tv_error) ────────────────────────────────────────────

    private void SetLoginError(string messageKey)
    {
        _loginErrorKey = messageKey;
        UpdateErrorLine();
    }

    /// <summary>Ошибка логин-потока приоритетнее общего ErrorText; reveal при появлении (opacity 0→1 +
    /// translateY −4→0, 220мс), lite — мгновенно. Ключ переводится вживую.</summary>
    private void UpdateErrorLine()
    {
        var text = _loginErrorKey.IsNotEmpty() ? L.T(_loginErrorKey) : (_vm?.ErrorText ?? string.Empty);
        ErrorLine.Text = text;
        var show = text.IsNotEmpty();

        if (show == _errorShown)
        {
            if (!show)
            {
                ErrorLine.IsVisible = false;
            }
            return;
        }
        _errorShown = show;

        if (!show)
        {
            ErrorLine.IsVisible = false;
            return;
        }
        ErrorLine.IsVisible = true;
        if (!_firstRenderDone || IsReducedMotion())
        {
            ErrorLine.Opacity = 1;
            ErrorLine.RenderTransform = null;
            return;
        }
        _ = RevealFrom(ErrorLine, _riseNeg4, Motion.Dur.State, Motion.Ease.Standard);
    }

    /// <summary>
    /// Вспышка рамок email+пароль в Red на ~220мс (класс .fieldError → внутренняя рамка Red, возврат
    /// по BrushTransition шаблона). Только цвет — без shake/bounce (§3.5).
    /// </summary>
    private void FlashCredentialFields()
    {
        SetClass(EmailBox, "fieldError", true);
        SetClass(PasswordBox, "fieldError", true);
        DispatcherTimer.RunOnce(
            () =>
            {
                SetClass(EmailBox, "fieldError", false);
                SetClass(PasswordBox, "fieldError", false);
            },
            Motion.Dur.State);
    }

    /// <summary>Ключ строки отказа (порт LoginActivity.messageFor); текст берёт локализация.</summary>
    private static string MessageKeyFor(ApiError error) => error switch
    {
        // 401/403 на входе через сайт — почти всегда неверные учётные данные.
        ApiError.Unauthorized => "Login_ErrBadCreds",
        // 409 — registration hit an existing account (backend returns Conflict).
        ApiError.Server { Code: 409 } => "Login_ErrEmailTaken",
        ApiError.GoneError => "Login_ErrLinkExpired",
        ApiError.ServiceUnavailable => "Common_ServiceUnavailable",
        ApiError.NetworkError or ApiError.TimeoutError => "Common_NetworkError",
        ApiError.NotConfiguredError => "Login_ErrUnavailable",
        _ => "Login_ErrRetry",
    };

    // ── Success-момент + гейт хэндоффа ──────────────────────────────────────

    /// <summary>
    /// Вход выполнен. BackRequested НЕ отпускаем сразу: сперва играем success-момент (дуга→полное
    /// кольцо → самолётик→галочка + hold), затем хэндофф. Оверлей IsImportingAccount уже стоит ПОД
    /// суб-страницей (subPageHost поверх шелла) и перекрывает переход после pop — без пустого кадра.
    /// </summary>
    private void OnLoggedIn()
    {
        _loggedIn = true;
        // Защитно: если success-состояние не запустило момент (не должно случаться — Success ⟺
        // OnAuthenticated), запускаем здесь, чтобы всё равно был кадр подтверждения.
        if (!_beatStarted)
        {
            PlaySuccessBeat();
        }
        TryHandoff();
    }

    private void TryHandoff()
    {
        if (_handoffFired || _detached || !_loggedIn || !_beatDone)
        {
            return;   // already handed off, or the page was popped/detached — never fire on a dead view
        }
        _handoffFired = true;
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Проигрывает success-момент один раз, затем отпускает хэндофф (finally — даже на ошибке).</summary>
    private async void PlaySuccessBeat()
    {
        if (_beatStarted)
        {
            return;
        }
        _beatStarted = true;
        try
        {
            var lite = IsReducedMotion();
            if (_awaiting)
            {
                await PlayAwaitingSuccess(lite);
            }
            else
            {
                await PlayBadgeSuccess(lite);
            }
        }
        catch
        {
            // Никогда не оставляем пользователя на странице входа после реального успеха.
        }
        finally
        {
            _beatDone = true;
            TryHandoff();
        }
    }

    /// <summary>Success на кольце ожидания: дуга→полное кольцо (220), самолётик→галочка (0.9→1, 160), hold 120.</summary>
    private async Task PlayAwaitingSuccess(bool lite)
    {
        SetSpinning(AwaitingSpinner, false);
        AwaitingPlane.Classes.Remove("breathing");

        if (lite)
        {
            AwaitingSpinner.Opacity = 0;
            AwaitingRingFull.Opacity = 1;
            AwaitingPlane.Opacity = 0;
            AwaitingCheck.Opacity = 1;
            AwaitingCheck.RenderTransform = null;
            await Task.Delay(120);
            return;
        }

        // 1) штрих-дуга → полное кольцо.
        var ring = Task.WhenAll(
            Fade(AwaitingSpinner, 1d, 0d, Motion.Dur.State, Motion.Ease.OutQuint),
            Fade(AwaitingRingFull, 0d, 1d, Motion.Dur.State, Motion.Ease.OutQuint));
        // 2) самолётик → галочка (scale 0.9→1), старт на +160.
        await Task.Delay(160);
        var check = Task.WhenAll(
            Fade(AwaitingPlane, 1d, 0d, Motion.Dur.PressOut, Motion.Ease.OutQuint),
            ScaleFadeIn(AwaitingCheck, Motion.Dur.PressOut, Motion.Ease.OutQuint));
        await Task.WhenAll(ring, check);
        // 3) короткий hold — сигнал-истина, а не декор.
        await Task.Delay(120);
    }

    /// <summary>Success без AwaitingBlock (сайт/2FA/регистрация/verify-email): 64-галочка проявляется,
    /// текущий видимый блок (форма или пред-экран) гаснет под ней.</summary>
    private async Task PlayBadgeSuccess(bool lite)
    {
        var active = BlockControl(_viewBlock);
        SuccessBadge.IsVisible = true;
        if (lite)
        {
            active.Opacity = 0;
            SuccessBadge.Opacity = 1;
            SuccessBadge.RenderTransform = null;
            await Task.Delay(120);
            return;
        }
        _ = Fade(active, active.Opacity, 0d, Motion.Dur.PressOut, Motion.Ease.Standard);
        await ScaleFadeIn(SuccessBadge, Motion.Dur.State, Motion.Ease.OutQuint);
        await Task.Delay(120);
    }

    // ── Действия ────────────────────────────────────────────────────────────

    /// <summary>«Открыть Telegram»: повторно открывает текущий deep link (ссылка ещё живая).</summary>
    private void OnOpenTelegramClick(object? sender, RoutedEventArgs e)
    {
        var deepLink = _vm?.TelegramDeepLink;
        if (deepLink.IsNotEmpty())
        {
            ProcUtils.ProcessStart(deepLink);
        }
    }

    /// <summary>«Войти через Telegram»: запускает вход через Telegram (всегда доступен, кроме опроса).</summary>
    private void OnTelegramClick(object? sender, RoutedEventArgs e)
    {
        SetLoginError(string.Empty);
        Execute(_vm?.LoginTelegramCmd);
    }

    /// <summary>«Начать заново»: новая попытка входа через Telegram со свежим deep link.</summary>
    private void OnRestartClick(object? sender, RoutedEventArgs e)
    {
        SetLoginError(string.Empty);
        Execute(_vm?.LoginTelegramCmd);
    }

    /// <summary>
    /// «Другой способ входа» (§7): CancelLogin останавливает ≤3-мин опрос и сбрасывает состояние в Idle
    /// (только если не вошли) → ApplyLoginState(Idle) → SetAwaiting(false) → обратный кроссфейд к
    /// MethodBlock В СТРАНИЦЕ, не покидая экран. Отличается от «Начать заново» (перезапуск Telegram).
    /// </summary>
    private void OnChooseAnotherClick(object? sender, RoutedEventArgs e)
    {
        SetLoginError(string.Empty);
        _vm?.CancelLogin();
    }

    /// <summary>«Отправить снова» на пред-экране — повторяет запрос ПО ВИДУ состояния (verify/magic/reset).</summary>
    private void OnResendClick(object? sender, RoutedEventArgs e)
    {
        switch (_pendingKind)
        {
            case PendingKind.Magic:
                Execute(_vm?.MagicLinkCmd);
                break;
            case PendingKind.Reset:
                Execute(_vm?.PasswordResetCmd);
                break;
            default:
                // Verify-email: повторная регистрация переотправляет письмо и перезапускает поллинг.
                Execute(_vm?.RegisterCmd);
                break;
        }
    }

    /// <summary>«Вернуться ко входу»: отменяет поллинг/запрос и возвращает к форме (Idle → кроссфейд к MethodBlock).</summary>
    private void OnBackToSignInClick(object? sender, RoutedEventArgs e)
    {
        SetLoginError(string.Empty);
        // The button says «Вернуться ко входу» — land on the sign-in form, not the register form we may
        // have come from (register → verify-email → back).
        SetMode(register: false);
        _vm?.CancelLogin();
    }

    /// <summary>«Войти по коду»: разворачивает поле ручного ввода handoff-кода и фокусирует его.</summary>
    private void OnToggleCodeEntry(object? sender, RoutedEventArgs e)
    {
        var show = !CodeEntryHost.IsVisible;
        CodeEntryHost.IsVisible = show;
        if (show && !Design.IsDesignMode)
        {
            HandoffCodeBox.Focus();
        }
    }

    /// <summary>Enter в поле handoff-кода = отправить код тем же путём, что кнопка (LoginByCodeCmd).</summary>
    private void OnHandoffCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            Execute(_vm?.LoginByCodeCmd);
            e.Handled = true;
        }
    }

    private void OnTogglePasswordClick(object? sender, RoutedEventArgs e)
    {
        _revealPassword = !_revealPassword;
        PasswordBox.RevealPassword = _revealPassword;
        EyeOnIcon.IsVisible = !_revealPassword;
        EyeOffIcon.IsVisible = _revealPassword;
        ToolTip.SetTip(TogglePasswordButton, L.T(_revealPassword ? "Login_HidePassword" : "Login_ShowPassword"));
    }

    // ── Клавиатура ──────────────────────────────────────────────────────────

    private void OnEmailKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            PasswordBox.Focus();
            e.Handled = true;
        }
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            // В регистрации Enter ведёт к повтору пароля; во входе — сразу вход.
            if (_registerMode)
            {
                ConfirmPasswordBox.Focus();
            }
            else
            {
                SubmitSite();
            }
            e.Handled = true;
        }
    }

    private void OnConfirmPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            SubmitRegister();
            e.Handled = true;
        }
    }

    private void OnCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            Submit2Fa();
            e.Handled = true;
        }
    }

    /// <summary>Валидирует поля и запускает вход email/паролем (кнопка и Enter — один путь).</summary>
    private void SubmitSite()
    {
        if (_vm is null || _siteBusy)
        {
            return;
        }
        TrimEmail();
        if (!IsEmail(_vm.LoginEmail ?? string.Empty) || (_vm.LoginPassword ?? string.Empty).Length == 0)
        {
            return;
        }
        Execute(_vm.LoginSiteCmd);
    }

    /// <summary>Валидирует поля регистрации и запускает создание аккаунта (кнопка и Enter — один путь).</summary>
    private void SubmitRegister()
    {
        if (_vm is null || _registerBusy)
        {
            return;
        }
        TrimEmail();
        var email = _vm.LoginEmail ?? string.Empty;
        var password = _vm.LoginPassword ?? string.Empty;
        var confirm = _vm.RegisterConfirmPassword ?? string.Empty;
        if (!IsEmail(email) || password.Length < 8 || confirm != password)
        {
            return;
        }
        Execute(_vm.RegisterCmd);
    }

    /// <summary>Валидирует 6-значный код и завершает вход 2FA.</summary>
    private void Submit2Fa()
    {
        if (_vm is null || _siteBusy || !_twoFaVisible || !IsSixDigits(_vm.TwoFaCode ?? string.Empty))
        {
            return;
        }
        Execute(_vm.Submit2FaCmd);
    }

    // ── Entrance-стаггер (§P2 9) ────────────────────────────────────────────

    /// <summary>
    /// Раскрывает колонку метода 4 АВТОРСКИМИ битами (не равномерный drip), паритет с онбордингом (§3a):
    /// щит → идентичность → CTA Telegram+разделитель → форма входа (как одна группа). Щит (бит 1) —
    /// scale 0.90→1 (§3b, общий с connect-героем); остальное — rise translateY 8→0. Итог ≈500мс.
    /// </summary>
    private void PlayEntryStagger()
    {
        var children = MethodBlock.Children;
        for (var i = 0; i < children.Count; i++)
        {
            var delay = BeatDelayMs(i);
            var from = i == 0 ? _scale090 : _rise8;
            var to = i == 0 ? _scale1 : _rise0;
            _ = PlayReveal((Control)children[i], delay, from, to);
        }
    }

    /// <summary>
    /// Задержка entrance-бита по роли ребёнка MethodBlock (§3a). Порядок XAML после §A2-реструктуризации:
    /// 0 щит; 1–3 идентичность (вордмарк + заголовок + подзаголовок); 4 сегмент «Вход|Регистрация»;
    /// 5+ форма входа + демотированные альтернативы (email/пароль/«Войти»/ссылки/сайт/Telegram/код/2FA/
    /// строка ошибки) как ОДНА группа. Члены бита делят его задержку — 4 бита, а не равномерный drip.
    /// </summary>
    private static int BeatDelayMs(int childIndex) => childIndex switch
    {
        0 => 0,              // бит 1 · щит-марка
        1 or 2 or 3 => 60,   // бит 2 · идентичность (вордмарк + заголовок + подзаголовок)
        4 => 120,            // бит 3 · сегмент «Вход|Регистрация»
        _ => 180,            // бит 4 · форма входа + альтернативы как одна группа
    };

    /// <summary>Раскрывает элемент: opacity 0→1 + RenderTransform from→to, 300мс OutQuint, с задержкой
    /// бита. База возвращается по окончании — чтобы не затенять :pressed-scale кнопок.</summary>
    private static async Task PlayReveal(Control el, int delayMs, ITransform from, ITransform to)
    {
        try
        {
            await PlayMotion(el, 0d, 1d, from, to, Motion.Dur.Reveal, Motion.Ease.OutQuint, TimeSpan.FromMilliseconds(delayMs));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("LoginView.PlayReveal", ex);
        }
        finally
        {
            // Появление УЛУЧШАЕТ уже видимый дефолт, а не создаёт его: чем бы ни кончилось движение,
            // элемент остаётся видимым и без остаточного трансформа.
            el.Transitions = null;
            el.Opacity = 1;
            el.RenderTransform = null;
        }
    }

    /// <summary>Возвращает колонку метода видимой (когда стаггер пропущен: ожидание/lite).</summary>
    private void RestoreMethodChildren()
    {
        foreach (var child in MethodBlock.Children)
        {
            child.Opacity = 1;
            child.RenderTransform = null;
        }
    }

    // ── Помощники движения ──────────────────────────────────────────────────

    /// <summary>
    /// Прозрачность + трансформ одним движением. Ведут ПЕРЕХОДЫ (Transitions), а не Animation.
    ///
    /// ЭТО НЕ СТИЛИСТИЧЕСКИЙ ВЫБОР. Ключевые кадры по <see cref="Visual.RenderTransformProperty"/>
    /// в Avalonia не проигрываются вовсе: <c>Animation.RunAsync</c> бросает СРАЗУ, на разборе кадров —
    /// «No animator registered for the property RenderTransform». Всё, что здесь двигалось этим
    /// способом, на живом окне не двигалось никогда, а кроссфейд блоков (единственное место, где
    /// исключение не было проглочено) уносил ВСЁ ПРИЛОЖЕНИЕ: <c>async void</c> плюс
    /// <c>catch (OperationCanceledException)</c> — и падение уходило в необработанные. Достаточно было
    /// на странице «Вход» нажать «Войти через Telegram» или «Другой способ входа»: вместо экрана
    /// ожидания окно просто исчезало. Тот же диагноз и тот же вывод уже записаны в шапке
    /// <see cref="OnboardingView"/> — TransformOperationsTransition работает, на нём стоит вся
    /// лестница :pressed в GlobalStyles.
    ///
    /// Порядок обязателен: исходное состояние ставится БЕЗ переходов, переходы вешаются вторым шагом,
    /// и только следующим оборотом диспетчера ставится целевое — эта установка и анимируется. По
    /// окончании переходы снимаются, а трансформ обнуляется: он живёт на том же свойстве, что и
    /// :pressed-прогиб кнопок внутри блока, и оставленная «единица» перебивала бы его.
    /// </summary>
    /// <param name="keepTransform">
    /// Оставить конечный трансформ на элементе (уходящий блок кроссфейда — его тут же прячут).
    /// По умолчанию база возвращается, чтобы не мешать нажатиям.
    /// </param>
    private static async Task PlayMotion(
        Control el,
        double fromOpacity,
        double toOpacity,
        ITransform? from,
        ITransform? to,
        TimeSpan duration,
        Easing easing,
        TimeSpan delay = default,
        CancellationToken token = default,
        bool keepTransform = false)
    {
        el.Transitions = null;
        el.Opacity = fromOpacity;
        el.RenderTransform = from;

        el.Transitions =
        [
            new TransformOperationsTransition
            {
                Property = Visual.RenderTransformProperty,
                Duration = duration,
                Delay = delay,
                Easing = easing,
            },
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = duration,
                Delay = delay,
                Easing = easing,
            },
        ];

        // Отдельный оборот на фоновом приоритете: раскладка/отрисовка успевают увидеть исходное
        // состояние, иначе переходу нечего интерполировать и кадр «перепрыгивает».
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                el.Opacity = toOpacity;
                el.RenderTransform = to;
            },
            DispatcherPriority.Background);

        try
        {
            await Task.Delay(delay + duration + TimeSpan.FromMilliseconds(40), token);
        }
        finally
        {
            el.Transitions = null;
            if (!token.IsCancellationRequested)
            {
                el.Opacity = toOpacity;
                el.RenderTransform = keepTransform ? to : null;
            }
        }
    }

    /// <summary>Reveal: opacity 0→1 + translateY(from)→0, затем сброс базы.</summary>
    private static async Task RevealFrom(Control el, ITransform from, TimeSpan dur, Easing easing)
    {
        try
        {
            await PlayMotion(el, 0d, 1d, from, _rise0, dur, easing);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("LoginView.RevealFrom", ex);
        }
        finally
        {
            el.Transitions = null;
            el.Opacity = 1;
            el.RenderTransform = null;
        }
    }

    /// <summary>Чистый fade между двумя значениями opacity.</summary>
    private static Task Fade(Visual el, double from, double to, TimeSpan dur, Easing easing)
    {
        var anim = new Animation
        {
            Duration = dur,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, to) } },
            },
        };
        return anim.RunAsync(el, CancellationToken.None);
    }

    /// <summary>opacity 0→1 + scale 0.9→1 (галочка success / плашка).</summary>
    private static Task ScaleFadeIn(Control el, TimeSpan dur, Easing easing)
        => PlayMotion(el, 0d, 1d, _scale090, _scale1, dur, easing);

    /// <summary>reduced-motion: превью-хук (PREVIEW_VIEW), дизайн-режим ИЛИ live lite (MotionState).</summary>
    private static bool IsReducedMotion()
        => Design.IsDesignMode
           || Environment.GetEnvironmentVariable("PREVIEW_VIEW") is not null
           || MotionState.IsLite;

    private static void Execute(ReactiveCommand<Unit, Unit>? command)
    {
        command?.Execute().Subscribe(_ => { }, _ => { });
    }

    /// <summary>Крутит дугу-спиннер только пока она видна (класс .spinning, как ConnectHeroView).</summary>
    private static void SetSpinning(Avalonia.Controls.Shapes.Ellipse spinner, bool spinning)
    {
        if (spinning)
        {
            if (!spinner.Classes.Contains("spinning"))
            {
                spinner.Classes.Add("spinning");
            }
        }
        else
        {
            spinner.Classes.Remove("spinning");
        }
    }
}
