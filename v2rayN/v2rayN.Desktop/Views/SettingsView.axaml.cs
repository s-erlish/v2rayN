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
///   • шеврон = НАВИГАЦИЯ (тап открывает суб-страницу): Прокси по приложениям, DNS,
///     Маршрутизация, Файлы ресурсов, О приложении, Резервное копирование, Схемы URL;
///   • шеврон-раскрытие (0↔90) = инлайн-панель: Локальный прокси;
///   • unfold_more = СПИСОК ЗНАЧЕНИЙ раскрывается у самой строки (ChoiceFlyout): Пинг, Язык,
///     Автообновление, Число Mux, Масштаб интерфейса;
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
        WireRow(RowRouting, () => OpenPage(new RoutingSubView()));
        WireRow(RowAssets, () => OpenPage(new GeoFilesPage()));
        WireRow(RowAbout, () => OpenPage(new AboutPage()));
        WireRow(RowBackup, () => OpenPage(new BackupPage()));
        WireRow(RowUrlScheme, () => OpenPage(new UrlSchemesPage()));

        // --- Строки-ВЫБОРА (unfold_more): тап раскрывает СПИСОК ЗНАЧЕНИЙ у самой строки ---
        // Владелец: «надо, чтобы просто варианты пинга при нажатии сбоку у кнопки высвечивались, и это
        // касается многих таких настроек». Здесь сходятся два прежних поведения, и оба были плохи:
        // «Пинг» занимал целую суб-страницу ради выбора из двух, а Язык / Автообновление / Число Mux /
        // Масштаб перещёлкивали значение вслепую — набор значений человек не видел вовсе. Теперь у всех
        // одна форма: нажал строку — увидел все значения рядом, выбрал — список закрылся.
        WireRow(RowPingMethod, ShowPingChoice);
        WireRow(RowMuxConcurrency, ShowMuxConcurrencyChoice);
        WireRow(RowLanguage, ShowLanguageChoice);
        WireRow(RowSubAutoUpdate, ShowAutoUpdateChoice);
        WireRow(RowUiScale, ShowUiScaleChoice);

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

    // ===================== Строки-выбора: список значений у строки =====================

    /// <summary>«Пинг»: метод измерения задержки + два параметра проверки, которые его сопровождают.
    /// Прежде это была целая суб-страница (тулбар, «назад», три карточки) ради выбора из двух —
    /// её содержимое целиком переехало сюда: те же два метода, тот же адрес, тот же тайм-аут.
    /// Метод применяется сразу; параметры коммитятся при закрытии списка (пустое поле = «как было»).</summary>
    private void ShowPingChoice()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var url = new TextBox
        {
            Text = vm.PingTestUrl,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            PlaceholderText = "https://www.gstatic.com/generate_204",
        };
        var timeout = new TextBox
        {
            Text = vm.PingTimeout > 0 ? vm.PingTimeout.ToString() : string.Empty,
            MaxLength = 3,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            PlaceholderText = "5",
        };

        var footer = new StackPanel { Spacing = 6 };
        footer.Children.Add(Caption(L.T("Ping_TestAddress")));
        footer.Children.Add(url);
        footer.Children.Add(Caption(L.T("Ping_Timeout"), topGap: true));
        footer.Children.Add(timeout);

        ChoiceFlyout.Show(
            RowPingMethod,
            [
                new ChoiceItem(L.T("Ping_RealTitle"), L.T("Ping_RealHint"), vm.PingMethod == SettingsViewModel.PingMethodReal,
                    () => _ = vm.SetPingMethodAsync(SettingsViewModel.PingMethodReal)),
                new ChoiceItem("TCP", L.T("Ping_TcpHint"), vm.PingMethod == SettingsViewModel.PingMethodTcp,
                    () => _ = vm.SetPingMethodAsync(SettingsViewModel.PingMethodTcp)),
            ],
            footer,
            () => _ = vm.SetPingParamsAsync(url.Text, timeout.Text));
    }

    /// <summary>«Число соединений Mux»: реальный набор значений движка.</summary>
    private void ShowMuxConcurrencyChoice()
    {
        if (Vm is not { } vm)
        {
            return;
        }
        var cur = vm.MuxConcurrency;
        ChoiceFlyout.Show(
            RowMuxConcurrency,
            [.. SettingsViewModel.MuxConcurrencyOptions.Select(n =>
                new ChoiceItem(n.ToString(), null, n == cur, () => _ = vm.SetMuxConcurrencyAsync(n)))]);
    }

    /// <summary>«Язык»: переключается вживую, без перезапуска.</summary>
    private void ShowLanguageChoice()
    {
        if (Vm is not { } vm)
        {
            return;
        }
        var cur = vm.CurrentLanguage;
        ChoiceFlyout.Show(
            RowLanguage,
            [.. SettingsViewModel.LanguageOptions.Select(code =>
                new ChoiceItem(LanguageLabel(code), null, code == cur, () => _ = vm.SetLanguageAsync(code)))]);
    }

    /// <summary>Подпись языка — на самом языке (так его узнают, не зная текущего).</summary>
    private static string LanguageLabel(string code) => code switch
    {
        "en" => "English",
        _ => L.T("Settings_LangRussian"),
    };

    /// <summary>«Автообновление подписок»: интервалы в часах (значение хранится в минутах).</summary>
    private void ShowAutoUpdateChoice()
    {
        if (Vm is not { } vm)
        {
            return;
        }
        var cur = vm.AutoUpdateInterval;
        ChoiceFlyout.Show(
            RowSubAutoUpdate,
            [.. SettingsViewModel.AutoUpdateOptions.Select(m =>
                new ChoiceItem(L.F("Common_HoursShort", m / 60), null, m == cur, () => _ = vm.SetAutoUpdateAsync(m)))]);
    }

    /// <summary>«Масштаб интерфейса»: пресеты zoom; те же значения дают Ctrl +/Ctrl −/Ctrl 0.</summary>
    private void ShowUiScaleChoice()
    {
        if (Vm is not { } vm)
        {
            return;
        }
        var cur = vm.UiScale;
        ChoiceFlyout.Show(
            RowUiScale,
            [.. SettingsViewModel.UiScaleOptions.Select(s =>
                new ChoiceItem(SettingsViewModel.FormatUiScale(s), null, Math.Abs(s - cur) < 0.001, () => vm.SetUiScale(s)))]);
    }

    /// <summary>Подпись поля в подвале списка выбора (Subtitle, с отступом сверху у второго и далее).</summary>
    private static TextBlock Caption(string text, bool topGap = false)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add("Subtitle");
        if (topGap)
        {
            block.Margin = new Thickness(0, 6, 0, 0);
        }
        return block;
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

    // Re-entrancy latch: строка — обычный Tapped без гарда, а RevealPanel не отменяем. Двойной тап
    // запускал открытие и закрытие ОДНОВРЕМЕННО на одном Opacity/TranslateY, и хвост открытия
    // (Opacity=1, RenderTransform=null) мог приземлиться ПОСЛЕ того, как закрытие уже спрятало панель —
    // шеврон и IsVisible расходились. Пока переход идёт, повторный тап игнорируется.
    private bool _proxyPanelBusy;

    private async void ToggleLocalProxy()
    {
        if (_proxyPanelBusy)
        {
            return;
        }
        _proxyPanelBusy = true;
        try
        {
            var open = !LocalProxyPanel.IsVisible;
            if (open)
            {
                SetProxyChevron(true);
                LocalProxyPanel.IsVisible = true;
                await RevealPanel(LocalProxyPanel, show: true);
                return;
            }

            // Сворачивание = коммит введённых значений (порт/логин/пароль → Inbound[0]). Коммитим ДО
            // скрытия: раньше панель гасла первой, поэтому единственная существующая обратная связь —
            // откат поля к сохранённому порту — происходила за кадром, и «ввёл порт, свернул, ничего не
            // произошло» было неотличимо от «приложение меня не увидело». Неверный порт теперь ОСТАВЛЯЕТ
            // панель раскрытой и возвращает фокус в поле.
            var ok = Vm is null || await Vm.CommitLocalProxyAsync();
            if (!ok)
            {
                ProxyPortBox.Focus();
                ProxyPortBox.SelectAll();
                return;
            }

            SetProxyChevron(false);
            await RevealPanel(LocalProxyPanel, show: false);
            LocalProxyPanel.IsVisible = false;
        }
        finally
        {
            _proxyPanelBusy = false;
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
