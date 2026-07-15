namespace v2rayN.Desktop.Views;

/// <summary>
/// Connect-щит (герой) + стата-строка — правая панель «Главной». Перенос 1:1 из Android
/// (activity_main.xml hero + MainActivity applyRunningState/applyConnectedState/applyIdleState).
///
/// Разметка чисто визуальная; три состояния подключения (idle/connecting/connected) и
/// переключение «есть подписка ↔ нет подписки» задаёт публичный API ниже. Источники данных
/// (мастер-план §1.b): состояние подключения ← running-состояние StatusBarViewModel;
/// скорости/аптайм ← статистика; идентичность сервера ← выбранный ProfileItem. Будущий
/// HomeViewModel вызывает эти методы/подписывается на события — трогать XAML не нужно.
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

    private bool _pressing;

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

        //  Диск — кнопка connect: press-scale (класс .pressed) + клик = переключение.
        ConnectDisc.PointerPressed += OnDiscPointerPressed;
        ConnectDisc.PointerReleased += OnDiscPointerReleased;
        ConnectDisc.PointerCaptureLost += OnDiscPressCancel;
        ConnectDisc.PointerExited += OnDiscPressCancel;

        AddButton.Click += (_, _) => AddRequested?.Invoke(this, EventArgs.Empty);
        AddQrButton.Click += (_, _) => AddByQrRequested?.Invoke(this, EventArgs.Empty);
        AddClipboardButton.Click += (_, _) => AddFromClipboardRequested?.Invoke(this, EventArgs.Empty);

        //  Дизайн-тайм / первый рендер: idle с выбранным сервером (образец идентичности виден).
        SetConnectState(ConnectVisualState.Idle, hasServer: true, animate: false);
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
    /// <paramref name="animate"/> = живой переход (играет сонар-подтверждение).
    /// </summary>
    public void SetConnectState(ConnectVisualState state, bool hasServer = true, bool animate = false)
    {
        var motion = animate && !ReducedMotion;

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
                ShieldOutline.Opacity = 0;
                ShieldFilled.Opacity = 1;
                StatusText.Text = "Прокси подключён";
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

    private void OnDiscPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressing = true;
        ConnectDisc.Classes.Add("pressed");
    }

    private void OnDiscPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ConnectDisc.Classes.Remove("pressed");
        if (_pressing)
        {
            _pressing = false;
            ConnectToggleRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnDiscPressCancel(object? sender, EventArgs e)
    {
        _pressing = false;
        ConnectDisc.Classes.Remove("pressed");
    }

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
        if (connecting && !ReducedMotion)
        {
            GlowHalo.IsVisible = true;
            GlowHalo.Classes.Add("breathing");
        }
        else if (connected)
        {
            GlowHalo.Classes.Remove("breathing");
            GlowHalo.IsVisible = true;
            GlowHalo.Opacity = 1;
        }
        else
        {
            GlowHalo.Classes.Remove("breathing");
            GlowHalo.Opacity = 0;
            GlowHalo.IsVisible = false;
        }
    }

    private void PlaySonar()
    {
        //  Перезапуск одноразовой анимации: снять класс, показать, снова навесить.
        SonarPulse.Classes.Remove("pulsing");
        SonarPulse.IsVisible = true;
        SonarPulse.Classes.Add("pulsing");
    }

    private void HideSonar()
    {
        SonarPulse.Classes.Remove("pulsing");
        SonarPulse.IsVisible = false;
    }
}
