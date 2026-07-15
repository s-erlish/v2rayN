using System.Runtime.InteropServices;
using Avalonia.Animation;
using Avalonia.Animation.Easings;

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
    }

    //  Кэш-кисти повторяют токены Incy (Brush.OnSurfaceVariant / Brush.Accent / Brush.OnSurface),
    //  чтобы смена состояния не зависела от рантайм-поиска ресурсов. Тема одна (тёмная).
    private static readonly IBrush ShieldGray = new SolidColorBrush(Color.Parse("#9BA1AD"));
    private static readonly IBrush AccentBlue = new SolidColorBrush(Color.Parse("#4C8DFF"));
    private static readonly IBrush OnSurface = new SolidColorBrush(Color.Parse("#F2F4F8"));

    //  Кривые зеркалят токены GlobalResources Ease.* 1:1 (ease_out_quart/_quint/_standard) —
    //  для императивных частей (press, перекл. длительностей). Декларативный XAML берёт токены.
    private static readonly Easing EaseOutQuart = new SplineEasing(0.25, 1, 0.5, 1);
    private static readonly Easing EaseOutQuint = new SplineEasing(0.22, 1, 0.36, 1);
    private static readonly Easing EaseStandard = new SplineEasing(0.2, 0, 0, 1);

    //  Cold-start «сборка» героя проигрывается ОДИН раз за процесс (Android shield_assemble).
    private static bool _assembled;

    private bool _pressing;
    private ConnectVisualState _visualState = ConnectVisualState.Idle;

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

        //  Держим ссылки на декларативные переходы, чтобы гнуть их темп по направлению.
        //  Порядок соответствует XAML: ShieldOutline[0]=Opacity,[1]=Fill; Filled[0]=Opacity; Glow[0]=Opacity.
        _outlineOpacity = ShieldOutline.Transitions?[0] as DoubleTransition;
        _outlineFill = ShieldOutline.Transitions?[1] as BrushTransition;
        _filledOpacity = ShieldFilled.Transitions?[0] as DoubleTransition;
        _glowOpacity = GlowHalo.Transitions?[0] as DoubleTransition;

        //  Свой press-scale вместо общего перехода RenderTransform из GlobalStyles: масштаб —
        //  единственный отклик (без ripple/glow). Перекрываем переходы диска пустыми и ставим ScaleTransform.
        _discScale.Transitions = new Transitions { _discScaleX, _discScaleY };
        ConnectDisc.Transitions = new Transitions();
        ConnectDisc.RenderTransform = _discScale;

        //  Диск — кнопка connect: press-scale + клик = переключение.
        ConnectDisc.PointerPressed += OnDiscPointerPressed;
        ConnectDisc.PointerReleased += OnDiscPointerReleased;
        ConnectDisc.PointerCaptureLost += OnDiscPressCancel;
        ConnectDisc.PointerExited += OnDiscPressCancel;

        AddButton.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);
        AddQrButton.Click += (_, _) => AddByQrRequested?.Invoke(this, EventArgs.Empty);
        AddClipboardButton.Click += (_, _) => AddFromClipboardRequested?.Invoke(this, EventArgs.Empty);

        //  Cold-start «сборка» героя по первому показу окна (гвард статик-флагом). Прячем герой
        //  ДО первого кадра, чтобы не мелькнул в покое перед анимацией «сборки».
        if (!ReducedMotion && !_assembled)
        {
            HeroFrame.Opacity = 0;
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
        var motion = animate && !ReducedMotion;

        //  Idle — «остывающая» цель ⇒ реверс-темп (165/225); connecting/connected — forward (220/300).
        PrepareStateTiming(reverse: state == ConnectVisualState.Idle);

        switch (state)
        {
            case ConnectVisualState.Connecting:
                ShieldOutline.Fill = AccentBlue;
                ShieldOutline.Opacity = 1;
                ShieldFilled.Opacity = 0;
                StatusText.Text = "Подключение…";
                StatusText.Foreground = AccentBlue;
                ServerInfo.IsVisible = true;
                SetArc(true);
                SetGlow(connecting: true, connected: false);
                HideSonar();
                break;

            case ConnectVisualState.Connected:
                ShieldOutline.Fill = AccentBlue;
                ShieldOutline.Opacity = 0;
                ShieldFilled.Opacity = 1;
                StatusText.Text = "Подключено";
                StatusText.Foreground = AccentBlue;
                ServerInfo.IsVisible = true;
                SetArc(false);
                SetGlow(connecting: false, connected: true);
                if (motion)
                {
                    PlaySonar();
                }
                else
                {
                    HideSonar();
                }

                break;

            default: // Idle
                ShieldOutline.Fill = ShieldGray;
                ShieldOutline.Opacity = hasServer ? 1 : 0.38;
                ShieldFilled.Opacity = 0;
                StatusText.Text = hasServer ? "Не подключено" : "Выберите сервер";
                StatusText.Foreground = OnSurface;
                ServerInfo.IsVisible = hasServer;
                SetArc(false);
                SetGlow(connecting: false, connected: false);
                HideSonar();
                UpSpeed.Text = "0 KB/s";
                DownSpeed.Text = "0 KB/s";
                Uptime.Text = "00:00:00";
                break;
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
        ConnectingArc.IsVisible = on;
        if (on && !ReducedMotion)
        {
            ConnectingArc.Classes.Add("spinning");
        }
        else
        {
            ConnectingArc.Classes.Remove("spinning");
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
            GlowHalo.Classes.Add("breathing");
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
            HeroFrame.Opacity = 1; //  сборку пропускаем — гарантируем видимость
            return;
        }

        _assembled = true;
        HeroFrame.Classes.Add("assembling");
        await Task.Delay(460);
        HeroFrame.Opacity = 1; //  вернуть базовую непрозрачность перед снятием forward-fill
        HeroFrame.Classes.Remove("assembling");
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
