using System.Reactive.Disposables;
using v2rayN.Desktop.Base;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

public partial class MainWindow : WindowBase<MainWindowViewModel>
{
    private static Config _config;
    private bool _blCloseByUser = false;

    // Вкладки. «Главная» подключена к реальному движку (HomeViewModel); список серверов живёт
    // в её левой колонке — отдельной вкладки «Сервера» в рейле нет.
    private readonly Control _homeView = new HomeView();
    private readonly Control _settingsView = new SettingsView();
    private readonly AccountView _accountView = new AccountView();
    private readonly Button[] _navButtons;
    private HomeViewModel? _homeViewModel;

    // Компактная (телефонная) «Главная»: свой одностолбцовый экземпляр (широкая и компактная
    // делят ОДИН HomeViewModel, но каждая держит своё дерево «Главной» — см. ViewFor).
    private readonly CompactHomeView _compactHome = new();

    // Брейкпоинт адаптива: ширина < 760 → компакт, ≥ 760 → широкая. Гистерезис 24 (назад в компакт
    // только < 736), чтобы окно, «припаркованное» на границе, не мигало между раскладками при драге.
    private const double CompactBreakpointWidth = 760.0;
    private const double LayoutHysteresis = 24.0;
    private bool _compactMode = true;          // старт компактный (дефолт 310×630 < 760)

    // Целевые размеры тумблера раскладки (двойной клик по навигации / drag-to-edge). Компакт
    // держит title-bar на маленьком окне; широкая — рабочий десктоп. Оба клампятся в WorkingArea.
    private const double WideToggleWidth = 1120.0;
    private const double WideToggleHeight = 760.0;
    private const double CompactToggleWidth = 310.0;
    private const double CompactToggleHeight = 630.0;

    // Драг-к-краю: когда пользователь тащит компактное окно к верхнему/боковому краю рабочей
    // области — разворачиваем в широкую. Порог в физ. пикселях; _edgeSnapSuspended гасит ложные
    // срабатывания во время программного репозиционирования (клампы/тумблер).
    private const int EdgeSnapThreshold = 6;
    private bool _edgeSnapSuspended;
    private bool _titleDragging;
    private bool _edgeExpandRequested;
    private AppTab _currentTab = AppTab.Home;   // ОДНО состояние вкладки на обе раскладки
    private bool _isEmpty = true;

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

        // «Облегчённый режим» (reduced-motion): класс .lite обнуляет press/hover/reveal-переходы
        // оболочки (GlobalStyles :is(Window).lite), а тут снимаем анимацию смены вкладок
        // (page cross-fade). Хореографию connect-щита гасит сам ConnectHeroView по тому же флагу.
        if (_config.UiItem.LiteMode)
        {
            Classes.Add("lite");
            contentHost.PageTransition = null;
        }

        KeyDown += MainWindow_KeyDown;

        // Chrome окна: drag + системные кнопки.
        titleBar.PointerPressed += TitleBar_PointerPressed;
        btnMin.Click += (_, _) => WindowState = WindowState.Minimized;
        btnMax.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        btnClose.Click += (_, _) => Close();

        // Кнопка внизу рейла сворачивает/разворачивает ЛЕВУЮ навигацию (раньше прятала окно
        // в трей — убрано; в трей ведёт иконка App.axaml и «мин» в заголовке). Тумблит класс
        // .railCollapsed на bodyRoot → стили гонят navItems Width 76↔0 + шеврон ‹↔› (OutQuint,
        // гасится под .lite). Бренд-марка, railStatusDot и сама кнопка остаются видны в слим-полосе.
        btnRailToggle.Click += (_, _) => ToggleRail();

        // Единое состояние вкладки для ОБЕИХ раскладок: рейл (широкая) и нижняя навигация
        // (компактная) пишут в ОДИН ShowTab, который кладёт контент в текущий видимый хост.
        _navButtons = [navHome, navSettings, navAccount];
        navHome.Click += (_, _) => ShowTab(AppTab.Home);
        navSettings.Click += (_, _) => ShowTab(AppTab.Settings);
        navAccount.Click += (_, _) => ShowTab(AppTab.Account);
        bottomNav.TabSelected += (_, tab) => ShowTab(tab);
        _compactHome.AccountRequested += (_, _) => ShowTab(AppTab.Account);

