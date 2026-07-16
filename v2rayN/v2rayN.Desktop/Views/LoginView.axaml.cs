using System.Reactive.Disposables;
using System.Text.RegularExpressions;
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
/// idle / awaiting-TG / loading-site / 2FA / error переключаются по
/// <see cref="AccountViewModel.CurrentLoginState"/> (WhenAnyValue на UI-шедулере, DisposeWith).
/// При <see cref="AccountViewModel.IsLoggedIn"/> = true поднимается <see cref="BackRequested"/> —
/// хост закрывает суб-страницу (паритет finish() в Android).
/// </summary>
public partial class LoginView : UserControl
{
    /// <summary>
    /// Основной сайт для регистрации (НЕ API-хост из BackendConfig). Порт константы
    /// LoginActivity.REGISTER_URL.
    /// </summary>
    private const string RegisterUrl = "https://departament.site";

    /// <summary>Прагматичная проверка email (аналог Android Patterns.EMAIL_ADDRESS).</summary>
    private static readonly Regex _emailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]{2,}$", RegexOptions.Compiled);

    /// <summary>Хост подписывается и закрывает суб-страницу (кнопка «назад» / успешный вход).</summary>
    public event EventHandler? BackRequested;

    private CompositeDisposable? _subscriptions;
    private AccountViewModel? _vm;

    // Запрос входа через сайт / 2FA в полёте (LoginState.SiteLoading).
    private bool _siteBusy;

    // Блок 2FA видим (TwoFaTempToken != null) — спиннер занятости идёт на «Подтвердить».
    private bool _twoFaVisible;

    // Ключ ошибки ИМЕННО логин-потока (LoginState.Error → auth_err_*); имеет приоритет
    // над общим AccountViewModel.ErrorText в строке под картами. Храним КЛЮЧ (не текст),
    // чтобы строка переводилась вживую при смене языка.
    private string _loginErrorKey = string.Empty;

    private bool _revealPassword;

    public LoginView()
    {
        InitializeComponent();

        if (Design.IsDesignMode)
        {
            DataContext = AccountViewModel.CreateDesign();
        }

        BackButton.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        TelegramButton.Click += OnTelegramClick;
        RestartButton.Click += OnRestartClick;
        OpenTelegramButton.Click += OnOpenTelegramClick;
        RegisterButton.Click += (_, _) => ProcUtils.ProcessStart(RegisterUrl);
        TogglePasswordButton.Click += OnTogglePasswordClick;

        // Отправка с клавиатуры (паритет imeOptions actionNext/actionDone).
        EmailBox.KeyDown += OnEmailKeyDown;
        PasswordBox.KeyDown += OnPasswordKeyDown;
        CodeBox.KeyDown += OnCodeKeyDown;

        // Email подрезается перед отправкой командой (VM использует значение как есть).
        SiteButton.Click += (_, _) => TrimEmail();

        DataContextChanged += (_, _) => Rebind();
        AttachedToVisualTree += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unbind();

        Rebind();
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

        // Машина состояний входа: idle / awaiting-TG / loading-site / success / error.
        _vm.WhenAnyValue(x => x.CurrentLoginState)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyLoginState)
            .DisposeWith(d);

        // Блок 2FA виден, пока бэкенд держит tempToken (паритет onTwoFactor).
        _vm.WhenAnyValue(x => x.TwoFaTempToken)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(Apply2Fa)
            .DisposeWith(d);

        // ВАЖНО: deep link Telegram открывает ТОЛЬКО VM (AccountViewModel.ApplyLoginState →
        // ProcUtils.ProcessStart на состоянии AwaitingTelegram). Раньше вид ПОВТОРНО открывал ту же
        // ссылку по WhenAnyValue(TelegramDeepLink) → браузер поднимал ДВЕ одинаковые вкладки. Вид
        // авто-открытие больше НЕ делает; ручной повтор доступен кнопкой «Открыть Telegram».

        // Живая валидация: submit активен только при валидном вводе (паритет
        // updateSiteSubmitEnabled / update2faSubmitEnabled + doAfterTextChanged).
        _vm.WhenAnyValue(x => x.LoginEmail, x => x.LoginPassword)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateSiteGate())
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

        // Вход выполнен — суб-страница закрывается (паритет setResult(RESULT_OK); finish()).
        _vm.WhenAnyValue(x => x.IsLoggedIn)
            .DistinctUntilChanged()
            .Where(loggedIn => loggedIn)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => BackRequested?.Invoke(this, EventArgs.Empty))
            .DisposeWith(d);

        // Живой перевод императивных строк (строка ошибки логин-потока + подсказка глаза-пароля):
        // XAML-подписи обновляются сами через {loc:T}, эти два — по событию смены языка.
        void OnLanguageChanged(object? s, EventArgs e) => RunOnUiLang(ApplyLanguage);
        L.Instance.LanguageChanged += OnLanguageChanged;
        Disposable.Create(() => L.Instance.LanguageChanged -= OnLanguageChanged).DisposeWith(d);
    }

    /// <summary>Диспетчеризует на UI-поток (событие языка может прийти не из UI).</summary>
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

            case LoginState.Success:
                SetAwaiting(false);
                SetSiteBusy(false);
                SetLoginError(string.Empty);
                break;

            case LoginState.Error error:
                SetAwaiting(false);
                SetSiteBusy(false);
                SetLoginError(MessageKeyFor(error.ErrorValue));
                break;

            default: // Idle. Ошибку НЕ трогаем: Idle приходит и сразу после показа
                     // ошибки (паритет showIntro — не затирает только что показанный текст).
                SetAwaiting(false);
                SetSiteBusy(false);
                break;
        }
    }

    /// <summary>
    /// Состояние ожидания подтверждения в Telegram (layout_awaiting). AwaitingBlock —
    /// отдельный сфокусированный экран: на время опроса он ЗАМЕНЯЕТ MethodBlock (выбор способа
    /// входа + форма сайта), чтобы фокус был на подтверждении. По выходу из ожидания
    /// (успех / ошибка / idle) MethodBlock возвращается — форма и её состояние сохранены.
    /// </summary>
    private void SetAwaiting(bool awaiting)
    {
        AwaitingBlock.IsVisible = awaiting;
        MethodBlock.IsVisible = !awaiting;
        SetSpinning(AwaitingSpinner, awaiting);
        // CTA Telegram неактивен ТОЛЬКО пока идёт опрос подтверждения (паритет showAwaiting:
        // btnTelegram.isEnabled = false). В любом другом состоянии он активен — вход через
        // Telegram не зависит от валидности формы сайта и не блокируется входом через сайт.
        TelegramButton.IsEnabled = !awaiting;
    }

    /// <summary>
    /// Занятость входа через сайт / 2FA: спиннер на инициировавшей кнопке, submit-кнопки
    /// заблокированы (паритет setSiteBusy). CTA Telegram НЕ трогаем — он не гейтится входом
    /// через сайт (им владеет SetAwaiting: активен, кроме времени опроса Telegram).
    /// </summary>
    private void SetSiteBusy(bool busy)
    {
        _siteBusy = busy;

        var onSite = busy && !_twoFaVisible;
        var on2Fa = busy && _twoFaVisible;

        SiteSpinner.IsVisible = onSite;
        SetSpinning(SiteSpinner, onSite);
        SiteButtonLabel.IsVisible = !onSite;

        ConfirmSpinner.IsVisible = on2Fa;
        SetSpinning(ConfirmSpinner, on2Fa);
        ConfirmButtonLabel.IsVisible = !on2Fa;

        UpdateSiteGate();
        Update2FaGate();
    }

    /// <summary>Показывает/прячет блок 2FA по tempToken (паритет onTwoFactor).</summary>
    private void Apply2Fa(string? tempToken)
    {
        var visible = tempToken != null;
        var appeared = visible && !_twoFaVisible;
        _twoFaVisible = visible;
        TwoFaBlock.IsVisible = visible;
        if (appeared)
        {
            SetLoginError(string.Empty);
            if (!Design.IsDesignMode)
            {
                CodeBox.Focus();
            }
        }
        Update2FaGate();
    }

    // ── Валидация ввода ─────────────────────────────────────────────────────

    /// <summary>Кнопка «Войти через сайт» активна только при валидном email + пароле.</summary>
    private void UpdateSiteGate()
    {
        var email = _vm?.LoginEmail?.Trim() ?? string.Empty;
        var password = _vm?.LoginPassword ?? string.Empty;

        EmailError.IsVisible = email.Length > 0 && !IsEmail(email);
        SiteButton.IsEnabled = !_siteBusy && IsEmail(email) && password.Length > 0;
    }

    /// <summary>
    /// «Подтвердить» активна только при 6 цифрах. Нецифровые символы отбрасываются
    /// (паритет inputType=number; MaxLength=6 задан в разметке).
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

    /// <summary>Ошибка логин-потока приоритетнее общего ErrorText VM; пусто — строка скрыта.
    /// Ключ разрешается в текущий язык при каждом вызове (переводится вживую).</summary>
    private void UpdateErrorLine()
    {
        var text = _loginErrorKey.IsNotEmpty() ? L.T(_loginErrorKey) : (_vm?.ErrorText ?? string.Empty);
        ErrorLine.Text = text;
        ErrorLine.IsVisible = text.IsNotEmpty();
    }

    /// <summary>Ключ строки отказа (порт LoginActivity.messageFor); текст берёт локализация.</summary>
    private static string MessageKeyFor(ApiError error) => error switch
    {
        // 401/403 на входе через сайт — почти всегда неверные учётные данные.
        ApiError.Unauthorized => "Login_ErrBadCreds",
        ApiError.GoneError => "Login_ErrLinkExpired",
        ApiError.ServiceUnavailable => "Common_ServiceUnavailable",
        ApiError.NetworkError or ApiError.TimeoutError => "Common_NetworkError",
        ApiError.NotConfiguredError => "Login_ErrUnavailable",
        _ => "Login_ErrRetry",
    };

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

    /// <summary>
    /// «Войти через Telegram»: запускает вход через Telegram. Всегда доступен (кроме времени опроса,
    /// когда кнопка неактивна через SetAwaiting) — вход не требует ввода формы (паритет btnTelegram:
    /// lastAttemptWasSite=false; hideError; startTelegramLogin).
    /// </summary>
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
            SubmitSite();
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

    /// <summary>Валидирует 6-значный код и завершает вход 2FA.</summary>
    private void Submit2Fa()
    {
        if (_vm is null || _siteBusy || !_twoFaVisible || !IsSixDigits(_vm.TwoFaCode ?? string.Empty))
        {
            return;
        }
        Execute(_vm.Submit2FaCmd);
    }

    // ── Помощники ───────────────────────────────────────────────────────────

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
