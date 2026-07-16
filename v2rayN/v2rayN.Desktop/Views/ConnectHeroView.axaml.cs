using System.Runtime.InteropServices;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Connect-щит (герой) + стата-строка — правая панель «Главной». Перенос 1:1 из Android
/// (activity_main.xml hero + MainActivity applyRunningState/applyConnectedState/applyIdleState).
///
/// Разметка чисто визуальная; три состояния подключения (idle/connecting/connected) и
/// переключение «есть подписка ↔ нет подписки» задаёт публичный API ниже. Будущий HomeViewModel
/// вызывает эти методы/подписывается на события — трогать XAML не нужно.
///
/// Хореография движения (§1.4 DESIGN_PLAN):
///   • press диска — scale 0.94: quart-in 90мс / quint-out 160мс (императивно через ScaleTransform);
///   • connecting — дуга крутится + glow «дышит» (850мс), tint щита серый→синий;
///   • connected  — crossfade контур→залив + tint→СИНИЙ (220мс), glow-reveal (300мс) и ОДИН сонар
///                  (1.0→1.6 + fade, 600мс);
///   • disconnect — реверс на ~75% темпа (state 165мс / reveal 225мс);
///   • cold-start — «сборка» героя (scale 0.9→1 + fade, 400мс) ОДИН раз за процесс.
/// Reduced-motion (Win32 SPI_GETCLIENTAREAANIMATION) → каждая ветка прыгает в конечный вид.
/// Кривые/длительности — токены Ease.*/Dur.* из GlobalResources.axaml.
/// </summary>
public partial class ConnectHeroView : UserControl
{
    /// <summary>Визуальное состояние connect-щита.</summary>
    public enum ConnectVisualState
    {
        Idle,
        Connecting,
        Connected,

        //  Неудачное подключение (§A4): щит тонируется Brush.Red + подпись «Не удалось подключиться»
        //  + подсказка-ретрай. Отличим от штатного отключения (то падает в Idle) — иначе провал
        //  «молча» выглядел как обычный disconnect. Само-сбрасывается в Idle при следующей попытке
        //  подключения (HomeViewModel гонит Connecting → ветка Error затирается).
        Error,
    }

    //  Fallbacks (Incy dark defaults) — used ONLY when a theme-resource lookup fails. The LIVE
    //  values below come from theme tokens so the shield + status text stay readable on the light
    //  theme AND under the «Чёрно-белая» (mono) overlay, not just on dark. Hardcoding these was the
    //  bug: on light/mono the disc collapsed to a light/grey surface while the grey shield + near-
    //  white status text washed out (black-on-black / white-on-white).
    private static readonly IBrush ShieldGrayFallback = new SolidColorBrush(Color.Parse("#9BA1AD"));
    private static readonly IBrush AccentFallback = new SolidColorBrush(Color.Parse("#4C8DFF"));
    private static readonly IBrush OnSurfaceFallback = new SolidColorBrush(Color.Parse("#F2F4F8"));
    private static readonly IBrush ErrorFallback = new SolidColorBrush(Color.Parse("#F04452"));