        // Двойной клик по навигации (рейл в широкой / нижний бар в компактной) тумблит окно через
        // брейкпоинт: компакт⇄широкая. handledEventsToo — ловим даже если кнопка «съела» тап.
        railHost.AddHandler(InputElement.DoubleTappedEvent, (_, _) => ToggleLayoutSize(), RoutingStrategies.Bubble, handledEventsToo: true);
        bottomNav.AddHandler(InputElement.DoubleTappedEvent, (_, _) => ToggleLayoutSize(), RoutingStrategies.Bubble, handledEventsToo: true);

        // Drag-to-edge: тащим компактное окно к краю рабочей области → разворот в широкую.
        PositionChanged += OnPositionChanged;

        // Первичная раскладка + вотчер ширины окна. Наблюдаем Bounds окна (ширина клиентской
        // области); при пересечении брейкпоинта раскладка меняется РОВНО один раз (гистерезис).
        ApplyLayoutMode(_compactMode);
        this.GetObservable(BoundsProperty).Subscribe(b => UpdateLayoutMode(b.Width));

        // Общий AccountViewModel на вкладку «Аккаунт» (в рантайме DataContext ставит MainWindow,
        // не сама вью — тот же экземпляр уедет в LoginView). «Управление»-строки и CTA входа
        // поднимают события — здесь они превращаются в открытие суб-страниц.
        _accountView.DataContext = _accountVm;
        _accountView.BuyRequested += (_, _) => OpenBuy();
        _accountView.DevicesRequested += (_, _) => OpenDevices();
        _accountView.HistoryRequested += (_, _) => OpenHistory();
        _accountView.LoginRequested += (_, _) => OpenLogin();

