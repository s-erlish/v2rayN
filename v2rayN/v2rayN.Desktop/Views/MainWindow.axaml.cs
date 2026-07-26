using System.Reactive.Disposables;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.VisualTree;
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
    private bool _compactMode = true;          // старт компактный (дефолт 372×630 < 760)

    // Целевые размеры тумблера раскладки (двойной клик по навигации / drag-to-edge). Компакт
    // держит title-bar на маленьком окне; широкая — рабочий десктоп. Оба клампятся в WorkingArea.
    private const double WideToggleWidth = 1120.0;
    private const double WideToggleHeight = 760.0;
    private const double CompactToggleWidth = 372.0;
    private const double CompactToggleHeight = 630.0;

    // ==================== In-app UI-масштаб (zoom всего интерфейса, независимо от OS DPI) ====================
    // Фактор zoom применяется к КОРНЮ контента через LayoutTransformControl (uiScaleHost/uiScaleTransform):
    // на 4K-мониторе при 100% OS-масштабе всё физически крошечное — этот слой даёт пользователю увеличить
    // интерфейс целиком (Ctrl +/Ctrl −/Ctrl 0 и строка настроек), НЕ трогая OS DPI. Базовые MinWidth/MinHeight
    // (из XAML) масштабируются под фактор — иначе на высоком zoom контенту не хватает места и он клиппится.
    // Значение живёт в UiScaleState (мгновенный обмен с настройками/клавишами) и персистится в UiItem.UiScale.
    private double _uiScale = 1.0;
    private double _baseMinWidth;   // == MinWidth в XAML при zoom 1.0 (снимается в ctor до правок мин-размера)
    private double _baseMinHeight;  // == MinHeight в XAML при zoom 1.0

    // Драг-к-краю: когда пользователь тащит компактное окно к верхнему/боковому краю рабочей
    // области — разворачиваем в широкую. Порог в физ. пикселях; _edgeSnapSuspended гасит ложные
    // срабатывания во время программного репозиционирования (клампы/тумблер).
    private const int EdgeSnapThreshold = 6;
    private bool _edgeSnapSuspended;
    private bool _titleDragging;
    private bool _edgeExpandRequested;
    private AppTab _currentTab = AppTab.Home;   // ОДНО состояние вкладки на обе раскладки
    // Онбординг-гейт: «у пользователя ПУСТО» — это ФАКТ, а не значение по умолчанию. Поле стартует с
    // false («ещё не знаем») и взводится в ctor по синхронному снимку локального хранилища; живую правду
    // потом ведёт HomeViewModel.IsEmpty (тоже «известно, что пусто»). Раньше здесь стояло true, и первый
    // кадр у вернувшегося пользователя без входа был приветственным экраном «добавьте подписку».
    private bool _isEmpty;
    private readonly bool? _storedServersAtLaunch;   // снимок «есть ли серверы в БД», снятый ДО первого кадра (null = неизвестно)
    private bool _isSyncing;                     // E3: идёт пост-логин импорт → оверлей синхронизации
    private bool _isStartupLoading;              // Bug4: холодный старт с сохранённой сессией → оверлей загрузки (НЕ гейт входа)
    private bool _isLoggedIn;                    // A1: залогинен ли пользователь → пустое состояние ведёт на Главную, а не на онбординг-вход
    private bool _layoutInitialized;             // C6: первый ApplyLayoutMode без кроссфейда морфинга

    // ОДИН экземпляр AccountViewModel на всё приложение: делится между вкладкой «Аккаунт»
    // (AccountView) и суб-страницей «Вход» (LoginView), поэтому состояние входа распространяется
    // на оба (P0-8). Ctor VM безопасен на этапе инициализации полей (без AppManager).
    private readonly AccountViewModel _accountVm =
        Design.IsDesignMode ? AccountViewModel.CreateDesign() : new AccountViewModel();

    // Стек открытых суб-страниц (Buy/Login/Devices/History) в хосте subPageHost. Back снимает
    // верхнюю; когда стек пуст — хост скрыт и снова видна вкладка/онбординг под ним.
    private readonly List<Control> _subStack = new();

    // Моушен-токены оболочки теперь берутся из ЕДИНОГО C#-каталога Common/Motion.cs (Motion.Ease.* /
    // Motion.Dur.*), который зеркалит XAML Ease.*/таблицу длительностей — так XAML и C# не расходятся.
    // Раньше здесь дублировались локальные easings и константы длительностей входа/выхода; их значения
    // были идентичны Motion.Ease.OutQuint/Standard и Motion.Dur.Reveal/Exit — дедуп без смены поведения.
    // Индикатор подключения в рейле красится ТЕМА-токенами через класс .on (Ellipse.ConnDot в разметке).

    // Смена вкладки (P0-2): вход = НАПРАВЛЕННЫЙ горизонтальный слайд translateX ±16→0 + fade-in
    // (Motion.Dur.State 220мс, Motion.Ease.OutQuint) поверх выхода = быстрый fade-out (Motion.Dur.Exit
    // 150мс, Motion.Ease.Standard, короче входа). Направление задаёт дельта nav-индекса (глубже по строке
    // навигации Home▸Settings▸Account → въезд СПРАВА; назад → СЛЕВА) — эхо геометрии навигации вместо
    // «единого рефлекса» одинакового вертикального подъёма на всех вкладках. Дистанция 16px = ЕДИНЫЙ
    // slide-словарь с суб-страницами (AnimateSubPageIn тоже 16px X). ContentSlideFrom — дистанция (px).
    private const double ContentSlideFrom = 16.0;

    // Токены отмены незавершённой анимации на каждый анимируемый узел (перезапуск отменяет предыдущую).
    private CancellationTokenSource? _subPageAnim;
    private CancellationTokenSource? _shellAnim;
    private CancellationTokenSource? _layoutAnim;
    private CancellationTokenSource? _resizeAnim;   // Bug6: плавная анимация размера окна при тумблере раскладки
    private CancellationTokenSource? _contentAnim;  // смена вкладки в едином contentHost (directional slide+fade)
    private CancellationTokenSource? _indicatorAnim; // скольжение путешествующего индикатора рейла (P1-4)
    private RegisteredWaitHandle? _programStartedWait;
    private Control? _currentShellView;          // текущий видимый оверлей оболочки (для кроссфейда)
    private Control? _currentContentView;        // текущая видимая вкладка в contentHost (keep-alive своп)
    private int _contentZ;                       // ZIndex-счётчик: входящая вкладка всегда поверх уходящей
    private TranslateTransform? _railIndicatorTransform;   // Y-слот путешествующего индикатора рейла (P0-1)
    private bool _railIndicatorSeeded;                     // первый показ индикатора — мгновенно на активном слоте
    private int _navIndex;                                 // индекс текущей вкладки (Home0/Settings1/Account2) → направление слайда (P0-2)
    private readonly HashSet<AppTab> _entrancePlayed = new();  // region-stagger: первая активация вкладки за сессию (P1-1)

    // Bug8: интеракции буфера/скана регистрируются на ВРЕМЯ ЖИЗНИ окна (а не под WhenActivated, что снимало
    // их при деактивации). Держим их здесь и освобождаем один раз в OnClosed — так угловой «+» (MenuFlyout,
    // деактивирующий окно) не теряет обработчик и «добавить из буфера/по QR» не проваливается молча.
    private readonly CompositeDisposable _windowInteractions = new();

    public MainWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        // ==================== Масштаб интерфейса (in-app zoom) ====================
        // Снимаем базовые мин-размеры из XAML (340×560) ДО каких-либо правок, затем засеваем фактор из
        // конфига (клампится) и применяем его к корневому ScaleTransform + мин-размерам окна. Подписка на
        // UiScaleState.Changed держит трансформ, мин-размер и брейкпоинт раскладки в синхроне при изменении
        // масштаба из настроек ИЛИ по горячим клавишам (единый путь применения — OnUiScaleChanged/ApplyUiScale).
        _baseMinWidth = MinWidth;
        _baseMinHeight = MinHeight;
        UiScaleState.Initialize(_config.UiItem.UiScale);
        _uiScale = UiScaleState.Current;
        UiScaleState.Changed += OnUiScaleChanged;
        ApplyUiScaleToWindow();   // трансформ + мин-размеры на старте (WindowBase.OnLoaded затем клампит окно)

        // Ресайз-грипы безрамочного окна: 8 зон → BeginResizeDrag. Видимость грипов = только Normal-состояние.
        WireResizeGrips();
        this.GetObservable(WindowStateProperty)
            .Subscribe(s => resizeGripHost.IsVisible = s == WindowState.Normal);

        // «Облегчённый режим» (reduced-motion) теперь РЕАКТИВЕН: единый источник — MotionState.
        // MainWindow сеет его из конфига и подписывается на изменения; SettingsViewModel двигает флаг
        // live (без рестарта). ApplyMotionMode вешает/снимает класс .lite (обнуляет press/hover/reveal
        // оболочки через :is(Window).lite + свёртку рейла); смену вкладок keep-alive-хоста глушит/оживляет
        // сам SwapContent по MotionState.IsLite (мгновенный своп vs rise/fade §A.4). Хореографию connect-
        // щита гасит сам ConnectHeroView по тому же MotionState. Итог: тумблер lite мгновенно ГЛУШИТ ВСЁ
        // движение (щит, переходы вкладок, page-rise) и так же мгновенно оживляет обратно.
        MotionState.Initialize(_config.UiItem.LiteMode);
        ApplyMotionMode(MotionState.IsLite);
        MotionState.Changed += OnMotionStateChanged;

        // Плавная смена темы: App.ApplyTheme (обе кнопки настроек — база и монохром) отдаёт свой своп
        // сюда, а мы оборачиваем его в круговую заливку (снимок → своп под снимком → расширяющийся клип).
        // Хук — единственный на приложение (одно окно), живёт всё время работы; в lite сам делает мгновенно.
        App.ThemeTransitionHook = RunThemeTransition;

        // Точка старта заливки = место последнего нажатия (тап по тумблеру темы в настройках). Туннельно и
        // handledEventsToo — ловим даже если кнопка «съест» событие; координата — в системе координат окна.
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

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
        // Bug4: тумблер размера должен срабатывать ТОЛЬКО по пустой хром-области, а НЕ по любой
        // нав-кнопке (navHome/navSettings/navAccount, кнопки нижнего бара, btnRailToggle). Раньше
        // исключался лишь btnRailToggle, поэтому двойной клик по любой другой нав-кнопке «проваливался»
        // в этот handler и разворачивал/сжимал окно. Поскольку handledEventsToo:true ловит событие даже
        // помеченным Handled, пометки на кнопке недостаточно — фильтруем по источнику: IsWithinInteractive
        // возвращает true, если клик попал ВНУТРЬ любого Button раньше, чем в host (railHost/bottomNav).
        railHost.AddHandler(InputElement.DoubleTappedEvent, (_, e) =>
        {
            if (!IsWithinInteractive(e.Source as Visual))
            {
                ToggleLayoutSize();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        bottomNav.AddHandler(InputElement.DoubleTappedEvent, (_, e) =>
        {
            if (!IsWithinInteractive(e.Source as Visual))
            {
                ToggleLayoutSize();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // Drag-to-edge: тащим компактное окно к краю рабочей области → разворот в широкую.
        PositionChanged += OnPositionChanged;

        // СЕМЕНИМ «пусто ли у пользователя» ДО первого ApplyShellVisibility — синхронным вопросом к
        // локальному хранилищу. Движок грузит серверы АСИНХРОННО (MainWindowViewModel.Init →
        // RefreshServersDispatcherAsync), поэтому на момент первой отрисовки список ещё пуст; решать по
        // нему = решать по значению по умолчанию, неотличимому от факта «у него ничего нет». Именно
        // отсюда брался баг: пользователь без входа, но с подпиской из буфера обмена, видел при каждом
        // запуске приветственный экран, пока его серверы не приезжали из БД. Снимок отдаётся и
        // HomeViewModel (SetupHome) — один вопрос, один ответ на обе стороны. Неизвестность (null) НЕ
        // считается пустотой: тогда показываем оболочку, а не гейт (см. ApplyShellVisibility).
        _storedServersAtLaunch = Design.IsDesignMode ? null : AppManager.Instance.HasStoredProfiles();
        _isEmpty = _storedServersAtLaunch == false;

        // Bug4: СЕМЕНИМ cold-start-сигнал ДО первого ApplyShellVisibility. _accountVm (field-init выше)
        // уже сконструирован, и его ctor синхронно взвёл IsStartupLoading, если есть сохранённая сессия.
        // Считываем сейчас, чтобы первый же ApplyLayoutMode→ApplyShellVisibility нацелился сразу на оверлей
        // загрузки (previous==null → мгновенно), а НЕ на онбординг-гейт с последующим кроссфейдом. Живые
        // изменения ловит подписка в SetupHome. (В дизайне IsStartupLoading=false → обычный путь.)
        _isStartupLoading = _accountVm.IsStartupLoading;

        // Keep-alive: ВСЕ вкладки — постоянные дети contentHost (широкая/компактная «Главная»,
        // Настройки, Аккаунт). Они всегда в дереве (measured/arranged), скрыты через Opacity/hit-test
        // (НЕ IsVisible — тот бы гнал повторный layout при показе). Смена вкладки = дешёвый композитный
        // Opacity+TranslateY на уже разложенной вью → без detach/reattach и без first-layout под кадром
        // перехода (это и был лаг переключения). ZIndex ставит SwapContent (входящая поверх уходящей).
        foreach (var v in new Control[] { _homeView, _compactHome, _settingsView, _accountView })
        {
            v.Opacity = 0d;
            v.IsHitTestVisible = false;
            contentHost.Children.Add(v);
        }

        // Первичная раскладка + вотчер ширины окна. Наблюдаем Bounds окна (ширина клиентской
        // области); при пересечении брейкпоинта раскладка меняется РОВНО один раз (гистерезис).
        ApplyLayoutMode(_compactMode);
        // Брейкпоинт компакт/широкая живёт в КООРДИНАТАХ КОНТЕНТА (после UI-zoom): LayoutTransformControl
        // масштабирует контент на _uiScale, поэтому контент видит ширину Bounds.Width/_uiScale. Делим здесь,
        // чтобы зумнутое окно переключало раскладку осмысленно (при _uiScale=1.0 — прежнее поведение 1:1).
        this.GetObservable(BoundsProperty).Subscribe(b => UpdateLayoutMode(b.Width / _uiScale));

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
                contentHost.Children.Add(preview);
                SwapContent(preview, animate: false);
            }
        }

        // Питаем «Главную» реальным HomeViewModel, как только доступен корневой ViewModel
        // (App присваивает его сразу после Build, до показа окна — DataContext успевает встать
        // раньше активации HomeView). Здесь же биндим индикатор подключения в рейле.
        this.WhenAnyValue(x => x.ViewModel)
            .Where(vm => vm != null)
            .Take(1)
            .Subscribe(vm =>
            {
                // Bug8: регистрируем интеракции буфера/скана на ВРЕМЯ ЖИЗНИ окна, как только доступен
                // ViewModel (App присваивает его сразу после Build). Раньше они жили под WhenActivated и
                // снимались при деактивации — открытие MenuFlyout углового «+» деактивировало окно,
                // обработчик пропадал, и AddServerViaClipboardAsync бросал UnhandledInteractionException в
                // незамеченную задачу → «ничего не происходит». Теперь обработчик жив всегда.
                RegisterWindowInteractions(vm!);
                SetupHome(vm!);
            });

        this.WhenActivated(disposables =>
        {
            // Питаем скрытый StatusBarView (сохраняем интеракции/иконку трея/StatusBarViewModel).
            this.OneWayBind(ViewModel, vm => vm.StatusBarViewModel, v => v.contentStatusBarView.Content).DisposeWith(disposables);

            // Live-смена языка: подсказка свёртки рейла ставится императивно (не {loc:T}), поэтому
            // переустанавливаем её по событию L.LanguageChanged. Отписка при деактивации.
            L.Instance.LanguageChanged += OnLanguageChanged;
            Disposable.Create(() => L.Instance.LanguageChanged -= OnLanguageChanged).DisposeWith(disposables);

            // Bug8: ReadTextFromClipboardInteraction / ScanScreenInteraction более НЕ регистрируются здесь —
            // они живут на время жизни окна (RegisterWindowInteractions), чтобы деактивация окна flyout-«+»
            // не снимала их. Оставшиеся интеракции (выбор файла/картинки, показ-скрытие) привязаны к
            // активной сессии окна и корректно снимаются при деактивации.
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
            // Держим RegisteredWaitHandle: без него регистрацию нечем снять в OnClosed.
            _programStartedWait = ThreadPool.RegisterWaitForSingleObject(Program.ProgramStarted, OnProgramStarted, null, -1, false);
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
    // ViewFor выбирает нужное по _compactMode. Все вкладки — постоянные keep-alive дети ЕДИНОГО хоста;
    // смена = переключение ВИДИМОЙ поверхности (SwapContent), без переноса между родителями и без
    // detach/reattach. Отдельной вкладки «Сервера» нет: серверы — часть «Главной».
    private void ShowTab(AppTab tab, bool animate = true)
    {
        // Направление слайда контента = дельта индекса в строке навигации (Home0▸Settings1▸Account2):
        // +1 глубже (въезд справа), −1 назад (слева). На мгновенном свопе (layout swap / первый показ)
        // направление не нужно (0). _navIndex обновляем ВСЕГДА, чтобы следующий переход считался от
        // фактической вкладки (layout-свопы зовут ShowTab с той же вкладкой → дельта 0, ничего не ломают).
        var toIndex = NavIndex(tab);
        var direction = animate ? toIndex.CompareTo(_navIndex) : 0;
        _navIndex = toIndex;
        _currentTab = tab;

        SetRailActive(tab, animate);
        bottomNav.SetSelected(tab);

        SwapContent(ViewFor(tab), animate, direction);
    }

    private static int NavIndex(AppTab tab) => tab switch
    {
        AppTab.Settings => 1,
        AppTab.Account => 2,
        _ => 0,
    };

    // Off-screen-guard (P0-3): анимируем оболочку только когда окно реально видно. В трее (Hide → !IsVisible)
    // или свёрнутом (Minimized) состоянии программный ShowTab/индикатор/стаггер снапятся в финал, а не тикают
    // за экраном — закрывает класс «off-screen animation loop».
    private bool IsWindowLive() => IsVisible && WindowState != WindowState.Minimized;

    // ==================== Keep-alive своп вкладок (rise+fade §A.4) ====================
    // Все вкладки постоянно реализованы детьми contentHost; здесь только меняем ВИДИМУЮ поверхность
    // дешёвой композитной анимацией (Opacity + TranslateY) на уже разложенной вью — без detach/reattach
    // и first-layout под кадром перехода. animate:false — мгновенно (первый показ, своп раскладки);
    // под .lite — тоже мгновенно (reduced-motion). Rise+fade идентичен прежнему page-transition.
    private void SwapContent(Control target, bool animate, int direction = 0)
    {
        var previous = _currentContentView;
        if (previous == target)
        {
            target.Opacity = 1d;
            target.IsHitTestVisible = true;
            target.RenderTransform = null;
            return;
        }
        _currentContentView = target;

        target.ZIndex = ++_contentZ;   // входящая ВСЕГДА поверх уходящей → подъём читается корректно

        // Bug7: РОВНО одна интерактивная поверхность. Гасим hit-test на ВСЕХ keep-alive вкладках, кроме
        // target (а не только на previous). Широкая и компактная «Главная» — РАЗНЫЕ экземпляры, которые
        // свопаются при смене ширины; устаревший IsHitTestVisible=true на скрытом инстансе Home перехватывал
        // клики поверх видимого → «мёртвый» выбор сервера/подключение в широкой раскладке. Теперь ровно одна
        // вкладка (target) хит-тестируется, остальные — прозрачны для указателя.
        // Заодно ЧИНИМ strand от прерванного свопа. Avalonia на отмене анимации НЕ откатывает свойство к
        // базовому: AnimationInstance.Unsubscribed() -> ApplyFinalFill() пишет ПОСЛЕДНЕЕ интерполированное
        // значение как LocalValue (FillMode.Forward). Отменённый выход оставлял вкладку прибитой на 0,05..0,82
        // непрозрачности навсегда — ранний return в AnimateContentSwap пропускает единственное место, где
        // previous.Opacity обнулялся, а оба места очистки трогают только ТЕКУЩУЮ previous. Нормализуем здесь
        // каждую невовлечённую поверхность: previous исключена — она сейчас гаснет со своей текущей opacity.
        foreach (var v in new Control[] { _homeView, _compactHome, _settingsView, _accountView })
        {
            v.IsHitTestVisible = ReferenceEquals(v, target);
            if (!ReferenceEquals(v, target) && !ReferenceEquals(v, previous))
            {
                v.Opacity = 0d;
                v.RenderTransform = null;
            }
        }
        target.IsHitTestVisible = true;   // покрывает и PREVIEW_VIEW (target вне keep-alive-набора)

        // Мгновенный своп: первый показ (previous == null), reduced-motion (.lite), своп раскладки или
        // окно вне экрана (P0-3: не крутим переход, которого никто не видит).
        if (!animate || previous is null || MotionState.IsLite || !IsWindowLive())
        {
            _contentAnim?.Cancel();
            target.Opacity = 1d;
            target.RenderTransform = null;
            if (previous != null)
            {
                previous.Opacity = 0d;
                previous.RenderTransform = null;
            }
            return;
        }

        _contentAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _contentAnim = cts;
        AnimateContentSwap(target, previous, direction, cts.Token);
    }

    private async void AnimateContentSwap(Control target, Control previous, int direction, CancellationToken ct)
    {
        target.Opacity = 0d;
        // Направленный вход (translateX ±16→0 + fade-in, State 220 OutQuint) поверх выхода (быстрый
        // fade-out, Exit 150 Standard) — параллельно. Знак X = направление по строке навигации: глубже
        // (direction ≥ 0) → въезд справа (+16); назад (direction < 0) → слева (−16). Уходящая только
        // гаснет (без встречного слайда) — keep-alive стек не клипается, не превращается в тяжёлую карусель.
        var fromX = direction < 0 ? -ContentSlideFrom : ContentSlideFrom;
        var enter = RunTranslateFade(target, TranslateTransform.XProperty, fromX, 0d, 0d, 1d, Motion.Dur.State, Motion.Ease.OutQuint, ct);
        var exit = RunFade(previous, previous.Opacity, 0d, Motion.Dur.Exit, Motion.Ease.Standard, ct);
        // Внутренний region-stagger (Home/Settings) — поверх слайда корня; Account сам себя проигрывает.
        PlayTabEntrance(target);
        try { await Task.WhenAll(enter, exit); }
        catch { }
        if (ct.IsCancellationRequested)
        {
            return;
        }
        target.Opacity = 1d;
        target.RenderTransform = null;
        // Гасим уходящую и чистим transform — только если её снова не выбрали (быстрый обратный своп).
        if (previous != _currentContentView)
        {
            previous.Opacity = 0d;
            previous.RenderTransform = null;
        }
    }

    // Home разный на раскладку (компакт vs широкая); Настройки/Аккаунт — единые экземпляры.
    private Control ViewFor(AppTab tab) => tab switch
    {
        AppTab.Settings => _settingsView,
        AppTab.Account => _accountView,
        _ => _compactMode ? _compactHome : _homeView,
    };

    // ==================== Layout-aware Home binding (connect pipeline + RAM release) ====================
    // ТОЛЬКО «Главная» ТЕКУЩЕЙ раскладки держит живой HomeViewModel: она и хит-тестируется (SwapContent),
    // и несёт connect-щит/список серверов. «Главная» НЕактивной раскладки отвязывается (DataContext=null),
    // что: (1) освобождает её невиртуализованное дерево строк серверов из памяти — при 80–150 серверах это
    // доминирующая стоимость, и держать ДВЕ копии (широкую+компактную) вечно незачем; (2) страхует от
    // «мёртвой широкой»: активная «Главная» ВСЕГДА получает VM (значит HomeView.BindHero привязывает щит,
    // а строки получают DataContext), а скрытая никогда не перехватывает ввод и не биндит щит.
    // Идемпотентно и layout-верно: зовётся из SetupHome (первичная привязка) и из ApplyLayoutMode (на
    // каждом свопе 760px). Переактивация раскладки заново ставит DataContext → строки и щит оживают.
    private void BindActiveHome()
    {
        if (_homeViewModel is null)
        {
            return;   // VM ещё не готов (первый ApplyLayoutMode до SetupHome) — привяжет SetupHome позже
        }

        if (_compactMode)
        {
            if (!ReferenceEquals(_homeView.DataContext, null))
            {
                _homeView.DataContext = null;   // отвязать широкую → освободить её строки
            }
            _compactHome.DataContext = _homeViewModel;
        }
        else
        {
            if (!ReferenceEquals(_compactHome.DataContext, null))
            {
                _compactHome.DataContext = null;   // отвязать компактную → освободить её строки
            }
            _homeView.DataContext = _homeViewModel;
        }
    }

    private void SetRailActive(AppTab tab, bool animate)
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

        MoveRailIndicator(tab, animate);
    }

    // ==================== Путешествующий индикатор рейла (P0-1) ====================
    // ОДНА акцентная полоса (railIndicator) физически СКОЛЬЗИТ по Y между слотами (M3 nav-rail idiom),
    // вместо трёх независимых пилюль, «мигавших» на месте. Рейл всегда показывает 3 пункта (Home/Settings/
    // Account), поэтому шаг фиксированный (высота пункта 64), слот индикатора (высота 28) центрируется:
    // Y = index·64 + (64−28)/2. Первый показ (не seeded) / lite / off-screen / layout-своп (animate:false) —
    // мгновенно на активном слоте (без скольжения с Y=0); дальше — Motion.Dur.State 220 OutQuint. Токен
    // _indicatorAnim отменяет незавершённое скольжение при новом тапе; на layout-свопе рейл↔бар пере-садим
    // мгновенно (animate:false из ShowTab), т.к. геометрии разные.
    private static double RailSlotY(AppTab tab) => (NavIndex(tab) * 64d) + 18d;

    private void MoveRailIndicator(AppTab tab, bool animate)
    {
        if (railIndicator is null)
        {
            return;
        }
        _railIndicatorTransform ??= new TranslateTransform();
        if (!ReferenceEquals(railIndicator.RenderTransform, _railIndicatorTransform))
        {
            railIndicator.RenderTransform = _railIndicatorTransform;
        }

        var targetY = RailSlotY(tab);
        //  В компактном режиме рейл СКРЫТ — тап нижнего бара не должен гонять 220мс скольжение на
        //  невидимой полосе (лишняя работа компоновщика); на свопе в широкий рейл сядет мгновенно.
        var instant = !animate || !_railIndicatorSeeded || MotionState.IsLite || !IsWindowLive() || _compactMode;
        //  Текущее Y (в т.ч. на СЕРЕДИНЕ идущего скольжения) ловим ДО Cancel: отмена ревертит свойство к
        //  базе, поэтому чтение внутри аниматора давало «откат-кадр» при быстрых тапах трёх вкладок.
        var fromY = _railIndicatorTransform.Y;
        _indicatorAnim?.Cancel();
        if (instant)
        {
            _railIndicatorTransform.Y = targetY;
            _railIndicatorSeeded = true;
            return;
        }

        var cts = new CancellationTokenSource();
        _indicatorAnim = cts;
        AnimateRailIndicator(fromY, targetY, cts.Token);
    }

    private async void AnimateRailIndicator(double from, double targetY, CancellationToken ct)
    {
        if (_railIndicatorTransform is null)
        {
            return;
        }
        var anim = new Animation
        {
            Duration = Motion.Dur.State,
            Easing = Motion.Ease.OutQuint,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(TranslateTransform.YProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(TranslateTransform.YProperty, targetY) } },
            },
        };
        try { await anim.RunAsync(_railIndicatorTransform, ct); }
        catch { }
        if (!ct.IsCancellationRequested)
        {
            _railIndicatorTransform.Y = targetY;
        }
    }

    // ==================== Per-tab region stagger (P1-1) ====================
    // На активации вкладки её КРУПНЫЕ регионы (≤3) приезжают со сдвигом 40мс (opacity 0→1 + translateY
    // 6→0, State 220 OutQuint) — «одна кривая, разные голоса» вместо единого рефлекса. Императивно, только
    // при анимируемом свопе (значит уже не lite и на экране — AnimateContentSwap туда не заходит иначе),
    // и ОДИН раз за сессию на вкладку (без повторного ре-fade при каждом возврате). Account ПРОПУСКАЕМ —
    // он сам проигрывает свой group-2 стаггер (IsHitTestVisible false→true); connect-щит (ConnectHeroView)
    // тоже НЕ трогаем — он владеет собственной cold-start сборкой и connect-хореографией (никогда не
    // анимируем дважды). Поэтому Home = чип + список серверов (щит несёт свой вход сам); Settings = первые
    // группы-карточки в порядке чтения.
    private void PlayTabEntrance(Control target)
    {
        if (MotionState.IsLite || !IsWindowLive())
        {
            return;
        }
        if (ReferenceEquals(target, _accountView))
        {
            return;   // Account сам проигрывает вход
        }

        var regions = new List<Control>();
        AppTab tab;
        if (ReferenceEquals(target, _settingsView))
        {
            tab = AppTab.Settings;
            // Первые ≤3 верхнеуровневых ребёнка контент-стека (заголовок/карточка… в порядке чтения).
            var sv = target.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv?.Content is Panel panel)
            {
                foreach (var child in panel.Children.OfType<Control>())
                {
                    regions.Add(child);
                    if (regions.Count >= 3)
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            tab = AppTab.Home;   // широкая или компактная «Главная»
            // ТОЛЬКО список серверов. Щит (ConnectHero) НЕ включаем — у него свой вход; чип аккаунта
            // ТОЖЕ не включаем — HomeAccountChip.MaybeRunEntrance сам проигрывает своё появление при
            // резолве аккаунта, и второй стаггер здесь давал бы двойной вход (а при одновременном
            // логине — два аниматора на одном Opacity/RenderTransform). Стаггерим только список.
            if (FindNamed(target, "ServerList") is { } list)
            {
                regions.Add(list);
            }
        }

        if (regions.Count == 0 || !_entrancePlayed.Add(tab))
        {
            return;   // нечего стаггерить, либо уже играли за эту сессию
        }

        for (var i = 0; i < regions.Count; i++)
        {
            RunRegionReveal(regions[i], i);
        }
    }

    private static Control? FindNamed(Visual root, string name)
        => root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

    private static async void RunRegionReveal(Control region, int index)
    {
        region.Opacity = 0d;
        var anim = new Animation
        {
            Duration = Motion.Dur.State,
            Delay = TimeSpan.FromTicks(Motion.Dur.Stagger.Ticks * index),
            Easing = Motion.Ease.OutQuint,
            FillMode = FillMode.Both,   // держим стартовый кадр (opacity 0) на время задержки региона
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 6d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
            },
        };
        try { await anim.RunAsync(region); }
        catch { }
        // Контент виден, даже если стаггер no-op/прерван — гейтить видимость нельзя.
        region.Opacity = 1d;
        region.RenderTransform = null;
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

        // Bug5: мягкий градиент-скрим снизу контента ТОЛЬКО в компакте (в широкой — рейл, нижней
        // навигации нет). Контент «растворяется» в фон под безрамочной нижней навигацией вместо
        // резкого обрыва. Клик сквозной (IsHitTestVisible=False в разметке).
        navScrim.IsVisible = compact;

        ApplyShellVisibility();

        // ==================== Плавный своп раскладки (без джанка) ====================
        // Смена дерева «Главной» (компактное ↔ широкое) под page-rise «дерётся» с рефлоу сетки —
        // видимый скачок «контент прыгнул и осел». Гасим переход НА ВРЕМЯ свопа контента (мгновенная
        // подмена), затем возвращаем режим-верную анимацию (lite-aware) для последующей навигации.
        //
        // Bug6: раньше мгновенная пересборка дерева (ShowTab) происходила при Opacity=1 — один кадр
        // пересобранного контента ВСПЫХИВАЛ до того, как AnimateLayoutSwap ронял opacity в 0 и проявлял
        // заново (это и был видимый «джерк»). Теперь при анимируемом свопе ПРЯЧЕМ contentArea (Opacity=0)
        // ДО пересборки — она проходит невидимо, затем плавно проявляется. delicate-логику свопа (единый
        // хост, отсутствие reparent, гашение перехода) не трогаем — только порядок гашения opacity.
        var willAnimateSwap = _layoutInitialized && !MotionState.IsLite;
        if (willAnimateSwap)
        {
            contentArea.Opacity = 0d;
        }

        // Своп раскладки крутит ТОЛЬКО кроссфейд contentArea (AnimateLayoutSwap); смену дерева «Главной»
        // делаем мгновенно (animate:false) под уже спрятанным contentArea — без гонки rise/fade вкладки
        // с рефлоу сетки (Bug6). Keep-alive дети остаются в дереве, переносов родителя по-прежнему нет.
        ShowTab(_currentTab, animate: false);

        // Привязываем живой HomeViewModel к «Главной» ТЕКУЩЕЙ раскладки и отвязываем неактивную (RAM +
        // «мёртвая широкая»). Под анимируемым свопом contentArea уже скрыт (Opacity=0 выше), а неактивная
        // «Главная» и так на Opacity=0, поэтому освобождение её строк проходит невидимо, без глитча.
        BindActiveHome();

        // C6: мягкий кроссфейд морфинга раскладки (compact↔wide) поверх мгновенной подмены — маскирует
        // рефлоу дерева. ТОЛЬКО opacity на contentArea (без layout/transform); сквозная подложка-градиент
        // bodyRoot остаётся за ним, поэтому не мигает «белым». Пропускаем первый вызов (старт) и .lite.
        if (willAnimateSwap)
        {
            AnimateLayoutSwap();
        }
        _layoutInitialized = true;
    }

    private async void AnimateLayoutSwap()
    {
        _layoutAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _layoutAnim = cts;

        contentArea.Opacity = 0d;
        // P1-2: длительность = Motion.Dur.Shell 200мс (было off-scale 130). Совпадает с анимацией
        // размера окна (AnimateWindowSize 200 OutQuint) → при тумблере/drag-to-edge контент
        // до-материализуется РОВНО на том кадре, где окно перестаёт расти = один плавный морфинг,
        // а не два рассинхронных события. Семантически Shell = «оболочка перекладывается» — это оно.
        try { await RunFade(contentArea, 0d, 1d, Motion.Dur.Shell, Motion.Ease.Standard, cts.Token); }
        catch { }
        if (cts.IsCancellationRequested)
        {
            return;
        }
        contentArea.Opacity = 1d;
    }

    // ==================== Реактивный «Облегчённый режим» (lite) ====================
    // Единственная точка применения reduced-motion к ОБОЛОЧКЕ. Класс .lite обнуляет press/hover/
    // reveal-переходы и свёртку рейла (стили :is(Window).lite), а page-transition единого contentHost
    // переключается на null (мгновенный своп) под lite / на rise-fade иначе. Вызывается на старте и на
    // КАЖДОМ рантайм-переключении MotionState — переход lite происходит без перезапуска приложения.
    private void OnMotionStateChanged(object? sender, bool lite)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyMotionMode(lite);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyMotionMode(lite));
        }
    }

    private void ApplyMotionMode(bool lite)
    {
        if (lite)
        {
            if (!Classes.Contains("lite"))
            {
                Classes.Add("lite");
            }
        }
        else
        {
            Classes.Remove("lite");
        }
        // Смену вкладок «оживляет»/глушит сам SwapContent по MotionState.IsLite (rise+fade §A.4 vs
        // мгновенный своп) — отдельный page-transition единому contentHost больше не нужен (keep-alive).
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

        // 3-way gate (E3 + Bug4): SYNCING > EMPTY > CONTENT. Оверлей синхронизации перекрывает и пустой
        // онбординг, и половинчатую «Главную». Его поднимают ДВА независимых сигнала загрузки:
        //   • _isSyncing (IsImportingAccount) — пост-логин импорт: между закрытием «Входа» и приходом
        //     серверов НЕ мелькает пустой онбординг;
        //   • _isStartupLoading (IsStartupLoading) — ХОЛОДНЫЙ старт с сохранённой сессией: пока идёт
        //     восстановление аккаунта/подписок/серверов при запуске, показываем загрузку, а НЕ гейт
        //     входа (иначе у уже-вошедшего пользователя ~2с мелькал бы экран «Войдите в аккаунт»).
        // Оба сигнала снимаются только ПОСЛЕ завершения загрузки, к тому моменту _isEmpty уже false
        // (сервера пришли) → кадр уходит прямо в заполненный bodyRoot без промежуточного онбординга.
        //
        // A1: онбординг-гейт (с CTA входа) осмыслен ТОЛЬКО для вышедшего из аккаунта пользователя. Если
        // пользователь ВОШЁЛ, но подписок/серверов нет (пустой аккаунт), НЕ показываем ему снова экран
        // входа — ведём в оболочку «Главной» (там пустое состояние героя + вкладка «Аккаунт» с «Купить
        // подписку»), а не на онбординг-вход. Онбординг остаётся первым кадром только для logged-out.
        //
        // ИНВАРИАНТ гейта: _isEmpty означает «ЗНАЕМ, что пусто», и никогда «ещё не загрузили». Он
        // взводится синхронным снимком хранилища в ctor и дальше ведётся HomeViewModel.IsEmpty с тем же
        // смыслом. Пока правда неизвестна оба источника дают false → показываем оболочку, а не гейт:
        // ошибиться в сторону «показали приложение владельцу подписки» несравнимо дешевле, чем
        // встретить его приветствием «добавьте подписку».
        Control target = (_isSyncing || _isStartupLoading)
            ? accountSyncView
            : (_isEmpty && !_isLoggedIn) ? onboardingView : bodyRoot;
        CrossfadeShellTo(target);
    }

    // C4/E3: тихий кроссфейд (200мс Ease.Standard, opacity-only) между тремя оверлеями оболочки
    // (accountSyncView / onboardingView / bodyRoot). Первый показ и .lite — мгновенно (без анимации
    // на старте, чтобы окно не «проявлялось» при запуске).
    private void CrossfadeShellTo(Control target)
    {
        var previous = _currentShellView;
        if (previous == target)
        {
            target.IsVisible = true;
            target.Opacity = 1;
            return;
        }
        _currentShellView = target;

        if (MotionState.IsLite || previous is null)
        {
            accountSyncView.IsVisible = target == accountSyncView;
            onboardingView.IsVisible = target == onboardingView;
            bodyRoot.IsVisible = target == bodyRoot;
            target.Opacity = 1;
            return;
        }

        // Мгновенно прячем третий оверлей (ни target, ни previous) — страхует от прерванного кроссфейда,
        // чтобы никогда не остались видны сразу три поверхности.
        foreach (var v in new Control[] { accountSyncView, onboardingView, bodyRoot })
        {
            if (v != target && v != previous)
            {
                v.IsVisible = false;
                v.Opacity = 1;
            }
        }

        _shellAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _shellAnim = cts;

        target.Opacity = 0;
        target.IsVisible = true;
        FadeShellIn(target, cts.Token);
        FadeShellOutThenHide(previous, cts.Token);
    }

    private async void FadeShellIn(Control c, CancellationToken ct)
    {
        try { await RunFade(c, 0d, 1d, TimeSpan.FromMilliseconds(200), Motion.Ease.Standard, ct); }
        catch { }
        if (!ct.IsCancellationRequested)
        {
            c.Opacity = 1;
        }
    }

    private async void FadeShellOutThenHide(Control c, CancellationToken ct)
    {
        try { await RunFade(c, c.Opacity, 0d, TimeSpan.FromMilliseconds(200), Motion.Ease.Standard, ct); }
        catch { }
        if (ct.IsCancellationRequested)
        {
            return;
        }
        // Прячем ушедший оверлей ТОЛЬКО если он не стал снова целевым (быстрый обратный своп).
        if (c != _currentShellView)
        {
            c.IsVisible = false;
            c.Opacity = 1;
        }
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
        ApplyRailToggleTip();
    }

    // Подсказка кнопки свёртки рейла ставится императивно (ToggleRail перебивает {loc:T}-биндинг из
    // разметки), поэтому её нужно переустанавливать и при live-смене языка — см. OnLanguageChanged.
    private void ApplyRailToggleTip()
        => ToolTip.SetTip(btnRailToggle, _railCollapsed ? L.T("Nav_ExpandPanel") : L.T("Nav_CollapsePanel"));

    private void OnLanguageChanged(object? sender, EventArgs e) => ApplyRailToggleTip();

    // Bug8: интеракции буфера обмена и скана экрана. Раньше они регистрировались под WhenActivated и
    // снимались при деактивации окна. Угловой «+» открывает MenuFlyout, который может деактивировать
    // окно; к моменту вызова ReadTextFromClipboardInteraction.Handle обработчика уже не было → бросок
    // UnhandledInteractionException в незамеченную fire-and-forget задачу (_ = vm.AddViaClipboard()) →
    // тихая неудача добавления. Регистрируем на ВРЕМЯ ЖИЗНИ окна (_windowInteractions, освобождается в
    // OnClosed) — обработчик доступен из любого места независимо от активации.
    private void RegisterWindowInteractions(MainWindowViewModel vm)
    {
        vm.ReadTextFromClipboardInteraction.RegisterHandler(async interaction =>
        {
            var result = await AvaUtils.GetClipboardData(this);
            interaction.SetOutput(result);
        }).DisposeWith(_windowInteractions);

        vm.ScanScreenInteraction.RegisterHandler(async interaction =>
        {
            ShowHideWindow(false);
            await Task.Delay(200);
            var result = QRCodeAvaloniaUtils.CaptureScreen();
            ShowHideWindow(true);
            interaction.SetOutput(result);
        }).DisposeWith(_windowInteractions);
    }

    // Создаёт HomeViewModel поверх реального движка (ProfilesViewModel + StatusBarViewModel из
    // MainWindowViewModel) и отдаёт его «Главной». Индикатор рейла следует за IsConnected.
    private void SetupHome(MainWindowViewModel vm)
    {
        // Снимок хранилища из ctor едет в HomeViewModel: до первой загрузки движка он отвечает за
        // «есть ли серверы», после — живой список. Так оболочка и «Главная» не могут разойтись во мнении.
        _homeViewModel = new HomeViewModel(vm, _storedServersAtLaunch);
        // ОДИН HomeViewModel питает ОБЕ раскладки (широкую и компактную «Главную»), поэтому
        // connect-состояние, выбранный сервер, скорости и таймер одинаковы при любой ширине.
        // НО живой VM держит ТОЛЬКО активная по текущей раскладке «Главная» (BindActiveHome):
        // неактивная раскладка отвязывается, чтобы её (невиртуализованные) строки серверов
        // освобождались из памяти и она никогда не перехватывала ввод. Онбординг — лёгкий (без
        // списка серверов), поэтому его DataContext держим постоянно.
        onboardingView.DataContext = _homeViewModel;
        BindActiveHome();

        // Индикатор рейла: серый в покое, синий при подключении И в процессе подключения (P1-3).
        // Цвет ведёт класс .on (C5): BrushTransition OnSurfaceVariant↔Accent из тема-токенов.
        _homeViewModel.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting, (connected, connecting) => connected || connecting)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(active =>
            {
                if (active)
                {
                    if (!railStatusDot.Classes.Contains("on"))
                    {
                        railStatusDot.Classes.Add("on");
                    }
                }
                else
                {
                    railStatusDot.Classes.Remove("on");
                }
            });

        // E3: пока идёт пост-логин импорт (AccountViewModel.IsImportingAccount) — показываем оверлей
        // синхронизации, а НЕ пустой онбординг. Флаг взводится в тот же UI-тик, что и IsLoggedIn (до
        // первого await), поэтому оверлей уже стоит в момент закрытия LoginView — пустой кадр не мелькает.
        _accountVm.WhenAnyValue(x => x.IsImportingAccount)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(syncing =>
            {
                _isSyncing = syncing;
                ApplyShellVisibility();
            });

        // Bug4: холодный старт с уже сохранённой сессией. IsStartupLoading взводится СИНХРОННО в ctor
        // AccountViewModel (до присвоения IsLoggedIn, только при наличии persisted-сессии) и снимается в
        // finally StartupLoad — после того как импорт аккаунта + подписки + refresh «Главной» завершились.
        // Пока он true — держим оверлей загрузки вместо logged-out онбординга, чтобы у вернувшегося
        // пользователя не мелькал гейт входа. Отличается от IsImportingAccount (пост-логин импорт).
        _accountVm.WhenAnyValue(x => x.IsStartupLoading)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(loading =>
            {
                _isStartupLoading = loading;
                ApplyShellVisibility();
            });

        // Пустой старт (нет подписок): показываем ТОЛЬКО онбординг на всю ширину под chrome — оба
        // дерева скрыты. После добавления подписки (IsEmpty=false) — дерево по текущей раскладке.
        _homeViewModel.WhenAnyValue(x => x.IsEmpty)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(empty =>
            {
                _isEmpty = empty;
                ApplyShellVisibility();
            });

        // A1: вошёл/вышел из аккаунта → пере-оцениваем гейт (logged-in + пусто ведёт на Главную, а не
        // на онбординг-вход). Держится в паре с IsEmpty выше.
        _accountVm.WhenAnyValue(x => x.IsLoggedIn)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(loggedIn =>
            {
                _isLoggedIn = loggedIn;
                ApplyShellVisibility();
            });
    }

    /// <summary>
    /// Browser→app SSO callback (departamentvpn://auth?code=…): brings the window forward so the sign-in
    /// completion is visible, ensures the login sub-page is up (so the «завершаем вход через сайт…» step +
    /// success beat render), and redeems the one-time handoff code on the shared <see cref="AccountViewModel"/>
    /// — the SAME terminal path as an email/Telegram login. A duplicate/stale callback after we're already
    /// signed in is ignored.
    /// </summary>
    public void HandleAuthCallback(string code)
    {
        ShowHideWindow(true);
        Activate();
        if (_accountVm.IsLoggedIn)
        {
            return;
        }
        if (_subStack.LastOrDefault() is not LoginView)
        {
            OpenLogin();
        }
        _ = _accountVm.CompleteAppHandoff(code);
    }

    #region Sub-page host (Buy / Login / Devices / History)

    // Кладёт суб-страницу поверх контента/онбординга и показывает хост с направленным slide+fade (C2).
    private void PushSubPage(Control view)
    {
        // Идемпотентность по ТИПУ страницы. Ни один OpenBuy/OpenDevices/OpenHistory/OpenSubPage не
        // проверял стек, а строки настроек висят на голом Tapped — двойной клик по «DNS» клал ДВА
        // DnsSubView (два «назад» на выход), а по «Устройства»/«История»/«Купить» ещё и запускал второй
        // сетевой запрос из конструктора. Гард той же формы уже стоит в HandleAuthCallback.
        if (_subStack.LastOrDefault() is { } top && top.GetType() == view.GetType())
        {
            return;
        }

        _subStack.Add(view);
        subPageHost.Content = view;
        subPageHost.IsVisible = true;
        AnimateSubPageIn();
    }

    // Снимает верхнюю суб-страницу: анимирует уход текущей (translateX 0→16 + fade-out), затем
    // показывает предыдущую из стека (тем же slide+fade) либо прячет хост целиком (тогда снова виден
    // шелл-контент или онбординг, смотря по IsEmpty/IsSyncing).
    private void PopSubPage()
    {
        if (_subStack.Count > 0)
        {
            _subStack.RemoveAt(_subStack.Count - 1);
        }
        var next = _subStack.Count > 0 ? _subStack[^1] : null;
        AnimateSubPageOut(next);
    }

    // ==================== Направленный slide+fade суб-страниц (C2) ====================
    // Push (вперёд, вглубь) = входящая translateX 16→0 + opacity 0→1, 300мс Ease.OutQuint.
    // Pop (назад)          = уходящая translateX 0→16 + opacity 1→0, 200мс Ease.Standard (выход
    // быстрее входа). ТОЛЬКО translate+opacity (никаких scale/rotate — страница не «улетает» из угла).
    // Под .lite — мгновенно (как contentHost). subPageHost перекрывает шелл непрозрачным Brush.Bg,
    // поэтому «под» уходящей страницей аккуратно проступает шелл/онбординг.
    private async void AnimateSubPageIn()
    {
        if (MotionState.IsLite)
        {
            subPageHost.Opacity = 1;
            subPageHost.RenderTransform = null;
            return;
        }
        _subPageAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _subPageAnim = cts;

        subPageHost.Opacity = 0;
        try { await RunTranslateFade(subPageHost, TranslateTransform.XProperty, 16d, 0d, 0d, 1d, TimeSpan.FromMilliseconds(300), Motion.Ease.OutQuint, cts.Token); }
        catch { }
        if (cts.IsCancellationRequested)
        {
            return;
        }
        subPageHost.Opacity = 1;
        subPageHost.RenderTransform = null;
    }

    private async void AnimateSubPageOut(Control? next)
    {
        if (MotionState.IsLite)
        {
            ApplySubPageResult(next);
            return;
        }
        _subPageAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _subPageAnim = cts;

        try { await RunTranslateFade(subPageHost, TranslateTransform.XProperty, 0d, 16d, 1d, 0d, TimeSpan.FromMilliseconds(200), Motion.Ease.Standard, cts.Token); }
        catch { }
        if (cts.IsCancellationRequested)
        {
            return;
        }
        ApplySubPageResult(next);
        if (next != null)
        {
            AnimateSubPageIn();   // предыдущая страница въезжает тем же slide+fade
        }
    }

    private void ApplySubPageResult(Control? next)
    {
        if (next != null)
        {
            subPageHost.Content = next;
        }
        else
        {
            subPageHost.Content = null;
            subPageHost.IsVisible = false;
            subPageHost.Opacity = 1;
            subPageHost.RenderTransform = null;
        }
    }

    // «Купить подписку»: BuyView со своим BuyViewModel (грузит каталог в ctor).
    public void OpenBuy()
    {
        // Гард ДО конструктора: BuyView/DevicesView/PaymentHistoryView грузят данные из ctor, поэтому
        // одного дедупа в PushSubPage мало — второй клик иначе выстрелит вторым сетевым запросом.
        if (_subStack.LastOrDefault() is BuyView)
        {
            return;
        }

        var view = new BuyView();
        view.BackRequested += (_, _) => PopSubPage();
        PushSubPage(view);
    }

    // «Устройства»: DevicesView со своим DevicesViewModel (uuid активной подписки резолвит сам
    // из вошедшего профиля; список грузится в ctor).
    public void OpenDevices()
    {
        if (_subStack.LastOrDefault() is DevicesView)
        {
            return;
        }

        var view = new DevicesView();
        view.BackRequested += (_, _) => PopSubPage();
        PushSubPage(view);
    }

    // «История платежей»: PaymentHistoryView; пустой CTA «Купить подписку» ведёт на Buy поверх.
    public void OpenHistory()
    {
        if (_subStack.LastOrDefault() is PaymentHistoryView)
        {
            return;
        }

        var view = new PaymentHistoryView();
        view.BackRequested += (_, _) => PopSubPage();
        view.BuyRequested += (_, _) => OpenBuy();
        PushSubPage(view);
    }

    // «Вход»: LoginView на ОБЩЕМ AccountViewModel — состояние входа видит и вкладка «Аккаунт».
    // BackRequested поднимается и по кнопке «назад», и по успешному входу (закрывает суб-страницу).
    public void OpenLogin()
    {
        // Идемпотентность — тот же гард, что уже стоит в HandleAuthCallback. Вторая активация CTA входа
        // (клавиатурой: кнопка онбординга остаётся сфокусированной и включённой под непрозрачным хостом,
        // а Enter/пробел идут в фокус, а не по хит-тесту) клала ВТОРОЙ LoginView на стек и отцепляла
        // первый; после успешного входа первый возвращался и залипал с галочкой «успех», потому что
        // хэндофф на нём уже запрещён. VM общая, так что повторный вызов и так попадает в живую вью.
        if (_subStack.LastOrDefault() is LoginView)
        {
            return;
        }

        var view = new LoginView { DataContext = _accountVm };
        view.BackRequested += (_, _) =>
        {
            // A10: настоящее закрытие «Входа» (кнопка «назад» / бэк) — отменяем опрос Telegram-логина,
            // иначе он тикает до ~3 мин на снятой странице и поздний Confirmed/Error ещё дёргает UI.
            // При УСПЕХЕ (IsLoggedIn) НЕ отменяем: опрос уже завершился, а дальше кадр ведёт оверлей
            // синхронизации (IsImportingAccount) — CancelLogin тут был бы лишним.
            if (!_accountVm.IsLoggedIn)
            {
                _accountVm.CancelLogin();
            }
            PopSubPage();
        };
        PushSubPage(view);
    }

    // Онбординг «Войти через Telegram»: НЕ показываем промежуточный выбор метода — сразу стартуем
    // Telegram-авторизацию на ОБЩЕМ AccountViewModel (открывает Telegram deep link) и открываем
    // LoginView, который переходит в состояние ожидания подтверждения по CurrentLoginState.
    public void OpenLoginTelegram()
    {
        OpenLogin();
        // onError is MANDATORY: ReactiveCommand.Execute() faults the returned sequence as well as
        // ThrownExceptions, and a parameterless Subscribe() installs a rethrowing stub that kills the
        // process. Route it to the account error surface so the user reads a real message.
        _accountVm.LoginTelegramCmd.Execute().Subscribe(_ => { }, _accountVm.ReportCommandException);
    }

    // Онбординг «Войти через сайт»: открываем LoginView (чтобы возврату из браузера было куда сесть и
    // чтобы показать шаг «завершаем вход через сайт…») И СРАЗУ запускаем браузер-хэндофф (§A1): сайт
    // /app-login чеканит одноразовый код у залогиненной веб-сессии и возвращается по departamentvpn://auth.
    public void OpenLoginSite()
    {
        OpenLogin();
        _accountVm.LoginBrowserCmd.Execute().Subscribe(_ => { }, _accountVm.ReportCommandException);
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
                // Масштабируем цель на _uiScale (см. ToggleLayoutSize): раскладка живёт в координатах контента.
                ResizeClamped(WideToggleWidth * _uiScale, WideToggleHeight * _uiScale);
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
        // Цели тумблера — в ФИЗ. размере окна, поэтому масштабируем на _uiScale: тумблер задаёт РАСКЛАДКУ,
        // а брейкпоинт живёт в координатах контента (Bounds/_uiScale). Без умножения на высоком zoom «широкая»
        // цель в контенте оказалась бы < 760 и раскладка не переключилась бы. ApplySizeCentered клампит в экран.
        if (_compactMode)
        {
            AnimateWindowSize(WideToggleWidth * _uiScale, WideToggleHeight * _uiScale);
        }
        else
        {
            AnimateWindowSize(CompactToggleWidth * _uiScale, CompactToggleHeight * _uiScale);
        }
    }

    // Bug4: истина, если источник двойного клика лежит ВНУТРИ любого интерактивного контрола (нав-кнопки,
    // кнопки нижнего бара, кнопки свёртки рейла). Поднимаемся по визуальному дереву от источника: встретив
    // Button РАНЬШЕ, чем host (railHost/bottomNav), считаем клик «по кнопке» → тумблер размера НЕ срабатывает.
    // Дойдя до самого host, не встретив кнопки, — это пустая хром-область, тумблер разрешён (false).
    private bool IsWithinInteractive(Visual? source)
    {
        for (var v = source; v is not null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, railHost) || ReferenceEquals(v, bottomNav))
            {
                return false;
            }
            if (v is Button)
            {
                return true;
            }
        }
        return false;
    }

    // ==================== Bug6: плавная анимация размера окна (тумблер компакт⇄широкая) ====================
    // Раньше тумблер жёстко «щёлкал» размер (ResizeClamped мгновенно), из-за чего разворот/сворачивание
    // ощущались рывком. Теперь размер плавно интерполируется (OutQuint ~200мс), удерживая ТЕКУЩИЙ центр
    // окна зафиксированным (тот же center-anchor + кламп в WorkingArea, что у ResizeClamped, но центр
    // берётся ОДИН раз в начале — без дрейфа). По ходу анимации Bounds-вотчер один раз пересекает
    // брейкпоинт и меняет раскладку (с невидимой пересборкой контента, Bug6 выше) — контент морфится
    // синхронно с ростом окна. Под .lite (или без экрана) — мгновенно, как прежде. Перезапуск отменяет
    // предыдущую анимацию; _edgeSnapSuspended гасит drag-to-edge на всё время прогона.
    private async void AnimateWindowSize(double targetWidth, double targetHeight)
    {
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (MotionState.IsLite || screen is null)
        {
            ResizeClamped(targetWidth, targetHeight);
            return;
        }

        _resizeAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _resizeAnim = cts;

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        // Центр фиксируем ОДНОКРАТНО на старте (физ. пиксели) → окно растёт/сжимается «на месте».
        var centerX = Position.X + (Width * scaling / 2);
        var centerY = Position.Y + (Height * scaling / 2);
        var startWidth = Width;
        var startHeight = Height;

        _edgeSnapSuspended = true;
        try
        {
            var startTicks = Environment.TickCount64;
            const double durationMs = 200d;
            while (true)
            {
                var t = Math.Min(1d, (Environment.TickCount64 - startTicks) / durationMs);
                var eased = Motion.Ease.OutQuint.Ease(t);
                var w = startWidth + ((targetWidth - startWidth) * eased);
                var h = startHeight + ((targetHeight - startHeight) * eased);
                ApplySizeCentered(w, h, centerX, centerY, screen, scaling);
                if (t >= 1d)
                {
                    break;
                }
                await Task.Delay(16, cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            // Снимаем подавление edge-snap только если нас не сменила новая анимация (та уже взвела его).
            if (_resizeAnim == cts)
            {
                _edgeSnapSuspended = false;
            }
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
    // Растёт/сжимается НА МЕСТЕ: держим ТЕКУЩИЙ центр окна фиксированным, а не «телепортируем» рамку
    // в центр экрана (владелец: при разворачивании окно «улетало в угол, потом расширялось»). Позицию
    // ставим ДО размера, обе в одном синхронном проходе (до следующего кадра) → без промежуточной
    // отрисовки крупной рамки в старом углу.
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

            // Якорь = текущий геометрический центр окна (физ. пиксели).
            var centerX = Position.X + (Width * scaling / 2);
            var centerY = Position.Y + (Height * scaling / 2);

            ApplySizeCentered(width, height, centerX, centerY, screen, scaling);
        }
        catch { }
        finally
        {
            _edgeSnapSuspended = false;
        }
    }

    // Ставит размер и КЛАМПИТ его + позицию в WorkingArea вокруг ЗАДАННОГО центра (физ. пиксели): окно
    // всегда целиком на экране (верх никогда не за границей). Центр передаётся явно, поэтому годится и
    // для одиночного ResizeClamped (центр = текущий), и для покадровой анимации AnimateWindowSize
    // (центр фиксирован на старте → без дрейфа). Позицию ставим ДО размера — без промежуточного кадра.
    private void ApplySizeCentered(double width, double height, double centerX, double centerY, Screen screen, double scaling)
    {
        var wa = screen.WorkingArea;
        var maxW = wa.Width / scaling;
        var maxH = wa.Height / scaling;

        var w = Math.Clamp(width, Math.Min(MinWidth, maxW), maxW);
        var h = Math.Clamp(height, Math.Min(MinHeight, maxH), maxH);

        var physW = w * scaling;
        var physH = h * scaling;

        var x = centerX - (physW / 2);
        var y = centerY - (physH / 2);
        x = Math.Max(wa.X, Math.Min(x, wa.X + wa.Width - physW));
        y = Math.Max(wa.Y, Math.Min(y, wa.Y + wa.Height - physH));

        Position = new PixelPoint((int)x, (int)y);
        Width = w;
        Height = h;
    }

    // ==================== Ресайз безрамочного окна (грипы → BeginResizeDrag) ====================
    // 8 прозрачных грипов (XAML resizeGripHost) на PointerPressed зовут кроссплатформенный
    // Window.BeginResizeDrag(WindowEdge, e). Он сосуществует с остальным chrome без конфликтов:
    //   • title-drag: North-грип лежит ПОВЕРХ верхних 6px title-bar → хит-тест выбирает грип (верхний
    //     z-order), нажатие ниже 6px уходит в titleBar → перенос. Разные зоны, не спорят.
    //   • edge-snap (OnPositionChanged): гейтится _titleDragging, который ресайз НЕ взводит → не мигает.
    //   • auto-swap 760: ресайз меняет Bounds → Bounds-вотчер живьём переключает раскладку (желаемо).
    //   • max-кнопка: грипы видимы только в Normal (WindowStateProperty-вотчер), BeginResize тоже гейтит.
    private void WireResizeGrips()
    {
        gripNW.PointerPressed += (_, e) => BeginResize(WindowEdge.NorthWest, e);
        gripN.PointerPressed += (_, e) => BeginResize(WindowEdge.North, e);
        gripNE.PointerPressed += (_, e) => BeginResize(WindowEdge.NorthEast, e);
        gripW.PointerPressed += (_, e) => BeginResize(WindowEdge.West, e);
        gripE.PointerPressed += (_, e) => BeginResize(WindowEdge.East, e);
        gripSW.PointerPressed += (_, e) => BeginResize(WindowEdge.SouthWest, e);
        gripS.PointerPressed += (_, e) => BeginResize(WindowEdge.South, e);
        gripSE.PointerPressed += (_, e) => BeginResize(WindowEdge.SouthEast, e);
    }

    // Стартует нативный ресайз за указанный край/угол. Только ЛКМ и только в Normal (в maximized/minimized
    // ресайз бессмыслен). Handled — чтобы нажатие не «протекло» дальше. BeginResizeDrag кроссплатформенный
    // (на Windows блокирует до конца тяги, на X11/Wayland отдаёт WM) и сам уважает MinWidth/MinHeight —
    // потолка по ширине нет, поэтому окно тянется вплоть до края экрана.
    private void BeginResize(WindowEdge edge, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        BeginResizeDrag(edge, e);
    }

    #region In-app UI-масштаб (zoom)

    // Реакция на смену фактора извне (настройки/горячие клавиши). Единый путь применения: трансформ +
    // мин-размеры + пере-кламп окна + пере-оценка брейкпоинта. Персист делает ИНИЦИАТОР (SetUiScale/настройки),
    // здесь только применение, поэтому смена из настроек и из клавиш дают идентичный результат.
    private void OnUiScaleChanged(object? sender, double scale)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyUiScale(scale);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyUiScale(scale));
        }
    }

    private void ApplyUiScale(double scale)
    {
        _uiScale = UiScaleState.Clamp(scale);
        ApplyUiScaleToWindow();

        // Окно могло стать меньше нового мин-размера (zoom вырос) — переклампим текущий размер в экран
        // (вырастет до мин, останется по центру, не уедет за край). ResizeClamped уже уважает MinWidth/Height.
        if (WindowState == WindowState.Normal)
        {
            ResizeClamped(Width, Height);
        }

        // Брейкпоинт компакт/широкая — в координатах контента (Bounds/_uiScale). На старте Bounds=0 → no-op.
        if (Bounds.Width > 0)
        {
            UpdateLayoutMode(Bounds.Width / _uiScale);
        }
    }

    // Применяет ТЕКУЩИЙ _uiScale к корневому ScaleTransform и к мин-размерам окна. Мин-размер контента
    // растёт с zoom (контенту нужно _base*scale физ. DIP, иначе клип); клампим под рабочую область, чтобы
    // MinWidth/Height НИКОГДА не превысили экран (иначе Avalonia распёрла бы окно за его пределы). НЕ
    // персистит и не трогает layout-режим сам по себе.
    private void ApplyUiScaleToWindow()
    {
        // Ставим/обновляем масштаб корня через сам LayoutTransformControl (у ScaleTransform внутри
        // property-элемента компилятор XAML не генерирует поле — обращаемся к хосту).
        if (uiScaleHost is not null)
        {
            uiScaleHost.LayoutTransform = new ScaleTransform(_uiScale, _uiScale);
        }

        // По умолчанию (в т.ч. если экран ещё не доступен — вызов из ctor до реализации окна) — просто
        // base*scale без клампа; фактический размер окна на старте всё равно клампит WindowBase.OnLoaded, а
        // MainWindow.OnLoaded ниже повторно зовёт этот метод уже с доступным экраном.
        var minW = _baseMinWidth * _uiScale;
        var minH = _baseMinHeight * _uiScale;
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is not null)
            {
                var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
                // Клампим мин под рабочую область: MinWidth/Height НИКОГДА не должны превысить экран, иначе
                // Avalonia распёрла бы окно за его пределы (высокий zoom на маленьком дисплее).
                minW = Math.Min(minW, screen.WorkingArea.Width / scaling);
                minH = Math.Min(minH, screen.WorkingArea.Height / scaling);
            }
        }
        catch { }
        MinWidth = minW;
        MinHeight = minH;
    }

    // Горячие клавиши: сдвиг на шаг (Ctrl +/Ctrl −) и установка точного значения (Ctrl 0 = сброс).
    private void NudgeUiScale(double delta) => SetUiScale(_uiScale + delta);

    private void SetUiScale(double scale)
    {
        var clamped = UiScaleState.Clamp(scale);
        if (Math.Abs(clamped - _uiScale) < 0.0001)
        {
            return;   // уже на границе — незачем писать конфиг/дёргать применение
        }
        PersistUiScale(clamped);
        UiScaleState.Set(clamped);   // → OnUiScaleChanged применит трансформ/мин/раскладку (+ строку настроек)
    }

    private void PersistUiScale(double scale)
    {
        _config.UiItem.UiScale = scale;
        _ = ConfigHandler.SaveConfig(_config);
    }

    #endregion In-app UI-масштаб (zoom)

    #endregion Nav & Chrome

    #region Theme transition (круговая заливка смены темы)

    // ==================== Плавная смена темы: круговая «заливка» новой темы ====================
    // App.ApplyTheme (обе кнопки настроек: смена базы Тёмная↔Светлая и монохромный оверлей) вместо
    // мгновенного свопа зовёт этот хук. Техника: снимок ТЕКУЩЕЙ темы (RenderTargetBitmap) кладём поверх
    // окна → applySwap перекрашивает живые контролы ПОД снимком (без вспышки, один синхронный тик) →
    // снимок «вытекает» расширяющимся круговым клипом из точки нажатия по тумблеру, открывая новую тему.
    // OutQuint ~520мс, одноразово, покадрово (16мс). В lite/reduced-motion — мгновенный своп без снимка.
    // Bitmap освобождается по завершении/отмене (FinishThemeTransition) — утечки нет; оверлей hit-test-
    // прозрачен и скрыт в покое, поэтому UI полностью интерактивен во время и после перехода.
    private CancellationTokenSource? _themeAnim;
    private RenderTargetBitmap? _themeSnapshot;
    private Point? _lastPointerInWindow;
    private static readonly TimeSpan ThemeRevealDuration = TimeSpan.FromMilliseconds(520);

    // Точку старта заливки берём относительно chromeRoot (а НЕ окна): именно chromeRoot снимается в bitmap
    // и его Bounds задают w/h заливки. Под UI-zoom окно и chromeRoot в РАЗНЫХ координатах (контент масштабирован
    // LayoutTransformControl); координаты chromeRoot совпадают с w/h при любом факторе. При _uiScale=1.0 —
    // тождественно прежнему GetPosition(this) (chromeRoot в начале координат окна).
    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
        => _lastPointerInWindow = e.GetPosition(chromeRoot);

    // Хук из App.ApplyTheme. applySwap = мгновенная перекраска (RequestedThemeVariant + моно-оверлей).
    private void RunThemeTransition(Action applySwap)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RunThemeTransition(applySwap));
            return;
        }

        // Escape-hatch владельца: в lite/reduced-motion, пока окно скрыто/не разложено — мгновенно, без снимка.
        var w = chromeRoot.Bounds.Width;
        var h = chromeRoot.Bounds.Height;
        if (MotionState.IsLite || !IsVisible || w < 1 || h < 1)
        {
            applySwap();
            return;
        }

        // Снимаем любой незавершённый предыдущий переход (быстрый повторный тумблер) и освобождаем его bitmap.
        CancelThemeTransition();

        RenderTargetBitmap? snapshot = null;
        try
        {
            var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
            var px = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(w * scaling)),
                Math.Max(1, (int)Math.Ceiling(h * scaling)));
            snapshot = new RenderTargetBitmap(px, new Vector(96 * scaling, 96 * scaling));
            snapshot.Render(chromeRoot);
        }
        catch
        {
            // Редкий сбой рендера → безопасный откат в мгновенную смену (снимок не оставляем).
            snapshot?.Dispose();
            applySwap();
            return;
        }

        _themeSnapshot = snapshot;
        themeTransitionImage.Source = snapshot;
        // Подложка снимка = СТАРЫЙ Brush.Bg (до свопа): закрывает прозрачную полосу заголовка (Grid без фона),
        // чтобы под ней не «просвечивала» новая тема до прихода круга.
        themeTransitionOverlay.Background = ResolveThemeBrush("Brush.Bg");
        themeTransitionOverlay.Clip = null;
        themeTransitionOverlay.Opacity = 1d;
        themeTransitionOverlay.IsVisible = true;

        // Новая тема применяется СЕЙЧАС — под непрозрачным снимком. Живые контролы перекрашиваются скрыто,
        // без вспышки: кадр ещё не скомпонован, всё синхронно на UI-потоке (снимок сверху = полный, без клипа).
        applySwap();

        var cts = new CancellationTokenSource();
        _themeAnim = cts;
        AnimateThemeReveal(w, h, cts);
    }

    private async void AnimateThemeReveal(double w, double h, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var origin = ResolveThemeOrigin(w, h);
        var maxRadius = MaxCornerDistance(origin, w, h);
        var rect = new RectangleGeometry(new Rect(0, 0, w, h));

        var startTicks = Environment.TickCount64;
        var durationMs = ThemeRevealDuration.TotalMilliseconds;
        try
        {
            while (true)
            {
                var t = Math.Min(1d, (Environment.TickCount64 - startTicks) / durationMs);
                var radius = Motion.Ease.OutQuint.Ease(t) * maxRadius;
                // Клип = прямоугольник окна МИНУС растущий круг: снимок виден только СНАРУЖИ круга, внутри
                // проступает уже перекрашенная новая тема. Круг растёт из точки нажатия → «заливка» новой темы.
                var hole = new EllipseGeometry { Center = origin, RadiusX = radius, RadiusY = radius };
                themeTransitionOverlay.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, rect, hole);
                if (t >= 1d)
                {
                    break;
                }
                await Task.Delay(16, ct);
            }
        }
        catch (OperationCanceledException) { return; }
        catch { }

        // Финализируем только если нас не сменил/не отменил новый переход.
        if (ct.IsCancellationRequested || _themeAnim != cts)
        {
            return;
        }
        FinishThemeTransition();
    }

    // Старт заливки = последнее нажатие в окне (тап по тумблеру темы), если оно в пределах окна; иначе центр.
    private Point ResolveThemeOrigin(double w, double h)
    {
        if (_lastPointerInWindow is { } p && p.X >= 0 && p.Y >= 0 && p.X <= w && p.Y <= h)
        {
            return p;
        }
        return new Point(w / 2, h / 2);
    }

    // Радиус, чтобы круг накрыл самый дальний угол окна от точки старта (полная заливка).
    private static double MaxCornerDistance(Point o, double w, double h)
    {
        var dx = Math.Max(o.X, w - o.X);
        var dy = Math.Max(o.Y, h - o.Y);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    // Тема-кисть по ключу в ТЕКУЩЕМ варианте (совпадает с паттерном ConnectHeroView/SubscriptionMetaView).
    private IBrush? ResolveThemeBrush(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : null;

    // Снятие оверлея + освобождение снимка (нет утечки RenderTargetBitmap). Идемпотентно.
    private void FinishThemeTransition()
    {
        _themeAnim = null;
        themeTransitionOverlay.IsVisible = false;
        themeTransitionOverlay.Clip = null;
        themeTransitionOverlay.Background = null;
        themeTransitionImage.Source = null;
        _themeSnapshot?.Dispose();
        _themeSnapshot = null;
    }

    private void CancelThemeTransition()
    {
        _themeAnim?.Cancel();
        FinishThemeTransition();
    }

    #endregion Theme transition

    #region Event

    private void OnProgramStarted(object state, bool timeout)
    {
        Dispatcher.UIThread.Post(() =>
                ShowHideWindow(true),
            DispatcherPriority.Default);
    }

    // ==================== Нижняя пилюля-тост ОТКЛЮЧЕНА; фидбэк уходит в панель сообщений (Bug8) ====================
    // Владелец: НИКАКИХ всплывающих нижних тостов (snackHost) — ни на подключении/отключении, ни на
    // добавлении/обновлении подписки. Пилюля снизу по-прежнему НЕ показывается (snackHost остаётся
    // скрытым). Но раньше это был ПОЛНЫЙ no-op: весь фидбэк добавления (пустой буфер, неверные данные,
    // дубликат, успех) шёл через NoticeManager.Enqueue → это событие и молча ПРОПАДАЛ → «добавляю
    // подписку — ничего не происходит, без объяснений». Теперь вместо тоста маршрутизируем текст в
    // ИНЛАЙН-панель сообщений (NoticeManager.SendMessage → SendMsgViewRequested → MsgViewModel-лог) —
    // не плавающий тост, а лог-поверхность (owner-aligned): исход добавления больше не теряется.
    // (SubscriptionImportLogHandler в MainWindowViewModel уже пишет прогресс скачивания в ту же панель.)
    private Task DelegateSnackMsg(string content)
    {
        NoticeManager.Instance.SendMessage(content);
        return Task.CompletedTask;
    }

    // ==================== Общие аниматоры оболочки (transform+opacity, §A) ====================
    // Все переходы MainWindow строятся из этих двух примитивов: чистый fade и translate+fade (две
    // параллельные анимации на одном визуале — opacity и translate идут разными аниматорами Avalonia,
    // ровно как в SwapContent-переходе вкладок). FillMode.Forward держит конечный кадр до сброса.
    private static Task RunFade(Visual target, double fromO, double toO, TimeSpan duration, Easing easing, CancellationToken ct)
    {
        var fade = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, fromO) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, toO) } },
            },
        };
        return fade.RunAsync(target, ct);
    }

    private static Task RunTranslateFade(Visual target, AvaloniaProperty axis, double fromT, double toT, double fromO, double toO, TimeSpan duration, Easing easing, CancellationToken ct)
    {
        var fade = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, fromO) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, toO) } },
            },
        };
        var slide = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(axis, fromT) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(axis, toT) } },
            },
        };
        return Task.WhenAll(fade.RunAsync(target, ct), slide.RunAsync(target, ct));
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

    // Bug8: освобождаем интеракции, зарегистрированные на время жизни окна (буфер/скан). Окно реально
    // закрывается только при завершении приложения (обычное закрытие уходит в трей через OnClosing),
    // поэтому одноразового освобождения здесь достаточно.
    protected override void OnClosed(EventArgs e)
    {
        _windowInteractions.Dispose();
        // Снимаем СТАТИЧЕСКИЕ подписки: MotionState/UiScaleState/App — статика, поэтому закрытое окно
        // оставалось достижимым на весь процесс, а App.ApplyTheme мог заново войти в RunThemeTransition
        // на мёртвом окне (оно трогает chromeRoot.Bounds ДО спасительной проверки !IsVisible).
        UiScaleState.Changed -= OnUiScaleChanged;
        MotionState.Changed -= OnMotionStateChanged;
        if (App.ThemeTransitionHook?.Target == this)
        {
            App.ThemeTransitionHook = null;
        }
        _programStartedWait?.Unregister(null);
        _programStartedWait = null;
        // If the window closes mid theme-transition, cancel the reveal loop's token and dispose the
        // RenderTargetBitmap snapshot (else it's left to GC and the async-void loop keeps poking a
        // closing window). No-op when no transition is in flight.
        CancelThemeTransition();
        // P1-4: снимаем незавершённое скольжение путешествующего индикатора рейла (та же CTS-дисциплина,
        // что у остальных узлов) — async-void аниматор не дёргает закрывающееся окно.
        // Та же CTS-дисциплина для ВСЕХ узлов, а не для двух из семи. Особенно важен _resizeAnim: это
        // рукописный 60-Гц цикл, который пишет Position/Width/Height ЗАКРЫВАЮЩЕГОСЯ окна.
        _indicatorAnim?.Cancel();
        _subPageAnim?.Cancel();
        _shellAnim?.Cancel();
        _layoutAnim?.Cancel();
        _resizeAnim?.Cancel();
        _contentAnim?.Cancel();
        base.OnClosed(e);
    }

    private async void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl (или ⌘) присутствует — но НЕ обязательно один: Ctrl «+» на большинстве раскладок = Ctrl+Shift+=,
        // поэтому масштаб проверяем по НАЛИЧИЮ Control/Meta (допуская Shift), а V/S/F5 — по прежней точной маске.
        var zoomMod = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (zoomMod)
        {
            switch (e.Key)
            {
                // Увеличить: Ctrl «=» / Ctrl «+» (OemPlus) и «+» цифрового блока (Add).
                case Key.OemPlus:
                case Key.Add:
                    NudgeUiScale(UiScaleState.Step);
                    e.Handled = true;
                    return;

                // Уменьшить: Ctrl «−» (OemMinus) и «−» цифрового блока (Subtract).
                case Key.OemMinus:
                case Key.Subtract:
                    NudgeUiScale(-UiScaleState.Step);
                    e.Handled = true;
                    return;

                // Сброс к 100%: Ctrl 0 (верхний ряд / цифровой блок).
                case Key.D0:
                case Key.NumPad0:
                    SetUiScale(UiScaleState.Default);
                    e.Handled = true;
                    return;
            }
        }

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

    // Оба помощника ниже вызываются из async void KeyDown, поэтому ловят сами: непойманный бросок из
    // импорта движка стал бы необработанным исключением на UI-потоке, а не строкой в панели сообщений.
    public async Task AddServerViaClipboardAsync()
    {
        try
        {
            var clipboardData = await AvaUtils.GetClipboardData(this);
            if (clipboardData.IsNotEmpty() && ViewModel != null)
            {
                await ViewModel.AddServerViaClipboardAsync(clipboardData);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AddServerViaClipboard", ex);
            NoticeManager.Instance.SendMessage(ex.Message);
        }
    }

    public async Task ScanScreenTaskAsync()
    {
        ShowHideWindow(false);
        try
        {
            await Task.Delay(200);

            var bytes = QRCodeAvaloniaUtils.CaptureScreen();
            if (bytes != null && ViewModel != null)
            {
                await ViewModel.ScanScreenResult(bytes);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ScanScreen", ex);
            NoticeManager.Instance.SendMessage(ex.Message);
        }
        finally
        {
            // В finally: без этого бросок оставлял ОКНО СКРЫТЫМ, и вернуть его можно было только через
            // иконку в трее.
            ShowHideWindow(true);
        }
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
        // Экран теперь доступен — переклампим мин-размеры под UI-масштаб корректно (в ctor Screens мог быть
        // ещё не готов). base.OnLoaded уже вписал стартовый размер окна в рабочую область.
        ApplyUiScaleToWindow();
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
