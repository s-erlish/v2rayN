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
            }

            var mainWindowViewModel = new MainWindowViewModel();
            var mainWindow = (MainWindow)viewLocator.Build(mainWindowViewModel);
            mainWindow.ViewModel = mainWindowViewModel;
            desktop.MainWindow = mainWindow;

            if (!Design.IsDesignMode)
            {
                SetupTrayMenu();
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

    // Пункт-переключатель, чью подпись держим синхронной с реальным состоянием ядра.
    private NativeMenuItem? _trayToggleItem;
    private DispatcherTimer? _trayStateTimer;

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);

    private void SetupTrayMenu()
    {
        try
        {
            var icons = TrayIcon.GetIcons(this);
            if (icons is { Count: > 0 } && icons[0].Menu is { } menu)
            {
                _trayToggleItem = menu.Items
                    .OfType<NativeMenuItem>()
                    .FirstOrDefault(i => Equals(i.CommandParameter, "toggleConnect"));
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SetupTrayMenu", ex);
        }

        UpdateTrayToggleLabel();

        // Native-меню трея читается ОС только в момент открытия, поэтому лёгкий опрос (2с)
        // достаточен и дёшев, чтобы подпись «Подключить/Отключить» всегда совпадала с ядром,
        // как бы состояние ни менялось (щит, горячие клавиши, падение ядра).
        _trayStateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _trayStateTimer.Tick += (_, _) => UpdateTrayToggleLabel();
        _trayStateTimer.Start();
    }

    private void UpdateTrayToggleLabel()
    {
        if (_trayToggleItem is not null)
        {
            _trayToggleItem.Header = IsCoreRunning() ? "Отключить" : "Подключить";
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
                await CoreManager.Instance.CoreStop();
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
    /// Единая точка применения оформления. <paramref name="theme"/> = имя <see cref="ETheme"/>
    /// (Dark/Light) → базовый <see cref="ThemeVariant"/>; <paramref name="black"/> = ОТДЕЛЬНЫЙ
    /// МОНОХРОМНЫЙ оверлей поверх любой базы (как ThemeOverlay.Mono в Android поверх day/night:
    /// нейтрализует ВСЕ акцентные оттенки в серый, палитра — чёрно-белая; красный сохраняется
    /// только для деструктива). Вызывается на старте (из конфига) и вживую из настроек —
    /// перекраска без перезапуска.
    /// </summary>
    public static void ApplyTheme(string? theme, bool black)
    {
        var app = Current;
        if (app is null)
        {
            return;
        }

        app.RequestedThemeVariant = theme switch
        {
            nameof(ETheme.Light) => ThemeVariant.Light,
            nameof(ETheme.Dark) => ThemeVariant.Dark,
            // Владелец: базы только Тёмная/Светлая; всё прочее (в т.ч. FollowSystem/null) → Тёмная.
            _ => ThemeVariant.Dark,
        };

        ApplyMonoOverlay(black);
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
        var accent = light ? "#111214" : "#FFFFFF"; // primary
        var onAccent = light ? "#FFFFFF" : "#111214"; // onPrimary
        var accentContainer = light ? "#E6E6E8" : "#2A2A2E"; // primaryContainer
        var onAccentContainer = light ? "#111214" : "#F4F4F5"; // текст на контейнере ≈ onSurface
        var connected = light ? "#111214" : "#FFFFFF"; // mono connected (не синий)
        var onSurface = light ? "#111214" : "#F4F4F5";
        var onSurfaceVariant = light ? "#5A5A5E" : "#B0B0B4";
        var highest = light ? "#E7E7E9" : "#232326"; // surfaceContainerHighest
        // Красный сохраняем для деструктива (Android mono держит iconTintRed/failed красным).
        var red = light ? "#C42B32" : "#E5484D";

        // Полупрозрачные производные — единый серый под tile/selected/статус-чип.
        // На тёмной базе — белый лифт, на светлой — чёрный (как ховер).
        var greyTint = light ? "#111214" : "#FFFFFF";

        return new ResourceDictionary
        {
            // ── Поверхности / чернила / контуры (mono_*) ──
            ["Brush.Bg"] = Solid(light ? "#FFFFFF" : "#000000"),
            ["Brush.Surface"] = Solid(light ? "#FFFFFF" : "#121214"),
            ["Brush.SurfaceHigh"] = Solid(light ? "#EEEEEF" : "#1B1B1E"), // surfaceContainerHigh
            ["Brush.SurfaceVariant"] = Solid(light ? "#F1F1F2" : "#1E1E20"),
            ["Brush.SurfaceHighest"] = Solid(highest),
            ["Brush.OnSurface"] = Solid(onSurface),
            ["Brush.OnSurfaceVariant"] = Solid(onSurfaceVariant),
            ["Brush.Outline"] = Solid(light ? "#D2D2D6" : "#38383C"),
            ["Brush.OutlineVariant"] = Solid(light ? "#E6E6E8" : "#28282C"),

            // ── Акцент → серый (схлопывание #4C8DFF) ──
            ["Brush.Accent"] = Solid(accent),
            ["Brush.OnAccent"] = Solid(onAccent),
            ["Brush.AccentContainer"] = Solid(accentContainer),
            ["Brush.OnAccentContainer"] = Solid(onAccentContainer),
            // Semi-тема тянет primary по DynamicResource — тоже в серый, иначе синие фокусы/кнопки.
            ["SemiColorPrimary"] = Solid(accent),
            ["SemiColorPrimaryHover"] = Solid(light ? "#2A2A2E" : "#E7E7E9"),
            ["SemiColorPrimaryActive"] = Solid(light ? "#000000" : "#C9C9CD"),

            // ── Семантика: зелёный/оранжевый/жёлтый → серый; КРАСНЫЙ остаётся (деструктив) ──
            ["Brush.Green"] = Solid(connected), // «подключено»/успех = mono connected (серо-белый)
            ["Brush.Red"] = Solid(red),

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

            // ── Ховер: белый лифт на тёмной базе, чёрное затемнение на светлой ──
            ["Brush.Hover"] = light ? Alpha("#000000", 0.05) : Alpha("#FFFFFF", 0.06),

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
        if (light)
        {
            brush.GradientStops.Add(new GradientStop(Color.Parse("#FFFFFF"), 0));
            brush.GradientStops.Add(new GradientStop(Color.Parse("#FAFAFB"), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.Parse("#F1F1F2"), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(Color.Parse("#1B1B1E"), 0));
            brush.GradientStops.Add(new GradientStop(Color.Parse("#121214"), 0.55));
            brush.GradientStops.Add(new GradientStop(Color.Parse("#000000"), 1));
        }
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