        // Вкладка «Аккаунт» ВСЕГДА видна в шелле (как нижняя навигация Android): пользователь
        // с подпиской, но без входа, иначе не доберётся до логина. В logged-out AccountView сам
        // показывает гейт входа («Войдите в аккаунт» + Telegram CTA + «Управление»). Гейтинга нет.
        // DEV screenshot hook: INITIAL_TAB=settings|account opens that tab on launch.
        switch (Environment.GetEnvironmentVariable("INITIAL_TAB"))
        {
            case "settings": ShowTab(AppTab.Settings); break;
            case "account": ShowTab(AppTab.Account); break;
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
                bodyRoot.IsVisible = true;
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

    // ==================== Единая смена вкладки (обе раскладки) ====================
    // ОДИН источник истины (_currentTab) и ОДИН общий хост (contentHost). Настройки/Аккаунт —
    // общие экземпляры, которые ВСЕГДА живут в этом ЕДИНОМ хосте, поэтому смена ширины физически
    // не «перецепляет» живой контрол в другой хост (был краш компакт→Настройки→расширение).
    // «Главная» имеет своё дерево на раскладку (компактное одностолбцовое vs широкое двухколоночное);
    // ViewFor выбирает нужное по _compactMode — это просто смена Content ОДНОГО хоста, без переноса
    // между родителями. Отдельной вкладки «Сервера» нет: серверы — часть «Главной».
    private void ShowTab(AppTab tab)
    {
        _currentTab = tab;

        SetRailActive(tab);
        bottomNav.SetSelected(tab);

        contentHost.Content = ViewFor(tab);
    }

    // Home разный на раскладку (компакт vs широкая); Настройки/Аккаунт — единые экземпляры.
    private Control ViewFor(AppTab tab) => tab switch
    {
        AppTab.Settings => _settingsView,
        AppTab.Account => _accountView,
        _ => _compactMode ? _compactHome : _homeView,
    };

    private void SetRailActive(AppTab tab)
    {
        foreach (var b in _navButtons)
        {
            b.Classes.Remove("active");
        }
        var active = tab switch
        {
            AppTab.Settings => navSettings,
            AppTab.Account => navAccount,
            _ => navHome,
        };
        active.Classes.Add("active");
    }

    // ==================== Адаптивный своп (ширина окна ↔ раскладка) ====================
    // Гистерезис: из компакта в широкую при ширине ≥ 760, обратно в компакт при < 736.
    private void UpdateLayoutMode(double width)
    {
        if (width <= 0)
        {
            return;
        }
        var compact = _compactMode
            ? width < CompactBreakpointWidth
            : width < CompactBreakpointWidth - LayoutHysteresis;
        if (compact != _compactMode)
        {
            ApplyLayoutMode(compact);
        }
    }

    // Переклад chrome вокруг ЕДИНОГО contentHost: широкая = [рейл(Auto) | контент(*)], компакт =
    // [контент(*) / нижняя-нав(Auto)]. Меняем только Grid-раскладку/видимость chrome и Content
    // хоста — сами контролы НЕ переносятся между деревьями (нет двойного родителя → нет краша).
    private void ApplyLayoutMode(bool compact)
    {
        _compactMode = compact;

        if (compact)
        {
            // Одна колонка, две строки: контент над нижней навигацией.
            bodyRoot.ColumnDefinitions = new ColumnDefinitions("*");
            bodyRoot.RowDefinitions = new RowDefinitions("*,Auto");
            Grid.SetColumn(contentArea, 0);
            Grid.SetRow(contentArea, 0);
            Grid.SetColumn(bottomNav, 0);
            Grid.SetRow(bottomNav, 1);
        }
        else
        {
            // Две колонки, одна строка: рейл слева, контент справа.
            bodyRoot.ColumnDefinitions = new ColumnDefinitions("Auto,*");
            bodyRoot.RowDefinitions = new RowDefinitions("*");
            Grid.SetColumn(railHost, 0);
            Grid.SetRow(railHost, 0);
            Grid.SetColumn(contentArea, 1);
            Grid.SetRow(contentArea, 0);
        }

        railHost.IsVisible = !compact;
        bottomNav.IsVisible = compact;

        ApplyShellVisibility();
        ShowTab(_currentTab);
    }

    // Онбординг/суб-страницы — mode-agnostic оверлеи. Пусто (нет подписок) → только онбординг на всю
    // ширину. Есть подписки → виден единый bodyRoot (chrome раскладывает ApplyLayoutMode).
    private void ApplyShellVisibility()
    {
        // Не трогаем видимость в режиме превью суб-экрана (DEV screenshot hook).
        if (Environment.GetEnvironmentVariable("PREVIEW_VIEW") is not null)
        {
            return;
        }
        onboardingView.IsVisible = _isEmpty;
        bodyRoot.IsVisible = !_isEmpty;
    }

    // Свёртка/разворот левого нав-рейла. Всё движение (navItems Width/Opacity, поворот шеврона)
    // живёт в стилях по классу .railCollapsed на bodyRoot; здесь только тумблим класс и правим
    // подсказку. Столбец рейла Auto → контент сам занимает освободившееся место, ничего не клипая.
    // Кнопка и индикатор остаются в слим-полосе, так что развернуть можно всегда (не «застрять»).
    private bool _railCollapsed;

    private void ToggleRail()
    {
        _railCollapsed = !_railCollapsed;
        if (_railCollapsed)
        {
            bodyRoot.Classes.Add("railCollapsed");
        }
        else
        {
            bodyRoot.Classes.Remove("railCollapsed");
        }
        ToolTip.SetTip(btnRailToggle, _railCollapsed ? "Развернуть панель" : "Свернуть панель");
    }

    // Создаёт HomeViewModel поверх реального движка (ProfilesViewModel + StatusBarViewModel из
    // MainWindowViewModel) и отдаёт его «Главной». Индикатор рейла следует за IsConnected.
    private void SetupHome(MainWindowViewModel vm)
    {
        _homeViewModel = new HomeViewModel(vm);
        // ОДИН HomeViewModel питает ОБЕ раскладки (широкую и компактную «Главную»), поэтому
        // connect-состояние, выбранный сервер, скорости и таймер одинаковы при любой ширине.
        _homeView.DataContext = _homeViewModel;
        _compactHome.DataContext = _homeViewModel;
        onboardingView.DataContext = _homeViewModel;

        // Индикатор рейла: серый в покое, синий при подключении И в процессе подключения (P1-3).
        _homeViewModel.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting, (connected, connecting) => connected || connecting)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(active => railStatusDot.Fill = active ? _dotOn : _dotOff);

        // Пустой старт (нет подписок): показываем ТОЛЬКО онбординг на всю ширину под chrome — оба
        // дерева скрыты. После добавления подписки (IsEmpty=false) — дерево по текущей раскладке.
        _homeViewModel.WhenAnyValue(x => x.IsEmpty)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(empty =>
            {
                _isEmpty = empty;
                ApplyShellVisibility();
            });
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

    // Общий вход для суб-страниц НАСТРОЕК (DNS, Маршрутизация, Прокси по приложениям, Провайдеры,
    // Файлы ресурсов, Пинг, О приложении, Резервное копирование, Схемы URL). Раньше это были
    // отдельные OS-окна — теперь любая реализующая <see cref="ISubPage"/> вью кладётся на ТОТ ЖЕ стек
    // «назад», что и Buy/Login/Devices/History. Никаких отдельных окон в приложении быть не должно.
    public void OpenSubPage(Control view)
    {
        if (view is ISubPage sub)
        {
            sub.BackRequested += (_, _) => PopSubPage();
        }
        PushSubPage(view);
    }

    #endregion Sub-page host

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // Арм drag-to-edge только на реальный перенос заголовка. На Windows BeginMoveDrag
            // блокирует до конца перетаскивания (PositionChanged летят реентрантно и лишь помечают
            // _edgeExpandRequested), поэтому фактический разворот делаем ПОСЛЕ выхода из move-loop —
            // не воюем с нативным циклом перемещения за позицию окна.
            _titleDragging = true;
            _edgeExpandRequested = false;
            BeginMoveDrag(e);
            _titleDragging = false;
            if (_edgeExpandRequested)
            {
                _edgeExpandRequested = false;
                ResizeClamped(WideToggleWidth, WideToggleHeight);
            }
        }
    }

    // ==================== Двойной клик по навигации: тумблер компакт⇄широкая ====================
    // Компакт → широкая (WideToggle), широкая → компакт (CompactToggle). Смена ширины через
    // брейкпоинт триггерит ApplyLayoutMode из Bounds-вотчера, так что раскладка следует за размером.
    private void ToggleLayoutSize()
    {
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }
        if (_compactMode)
        {
            ResizeClamped(WideToggleWidth, WideToggleHeight);
        }
        else
        {
            ResizeClamped(CompactToggleWidth, CompactToggleHeight);
        }
    }

    // ==================== Drag-to-edge: разворот компакта у края экрана ====================
    // Тащим компактное окно так, что его верх/левый/правый край касается края рабочей области →
    // разворачиваем в широкую. Только из компакта и только при реальном drag заголовка (не дёргает
    // при программных клампах). После разворота _compactMode=false — повторно не срабатывает.
    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_edgeSnapSuspended || !_titleDragging || !_compactMode || WindowState != WindowState.Normal)
        {
            return;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var wa = screen.WorkingArea;
        var p = Position;
        var physW = (int)(Width * scaling);

        var hitTop = p.Y <= wa.Y + EdgeSnapThreshold;
        var hitLeft = p.X <= wa.X + EdgeSnapThreshold;
        var hitRight = p.X + physW >= wa.X + wa.Width - EdgeSnapThreshold;
        if (hitTop || hitLeft || hitRight)
        {
            // Помечаем разворот; сам ResizeClamped выполнит TitleBar_PointerPressed после move-loop.
            _edgeExpandRequested = true;
        }
    }

    // Ставит размер и КЛАМПИТ его + позицию в WorkingArea текущего экрана: окно (и кастомный
    // заголовок) всегда целиком на экране — верх никогда не уходит за границу (y ≥ wa.Y).
    private void ResizeClamped(double width, double height)
    {
        _edgeSnapSuspended = true;
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is null)
            {
                Width = width;
                Height = height;
                return;
            }
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var wa = screen.WorkingArea;
            var maxW = wa.Width / scaling;
            var maxH = wa.Height / scaling;

            var w = Math.Clamp(width, Math.Min(MinWidth, maxW), maxW);
            var h = Math.Clamp(height, Math.Min(MinHeight, maxH), maxH);
            Width = w;
            Height = h;

            var physW = w * scaling;
            var physH = h * scaling;
            var x = wa.X + Math.Max(0, (wa.Width - physW) / 2);
            var y = wa.Y + Math.Max(0, (wa.Height - physH) / 2);
            x = Math.Max(wa.X, Math.Min(x, wa.X + wa.Width - physW));
            y = Math.Max(wa.Y, Math.Min(y, wa.Y + wa.Height - physH));
            Position = new PixelPoint((int)x, (int)y);
        }
        catch { }
        finally
        {
            _edgeSnapSuspended = false;
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

    // Владелец: НИКАКИХ плавающих уведомлений. Раньше здесь жил TopRight
    // WindowNotificationManager — все снек-сообщения (connect/fail/disconnect/refresh/copy)
    // теперь глухо гасятся в единственном стоке. Ни один издатель не трогаем: конечный
    // автомат подключения лишь публикует строки, статус показывает сам щит.
    private Task DelegateSnackMsg(string content) => Task.CompletedTask;

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
        // Сохраняем размер ТОЛЬКО в обычном состоянии: развёрнутое/свёрнутое окно
        // не должно перетекать в персист (иначе следующий запуск открывается «на весь экран»).
        if (WindowState != WindowState.Normal)
        {
            return;
        }
        ConfigHandler.SaveWindowSizeItem(_config, GetType().Name, Width, Height);
    }

    #endregion UI
}
