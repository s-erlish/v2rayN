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

    //  Layout-deactivation pause (RAM/CPU): in the keep-alive shell BOTH the wide and compact hero
    //  are permanent children of contentHost; only the active-layout hero holds the live VM. The
    //  INACTIVE hero sits at Opacity=0 — which does NOT stop Style animations (only IsVisible=false
    //  would) — so without this its ambient breathe/sonar + glow/arc/shield loops would tick the
    //  compositor forever off-screen. HomeHeroPresenter.Deactivate() (on unbind) sets this and strips
    //  the loops; Activate() (on rebind) clears it and the presenter's re-apply re-attaches them.
    //  Folded with _animationsPaused into MotionSuppressed so every loop gate honours BOTH reasons.
    private bool _deactivated;

    //  True when NO infinite loop may run right now — window hidden/minimized OR this layout is the
    //  inactive one. Every loop-attach site checks this so a suppressed hero never drives the clock.
    private bool MotionSuppressed => _animationsPaused || _deactivated;

    private Window? _heroWindow;
    private IDisposable? _winStateSub;
    private IDisposable? _winVisibleSub;

    //  Last hasServer passed to SetConnectState — remembered so a runtime lite toggle can re-apply
    //  the CURRENT visual state (jump to its end-look) without the presenter re-driving it.
    private bool _hasServer = true;

    //  Онбординг «нет подписки» активен (ShowEmptyState(true)): герой снят со сцены, поэтому ambient-
    //  петля не должна крутить компоновщик под скрытым слоем. SetAmbient уважает этот флаг.
    private bool _empty;

    //  Press-scale диска: собственный ScaleTransform, чей ScaleX/Y анимируем через его же
    //  переходы — длительность+кривую перекл. по направлению (quart-in 90 / quint-out 160).
    //  ЭТОТ ЖЕ ScaleTransform ведёт connect-bloom (1.0→1.04→1.0) и error-contract (1.0→0.98→1.0):
    //  оба — оседания через те же переходы (симметрично прочитанные обе ноги, БЕЗ overshoot/bounce).
    private readonly ScaleTransform _discScale = new(1, 1);
    private readonly DoubleTransition _discScaleX = new() { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(160) };
    private readonly DoubleTransition _discScaleY = new() { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(160) };

    //  Глиф-щит parallax-dip на press (1.0→0.97): вложенный self-centering ScaleTransform на Viewbox —
    //  глиф «вдавливается» в диск чуть глубже самого диска (0.94). Тайминг зеркалит press-scale (90/160).
    private readonly ScaleTransform _glyphScale = new(1, 1);
    private readonly DoubleTransition _glyphScaleX = new() { Property = ScaleTransform.ScaleXProperty, Duration = TimeSpan.FromMilliseconds(160) };
    private readonly DoubleTransition _glyphScaleY = new() { Property = ScaleTransform.ScaleYProperty, Duration = TimeSpan.FromMilliseconds(160) };

    //  Press-скрим диска (0→~0.12): чёрная «лунка» под глифом на press. Тайминг зеркалит press-scale.
    private readonly DoubleTransition _scrimOpacity = new() { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) };

    //  Opacity-переход дуги: 200мс OutQuint на wind-up (fade-in из покоя) / 220мс Standard на dissolve
    //  (растворение в glow на connect-confirm). Длительность/кривую перекл. код-behind по сайту.
    private readonly DoubleTransition _arcOpacity = new() { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) };

    //  Hover-переходы (P0-1): поверхность диска (Brush) + накладное кольцо (Opacity) на наведении.
    //  120мс OutQuart в движении, 0мс в lite (мгновенный swap, без «моушена» — только цвет/яркость).
    private const double HoverMs = 120;
    private readonly BrushTransition _discSurface = new() { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(HoverMs) };
    private readonly DoubleTransition _ringHoverOpacity = new() { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(HoverMs) };

    //  Retry-hint fade (Error): 220мс Standard в движении, 0мс в lite (мгновенно виден).
    private readonly DoubleTransition _retryHintOpacity = new() { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) };

    //  Указатель над диском — держим флаг, чтобы hover-exit был идемпотентен (PointerExited приходит
    //  и как отмена press, и как выход hover) и чтобы не «залипнуть» в hover-виде.
    private bool _hovering;

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
        //  Переход фона диска (hover surface-lift) живёт на самом Border; press-scale — отдельный
        //  вложенный ScaleTransform (ниже), поэтому эти два отклика не мешают друг другу.
        ConnectDisc.Transitions = new Transitions { _discSurface };
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

        //  Глиф-щит: self-centering ScaleTransform (parallax-dip). Тот же приём, что диск — origin НЕ
        //  применяется к анимируемым render-transform в этой сборке, поэтому центрируем явным сдвигом
        //  (−40 → scale → +40, 40 = Size.ShieldGlyph 80 / 2), иначе глиф «уезжал» бы в левый-верх.
        _glyphScale.Transitions = new Transitions { _glyphScaleX, _glyphScaleY };
        const double glyphHalf = 40; // Size.ShieldGlyph (80) / 2
        ShieldViewbox.RenderTransform = new TransformGroup
        {
            Children =
            {
                new TranslateTransform { X = -glyphHalf, Y = -glyphHalf },
                _glyphScale,
                new TranslateTransform { X = glyphHalf, Y = glyphHalf },
            },
        };

        //  Переходы, которыми управляет код-behind по сайту вызова (длительность/кривая/направление).
        PressScrim.Transitions = new Transitions { _scrimOpacity };
        ConnectingArc.Transitions = new Transitions { _arcOpacity };
        RingHoverGlow.Transitions = new Transitions { _ringHoverOpacity };
        RetryHint.Transitions = new Transitions { _retryHintOpacity };

        //  Кривые hover/retry-переходов + начальные длительности по режиму (lite = 0мс).
        _discSurface.Easing = _ringHoverOpacity.Easing = EaseOutQuart;
        _retryHintOpacity.Easing = EaseStandard;
        ApplyMotionGatedDurations();

        //  Диск — кнопка connect: press-scale + клик = переключение; hover = surface-lift + ring-brighten.
        ConnectDisc.PointerPressed += OnDiscPointerPressed;
        ConnectDisc.PointerReleased += OnDiscPointerReleased;
        ConnectDisc.PointerCaptureLost += OnDiscPressCancel;
        ConnectDisc.PointerExited += OnDiscPressCancel;
        ConnectDisc.PointerEntered += OnDiscPointerEntered;
        ConnectDisc.PointerExited += OnDiscHoverExit;

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
        _empty = empty;
        LayerEmpty.IsVisible = empty;
        LayerNormal.IsVisible = !empty;

        //  Онбординг снимает герой со сцены → гасим ambient-петлю (иначе она крутила бы компоновщик
        //  под скрытым слоем). Возврат к герою re-применяет ambient текущего состояния.
        if (empty)
        {
            RemoveAmbientLoops();
            AmbientRing.IsVisible = false;
            AmbientSonar.IsVisible = false;
        }
        else
        {
            SetAmbient(_visualState);
        }
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

        //  Вход в состояние (а не re-apply/восстановление): считаем по ПРЕДЫДУЩЕМУ _visualState (он
        //  ещё старый — обновляется в конце метода). Ведёт wind-up дуги и error-contract — оба
        //  приходят с animate:false, поэтому «свежесть» перехода нельзя брать из motion.
        var enteringConnecting = state == ConnectVisualState.Connecting && _visualState != ConnectVisualState.Connecting;
        var enteringError = state == ConnectVisualState.Error && _visualState != ConnectVisualState.Error;

        switch (state)
        {
            case ConnectVisualState.Connecting:
                ShieldOutline.Fill = AccentBrush;
                ShieldOutline.Opacity = 1;
                ShieldFilled.Opacity = 0;
                StatusText.Text = L.T("Status_Connecting");
                StatusText.Foreground = AccentBrush;
                ServerInfo.IsVisible = true;
                //  Разгон из покоя только на СВЕЖЕМ входе; re-apply уже в connecting → ровный спин.
                SetArc(true, windUp: enteringConnecting);
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
                SetGlow(connecting: false, connected: true);
                //  Payoff — одноразовый; играется ТОЛЬКО на живом переходе (motion) и на экране: дуга
                //  растворяется (не моргает), диск «приземляется» bloom-ом, двойной пинг сонара. На
                //  восстановлении/rebind (animate:false) — прыжок в конечный вид без повтора payoff.
                if (motion && !MotionSuppressed)
                {
                    DissolveArc();
                    PlaySonar();
                    PlayConnectBloom();
                }
                else
                {
                    SetArc(false);
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
                //  Один спокойный «вдав» при входе (P1-3) — тихое подтверждение обрыва, НЕ shake/bounce.
                if (enteringError && !ReducedMotion && !MotionSuppressed)
                {
                    PlayErrorContract();
                }

                UpSpeed.Text = "0 KB/s";
                DownSpeed.Text = "0 KB/s";
                Uptime.Text = "00:00:00";
                break;

            default: // Idle
                //  Если указатель всё ещё над диском (in-place возврат в Idle / смена темы re-assert'ит
                //  Fill), сохраняем hover-подогрев глифа — иначе он «остыл» бы к серому под курсором.
                ShieldOutline.Fill = _hovering && hasServer ? OnSurfaceBrush : ShieldIdleBrush;
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

        //  Ambient «живой» слой вокруг щита: калм в Idle, чуть ярче/крупнее в Connected (поверх
        //  статичного glow), выключен в Connecting (там уже своё движение) и Error (статичен).
        //  Внутри gated по lite/reduced-motion, паузе окна и онбордингу — см. SetAmbient.
        SetAmbient(state);

        //  Подсказка-ретрай видна ТОЛЬКО в Error — тихая аффорданс «тапни щит, чтобы повторить».
        //  На свежем входе в Error плавно проявляем (fade 0→1); иначе (lite/suppressed/re-apply) —
        //  мгновенно (переход 0мс в lite). Вне Error — скрываем и сбрасываем Opacity для след. входа.
        if (state == ConnectVisualState.Error)
        {
            RetryHint.IsVisible = true;
            if (enteringError && !ReducedMotion && !MotionSuppressed)
            {
                RetryHint.Opacity = 0;
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (_visualState == ConnectVisualState.Error)
                        {
                            RetryHint.Opacity = 1;
                        }
                    },
                    DispatcherPriority.Background);
            }
            else
            {
                RetryHint.Opacity = 1;
            }
        }
        else
        {
            RetryHint.IsVisible = false;
            RetryHint.Opacity = 0;
        }

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

        //  Reactive theme (Bug 1): the idle status foreground / shield / glyph tints are SNAPSHOT
        //  IBrush-и, разрешаемые из тема-токенов (Brush.OnSurface / OnSurfaceVariant / Accent / Red)
        //  под ТЕКУЩИЙ ActualThemeVariant. Приложение стартует Dark, а сохранённая Light/mono-тема
        //  применяется ПОСЛЕ первого SetConnectState(Idle) в ctor — поэтому idle-подпись «Выберите
        //  сервер» захватывала почти-белую Dark-кисть и была невидима на светлой/ч-б теме. Пере-
        //  применяем текущее состояние на смену темы (animate:false = прыжок в конечный вид, без
        //  повторного сонара) — это заново разрешает OnSurfaceBrush/ShieldIdleBrush/AccentBrush/
        //  ErrorBrush через их геттеры, и подпись + щит + глиф становятся корректны для новой темы.
        ActualThemeVariantChanged += OnThemeVariantChanged;
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
        ActualThemeVariantChanged -= OnThemeVariantChanged;

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
            ConnectingArc.Classes.Remove("arc-windup");
            GlowHalo.Classes.Remove("breathing");
            ShieldOutline.Classes.Remove("shieldbreathe");
            HideSonar();
            RemoveAmbientLoops(); // ambient «живой» слой — не тикаем компоновщик за экраном
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

    // ── Layout deactivation: stop this hero's infinite loops when its layout goes inactive ─────────
    //  In the keep-alive shell only the ACTIVE-layout hero holds the live VM; the other sits at
    //  Opacity=0 (which does NOT halt Style animations) and is NEVER DetachedFromVisualTree, so its
    //  loops would otherwise tick the compositor forever off-screen — a real CPU/RAM regression.
    //  HomeHeroPresenter calls Deactivate() when it unbinds (host nulls the inactive DataContext) and
    //  Activate() when it (re)binds. Deactivate strips every loop immediately (same teardown as the
    //  window-hidden pause); Activate just clears the gate — the presenter's subsequent SetConnectState
    //  re-attaches whatever the current state needs. Idempotent.
    public void Deactivate()
    {
        if (_deactivated)
        {
            return;
        }
        _deactivated = true;
        ConnectingArc.Classes.Remove("spinning");
        ConnectingArc.Classes.Remove("arc-windup");
        GlowHalo.Classes.Remove("breathing");
        ShieldOutline.Classes.Remove("shieldbreathe");
        HideSonar();
        RemoveAmbientLoops();
        ClearHover();
    }

    public void Activate()
    {
        //  Just lift the gate; the presenter re-applies the current state (animate:false) right after,
        //  which re-attaches the loops this hero should be running now that it is the active layout.
        _deactivated = false;
    }

    private void OnMotionStateChanged(object? sender, bool lite) => ApplyLiteMode(lite, reapply: true);

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        SetConnectState(_visualState, hasServer: _hasServer, animate: false);

    //  Тема сменилась (Dark ↔ Light ↔ mono) → пере-применяем текущее состояние, чтобы snapshot-кисти
    //  подписи/щита/глифа (OnSurfaceBrush / ShieldIdleBrush / AccentBrush / ErrorBrush) разрешились
    //  заново под новый ActualThemeVariant. animate:false = прыжок в конечный вид (без петель/сонара).
    private void OnThemeVariantChanged(object? sender, EventArgs e) =>
        SetConnectState(_visualState, hasServer: _hasServer, animate: false);

    private void ApplyLiteMode(bool lite, bool reapply)
    {
        //  Система тоже может требовать reduced-motion — тогда lite=off всё равно оставляет движение
        //  выключенным (как в ctor).
        ReducedMotion = lite || !SystemAnimationsEnabled();

        //  Task 2: показатели скорости/аптайма над кнопкой пропадают в lite.
        StatsRow.IsVisible = !ReducedMotion;

        //  Hover/retry-переходы мгновенны в lite (0мс) — «swap без моушена».
        ApplyMotionGatedDurations();

        if (!reapply)
        {
            return;
        }

        //  Рантайм-переключение: убиваем любую идущую петлю анимаций и прыгаем в текущий конечный вид.
        //  (При выключении lite SetConnectState заново навесит нужные петли, т.к. ReducedMotion=false.)
        ConnectingArc.Classes.Remove("spinning");
        ConnectingArc.Classes.Remove("arc-windup");
        GlowHalo.Classes.Remove("breathing");
        ShieldOutline.Classes.Remove("shieldbreathe");
        HideSonar();
        RemoveAmbientLoops();
        ClearHover();
        HeroFrame.Classes.Remove("assembling");
        EnsureHeroVisible();
        SetConnectState(_visualState, hasServer: _hasServer, animate: false);
    }

    //  Длительности hover/retry-переходов по режиму движения: в lite/reduced-motion — 0мс (мгновенный
    //  swap без «моушена»), иначе штатные 120/220мс. Зовём из ctor и на смену режима (ApplyLiteMode).
    private void ApplyMotionGatedDurations()
    {
        var hover = ReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(HoverMs);
        _discSurface.Duration = hover;
        _ringHoverOpacity.Duration = hover;
        _retryHintOpacity.Duration = ReducedMotion ? TimeSpan.Zero : TimeSpan.FromMilliseconds(220);
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
        // ТОЛЬКО левая кнопка. Диск — обычный Border с сырыми Pointer-событиями, а не Button, поэтому
        // фильтра кнопок «бесплатно» тут нет: правый клик (за которым пользователь идёт в контекстное
        // меню, которого у диска нет) или средний клик поднимали и рвали туннель. Все остальные
        // pointer-контролы приложения этот фильтр уже делают (ServerListView, AccountView, MainWindow).
        _pressing = e.GetCurrentPoint(ConnectDisc).Properties.IsLeftButtonPressed;
        if (!_pressing || ReducedMotion)
        {
            return;
        }

        _discScaleX.Duration = _discScaleY.Duration = TimeSpan.FromMilliseconds(90);
        _discScaleX.Easing = _discScaleY.Easing = EaseOutQuart;
        _discScale.ScaleX = _discScale.ScaleY = 0.94;

        //  Глубина нажатия (P1-2): скрим-«лунка» темнеет 0→0.12 и глиф чуть глубже диска (→0.97),
        //  в унисон с press-scale (90мс OutQuart). БЕЗ ripple/glow — только физический «вдав».
        _scrimOpacity.Duration = _glyphScaleX.Duration = _glyphScaleY.Duration = TimeSpan.FromMilliseconds(90);
        _scrimOpacity.Easing = _glyphScaleX.Easing = _glyphScaleY.Easing = EaseOutQuart;
        PressScrim.Opacity = 0.12;
        _glyphScale.ScaleX = _glyphScale.ScaleY = 0.97;
    }

    private void OnDiscPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ReleaseDiscScale();
        if (_pressing && e.InitialPressMouseButton == MouseButton.Left)
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

        //  Отпускание: скрим гаснет и глиф возвращается вместе с диском (160мс OutQuint).
        _scrimOpacity.Duration = _glyphScaleX.Duration = _glyphScaleY.Duration = TimeSpan.FromMilliseconds(160);
        _scrimOpacity.Easing = _glyphScaleX.Easing = _glyphScaleY.Easing = EaseOutQuint;
        PressScrim.Opacity = 0;
        _glyphScale.ScaleX = _glyphScale.ScaleY = 1.0;
    }

    // ── Hover диска (P0-1): surface-lift + ring-brighten + (в Idle) glyph-warm ──────────────
    //  Desktop-герой обязан отвечать на указатель. Отклик — БЕЗ scale и БЕЗ glow (glow зарезервирован
    //  под connected-payoff): диск поднимается на шаг поверхности (Brush.SurfaceHigh→Highest через
    //  класс .hover, тема-реактивно), внешнее кольцо ярче (накладной RingHoverGlow), а в Idle глиф
    //  теплеет к чернилам (OnSurfaceVariant→OnSurface — НЕ к акценту). Переходы 120мс (0мс в lite).
    private void OnDiscPointerEntered(object? sender, PointerEventArgs e)
    {
        _hovering = true;
        ConnectDisc.Classes.Add("hover");
        RingHoverGlow.Opacity = 0.5;

        //  Glyph-warm имеет смысл только в Idle (в остальных состояниях Fill щита ведёт state-машина
        //  и re-assert перекроет hover); при отсутствии сервера не «приглашаем» (щит приглушён).
        if (_visualState == ConnectVisualState.Idle && _hasServer)
        {
            ShieldOutline.Fill = OnSurfaceBrush;
        }
    }

    private void OnDiscHoverExit(object? sender, PointerEventArgs e) => ClearHover();

    //  Снять hover-вид. Идемпотентно (PointerExited приходит и как отмена press, и как выход hover;
    //  зовётся также из teardown-ов раскладки/lite). Glyph восстанавливаем ТОЛЬКО если всё ещё Idle —
    //  иначе Fill уже выставлен верной state-веткой.
    private void ClearHover()
    {
        if (!_hovering)
        {
            return;
        }

        _hovering = false;
        ConnectDisc.Classes.Remove("hover");
        RingHoverGlow.Opacity = 0;
        if (_visualState == ConnectVisualState.Idle)
        {
            ShieldOutline.Fill = ShieldIdleBrush;
        }
    }

    // ── Дуга / glow / сонар ───────────────────────────────────────────────────────────
    private void SetArc(bool on, bool windUp = false)
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
        if (on && !ReducedMotion && !MotionSuppressed)
        {
            //  Вход в connecting с движения = разгон из покоя (P0-3); re-apply/восстановление уже
            //  в connecting = сразу ровный спин (без повторного wind-up).
            if (windUp)
            {
                StartArcWindup();
            }
            else
            {
                ConnectingArc.Classes.Remove("arc-windup");
                ConnectingArc.Opacity = 1;
                ConnectingArc.Classes.Add("spinning");
            }
        }
        else
        {
            //  Off / suppressed / lite: снимаем обе фазы, сбрасываем Opacity (статик-видимая дуга под
            //  паузой = полностью непрозрачна; следующий wind-up сам стартует с 0).
            ConnectingArc.Classes.Remove("spinning");
            ConnectingArc.Classes.Remove("arc-windup");
            ConnectingArc.Opacity = 1;
        }
    }

    //  Wind-up (P0-3): дуга «набегает» из покоя — Opacity 0→1 (200мс OutQuint) + одноразовый разгон
    //  вращения 0→360° (200мс OutQuint, .arc-windup), затем хэндофф на ровный .spinning. Угол 360°≡0°
    //  → стык бесшовный. Всё в рамках !ReducedMotion && !MotionSuppressed (гарантировано вызывающим).
    private void StartArcWindup()
    {
        ConnectingArc.Classes.Remove("spinning");
        ConnectingArc.Classes.Remove("arc-windup");
        _arcOpacity.Duration = TimeSpan.FromMilliseconds(200);
        _arcOpacity.Easing = EaseOutQuint;
        ConnectingArc.Opacity = 0;

        //  Стартуем fade+ramp на следующем цикле (переход ловит 0→1), затем через 200мс хэндофф в спин.
        //  Стартуем fade+ramp на следующем цикле (переход ловит 0→1). Таймер хэндоффа в спин ставим
        //  ВНУТРИ этого Post — так его 200мс отсчитываются ОТ старта wind-up-анимации, а не от вызова
        //  метода. Иначе таймер опережал бы анимацию на ~1 кадр и на стыке был микро-откат угла
        //  (wind-up ещё ~356°, а spin стартует с 0° → заметный дёрг). Плюс это устраняет гонку очереди:
        //  RunOnce теперь ставится только ПОСЛЕ того, как Post реально навесил arc-windup.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_visualState != ConnectVisualState.Connecting || ReducedMotion || MotionSuppressed || !ConnectingArc.IsVisible)
                {
                    return;
                }

                ConnectingArc.Opacity = 1;
                ConnectingArc.Classes.Add("arc-windup");

                DispatcherTimer.RunOnce(
                    () =>
                    {
                        ConnectingArc.Classes.Remove("arc-windup");
                        if (_visualState != ConnectVisualState.Connecting || ReducedMotion || MotionSuppressed || !ConnectingArc.IsVisible)
                        {
                            return;
                        }

                        ConnectingArc.Opacity = 1;
                        ConnectingArc.Classes.Add("spinning");
                    },
                    TimeSpan.FromMilliseconds(200));
            },
            DispatcherPriority.Background);
    }

    //  Arc dissolve (P0-2): на Connecting→Connected НЕ гасим дугу мгновенно (IsVisible=false на кадре
    //  payoff = моргание), а растворяем Opacity 1→0 (220мс Standard) ОДНОВРЕМЕННО с сонаром — дуга
    //  «стекает» в glow, продолжая вращаться. IsVisible=false выставляем ПОСЛЕ фейда. Зовётся только на
    //  живой connect (motion && !MotionSuppressed).
    private void DissolveArc()
    {
        if (!ConnectingArc.IsVisible)
        {
            return;
        }

        _arcOpacity.Duration = TimeSpan.FromMilliseconds(220);
        _arcOpacity.Easing = EaseStandard;
        ConnectingArc.Opacity = 0; // .spinning/.arc-windup остаётся → растворяется в движении

        DispatcherTimer.RunOnce(
            () =>
            {
                //  Финализируем только если всё ещё connected (не откатились обратно в connecting).
                if (_visualState != ConnectVisualState.Connected)
                {
                    return;
                }

                ConnectingArc.Classes.Remove("spinning");
                ConnectingArc.Classes.Remove("arc-windup");
                ConnectingArc.IsVisible = false;
                ConnectingArc.Opacity = 1; // сброс для следующего connect
            },
            TimeSpan.FromMilliseconds(220));
    }

    //  Connecting-«дыхание» щита: спокойный вторичный сигнал в унисон с glow-breathe (те же 850мс
    //  sine). Только OPACITY (1↔0.8) на контур-щите — БЕЗ transform, поэтому центрировать нечего и
    //  «улететь» физически нельзя. Reduced-motion/lite: класс не вешаем → щит статичен (дуга/сигнал
    //  подключения остаются читаемыми и без движения).
    private void SetShieldPulse(bool on)
    {
        if (on && !ReducedMotion && !MotionSuppressed)
        {
            ShieldOutline.Classes.Add("shieldbreathe");
        }
        else
        {
            ShieldOutline.Classes.Remove("shieldbreathe");
        }
    }

    // ── Ambient «живой» слой (idle + connected): дышащее кольцо + покойная сонар-волна ──────
    //  Медленные (5-6с) низкоконтрастные петли вокруг щита, чтобы герой «дышал» и в покое, и в
    //  подключении. Присутствует ТОЛЬКО в Idle и Connected (Connecting уже ведёт дугу+breathe-glow,
    //  Error статичен). Тот же guarded-паттерн, что SetArc/SetGlow/SetShieldPulse: сперва снимаем
    //  любую идущую петлю (никогда не стекаем), затем навешиваем нужную — и только если движение
    //  разрешено (не lite/reduced-motion), окно не на паузе (скрыто/свёрнуто) и не онбординг.
    private void SetAmbient(ConnectVisualState state)
    {
        //  Всегда снимаем ПЕРЕД условным добавлением → петли не стекают и не утекают.
        RemoveAmbientLoops();

        var idle = state == ConnectVisualState.Idle;
        var live = state == ConnectVisualState.Connected;
        //  P0-4: в Idle без активного сервера приглашать нечего → ambient OFF (не тикаем компоновщик,
        //  когда подключаться не к чему). В Connected сервер очевидно есть, поэтому (live || _hasServer).
        var show = (idle || live) && !ReducedMotion && !_empty && (live || _hasServer);

        AmbientRing.IsVisible = show;
        AmbientSonar.IsVisible = show;

        //  Пауза окна: держим слой без петли (base Opacity=0 → невидим); вернётся на восстановлении
        //  через re-apply (UpdateVisibilityPause → SetConnectState → сюда с _animationsPaused=false).
        if (!show || MotionSuppressed)
        {
            return;
        }

        if (idle)
        {
            //  Калм: медленный вдох 6с + волна 6.5с, не в фазе → «живое», а не «занятое».
            AmbientRing.Classes.Add("breathe-idle");
            AmbientSonar.Classes.Add("rest-idle");
        }
        else
        {
            //  Connected: чуть ярче/крупнее/быстрее (5 / 5.5с) поверх статичного glow = «активно».
            AmbientRing.Classes.Add("breathe-live");
            AmbientSonar.Classes.Add("rest-live");
        }
    }

    //  Снять все ambient-петли (обе фазы обоих слоёв). Идемпотентно — из SetAmbient, паузы окна,
    //  lite-тумблера и онбординга. Классы сняты → свойства ревертят к базе (Opacity=0, scale=1).
    private void RemoveAmbientLoops()
    {
        AmbientRing.Classes.Remove("breathe-idle");
        AmbientRing.Classes.Remove("breathe-live");
        AmbientSonar.Classes.Remove("rest-idle");
        AmbientSonar.Classes.Remove("rest-live");
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
            //  Пауза (окно скрыто ИЛИ раскладка неактивна): держим статичный halo без петли — она
            //  вернётся на восстановлении/реактивации.
            if (!MotionSuppressed)
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
        //  Осевший ДВОЙНОЙ пинг (≤2 кольца): ведущее 1.0→1.6 + alpha 1→0 (600мс quint), затем тихое
        //  эхо (старт α 0.5, 1→1.5) с задержкой ~120мс → «залочено», не радар-петля. Классы снимаем и
        //  вешаем на следующем цикле диспетчера, чтобы одноразовые анимации чисто перезапускались.
        SonarPulse.Classes.Remove("pulsing");
        SonarPulseEcho.Classes.Remove("pulsing-echo");
        SonarPulse.IsVisible = true;
        SonarPulseEcho.IsVisible = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_visualState == ConnectVisualState.Connected)
                {
                    SonarPulse.Classes.Add("pulsing");
                }
            },
            DispatcherPriority.Background);

        //  Эхо +120мс. Re-guard: если за это время ушли из connected или окно скрылось — не запускаем
        //  (не тикаем компоновщик за экраном одноразовым эхо).
        DispatcherTimer.RunOnce(
            () =>
            {
                if (_visualState == ConnectVisualState.Connected && !MotionSuppressed)
                {
                    SonarPulseEcho.Classes.Add("pulsing-echo");
                }
            },
            TimeSpan.FromMilliseconds(120));
    }

    private void HideSonar()
    {
        SonarPulse.Classes.Remove("pulsing");
        SonarPulseEcho.Classes.Remove("pulsing-echo");
        SonarPulse.IsVisible = false;
        SonarPulseEcho.IsVisible = false;
    }

    //  Connect-bloom (P1-1): диск «приземляется» — 1.0→1.04 (180мс) → 1.04→1.0 (260мс), ОБЕ ноги
    //  OutQuint. Это оседание, НЕ bounce: пик ≤1.04, ниже покоя НЕ уходит, кривой elastic нет. Реюзает
    //  тот же self-centering _discScale, поэтому центр гарантирован. Второй leg (заканчивается ~440мс)
    //  оседает уже поверх полного glow (reveal 300мс) — в бюджете Emphasis (600мс).
    private void PlayConnectBloom() => PlayDiscSettle(1.04, 180, 260);

    //  Error-contract (P1-3): один спокойный «вдав» 1.0→0.98→1.0 (150+150мс OutQuint) — тихое
    //  подтверждение «связь сорвалась», без shake/bounce. Пик 0.98, без осцилляции.
    private void PlayErrorContract() => PlayDiscSettle(0.98, 150, 150);

    //  Двух-ногое оседание диска через press-scale переходы. Вызывающий уже проверил движение и не-
    //  suppressed; здесь ещё раз страхуемся от ReducedMotion. Если во время оседания начали press —
    //  прекращаем (press-scale перехватит), чтобы не бороться за _discScale.
    private async void PlayDiscSettle(double peak, int leg1Ms, int leg2Ms)
    {
        if (ReducedMotion || _pressing)
        {
            return;
        }

        try
        {
            _discScaleX.Easing = _discScaleY.Easing = EaseOutQuint;
            _discScaleX.Duration = _discScaleY.Duration = TimeSpan.FromMilliseconds(leg1Ms);
            _discScale.ScaleX = _discScale.ScaleY = peak;

            await Task.Delay(leg1Ms);
            if (_pressing)
            {
                return; // press взял управление диском — не дёргаем обратно
            }

            _discScaleX.Duration = _discScaleY.Duration = TimeSpan.FromMilliseconds(leg2Ms);
            _discScale.ScaleX = _discScale.ScaleY = 1.0;
        }
        catch
        {
            //  Оседание — чистая косметика; любая гонка/отсоединение не должны падать в connect-путь.
        }
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
