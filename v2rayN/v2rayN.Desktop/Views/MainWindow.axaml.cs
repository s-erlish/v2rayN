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
    private readonly AccountView _accountView = new AccountView();
    private readonly Button[] _navButtons;
    private HomeViewModel? _homeViewModel;

    // ОДИН экземпляр AccountViewModel на всё приложение: делится между вкладкой «Аккаунт»
    // (AccountView) и суб-страницей «Вход» (LoginView), поэтому состояние входа распространяется
    // на оба (P0-8). Ctor VM безопасен на этапе инициализации полей (без AppManager).
    private readonly AccountViewModel _accountVm =
        Design.IsDesignMode ? AccountViewModel.CreateDesign() : new AccountViewModel();

    // Стек открытых суб-страниц (Buy/Login/Devices/History) в хосте subPageHost. Back снимает
    // верхнюю; когда стек пуст — хост скрыт и снова видна вкладка/онбординг под ним.
    private readonly List<Control> _subStack = new();

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

        // Общий AccountViewModel на вкладку «Аккаунт» (в рантайме DataContext ставит MainWindow,
        // не сама вью — тот же экземпляр уедет в LoginView). «Управление»-строки и CTA входа
        // поднимают события — здесь они превращаются в открытие суб-страниц.
        _accountView.DataContext = _accountVm;
        _accountView.BuyRequested += (_, _) => OpenBuy();
        _accountView.DevicesRequested += (_, _) => OpenDevices();
        _accountView.HistoryRequested += (_, _) => OpenHistory();
        _accountView.LoginRequested += (_, _) => OpenLogin();

        // P1-3: вкладка «Аккаунт» в рейле видна только когда пользователь вошёл. Начальное
        // значение приходит сразу (WhenAnyValue выдаёт текущее), далее следит за входом/выходом.
        _accountVm.WhenAnyValue(x => x.IsLoggedIn)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(UpdateAccountTabVisibility);
        // DEV screenshot hook: INITIAL_TAB=settings|account opens that tab on launch.
        switch (Environment.GetEnvironmentVariable("INITIAL_TAB"))
        {
            case "settings": SelectTab(navSettings, _settingsView); break;
            case "account": SelectTab(navAccount, _accountView); break;
        }

        // DEV screenshot hook: PREVIEW_VIEW=buy|login|devices|history renders that (still
        // un-wired) sub-page with design-time data into the content area for capture.
        var previewView = Environment.GetEnvironmentVariable("PREVIEW_VIEW");
        if (previewView is not null)
        {
            Control? preview = previewView switch
            {
                "buy" => new BuyView { DataContext = BuyViewModel.CreateDesign() },
                "login" => new LoginView { DataContext = AccountViewModel.CreateDesign() },
                "devices" => new DevicesView { DataContext = DevicesViewModel.CreateDesign() },
                "history" => new PaymentHistoryView { DataContext = PaymentHistoryViewModel.CreateDesign() },
                _ => null
            };
            if (preview is not null)
            {
                onboardingView.IsVisible = false;
                shellRoot.IsVisible = true;
                contentHost.Content = preview;
            }
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

        // Индикатор рейла: серый в покое, синий при подключении И в процессе подключения (P1-3).
        _homeViewModel.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting, (connected, connecting) => connected || connecting)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(active => railStatusDot.Fill = active ? _dotOn : _dotOff);

        // Пустой старт (нет подписок): показываем ТОЛЬКО онбординг на всю ширину под chrome —
        // рейл и контент скрыты. После добавления подписки (IsEmpty=false) — обычный шелл.
        _homeViewModel.WhenAnyValue(x => x.IsEmpty)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(empty =>
            {
                // Не трогаем видимость в режиме превью суб-экрана (DEV screenshot hook).
                if (Environment.GetEnvironmentVariable("PREVIEW_VIEW") is not null) return;
                onboardingView.IsVisible = empty;
                shellRoot.IsVisible = !empty;
            });
    }

    // P1-3: вкладка «Аккаунт» видна только для вошедших. Если её скрыли, пока она была активной
    // (выход из аккаунта), возвращаемся на «Главную».
    private void UpdateAccountTabVisibility(bool loggedIn)
    {
        navAccount.IsVisible = loggedIn;
        if (!loggedIn && navAccount.Classes.Contains("active"))
        {
            SelectTab(navHome, _homeView);
        }
    }

    #region Sub-page host (Buy / Login / Devices / History)

    // Кладёт суб-страницу поверх контента/онбординга и показывает хост.
    private void PushSubPage(Control view)
    {
        _subStack.Add(view);
        subPageHost.Content = view;
        subPageHost.IsVisible = true;
    }

    // Снимает верхнюю суб-страницу: показываем предыдущую из стека либо прячем хост целиком
    // (тогда снова виден шелл-контент или онбординг, смотря по IsEmpty).
    private void PopSubPage()
    {
        if (_subStack.Count > 0)
        {
            _subStack.RemoveAt(_subStack.Count - 1);
        }
        if (_subStack.Count > 0)
        {
            subPageHost.Content = _subStack[^1];
        }
        else
        {
            subPageHost.Content = null;
            subPageHost.IsVisible = false;
        }
    }

    // «Купить подписку»: BuyView со своим BuyViewModel (грузит каталог в ctor).
    public void OpenBuy()
    {
        var view = new BuyView();
        view.BackRequested += (_, _) => PopSubPage();
        PushSubPage(view);
    }

    // «Устройства»: DevicesView со своим DevicesViewModel (uuid активной подписки резолвит сам
    // из вошедшего профиля; список грузится в ctor).
    public void OpenDevices()
    {
        var view = new DevicesView();
        view.BackRequested += (_, _) => PopSubPage();
        PushSubPage(view);
    }

    // «История платежей»: PaymentHistoryView; пустой CTA «Купить подписку» ведёт на Buy поверх.
    public void OpenHistory()
    {
        var view = new PaymentHistoryView();
        view.BackRequested += (_, _) => PopSubPage();
        view.BuyRequested += (_, _) => OpenBuy();
        PushSubPage(view);
    }

    // «Вход»: LoginView на ОБЩЕМ AccountViewModel — состояние входа видит и вкладка «Аккаунт».
    // BackRequested поднимается и по кнопке «назад», и по успешному входу (закрывает суб-страницу).
    public void OpenLogin()
    {
        var view = new LoginView { DataContext = _accountVm };
        view.BackRequested += (_, _) => PopSubPage();
        PushSubPage(view);
    }

    #endregion Sub-page host

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
