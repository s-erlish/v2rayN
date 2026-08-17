using Microsoft.Win32;
using ServiceLib.Handler.SysProxy;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var viewLocator = SimpleViewLocator.Instance;
        DataTemplates.Add(viewLocator);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (!Design.IsDesignMode)
            {
                AppManager.Instance.InitComponents();
                DataContext = StatusBarViewModel.Instance;

                // Оформление из конфига ДО построения окна: первый кадр рисуется правильной темой.
                // База Тёмная/Светлая + отдельный МОНОХРОМНЫЙ (чёрно-белый) оверлей поверх неё.
                var cfg = AppManager.Instance.Config;
                ApplyTheme(cfg.UiItem.CurrentTheme, cfg.UiItem.BlackTheme);

                // Локализация из конфига ДО построения окна: первый кадр рисуется на нужном языке
                // (ru/en) и все {loc:T} биндинги открываются уже правильными. Живое переключение
                // без перезапуска — см. L.SetLanguage.
                L.Init();

                // Remnawave HWID: send the same stable per-machine device id the account API uses
                // (X-HWID) on every subscription GET, so a panel with HWID device-limit enabled serves
                // the real server list instead of the «Приложение не поддерживается» placeholder.
                ServiceLib.Global.SubscriptionHwidProvider = () => v2rayN.Desktop.Account.AuthTokenStore.DeviceId();
            }

            var mainWindowViewModel = new MainWindowViewModel();
            var mainWindow = (MainWindow)viewLocator.Build(mainWindowViewModel);
            mainWindow.ViewModel = mainWindowViewModel;
            desktop.MainWindow = mainWindow;

            // idle/perf B5: пометить UI как скрытый, пока окно свёрнуто, чтобы простаивающие защиты
            // (циклы статистики и любые проверки по ShowInTaskbar) считали «свёрнуто в панель задач»
            // тем же, что «скрыто в трей». Наблюдение живёт всё время работы приложения (одно окно);
            // GetObservable сразу отдаёт текущее состояние и далее — изменения.
            mainWindow
                .GetObservable(Window.WindowStateProperty)
                .Subscribe(state => AppManager.Instance.IsWindowMinimized = state == WindowState.Minimized);

            if (!Design.IsDesignMode)
            {
                SetupTrayMenu();
                SetupConnectivityHooks(desktop);
                SetupAppHandoff();
            }

            if (OperatingSystem.IsMacOS())
            {
                Current?.TryGetFeature<IActivatableLifetime>()?.Activated += OnMacOSActivated;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    #region MacOS Activation

    private void OnMacOSActivated(object? sender, ActivatedEventArgs args)
    {
        if (args.Kind != ActivationKind.Reopen)
        {
            return;
        }

        if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not MainWindow mainWindow)
        {
            return;
        }

        var isMiniaturized = MacAppUtils.IsWindowMiniaturized(mainWindow);

        Dispatcher.UIThread.Post(() =>
        {
            if (isMiniaturized)
            {
                RestoreMacOSAccessoryPolicyAfterMiniaturize(mainWindow);
                mainWindow.ShowHideWindow(true);
                return;
            }

            if (!AppManager.Instance.Config.UiItem.MacOSShowInDock)
            {
                MacAppUtils.SetActivationPolicyAccessory();
            }

            mainWindow.ShowHideWindow(true);
        });
    }

    private static void RestoreMacOSAccessoryPolicyAfterMiniaturize(MainWindow mainWindow)
    {
        if (AppManager.Instance.Config.UiItem.MacOSShowInDock)
        {
            return;
        }

        mainWindow
            .GetObservable(Window.WindowStateProperty)
            .Skip(1)
            .Where(state => state != WindowState.Minimized)
            .Take(1)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => QueueMacOSAccessoryPolicyRestore(mainWindow));
    }

    private static void QueueMacOSAccessoryPolicyRestore(MainWindow mainWindow)
    {
        // AppKit may keep isMiniaturized set until the Dock restore animation finishes.
        DispatcherTimer.RunOnce(() => RestoreMacOSAccessoryPolicy(mainWindow), TimeSpan.FromMilliseconds(300));
    }

    private static void RestoreMacOSAccessoryPolicy(MainWindow mainWindow)
    {
        if (AppManager.Instance.Config.UiItem.MacOSShowInDock || MacAppUtils.IsWindowMiniaturized(mainWindow))
        {
            return;
        }

        MacAppUtils.SetActivationPolicyAccessory();
        mainWindow.Activate();
        mainWindow.Focus();
    }

    #endregion MacOS Activation

    #region Browser→app SSO handoff (departamentvpn://auth?code=…)

    /// <summary>
    /// Registers the <c>departamentvpn://</c> URL scheme (Windows, per-user, no admin) so the OS routes the
    /// site's <c>/app-login</c> return back to the app, and wires the receiver that redeems the handoff
    /// code. The scheme name matches the site's safe-return allowlist (<c>^departament[a-z0-9]*$</c>), so a
    /// browser→app return needs zero site changes. On non-Windows the registry step is skipped (guarded);
    /// the pipe receiver + «войти по коду» fallback still work.
    /// </summary>
    private void SetupAppHandoff()
    {
        RegisterAuthScheme();
        // The pipe/cold-start URL is delivered on a background thread; marshal to the UI thread to route it.
        AppHandoffChannel.SetHandler(url => Dispatcher.UIThread.Post(() => OnAuthCallbackUrl(url)));
    }

    private static void RegisterAuthScheme()
    {
        if (!Utils.IsWindows())
        {
            return;
        }
        try
        {
            var exe = Environment.ProcessPath;
            if (exe.IsNullOrEmpty())
            {
                return;
            }
            using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\departamentvpn");
            root.SetValue(null, "URL:departament protocol");
            root.SetValue("URL Protocol", string.Empty);
            using (var icon = root.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, $"\"{exe}\",0");
            }
            using (var cmd = root.CreateSubKey(@"shell\open\command"))
            {
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("RegisterAuthScheme", ex);
        }
    }

    private void OnAuthCallbackUrl(string url)
    {
        try
        {
            var code = ParseHandoffCode(url);
            if (code.IsNullOrEmpty())
            {
                return;
            }
            if ((ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow mainWindow)
            {
                mainWindow.HandleAuthCallback(code!);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("OnAuthCallbackUrl", ex);
        }
    }

    /// <summary>Extracts the <c>code</c> query value from <c>departamentvpn://auth?code=…</c> (manual parse
    /// — a custom scheme isn't guaranteed to expose <see cref="Uri.Query"/>, and this avoids a System.Web dep).</summary>
    private static string? ParseHandoffCode(string url)
    {
        if (url.IsNullOrEmpty())
        {
            return null;
        }
        // Take the query/fragment portion, then match the pair whose key is EXACTLY "code" — a plain
        // IndexOf("code=") would also match "barcode="/"mycode=" if the return URL ever gains such a param.
        var qStart = url.IndexOfAny(new[] { '?', '#' });
        var query = qStart >= 0 ? url.Substring(qStart + 1) : url;
        foreach (var pair in query.Split('&', '#'))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            if (pair.Substring(0, eq).Trim().Equals("code", StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(pair.Substring(eq + 1).Trim());
                return value.IsNullOrEmpty() ? null : value;
            }
        }
        return null;
    }

    #endregion Browser→app SSO handoff

    #region Connectivity hooks (network change → core health check)

    // Retained so it can be unsubscribed on shutdown (NetworkChange holds a static handler list).
    private System.Net.NetworkInformation.NetworkAddressChangedEventHandler? _networkAddressChangedHandler;

    /// <summary>
    /// Cross-platform connection-reliability hook (BCL only — no new NuGet package). A network address
    /// change (Wi-Fi flip, DHCP lease, VPN adapter churn, or a sleep/resume storm) can leave the running
    /// core's tunnel stale. We ask <see cref="CoreManager.RequestHealthCheckAsync"/> to probe and reload
    /// if dead; it debounces internally and no-ops when idle. We deliberately do NOT reference the
    /// Windows-only <c>Microsoft.Win32.SystemEvents.PowerModeChanged</c>: a Windows resume also surfaces
    /// as network changes and the periodic watchdog re-probes within ~7s, so this hook plus the watchdog
    /// cover resume recovery while keeping the Linux/macOS build clean with zero new dependencies.
    /// </summary>
    private void SetupConnectivityHooks(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _networkAddressChangedHandler = (_, _) =>
        {
            // Only act when a core is supposed to be running (CoreManager re-checks + debounces too).
            if (IsCoreRunning())
            {
                _ = CoreManager.Instance.RequestHealthCheckAsync();
            }
        };

        try
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += _networkAddressChangedHandler;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SetupConnectivityHooks", ex);
        }

        // Clean up on app shutdown to avoid a leaked static handler.
        desktop.Exit += (_, _) =>
        {
            try
            {
                if (_networkAddressChangedHandler != null)
                {
                    System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= _networkAddressChangedHandler;
                    _networkAddressChangedHandler = null;
                }
            }
            catch
            {
            }
        };
    }

    #endregion Connectivity hooks (network change → core health check)

    #region App Event

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject != null)
        {
            Logging.SaveLog("CurrentDomain_UnhandledException", (Exception)e.ExceptionObject);
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logging.SaveLog("TaskScheduler_UnobservedTaskException", e.Exception);
    }

    private async void MenuAddServerViaClipboardClick(object? sender, EventArgs e)
    {
        try
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: not null })
            {
                AppEvents.AddServerViaClipboardRequested.Publish();
                await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MenuAddServerViaClipboardClick", ex);
        }
    }

    private async void MenuExit_Click(object? sender, EventArgs e)
    {
        await AppManager.Instance.AppExitAsync(false);
        AppManager.Instance.Shutdown(true);
    }

    #endregion App Event

    #region Tray menu (departament: Перезапустить · Подключить/Отключить · Показать · Выход)

    // Пункты трея. Порядок из App.axaml: Перезапустить · Подключить/Отключить · Показать · Выход.
    // Переключатель держим синхронным с реальным состоянием ядра; подписи всех — из L.T (live).
    private NativeMenuItem? _trayRestartItem;
    private NativeMenuItem? _trayToggleItem;
    private NativeMenuItem? _trayShowItem;
    private NativeMenuItem? _trayExitItem;

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);

    private void SetupTrayMenu()
    {
        try
        {
            var icons = TrayIcon.GetIcons(this);
            if (icons is { Count: > 0 })
            {
                var trayIcon = icons[0];

                // Bug2: подсказка иконки трея при наведении = ВСЕГДА «departament», а не строка статуса
                // подключения. В App.axaml ToolTipText привязан к {Binding RunningServerToolTipText};
                // перекрываем фиксированным именем бренда и УДЕРЖИВАЕМ его: если StatusBarViewModel
                // перепишет строку (обновление серверов/подключение), тем же кадром возвращаем «departament».
                trayIcon.ToolTipText = "departament";
                trayIcon.GetObservable(TrayIcon.ToolTipTextProperty)
                    .Where(t => t != "departament")
                    .Subscribe(_ => Dispatcher.UIThread.Post(() => trayIcon.ToolTipText = "departament"));

                if (trayIcon.Menu is { } menu)
                {
                    var items = menu.Items.OfType<NativeMenuItem>().ToList();
                    _trayRestartItem = items.ElementAtOrDefault(0);
                    _trayToggleItem = items.FirstOrDefault(i => Equals(i.CommandParameter, "toggleConnect")) ?? items.ElementAtOrDefault(1);
                    _trayShowItem = items.ElementAtOrDefault(2);
                    _trayExitItem = items.ElementAtOrDefault(3);
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SetupTrayMenu", ex);
        }

        UpdateTrayToggleLabel();

        // idle/perf B1: подпись «Подключить/Отключить» теперь обновляется ПО СОБЫТИЮ старта/останова
        // ядра (AppEvents.CoreRunningStateChanged из CoreManager), а не опросом каждые 2с. Событие
        // приходит с фонового потока (ядро стартует в Task.Run), поэтому обновление NativeMenuItem
        // маршалим в UI-поток. Подписка живёт всё время работы приложения (один трей).
        // Subscription lives for the whole app lifetime (single tray, like the old never-stopped poll);
        // the static EventChannel keeps the observer alive, so no field/dispose is needed.
        AppEvents.CoreRunningStateChanged
            .AsObservable()
            .Subscribe(_ => Dispatcher.UIThread.Post(UpdateTrayToggleLabel));

        // Локализация трея: применяем подписи один раз на старте и заново при смене языка (live).
        // Родное OS-меню читает Header при открытии, так что достаточно переустановить строки.
        LocalizeTray();
        L.Instance.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(LocalizeTray);
    }

    // Ре-локализация трея: подписи всех пунктов из L.T(...) — на старте и заново при смене языка.
    // Родное OS-меню читает Header при открытии, поэтому достаточно переустановить строки.
    private void LocalizeTray()
    {
        if (_trayRestartItem is not null)
        {
            _trayRestartItem.Header = L.T("Tray_Restart");
        }
        if (_trayShowItem is not null)
        {
            _trayShowItem.Header = L.T("Tray_Show");
        }
        if (_trayExitItem is not null)
        {
            _trayExitItem.Header = L.T("Tray_Exit");
        }
        UpdateTrayToggleLabel();
    }

    // Подпись переключателя = живое состояние ядра, из L.T (Отключить/Подключить · Disconnect/Connect).
    private void UpdateTrayToggleLabel()
    {
        if (_trayToggleItem is not null)
        {
            _trayToggleItem.Header = IsCoreRunning() ? L.T("Tray_Disconnect") : L.T("Tray_Connect");
        }
    }

    // «Подключить»/«Отключить» — тот же путь, что тап по connect-щиту.
    private async void MenuToggleConnect_Click(object? sender, EventArgs e)
    {
        try
        {
            if (IsCoreRunning())
            {
                // Отключение: стоп ядра + снятие системного прокси (иначе интернет ляжет на мёртвый порт).
                // byUser:true — намеренный стоп пользователем: гасит любой авто-рестарт (C1).
                await CoreManager.Instance.CoreStop(byUser: true);
                await SysProxyHandler.UpdateSysProxy(AppManager.Instance.Config, true);
            }
            else if ((Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow mw
                     && mw.ViewModel is { } vm)
            {
                // Подключение — явное действие пользователя ⇒ старт ядра разрешён. Reload собирает
                // конфиг и поднимает ядро для текущего сервера по умолчанию (путь щита Connect()).
                await vm.Reload();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MenuToggleConnect_Click", ex);
        }

        UpdateTrayToggleLabel();
    }

    // «Перезапустить» — поднимаем свежий процесс и выходим из текущего.
    private async void MenuRestart_Click(object? sender, EventArgs e)
    {
        try
        {
            var exePath = Utils.GetExePath();
            // Останавливаем ядро/сохраняем состояние, затем стартуем новый инстанс. Global.RebootAs
            // обходит одно-экземплярный guard (Program.OnStartup) на Windows, поэтому новый процесс
            // поднимается, даже пока текущий ещё завершает выход.
            await AppManager.Instance.AppExitAsync(false);
            ProcUtils.ProcessStart(exePath, Global.RebootAs);
            AppManager.Instance.Shutdown(true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("MenuRestart_Click", ex);
        }
    }

    #endregion Tray menu

    #region Theme (departament: Тёмная / Светлая базы + Монохромный (чёрно-белый) оверлей)

    // Кэш монохромного оверлея — ОТДЕЛЬНО для светлой и тёмной базы (как Android
    // ThemeOverlay.Mono поверх day/night: свой набор mono_* в values/ и values-night/).
    // Оверлей добавляется ПОСЛЕДНИМ словарём в Application.Resources.MergedDictionaries,
    // поэтому перекрывает тема-зависимые Brush.* из GlobalResources (merged ищутся с конца),
    // а DynamicResource перекрашивает вживую. Держим ссылку на добавленный сейчас словарь,
    // чтобы при смене базы (или выключении) снять именно его и поставить нужный.
    private static ResourceDictionary? _monoLight;
    private static ResourceDictionary? _monoDark;
    private static ResourceDictionary? _activeMono;

    /// <summary>
    /// Хук плавной смены темы, который ставит <see cref="MainWindow"/>. Получает делегат-СВОП
    /// (мгновенную перекраску живых контролов) и оборачивает его в красивый переход: снимок текущей
    /// темы → вызвать своп под снимком → «залить» новую тему расширяющимся круговым клипом. Пока окно
    /// не построено (старт из конфига) хук = null → своп выполняется мгновенно. В lite/reduced-motion
    /// хук сам делает мгновенный своп (см. MainWindow.RunThemeTransition). Единственная точка входа —
    /// <see cref="ApplyTheme"/> (обе кнопки настроек: смена базы и монохромный оверлей идут через неё).
    /// </summary>
    public static Action<Action>? ThemeTransitionHook;

    /// <summary>
    /// Единая точка применения оформления. <paramref name="theme"/> = имя <see cref="ETheme"/>
    /// (Dark/Light) → базовый <see cref="ThemeVariant"/>; <paramref name="black"/> = ОТДЕЛЬНЫЙ
    /// МОНОХРОМНЫЙ оверлей поверх любой базы (как ThemeOverlay.Mono в Android поверх day/night:
    /// нейтрализует ВСЕ акцентные оттенки в серый, палитра — чёрно-белая; красный сохраняется
    /// только для деструктива). Вызывается на старте (из конфига) и вживую из настроек —
    /// перекраска без перезапуска. Живая смена из настроек проходит через <see cref="ThemeTransitionHook"/>
    /// (круговая заливка новой темы), если он установлен; на старте (хук ещё null) — мгновенно.
    /// </summary>
    public static void ApplyTheme(string? theme, bool black)
    {
        var app = Current;
        if (app is null)
        {
            return;
        }

        // Сам своп темы (мгновенная перекраска): переменная базы + пере-композиция моно-оверлея.
        // Токены — DynamicResource, поэтому живые контролы перекрашиваются в этот же тик.
        void Swap()
        {
            app.RequestedThemeVariant = theme switch
            {
                nameof(ETheme.Light) => ThemeVariant.Light,
                nameof(ETheme.Dark) => ThemeVariant.Dark,
                // Владелец: базы только Тёмная/Светлая; всё прочее (в т.ч. FollowSystem/null) → Тёмная.
                _ => ThemeVariant.Dark,
            };

            ApplyMonoOverlay(black);
        }

        // Окно построено → отдаём своп в переход-хук (снимок → своп → круговая заливка). На старте,
        // до построения окна, хук ещё null → мгновенный своп (первый кадр рисуется правильной темой).
        var hook = ThemeTransitionHook;
        if (hook is not null)
        {
            hook(Swap);
        }
        else
        {
            Swap();
        }
    }

    // Ставит/снимает монохромный оверлей, выбирая light- или dark-набор по активной базе.
    // RequestedThemeVariant уже выставлен в ApplyTheme, поэтому набор всегда совпадает с базой;
    // при переключении базы под включённым mono старый набор снимается, ставится корректный.
    private static void ApplyMonoOverlay(bool on)
    {
        var app = Current;
        if (app is null)
        {
            return;
        }

        var merged = app.Resources.MergedDictionaries;

        // Снять текущий mono (если был) — чтобы выключение чисто восстановило базу,
        // а смена базы не оставила несовместимый набор.
        if (_activeMono is not null)
        {
            merged.Remove(_activeMono);
            _activeMono = null;
        }

        if (!on)
        {
            return;
        }

        var light = app.RequestedThemeVariant == ThemeVariant.Light;
        var overlay = light
            ? _monoLight ??= BuildMonoOverlay(light: true)
            : _monoDark ??= BuildMonoOverlay(light: false);

        merged.Add(overlay);
        _activeMono = overlay;
    }

    // Монохромный набор: полный список тема-зависимых И акцент-производных Brush.* → чёрно-белая
    // палитра. Акцент (#4C8DFF) схлопывается в серый (light #111214 / dark #FFFFFF), «connected»
    // становится серо-белым, ВСЕ цветные плитки/чипы/иконки → серые. КРАСНЫЙ сохраняется только
    // для деструктива (delete/failed). Значения = Android mono_* (values / values-night §mono).
    private static ResourceDictionary BuildMonoOverlay(bool light)
    {
        static SolidColorBrush Solid(string hex) => new(Color.Parse(hex));
        static SolidColorBrush Alpha(string hex, double opacity) => new(Color.Parse(hex)) { Opacity = opacity };

        // Серый «акцент»/чернила и поверхности из Android mono_* набора.
        var accent = "#F2F4F8";            // акцент чёрно-белой = белый
        var onAccent = "#0A0A0B";
        var accentContainer = "#22242A";
        var onAccentContainer = "#F2F4F8";
        var connected = "#F2F4F8";         // «хорошо» сведено к белому
        var onSurface = "#F2F4F8";
        var onSurfaceVariant = "#9BA1AD";
        var highest = "#282A2E";           // плитка / окошко
        // Красный сохраняем для деструктива (Android mono держит iconTintRed/failed красным).
        // «Плохо» в этой теме тоже белое (tokens.md): цвета не остаётся нигде, кроме глифа Telegram
        // — а он здесь тоже белый. Красный держим ОДИН, для кнопки закрытия окна: она системная,
        // и её наведение красное во всех темах по README «Хром окна».
        var red = "#F2F4F8";
        // Красного текста в этой теме нет: ошибки печатаются тем же белым, что и всё остальное —
        // «в чёрно-белой теме не остаётся синего», и цвета вообще. Ключ существует ради единого API.
        var redText = "#F2F4F8";

        // Полупрозрачные производные — единый серый под tile/selected/статус-чип.
        // На тёмной базе — белый лифт, на светлой — чёрный (как ховер).
        var greyTint = "#F2F4F8";

        return new ResourceDictionary
        {
            // ── Поверхности / чернила / контуры (mono_*) ──
            ["Brush.Bg"] = Solid("#141416"),
            ["Brush.Surface"] = Solid("#1A1A1D"),
            ["Brush.SurfaceHigh"] = Solid("#202024"),
            ["Brush.SurfaceVariant"] = Solid("#232326"),
            ["Brush.SurfaceHighest"] = Solid(highest),
            ["Brush.OnSurface"] = Solid(onSurface),
            ["Brush.OnSurfaceVariant"] = Solid(onSurfaceVariant),
            ["Brush.Outline"] = Solid("#34363C"),
            ["Brush.OutlineVariant"] = Solid("#26262A"),

            // ── Акцент → серый (схлопывание #4C8DFF) ──
            ["Brush.Accent"] = Solid(accent),
            ["Brush.OnAccent"] = Solid(onAccent),
            ["Brush.AccentContainer"] = Solid(accentContainer),
            ["Brush.OnAccentContainer"] = Solid(onAccentContainer),
            // Semi-тема тянет primary по DynamicResource — тоже в серый, иначе синие фокусы/кнопки.
            ["SemiColorPrimary"] = Solid(accent),
            ["SemiColorPrimaryHover"] = Solid("#E7E7E9"),
            ["SemiColorPrimaryActive"] = Solid("#C9C9CD"),

            // ── Семантика: зелёный/оранжевый/жёлтый → серый; КРАСНЫЙ остаётся (деструктив) ──
            ["Brush.Green"] = Solid(connected), // «подключено»/успех = mono connected (серо-белый)
            ["Brush.Red"] = Solid(red),
            ["Brush.RedText"] = Solid(redText),

            // ── Новые токены пакета. Без них чёрно-белая осталась бы с синими кольцами,
            //    синим диском и синим глифом Telegram — «в чёрно-белой не остаётся синего». ──
            ["Brush.ButtonOutline"] = Solid("#34363C"),
            ["Brush.ConnectDisc"] = Solid("#1E1E22"),
            ["Brush.ShieldOff"] = Solid("#4A4E57"),
            ["Brush.PressBg"] = Solid("#131315"),
            ["Brush.Yellow"] = Solid("#9BA1AD"),
            ["Brush.Telegram"] = Solid("#F2F4F8"),
            ["Brush.Ring.Outer"] = Alpha("#F2F4F8", 0.16),
            ["Brush.Ring.Inner"] = Alpha("#F2F4F8", 0.45),
            ["Brush.SelectedFill"] = Alpha("#F2F4F8", 0.10),

            // ── Плитки иконок: цветные → серые; красная плитка остаётся красной ──
            ["Brush.Tile.Neutral"] = Solid(highest),
            ["Brush.Tile.Blue"] = Alpha(greyTint, 0.10),
            ["Brush.Tile.Purple"] = Alpha(greyTint, 0.10),
            ["Brush.Tile.Green"] = Alpha(greyTint, 0.10),
            ["Brush.Tile.Orange"] = Alpha(greyTint, 0.10),
            ["Brush.Tile.Yellow"] = Alpha(greyTint, 0.10),
            ["Brush.Tile.Red"] = Alpha(red, 0.20),
            ["Brush.Icon.Orange"] = Solid(onSurfaceVariant),
            ["Brush.Icon.Yellow"] = Solid(onSurfaceVariant),

            // ── Выбор / статус-чипы: серые; failed остаётся красным ──
            ["Brush.SelectedFill"] = Alpha(greyTint, 0.12),
            ["Brush.StatusChip.Green"] = Alpha(greyTint, 0.12),
            ["Brush.StatusChip.Orange"] = Alpha(greyTint, 0.12),
            ["Brush.StatusChip.Yellow"] = Alpha(greyTint, 0.12),
            ["Brush.StatusChip.Red"] = Alpha(red, 0.18),

            // ── Ховер: белый лифт. Палитра одна, поэтому и ховер один (tokens.md
            //    «Наведение rgba(255,255,255,.06)»). ──
            ["Brush.Hover"] = Alpha("#FFFFFF", 0.06),

            // ── Тост / фон Главной ──
            ["Brush.Toast.Bg"] = Solid(highest),
            ["Brush.HomeGradient"] = BuildMonoHomeGradient(light),

            // ── Connect-щит: halo и кольца из синего в серо-белый (mono connected) ──
            ["Brush.ConnectGlow"] = BuildMonoConnectGlow(connected),
            ["Brush.Ring.Outer"] = Alpha(connected, 0.20),
            ["Brush.Ring.Inner"] = Alpha(connected, 0.50),
        };
    }

    private static RadialGradientBrush BuildMonoHomeGradient(bool light)
    {
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.30, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.30, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
        };
        // Одна палитра на тему, поэтому ветвления по базе больше нет. Значения tokens.md;
        // нижняя точка #101012, а НЕ чёрный — «не чистый чёрный, фон поднят до серого».
        brush.GradientStops.Add(new GradientStop(Color.Parse("#1C1C1F"), 0));
        brush.GradientStops.Add(new GradientStop(Color.Parse("#141416"), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.Parse("#101012"), 1));
        return brush;
    }

    // Серо-белое halo connect-щита (mono): непрозрачный центр connected → к нулю по краю.
    private static RadialGradientBrush BuildMonoConnectGlow(string connectedHex)
    {
        var c = Color.Parse(connectedHex);
        var brush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, c.R, c.G, c.B), 0.5));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1));
        return brush;
    }

    #endregion Theme
}
