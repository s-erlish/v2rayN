using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.VisualTree;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Настройки (вкладка «Настройки») — Incy секции-карточки со строками/тумблерами
/// (Подключение, Обход блокировок, Производительность, Интерфейс, Подписка, О приложении).
///
/// Значения/тумблеры биндятся к реальному <see cref="SettingsViewModel"/> (данные читаются из
/// живого <c>Config</c> и пишутся обратно через <c>ConfigHandler.SaveConfig</c>). КАЖДАЯ строка —
/// реальная рабочая функция, и КАЖДЫЙ правый affordance ЧЕСТЕН (Round2 Фаза D):
///   • шеврон = НАВИГАЦИЯ (тап открывает суб-страницу): Прокси по приложениям, DNS, Пинг,
///     Маршрутизация, Файлы ресурсов, О приложении, Резервное копирование, Схемы URL;
///   • шеврон-раскрытие (0↔90) = инлайн-панель: Локальный прокси;
///   • unfold_more = значение ЦИКЛИТСЯ на месте (≥3 значений): Язык, Автообновление, Число Mux,
///     Масштаб интерфейса;
///   • инлайн-сегмент (2 состояния) = смена на месте: Режим (TUN/Прокси), Оформление (Тёмная/Светлая);
///   • тумблер = булево: Обход сети, IPv6, Mux, Фрагментация, Облегчённый режим, Монохром, Запуск.
///
/// Доступность: каждая строка-действие фокусируема с клавиатуры (Focusable/IsTabStop) с активацией
/// Enter/Space и авто-кольцом FocusAdorner (как AccountChip); тумблеры сняты с таб-стопа — стопом
/// владеет строка. Сегменты — нативно фокусируемые контролы. OFF-модель: ни одна строка не запускает
/// ядро. Sample-данные — только design-time (<c>Design.DataContext</c>).
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // Runtime: bind the whole screen to the real config-backed ViewModel. Design-time uses the
        // axaml Design.DataContext (sample strings) so the previewer still renders.
        if (!Design.IsDesignMode)
        {
            DataContext = new SettingsViewModel();
        }

        // --- Строки-НАВИГАЦИИ (шеврон): тап кладёт Incy суб-страницу на общий стек оболочки ---
        WireRow(RowPerApp, () => OpenPage(new PerAppProxyPage(), refresh: true));
        WireRow(RowDns, () => OpenPage(new DnsSubView(), refresh: true));
        WireRow(RowPingMethod, () => OpenPage(new PingSettingsPage(), refresh: true));
        WireRow(RowRouting, () => OpenPage(new RoutingSubView()));
        WireRow(RowAssets, () => OpenPage(new GeoFilesPage()));
        WireRow(RowAbout, () => OpenPage(new AboutPage()));
        WireRow(RowBackup, () => OpenPage(new BackupPage()));
        WireRow(RowUrlScheme, () => OpenPage(new UrlSchemesPage()));

        // --- Строки-ЦИКЛА значения (unfold_more): тап продвигает реальное значение на месте ---
        WireRow(RowMuxConcurrency, () => _ = Vm?.CycleMuxConcurrencyAsync());
        WireRow(RowLanguage, () => _ = Vm?.CycleLanguageAsync());
        WireRow(RowSubAutoUpdate, () => _ = Vm?.CycleAutoUpdateAsync());
        // Масштаб интерфейса: тап циклит пресеты zoom (те же значения — на Ctrl +/Ctrl −). Оболочка
        // (MainWindow) применяет фактор мгновенно через общий UiScaleState.
        WireRow(RowUiScale, () => Vm?.CycleUiScale());

        // --- Локальный прокси — раскрытие инлайн-панели (анимированный шеврон 0↔90 + слайд панели) ---
        WireRow(RowLocalProxy, ToggleLocalProxy);
        ProxyPortBox.LostFocus += OnProxyFieldCommit;
        ProxyUserBox.LostFocus += OnProxyFieldCommit;
        ProxyPassBox.LostFocus += OnProxyFieldCommit;

        // --- Тумблер-строки: тап по всей строке (56dp) + Enter/Space переключают тумблер ---
        WireToggleRow(RowBypassLan, SwitchBypassLan);
        WireToggleRow(RowIpv6, SwitchIpv6);
        WireToggleRow(RowMux, SwitchMux);
        WireToggleRow(RowFragment, SwitchFragment);
        WireToggleRow(RowLiteMode, SwitchLiteMode);
        WireToggleRow(RowBlackTheme, SwitchBlackTheme);
        WireToggleRow(RowBoot, SwitchBoot);

        // --- Инлайн-сегменты (2 состояния): Режим + Оформление. Клик задаёт КОНКРЕТНОЕ значение
        //     (не слепой toggle); визуальный выбор отражается из VM. Сегменты — нативные фокусируемые
        //     ToggleButton'ы (Tab + Space/Enter), поэтому саму строку под них НЕ делаем таб-стопом. ---
        SegModeTun.Click += (_, _) => SelectMode(true);
        SegModeProxy.Click += (_, _) => SelectMode(false);
        SegThemeDark.Click += (_, _) => SelectTheme(false);
        SegThemeLight.Click += (_, _) => SelectTheme(true);

        // Отражаем начальное/внешнее состояние сегментов + микро-кроссфейд значений — из VM
        // PropertyChanged (единственный рантайм-экземпляр keep-alive, отписка не нужна).
        if (Vm is not null)
        {
            Vm.PropertyChanged += OnVmPropertyChanged;
            ReflectMode(Vm.IsTunMode);
            ReflectTheme(Vm.IsLightTheme);
        }

        // Зависимая строка «Число соединений Mux» видна только при включённом Mux
        // (аналог Android rowMuxConcurrency.isVisible = muxOn). Чистая view-логика.
        SwitchMux.IsCheckedChanged += (_, _) => UpdateMuxDependentRows();
        UpdateMuxDependentRows();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    // ===================== Строки: активация тапом + клавиатурой =====================

    /// <summary>Делает строку-действие фокусируемой (таб-стоп + Enter/Space = тот же жест, что тап).
    /// Единственный указательный путь — Tapped (жест «нажал+отпустил на строке»): ровно один вызов на
    /// тап, без PointerPressed-перехвата, который раньше гасил жест. Порядок Tab = порядок в разметке.
    /// Press-фидбек намеренно НЕ навешивается — владелец не хочет «продавливания» строки (scale-down);
    /// покой→ховер (Brush.Hover) остаётся, кольцо фокуса — тоже.</summary>
    private void WireRow(Border row, Action activate)
    {
        row.Focusable = true;
        row.IsTabStop = true;
        row.Tapped += (_, _) => activate();
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                activate();
                e.Handled = true;
            }
        };
    }

    /// <summary>Тумблер-строка: тап по всей строке (guard OriginatedInToggle гасит двойной ход, когда
    /// источник — сам тумблер) + Enter/Space переключают привязанный тумблер. Строка — единственный
    /// таб-стоп (сам тумблер снят с фокуса в стиле RowSwitch). Как и WireRow — Tapped-only, без
    /// PointerPressed/press-scale: один тап = одно переключение, надёжно.</summary>
    private void WireToggleRow(Border row, ToggleSwitch sw)
    {
        row.Focusable = true;
        row.IsTabStop = true;
        row.Tapped += (_, e) => ToggleFromRow(sw, e);
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                sw.IsChecked = !(sw.IsChecked ?? false);
                e.Handled = true;
            }
        };
    }

    // ===================== Инлайн-сегменты (Режим / Оформление) =====================

    private void SelectMode(bool tun)
    {
        // Пере-утверждаем выбор: клик по ToggleButton сам инвертирует IsChecked, а холостой повторный
        // тап по активному сегменту снял бы галочку — ReflectMode возвращает корректную пару.
        ReflectMode(tun);
        _ = Vm?.SetTunMode(tun);
    }

    private void SelectTheme(bool light)
    {
        ReflectTheme(light);
        _ = Vm?.SetAppearance(light);
    }

    private void ReflectMode(bool tun)
    {
        SegModeTun.IsChecked = tun;
        SegModeProxy.IsChecked = !tun;
    }

    private void ReflectTheme(bool light)
    {
        SegThemeLight.IsChecked = light;
        SegThemeDark.IsChecked = !light;
    }

    /// <summary>Отражает состояние сегментов из VM (в т.ч. ВНЕШНИЕ смены TUN/темы) и играет
    /// микро-кроссфейд на изменившемся значении строки (§3.3). Оформление/Режим — сегменты, кроссфейда
    /// нет; у Оформления обратная связь — общий theme-flood, поэтому здесь его не дублируем.</summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SettingsViewModel vm)
        {
            return;
        }
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.IsTunMode): ReflectMode(vm.IsTunMode); break;
            case nameof(SettingsViewModel.IsLightTheme): ReflectTheme(vm.IsLightTheme); break;
            case nameof(SettingsViewModel.MuxConcurrencyText): CrossfadeValue(ValueMuxConcurrency); break;
            case nameof(SettingsViewModel.LanguageText): CrossfadeValue(ValueLanguage); break;
            case nameof(SettingsViewModel.SubAutoUpdateText): CrossfadeValue(ValueSubAutoUpdate); break;
            case nameof(SettingsViewModel.UiScaleText): CrossfadeValue(ValueUiScale); break;
            case nameof(SettingsViewModel.PingMethodText): CrossfadeValue(ValuePingMethod); break;
            case nameof(SettingsViewModel.DnsText): CrossfadeValue(ValueDns); break;
            case nameof(SettingsViewModel.PerAppText): CrossfadeValue(ValuePerApp); break;
        }
    }

    // ===================== Моушен: value-кроссфейд · шеврон · панель =====================

    /// <summary>Значение сменилось → мягкий кроссфейд (opacity 0.3→1, 160мс Standard) на самом
    /// TextBlock, компоузер-only. Под lite — мгновенно (значение уже обновлено биндингом).</summary>
    private static void CrossfadeValue(TextBlock target)
    {
        if (MotionState.IsLite)
        {
            target.Opacity = 1d;
            return;
        }
        var anim = new Animation
        {
            Duration = Motion.Dur.PressOut,
            Easing = Motion.Ease.Standard,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 0.3d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, 1d) } },
            },
        };
        _ = anim.RunAsync(target);
    }

    private async void ToggleLocalProxy()
    {
        var open = !LocalProxyPanel.IsVisible;
        SetProxyChevron(open);
        if (open)
        {
            LocalProxyPanel.IsVisible = true;
            await RevealPanel(LocalProxyPanel, show: true);
        }
        else
        {
            await RevealPanel(LocalProxyPanel, show: false);
            LocalProxyPanel.IsVisible = false;
            // Сворачивание = коммит введённых значений (порт/логин/пароль → Inbound[0]).
            _ = Vm?.CommitLocalProxyAsync();
        }
    }

    /// <summary>Вращение шеврона 0↔90 (origin — относительный центр 50%,50%, задан в разметке). Плавно
    /// 220мс Standard; мгновенно под lite. Анимируем RotateTransform.Angle (тот же приём transform-
    /// анимации, что TranslateTransform.Y в HomeAccountChip).</summary>
    private void SetProxyChevron(bool open)
    {
        var to = open ? 90d : 0d;
        if (MotionState.IsLite)
        {
            LocalProxyChevron.RenderTransform = new RotateTransform(to);
            return;
        }
        var from = open ? 0d : 90d;
        var anim = new Animation
        {
            Duration = Motion.Dur.State,
            Easing = Motion.Ease.Standard,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Avalonia.Styling.Setter(RotateTransform.AngleProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Avalonia.Styling.Setter(RotateTransform.AngleProperty, to) } },
            },
        };
        _ = anim.RunAsync(LocalProxyChevron);
    }

    /// <summary>Инлайн-панель: раскрытие = fade + translateY −6→0 (300мс OutQuint), сворачивание =
    /// fade + 0→−6 (150мс Standard). Компоузер-only; под lite — мгновенно.</summary>
    private static async Task RevealPanel(Control panel, bool show)
    {
        if (MotionState.IsLite)
        {
            panel.Opacity = show ? 1d : 0d;
            panel.RenderTransform = null;
            return;
        }
        var dur = show ? Motion.Dur.Reveal : Motion.Dur.Exit;
        var ease = show ? Motion.Ease.OutQuint : Motion.Ease.Standard;
        var fromY = show ? -6d : 0d;
        var toY = show ? 0d : -6d;
        var fromO = show ? 0d : 1d;
        var toO = show ? 1d : 0d;
        var fade = new Animation
        {
            Duration = dur,
            Easing = ease,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, fromO) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, toO) } },
            },
        };
        var slide = new Animation
        {
            Duration = dur,
            Easing = ease,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Avalonia.Styling.Setter(TranslateTransform.YProperty, fromY) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Avalonia.Styling.Setter(TranslateTransform.YProperty, toY) } },
            },
        };
        await Task.WhenAll(fade.RunAsync(panel), slide.RunAsync(panel));
        if (show)
        {
            panel.Opacity = 1d;
            panel.RenderTransform = null;
        }
    }

    private void OnProxyFieldCommit(object? sender, RoutedEventArgs e) => _ = Vm?.CommitLocalProxyAsync();

    // ===================== Навигация / вспомогательное =====================

    /// <summary>Открывает Incy суб-страницу IN-APP, кладя её на общий стек «назад» оболочки
    /// (MainWindow.OpenSubPage) — НИКАКИХ отдельных окон. При <paramref name="refresh"/> по возврате
    /// перечитывает значения строк, которые страница могла изменить (DNS, прокси по приложениям и т.п.).</summary>
    private void OpenPage(Control page, bool refresh = false)
    {
        // Подписку на обновление вешаем ДО OpenSubPage, чтобы значения строк освежились перед снятием
        // страницы со стека (обе подписки на BackRequested отработают по возврату).
        if (refresh && page is ISubPage sub)
        {
            sub.BackRequested += (_, _) => Vm?.RefreshDisplayValues();
        }
        if (TopLevel.GetTopLevel(this) is MainWindow main)
        {
            main.OpenSubPage(page);
        }
    }

    /// <summary>Тап по строке-тумблеру переключает тумблер — но не когда сам тумблер и был источником
    /// тапа (он уже переключился сам).</summary>
    private static void ToggleFromRow(ToggleSwitch sw, TappedEventArgs e)
    {
        if (OriginatedInToggle(e.Source))
        {
            return;
        }
        sw.IsChecked = !(sw.IsChecked ?? false);
    }

    private static bool OriginatedInToggle(object? source)
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is ToggleSwitch)
            {
                return true;
            }
            visual = visual.GetVisualParent();
        }
        return false;
    }

    private void UpdateMuxDependentRows()
    {
        var muxOn = SwitchMux.IsChecked == true;
        RowMuxConcurrency.IsVisible = muxOn;
        DividerConcurrency.IsVisible = muxOn;
    }
}
