using System.Reactive.Disposables;
using Avalonia.Controls.Notifications;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

public partial class MainWindow : WindowBase<MainWindowViewModel>
{
    private static Config _config;
    private readonly WindowNotificationManager? _manager;
    private bool _blCloseByUser = false;

    // Вкладки. «Главная» подключена к реальному движку (HomeViewModel); список серверов живёт
    // в её левой колонке — отдельной вкладки «Сервера» в рейле нет.
    private readonly Control _homeView = new HomeView();
    private readonly Control _settingsView = new SettingsView();
    private readonly Control _accountView = new AccountView();
    private readonly Button[] _navButtons;
    private HomeViewModel? _homeViewModel;

    // Индикатор подключения в рейле: серый (idle) ↔ синий (connected).
    private static readonly IBrush _dotOff = new SolidColorBrush(Color.Parse("#9BA1AD"));
    private static readonly IBrush _dotOn = new SolidColorBrush(Color.Parse("#4C8DFF"));

    public MainWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;
        _manager = new WindowNotificationManager(TopLevel.GetTopLevel(this)) { MaxItems = 3, Position = NotificationPosition.TopRight };

        KeyDown += MainWindow_KeyDown;

        // Chrome окна: drag + системные кнопки.
        titleBar.PointerPressed += TitleBar_PointerPressed;
        btnMin.Click += (_, _) => WindowState = WindowState.Minimized;
        btnMax.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        btnClose.Click += (_, _) => Close();
        btnTray.Click += (_, _) => ShowHideWindow(false);

        // Нав-рейл: переключение вкладок (Главная · Настройки · Аккаунт).
        _navButtons = [navHome, navSettings, navAccount];
        navHome.Click += (_, _) => SelectTab(navHome, _homeView);
        navSettings.Click += (_, _) => SelectTab(navSettings, _settingsView);
        navAccount.Click += (_, _) => SelectTab(navAccount, _accountView);
        SelectTab(navHome, _homeView);
        // DEV screenshot hook: INITIAL_TAB=settings|account opens that tab on launch.
        switch (Environment.GetEnvironmentVariable("INITIAL_TAB"))
        {
            case "settings": SelectTab(navSettings, _settingsView); break;
            case "account": SelectTab(navAccount, _accountView); break;
        }

        // Питаем «Главную» реальным HomeViewModel, как только доступен корневой ViewModel
        // (App присваивает его сразу после Build, до показа окна — DataContext успевает встать
        // раньше активации HomeView). Здесь же биндим индикатор подключения в рейле.
        this.WhenAnyValue(x => x.ViewModel)
            .Where(vm => vm != null)
            .Take(1)
            .Subscribe(vm => SetupHome(vm!));

