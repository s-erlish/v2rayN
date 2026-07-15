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
}