    //  Resolve a theme brush for the CURRENT theme variant (so it picks up the light theme AND the
    //  merged mono overlay), never throwing out of the connect path — any failure silently returns
    //  the cached Incy fallback so the hero can never end up half-built / blank.
    private IBrush ResolveBrush(string key, IBrush fallback)
    {
        try
        {
            return this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    //  Connected shield/status accent → mono maps Brush.Accent to grey/white (contrast kept).
    private IBrush AccentBrush => ResolveBrush("Brush.Accent", AccentFallback);

    //  Idle shield glyph → Brush.OnSurfaceVariant (dark: grey #9BA1AD, light: #54607A,
    //  mono-dark: #B0B0B4, mono-light: #5A5A5E) — always contrasts the SurfaceHigh disc.
    private IBrush ShieldIdleBrush => ResolveBrush("Brush.OnSurfaceVariant", ShieldGrayFallback);

    //  Idle status text → Brush.OnSurface (theme ink), readable on light/mono, not fixed near-white.
    private IBrush OnSurfaceBrush => ResolveBrush("Brush.OnSurface", OnSurfaceFallback);

    //  Error shield/status tint → Brush.Red (dark #F04452 / light #C42B32; mono keeps a readable
    //  warning red). Destructive/warning is the ONLY non-blue accent allowed (CLAUDE.md).
    private IBrush ErrorBrush => ResolveBrush("Brush.Red", ErrorFallback);

    //  Кривые зеркалят токены GlobalResources Ease.* 1:1 (ease_out_quart/_quint/_standard) —
    //  для императивных частей (press, перекл. длительностей). Декларативный XAML берёт токены.
    private static readonly Easing EaseOutQuart = new SplineEasing(0.25, 1, 0.5, 1);
    private static readonly Easing EaseOutQuint = new SplineEasing(0.22, 1, 0.36, 1);
    private static readonly Easing EaseStandard = new SplineEasing(0.2, 0, 0, 1);

    //  Cold-start «сборка» героя проигрывается ОДИН раз за процесс (Android shield_assemble).
    private static bool _assembled;

    private bool _pressing;
    private ConnectVisualState _visualState = ConnectVisualState.Idle;

    //  Visibility pause (§C3 / idle B5): while the window is minimized OR hidden-to-tray the hero is
    //  off-screen, yet the connecting arc / glow-breathe / shield-breathe keep ticking the compositor
    //  clock. We strip those infinite loops when the window goes invisible and re-attach them on
    //  restore (if still connecting/connected) — pure start/stop, no curve/duration change. Guarded so
    //  a state change ARRIVING while hidden (e.g. connect completes in tray) never starts a loop
    //  off-screen; the loop is (re)attached only on the restore re-apply.
    private bool _animationsPaused;
    private Window? _heroWindow;
    private IDisposable? _winStateSub;
    private IDisposable? _winVisibleSub;

    //  Last hasServer passed to SetConnectState — remembered so a runtime lite toggle can re-apply
    //  the CURRENT visual state (jump to its end-look) without the presenter re-driving it.
    private bool _hasServer = true;

    //  Press-scale диска: собственный ScaleTransform, чей ScaleX/Y анимируем через его же
    //  переходы — длительность+кривую перекл. по направлению (quart-in 90 / quint-out 160).
    private readonly ScaleTransform _discScale = new(1, 1);
    private readonly DoubleTransition _discScaleX = new() { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(160) };
    private readonly DoubleTransition _discScaleY = new() { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(160) };

    //  Переходы crossfade/tint/glow из XAML — длительность (и кривую glow) перекл. на реверсе (75%).
    private DoubleTransition? _outlineOpacity;
    private BrushTransition? _outlineFill;
    private DoubleTransition? _filledOpacity;
    private DoubleTransition? _glowOpacity;

    /// <summary>Тап по диску: переключить подключение (connect/disconnect).</summary>
    public event EventHandler? ConnectToggleRequested;

    /// <summary>«+» в стата-строке (добавить профиль/подписку).</summary>
    public event EventHandler? AddRequested;

    /// <summary>Онбординг «нет подписки»: добавить по QR-коду.</summary>
    public event EventHandler? AddByQrRequested;

    /// <summary>Онбординг «нет подписки»: добавить из буфера обмена.</summary>
    public event EventHandler? AddFromClipboardRequested;

    /// <summary>Если true — движение подавлено, состояния прыгают к конечному виду.</summary>
    public bool ReducedMotion { get; set; }

    public ConnectHeroView()
    {
        InitializeComponent();

        //  Движение подавляем, если включён «Облегчённый режим» (Настройки → LiteMode,
        //  _config.UiItem.LiteMode) ЛИБО система просит меньше анимаций
        //  («Показывать анимации в Windows» выкл). По умолчанию — движение включено.
        ReducedMotion = LiteModeEnabled() || !SystemAnimationsEnabled();

        //  Стата-строка над щитом скрыта целиком в lite (task 2) — начальное состояние по флагу.
        StatsRow.IsVisible = !ReducedMotion;

        //  Держим ссылки на декларативные переходы, чтобы гнуть их темп по направлению.
        //  Порядок соответствует XAML: ShieldOutline[0]=Opacity,[1]=Fill; Filled[0]=Opacity; Glow[0]=Opacity.
        _outlineOpacity = ShieldOutline.Transitions?[0] as DoubleTransition;
        _outlineFill = ShieldOutline.Transitions?[1] as BrushTransition;
        _filledOpacity = ShieldFilled.Transitions?[0] as DoubleTransition;
        _glowOpacity = GlowHalo.Transitions?[0] as DoubleTransition;

        //  Свой press-scale вместо общего перехода RenderTransform из GlobalStyles: масштаб —
        //  единственный отклик (без ripple/glow). Перекрываем переходы диска пустыми и ставим ScaleTransform.
        //
        //  ЦЕНТР МАСШТАБА. RenderTransformOrigin НЕ применяется к анимируемым render-transform в этой
        //  сборке (та же причина, по которой ConnectingArc держит центр через RotateTransform.CenterX/Y,
        //  а не через origin). У ScaleTransform НЕТ CenterX/CenterY, поэтому голый ScaleTransform
        //  масштабировал диск от ЛЕВОГО-ВЕРХНЕГО угла → диск «проваливался» влево-вверх. Оборачиваем в
        //  группу: сдвиг центра диска (88,88) в (0,0) → масштаб → сдвиг обратно = масштаб строго вокруг
        //  центра. Переход живёт на самом ScaleTransform (асимметрия 90/160 сохраняется).
        _discScale.Transitions = new Transitions { _discScaleX, _discScaleY };
        ConnectDisc.Transitions = new Transitions();
        const double discHalf = 88; // Size.ConnectDisc (176) / 2
        ConnectDisc.RenderTransform = new TransformGroup
        {
            Children =
            {
                new TranslateTransform { X = -discHalf, Y = -discHalf },
                _discScale,
                new TranslateTransform { X = discHalf, Y = discHalf },
            },
        };

        //  Диск — кнопка connect: press-scale + клик = переключение.
        ConnectDisc.PointerPressed += OnDiscPointerPressed;
        ConnectDisc.PointerReleased += OnDiscPointerReleased;
        ConnectDisc.PointerCaptureLost += OnDiscPressCancel;
        ConnectDisc.PointerExited += OnDiscPressCancel;

        AddQrButton.Click += (_, _) => AddByQrRequested?.Invoke(this, EventArgs.Empty);
        AddClipboardButton.Click += (_, _) => AddFromClipboardRequested?.Invoke(this, EventArgs.Empty);

        //  Угловой «+» (правый-верхний угол панели) — меню импорта (буфер / QR) через команды
        //  унаследованного HomeViewModel (см. XAML Command-биндинги); code-behind проводка не нужна.

        //  Реактивный lite: подписываемся, пока во визуальном дереве, и синхронизируем ReducedMotion
        //  с текущим MotionState при входе (значение уже засеяно MainWindow к моменту показа окна).
        AttachedToVisualTree += OnHeroAttached;
        DetachedFromVisualTree += OnHeroDetached;

        //  Cold-start «сборка» героя по первому показу окна (гвард статик-флагом). Прячем герой
        //  ДО первого кадра, чтобы не мелькнул в покое перед анимацией «сборки».
        if (!ReducedMotion && !_assembled)
        {
            HeroFrame.Opacity = 0;

            //  СТРАХОВКА ОТ ПУСТОГО ЩИТА. Пре-скрытие полагается на Loaded, который вернёт видимость.
            //  Если Loaded не придёт (переиспользование окна, прерванная раскладка, исключение внутри
            //  «сборки») — щит остался бы невидимым НАВСЕГДА. Форсируем видимость с запасом: на штатном
            //  пути «сборка» (≈460мс) уже вернула Opacity=1, поэтому здесь это no-op.
            DispatcherTimer.RunOnce(EnsureHeroVisible, TimeSpan.FromMilliseconds(700));
        }

        Loaded += OnFirstLoaded;

        //  Первый рендер: idle БЕЗ сервера — идентичность скрыта, пока HomeView не подставит
        //  реальный активный сервер (data-driven: никаких плейсхолдеров в рантайме).
        SetConnectState(ConnectVisualState.Idle, hasServer: false, animate: false);
    }

    /// <summary>
    /// Переключает панель между героем (подписка есть) и онбордингом «нет подписки»
    /// (нет серверов) — аналог Android updateHomeEmptyState.
    /// </summary>
    public void ShowEmptyState(bool empty)
    {
        LayerEmpty.IsVisible = empty;
        LayerNormal.IsVisible = !empty;
    }

    /// <summary>
    /// Задаёт щит/кольца/glow/подпись для состояния подключения. <paramref name="hasServer"/>
    /// тускнит idle-щит (0.38) и меняет «Не подключено» ↔ «Выберите сервер».
    /// <paramref name="animate"/> = живой переход в connected (играет сонар-подтверждение).
    /// Переход в idle трактуется как реверс (отключение) и идёт на ~75% темпа.
    /// </summary>
    public void SetConnectState(ConnectVisualState state, bool hasServer = true, bool animate = false)
    {
        _hasServer = hasServer;
        var motion = animate && !ReducedMotion;

        //  Idle/Error — «остывающая» цель ⇒ реверс-темп (165/225); connecting/connected — forward (220/300).
        //  Error приходит из Connecting как провал (мягко «оседает»), поэтому идёт по реверс-темпу.
        PrepareStateTiming(reverse: state is ConnectVisualState.Idle or ConnectVisualState.Error);

        //  Вторичный connecting-сигнал: щит «дышит» акцентом в УНИСОН с glow (см. SetShieldPulse).
        //  Ставим/снимаем ДО switch — на connected/idle класс снят прежде, чем ветка выставит
        //  Opacity контура (иначе анимация перекрыла бы crossfade контур→залив).
        SetShieldPulse(state == ConnectVisualState.Connecting);

        switch (state)
        {
            case ConnectVisualState.Connecting:
                ShieldOutline.Fill = AccentBrush;
                ShieldOutline.Opacity = 1;
                ShieldFilled.Opacity = 0;
                StatusText.Text = L.T("Status_Connecting");
                StatusText.Foreground = AccentBrush;
                ServerInfo.IsVisible = true;
                SetArc(true);
                SetGlow(connecting: true, connected: false);
                HideSonar();
                break;

            case ConnectVisualState.Connected:
                ShieldOutline.Fill = AccentBrush;
                ShieldOutline.Opacity = 0;
                ShieldFilled.Opacity = 1;
                StatusText.Text = L.T("Status_Connected");
                StatusText.Foreground = AccentBrush;
                ServerInfo.IsVisible = true;
                SetArc(false);
                SetGlow(connecting: false, connected: true);
                //  Сонар — одноразовый confirm; не проигрываем, пока окно скрыто (никто не увидит,
                //  а на восстановлении re-apply идёт с animate:false — повтор сонара не нужен).
                if (motion && !_animationsPaused)
                {
                    PlaySonar();
                }
                else
                {
                    HideSonar();
                }

                break;

            case ConnectVisualState.Error:
                //  Провал подключения: тот же контур-щит, но тонирован Red (crossfade синий→красный
                //  по реверс-темпу), подпись + подсказка-ретрай красным. Диск остаётся кнопкой —
                //  тап = ConnectToggleRequested = повторная попытка (тогда HomeVM гонит Connecting и
                //  ветка Error сама затирается). Никаких петель/сонара — статичный конечный вид.
                ShieldOutline.Fill = ErrorBrush;
                ShieldOutline.Opacity = 1;
                ShieldFilled.Opacity = 0;
                StatusText.Text = L.T("Common_CouldntConnect");
                StatusText.Foreground = ErrorBrush;
                ServerInfo.IsVisible = hasServer;
                SetArc(false);
                SetGlow(connecting: false, connected: false);
                HideSonar();
                UpSpeed.Text = "0 KB/s";
                DownSpeed.Text = "0 KB/s";
                Uptime.Text = "00:00:00";
                break;

            default: // Idle
                ShieldOutline.Fill = ShieldIdleBrush;
                ShieldOutline.Opacity = hasServer ? 1 : 0.38;
                ShieldFilled.Opacity = 0;
                StatusText.Text = hasServer ? L.T("Home_NotConnected") : L.T("Home_ChooseServer");
                StatusText.Foreground = OnSurfaceBrush;
                ServerInfo.IsVisible = hasServer;
                SetArc(false);
                SetGlow(connecting: false, connected: false);
                HideSonar();
                UpSpeed.Text = "0 KB/s";
                DownSpeed.Text = "0 KB/s";
                Uptime.Text = "00:00:00";
                break;
        }

        //  Подсказка-ретрай видна ТОЛЬКО в Error — тихая аффорданс «тапни щит, чтобы повторить».
        RetryHint.IsVisible = state == ConnectVisualState.Error;

        _visualState = state;
    }

    /// <summary>Обновляет ↑/↓ (строки Utils.HumanFy, напр. «1.2 MB/s»).</summary>
    public void SetSpeeds(string up, string down)
    {
        UpSpeed.Text = up;
        DownSpeed.Text = down;
    }

    /// <summary>Обновляет центральный таймер аптайма (hh:mm:ss).</summary>
    public void SetUptime(string uptime) => Uptime.Text = uptime;

    /// <summary>
    /// Обновляет идентичность выбранного/подключённого сервера под щитом. Флаг заполняет
    /// FlagResolver (мастер-план §5); null-источник оставляет нейтральный чип-плейсхолдер.
    /// </summary>
    public void SetServerInfo(string name, string protocol, string transport, IImage? flag = null)
    {
        ServerName.Text = name;
        ServerProtocol.Text = protocol;
        ServerTransport.Text = transport;
        ServerFlagImage.Source = flag;
    }

    /// <summary>Show/hide the corner «+» (add-subscription) affordance. The widescreen connect panel
    /// keeps it in its top-right corner; the compact layout hides it (its own header carries the «+»).</summary>
    public void SetCornerAddVisible(bool visible) => CornerAddButton.IsVisible = visible;

    //  Угловой «+» (добавить подписку) — те же события, что онбординг-кнопки; MainWindow ведёт их
    //  в реальный поток добавления (буфер обмена / QR).
    private void OnCornerAddClipboard(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => AddFromClipboardRequested?.Invoke(this, EventArgs.Empty);

    private void OnCornerAddQr(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => AddByQrRequested?.Invoke(this, EventArgs.Empty);

    // ── Реактивный «Облегчённый режим» (runtime, без рестарта) ───────────────────────────
    //  MotionState.Changed → мгновенно гасим/оживляем ВСЮ хореографию щита (спин дуги, breathe glow,
    //  shieldbreathe, сонар, «сборку») и прячем/показываем стата-строку, прыгая в конечный вид.
    private void OnHeroAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        MotionState.Changed += OnMotionStateChanged;

        //  Live language switch: the status caption is set imperatively (SetConnectState), so a
        //  {loc:T} binding can't refresh it. Re-apply the current visual state (animate:false = jump
        //  to its end-look, no re-played sonar) whenever the language changes so the caption follows.
        L.Instance.LanguageChanged += OnLanguageChanged;
        //  Синхронизируемся с текущим режимом на входе в дерево (без перезапуска состояния —
        //  connect-состояние подаст HomeHeroPresenter). Обновляем только флаг + видимость статы.
        ApplyLiteMode(MotionState.IsLite, reapply: false);

        //  §C3: пауза петель, пока окно свёрнуто/скрыто-в-трей. Наблюдаем WindowState (свёрнуто) и
        //  IsVisible (Hide() в трей не меняет WindowState). GetObservable сразу отдаёт текущее
        //  значение → начальное состояние паузы выставится корректно на входе в дерево.
        _heroWindow = TopLevel.GetTopLevel(this) as Window;
        if (_heroWindow is not null)
        {
            _winStateSub = _heroWindow.GetObservable(Window.WindowStateProperty).Subscribe(_ => UpdateVisibilityPause());
            _winVisibleSub = _heroWindow.GetObservable(Visual.IsVisibleProperty).Subscribe(_ => UpdateVisibilityPause());
        }
    }

    private void OnHeroDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        MotionState.Changed -= OnMotionStateChanged;
        L.Instance.LanguageChanged -= OnLanguageChanged;

        _winStateSub?.Dispose();
        _winStateSub = null;
        _winVisibleSub?.Dispose();
        _winVisibleSub = null;
        _heroWindow = null;
        //  Сброс флага: следующий attach заново оценит видимость окна (priming-эмиссия наблюдений).
        _animationsPaused = false;
    }

    // ── §C3: пауза/возобновление бесконечных петель по видимости окна ─────────────────────
    //  Скрыто == окно свёрнуто ЛИБО спрятано (Hide() в трей: IsVisible=false, WindowState не
    //  меняется). Простой Deactivated (окно видимо, но не в фокусе) НЕ ставит паузу намеренно —
    //  иначе петля «замерла» бы прямо на экране у пользователя, переключившего окно.
    private void UpdateVisibilityPause()
    {
        var hidden = _heroWindow is not null
            && (_heroWindow.WindowState == WindowState.Minimized || !_heroWindow.IsVisible);
        if (hidden == _animationsPaused)
        {
            return;
        }

        if (hidden)
        {
            //  Тот же teardown, что и при выходе из connecting/connected — снимаем ВСЕ петли.
            _animationsPaused = true;
            ConnectingArc.Classes.Remove("spinning");
            GlowHalo.Classes.Remove("breathing");
            ShieldOutline.Classes.Remove("shieldbreathe");
            HideSonar();
        }
        else
        {
            //  Восстановление: снимаем флаг ПЕРЕД re-apply, чтобы петли снова навесились. animate:false
            //  — прыгаем в конечный вид текущего состояния (без повторного сонара), поэтому щит не
            //  может «залипнуть» в неверном визуале после разворачивания.
            _animationsPaused = false;
            SetConnectState(_visualState, hasServer: _hasServer, animate: false);
        }
    }

    private void OnMotionStateChanged(object? sender, bool lite) => ApplyLiteMode(lite, reapply: true);

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        SetConnectState(_visualState, hasServer: _hasServer, animate: false);

    private void ApplyLiteMode(bool lite, bool reapply)
    {
        //  Система тоже может требовать reduced-motion — тогда lite=off всё равно оставляет движение
        //  выключенным (как в ctor).
        ReducedMotion = lite || !SystemAnimationsEnabled();

        //  Task 2: показатели скорости/аптайма над кнопкой пропадают в lite.
        StatsRow.IsVisible = !ReducedMotion;

        if (!reapply)
        {
            return;
        }

        //  Рантайм-переключение: убиваем любую идущую петлю анимаций и прыгаем в текущий конечный вид.
        //  (При выключении lite SetConnectState заново навесит нужные петли, т.к. ReducedMotion=false.)
        ConnectingArc.Classes.Remove("spinning");
        GlowHalo.Classes.Remove("breathing");
        ShieldOutline.Classes.Remove("shieldbreathe");
        HideSonar();
        HeroFrame.Classes.Remove("assembling");
        EnsureHeroVisible();
        SetConnectState(_visualState, hasServer: _hasServer, animate: false);
    }

    // ── Темп переходов (forward 220/300 ↔ реверс 165/225; reduced-motion → 0) ──────────
    private void PrepareStateTiming(bool reverse)
    {
        var state = ReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(reverse ? 165 : 220);
        var reveal = ReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(reverse ? 225 : 300);
        if (_outlineOpacity is not null)
        {
            _outlineOpacity.Duration = state;
        }

        if (_outlineFill is not null)
        {
            _outlineFill.Duration = state;
        }

        if (_filledOpacity is not null)
        {
            _filledOpacity.Duration = state;
        }

        if (_glowOpacity is not null)
        {
            _glowOpacity.Duration = reveal;
            _glowOpacity.Easing = reverse ? EaseStandard : EaseOutQuint; // reveal quint / hide standard
        }
    }

    // ── Press диска: quart-in 90мс / quint-out 160мс (без цвета/заливки — только масштаб) ──
    private void OnDiscPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressing = true;
        if (ReducedMotion)
        {
            return;
        }

        _discScaleX.Duration = _discScaleY.Duration = TimeSpan.FromMilliseconds(90);
        _discScaleX.Easing = _discScaleY.Easing = EaseOutQuart;
        _discScale.ScaleX = _discScale.ScaleY = 0.94;
    }

    private void OnDiscPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ReleaseDiscScale();
        if (_pressing)
        {
            _pressing = false;
            ConnectToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDiscPressCancel(object? sender, EventArgs e)
    {
        _pressing = false;
        ReleaseDiscScale();
    }

    private void ReleaseDiscScale()
    {
        if (ReducedMotion)
        {
            return;
        }

        _discScaleX.Duration = _discScaleY.Duration = TimeSpan.FromMilliseconds(160);
        _discScaleX.Easing = _discScaleY.Easing = EaseOutQuint;
        _discScale.ScaleX = _discScale.ScaleY = 1.0;
    }

    // ── Дуга / glow / сонар ───────────────────────────────────────────────────────────
    private void SetArc(bool on)
    {
        //  В lite/reduced-motion дуги НЕТ ВООБЩЕ (не статичная, а полностью скрыта): владелец
        //  не хочет «замёрзшую» синюю дугу в облегчённом режиме. Само состояние connecting всё
        //  равно читается подписью «Подключение…» — кольцо/дуга существуют только ради движения.
        //  Реактивно: MotionState.Changed → ApplyLiteMode → SetConnectState → сюда, поэтому живой
        //  тумблер lite мгновенно прячет/возвращает дугу.
        ConnectingArc.IsVisible = on && !ReducedMotion;

        //  ОДНА чистая центрированная дуга: крутится только пока реально нужно и не под
        //  ReducedMotion/lite (тогда её вообще нет — см. выше). Второй counter-arc убран —
        //  он «облетал» шит, т.к. не имел RenderTransformOrigin в центре.
        if (on && !ReducedMotion && !_animationsPaused)
        {
            ConnectingArc.Classes.Add("spinning");
        }
        else
        {
            ConnectingArc.Classes.Remove("spinning");
        }
    }

    //  Connecting-«дыхание» щита: спокойный вторичный сигнал в унисон с glow-breathe (те же 850мс
    //  sine). Только OPACITY (1↔0.8) на контур-щите — БЕЗ transform, поэтому центрировать нечего и
    //  «улететь» физически нельзя. Reduced-motion/lite: класс не вешаем → щит статичен (дуга/сигнал
    //  подключения остаются читаемыми и без движения).
    private void SetShieldPulse(bool on)
    {
        if (on && !ReducedMotion && !_animationsPaused)
        {
            ShieldOutline.Classes.Add("shieldbreathe");
        }
        else
        {
            ShieldOutline.Classes.Remove("shieldbreathe");
        }
    }

    private void SetGlow(bool connecting, bool connected)
    {
        GlowHalo.Classes.Remove("breathing");
        if (connecting)
        {
            //  Reduced-motion: halo не дышит — дуга остаётся единственным сигналом.
            if (ReducedMotion)
            {
                GlowHalo.Opacity = 0;
                return;
            }

            GlowHalo.IsVisible = true;
            GlowHalo.Opacity = 0.6; //  база под «дыханием» (0.3↔0.6) → плавный хэндофф к reveal
            //  Пауза (окно скрыто): держим статичный halo без петли — она вернётся на восстановлении.
            if (!_animationsPaused)
            {
                GlowHalo.Classes.Add("breathing");
            }
        }
        else if (connected)
        {
            GlowHalo.IsVisible = true;
            GlowHalo.Opacity = 1; //  reveal через переход (OutQuint, 300мс)
        }
        else
        {
            GlowHalo.Opacity = 0; //  hide через переход (Standard, 225мс); остаётся, но невидим
        }
    }

    private void PlaySonar()
    {
        //  ОДНО кольцо: 1.0→1.6 + alpha 1→0 за emphasis (600мс, quint). Класс снимаем и вешаем
        //  на следующем цикле диспетчера, чтобы одноразовая анимация чисто перезапускалась.
        SonarPulse.Classes.Remove("pulsing");
        SonarPulse.IsVisible = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_visualState == ConnectVisualState.Connected)
                {
                    SonarPulse.Classes.Add("pulsing");
                }
            },
            DispatcherPriority.Background);
    }

    private void HideSonar()
    {
        SonarPulse.Classes.Remove("pulsing");
        SonarPulse.IsVisible = false;
    }

    // ── Cold-start «сборка» героя (ОДИН раз за процесс) ─────────────────────────────────
    private async void OnFirstLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        if (_assembled || ReducedMotion)
        {
            EnsureHeroVisible(); //  сборку пропускаем — гарантируем видимость
            return;
        }

        _assembled = true;
        try
        {
            HeroFrame.Classes.Add("assembling");
            await Task.Delay(460);
        }
        finally
        {
            //  ВСЕГДА возвращаем видимость (даже если задержка/анимация прервана исключением) —
            //  щит физически не может остаться пустым.
            EnsureHeroVisible();
        }
    }

    //  Единая точка восстановления покоя героя: полная непрозрачность + снятие one-shot-класса
    //  «сборки». Идемпотентна — безопасно вызывать из Loaded, finally и страховочного таймера.
    private void EnsureHeroVisible()
    {
        HeroFrame.Classes.Remove("assembling");
        if (HeroFrame.Opacity < 1)
        {
            HeroFrame.Opacity = 1;
        }
    }

    // ── «Облегчённый режим» (lite): общий флаг reduced-motion из конфига ──────────────────
    private static bool LiteModeEnabled()
    {
        //  В дизайн-режиме конфига/движка нет — считаем режим выключенным.
        if (Design.IsDesignMode)
        {
            return false;
        }

        try
        {
            return AppManager.Instance.Config.UiItem.LiteMode;
        }
        catch
        {
            return false;
        }
    }

    // ── Reduced-motion: Win32 SPI_GETCLIENTAREAANIMATION («Показывать анимации в Windows») ──
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    private static bool SystemAnimationsEnabled()
    {
        //  Нет прямого сигнала вне Windows → считаем движение включённым.
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var enabled = true;
            return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0) || enabled;
        }
        catch
        {
            return true;
        }
    }
}