        this.WhenActivated(disposables =>
        {
            // Питаем скрытый StatusBarView (сохраняем интеракции/иконку трея/StatusBarViewModel).
            this.OneWayBind(ViewModel, vm => vm.StatusBarViewModel, v => v.contentStatusBarView.Content).DisposeWith(disposables);

            ViewModel.ReadTextFromClipboardInteraction.RegisterHandler(async interaction =>
            {
                var result = await AvaUtils.GetClipboardData(this);
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.ScanScreenInteraction.RegisterHandler(async interaction =>
            {
                ShowHideWindow(false);
                await Task.Delay(200);
                var result = QRCodeAvaloniaUtils.CaptureScreen();
                ShowHideWindow(true);
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.BrowseImageFileInteraction.RegisterHandler(async interaction =>
            {
                var result = await UI.OpenFileDialog(null);
                interaction.SetOutput(result);
            }).DisposeWith(disposables);

            ViewModel.ShowHideWindowInteraction.RegisterHandler(interaction =>
            {
                ShowHideWindow(interaction.Input);
                interaction.SetOutput(Unit.Default);
            }).DisposeWith(disposables);

            AppEvents.SendSnackMsgRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(async content => await DelegateSnackMsg(content))
              .DisposeWith(disposables);

            AppEvents.AppExitRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => StorageUI())
              .DisposeWith(disposables);

            AppEvents.ShutdownRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(Shutdown)
              .DisposeWith(disposables);
        });

        if (Utils.IsWindows() && !Design.IsDesignMode)
        {
            ThreadPool.RegisterWaitForSingleObject(Program.ProgramStarted, OnProgramStarted, null, -1, false);
            HotkeyManager.Instance.Init(_config, OnHotkeyHandler);
        }

        if (_config.UiItem.AutoHideStartup && Utils.IsWindows())
        {
            WindowState = WindowState.Minimized;
        }
    }

    #region Nav & Chrome

    private void SelectTab(Button active, Control view)
    {
        foreach (var b in _navButtons)
        {
            b.Classes.Remove("active");
        }
        active.Classes.Add("active");
        contentHost.Content = view;
    }

    // Создаёт HomeViewModel поверх реального движка (ProfilesViewModel + StatusBarViewModel из
    // MainWindowViewModel) и отдаёт его «Главной». Индикатор рейла следует за IsConnected.
    private void SetupHome(MainWindowViewModel vm)
    {
        _homeViewModel = new HomeViewModel(vm);
        _homeView.DataContext = _homeViewModel;
        onboardingView.DataContext = _homeViewModel;

        _homeViewModel.WhenAnyValue(x => x.IsConnected)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(connected => railStatusDot.Fill = connected ? _dotOn : _dotOff);

        // Пустой старт (нет подписок): показываем ТОЛЬКО онбординг на всю ширину под chrome —
        // рейл и контент скрыты. После добавления подписки (IsEmpty=false) — обычный шелл.
        _homeViewModel.WhenAnyValue(x => x.IsEmpty)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(empty =>
            {
                onboardingView.IsVisible = empty;
                shellRoot.IsVisible = !empty;
            });
    }

    // Онбординг «Войти через Telegram/сайт» пока ведёт на вкладку «Аккаунт»; реальный вход
    // подключит агент AccountViewModel (Ф-D6/D7).
    public void SelectAccountTab() => SelectTab(navAccount, _accountView);

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    #endregion Nav & Chrome

    #region Event

    private void OnProgramStarted(object state, bool timeout)
    {
        Dispatcher.UIThread.Post(() =>
                ShowHideWindow(true),
            DispatcherPriority.Default);
    }

    private async Task DelegateSnackMsg(string content)
    {
        _manager?.Show(new Avalonia.Controls.Notifications.Notification(null, content, NotificationType.Information));
        await Task.CompletedTask;
    }

    private void OnHotkeyHandler(EGlobalHotkey e)
    {
        switch (e)
        {
            case EGlobalHotkey.ShowForm:
                Dispatcher.UIThread.Post(() => ShowHideWindow(null));
                break;

            case EGlobalHotkey.SystemProxyClear:
            case EGlobalHotkey.SystemProxySet:
            case EGlobalHotkey.SystemProxyUnchanged:
            case EGlobalHotkey.SystemProxyPac:
                AppEvents.SysProxyChangeRequested.Publish((ESysProxyType)((int)e - 1));
                break;
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_blCloseByUser)
        {
            return;
        }

        Logging.SaveLog("OnClosing -> " + e.CloseReason.ToString());

        switch (e.CloseReason)
        {
            case WindowCloseReason.OwnerWindowClosing or WindowCloseReason.WindowClosing:
                e.Cancel = true;
                ShowHideWindow(false);
                break;

            case WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown:
                await AppManager.Instance.AppExitAsync(false);
                break;
        }

        base.OnClosing(e);
    }

    private async void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
        {
            switch (e.Key)
            {
                case Key.V:
                    await AddServerViaClipboardAsync();
                    break;

                case Key.S:
                    await ScanScreenTaskAsync();
                    break;
            }
        }
        else
        {
            if (e.Key == Key.F5)
            {
                ViewModel?.Reload();
            }
        }
    }

    public async Task AddServerViaClipboardAsync()
    {
        var clipboardData = await AvaUtils.GetClipboardData(this);
        if (clipboardData.IsNotEmpty() && ViewModel != null)
        {
            await ViewModel.AddServerViaClipboardAsync(clipboardData);
        }
    }

    public async Task ScanScreenTaskAsync()
    {
        ShowHideWindow(false);

        await Task.Delay(200);

        var bytes = QRCodeAvaloniaUtils.CaptureScreen();
        if (bytes != null && ViewModel != null)
        {
            await ViewModel.ScanScreenResult(bytes);
        }

        ShowHideWindow(true);
    }

    private void Shutdown(bool obj)
    {
        if (obj is bool b && _blCloseByUser == false)
        {
            _blCloseByUser = b;
        }
        StorageUI();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            HotkeyManager.Instance.Dispose();
            desktop.Shutdown();
        }
    }

    #endregion Event

    #region UI

    public void ShowHideWindow(bool? blShow)
    {
        var bl = blShow ??
                    (Utils.IsLinux() || Utils.IsMacOS()
                    ? (!AppManager.Instance.ShowInTaskbar ^ (WindowState == WindowState.Minimized))
                    : !AppManager.Instance.ShowInTaskbar);
        if (bl)
        {
            Show();
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            Focus();
        }
        else
        {
            if (Utils.IsLinux() && _config.UiItem.Hide2TrayWhenClose == false)
            {
                WindowState = WindowState.Minimized;
                return;
            }

            foreach (var ownedWindow in OwnedWindows)
            {
                ownedWindow.Close();
            }
            Hide();
        }

        AppManager.Instance.ShowInTaskbar = bl;
    }

    protected override void OnLoaded(object? sender, RoutedEventArgs e)
    {
        base.OnLoaded(sender, e);
        if (_config.UiItem.AutoHideStartup)
        {
            ShowHideWindow(false);
        }
    }

    private void StorageUI()
    {
        ConfigHandler.SaveWindowSizeItem(_config, GetType().Name, Width, Height);
    }

    #endregion UI
}
