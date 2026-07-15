using Avalonia.VisualTree;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Настройки (вкладка «Настройки») — Incy секции-карточки со строками/тумблерами
/// (Подключение, Обход блокировок, Производительность, Интерфейс, Подписка, О приложении).
///
/// Значения/тумблеры биндятся к реальному <see cref="SettingsViewModel"/> (данные читаются из
/// живого <c>Config</c> и пишутся обратно через <c>ConfigHandler.SaveConfig</c>). НИ ОДНА строка не
/// является мёртвым affordance:
///   • строки-с-действием (шеврон + hover): Режим, DNS, Маршрутизация — открывают/переключают;
///     Локальный прокси — раскрывает инлайн-поля; Число Mux / Язык / Автообновление — циклят значение;
///   • строки-тумблеры реагируют на тап по всей строке (56 px), не только по 52×32 тумблеру;
///   • строки «только значение» (Пинг, Оформление, О приложении) — без шеврона/hover;
///   • строки «Скоро» (Файлы ресурсов, Провайдеры, Резервное копирование, Схемы URL) — приглушены,
///     без действия, пока их экран не построен на ПК.
/// Sample-данные — только design-time (<c>Design.DataContext</c>).
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

        // --- Строки-с-действием (Border не имеет Command → тап из code-behind) ---
        RowMode.Tapped += (_, _) => _ = Vm?.ToggleTun();
        RowDns.Tapped += (_, _) => _ = Vm?.OpenDnsAsync();
        RowRouting.Tapped += (_, _) => _ = Vm?.OpenRoutingAsync();

        // Локальный прокси — раскрытие инлайн-панели с реальными полями Inbound[0].
        RowLocalProxy.Tapped += OnLocalProxyTapped;
        ProxyPortBox.LostFocus += OnProxyFieldCommit;
        ProxyUserBox.LostFocus += OnProxyFieldCommit;
        ProxyPassBox.LostFocus += OnProxyFieldCommit;

        // Циклящиеся значения (нет экрана-пикера → продвигаем реальное значение на месте).
        RowMuxConcurrency.Tapped += (_, _) => _ = Vm?.CycleMuxConcurrencyAsync();
        RowLanguage.Tapped += (_, _) => _ = Vm?.CycleLanguageAsync();
        RowSubAutoUpdate.Tapped += (_, _) => _ = Vm?.CycleAutoUpdateAsync();

        // --- Тумблер-строки: тап по всей строке переключает тумблер (кроме тапа по самому тумблеру,
        //     иначе двойное переключение = холостой ход) ---
        RowBypassLan.Tapped += (_, e) => ToggleFromRow(SwitchBypassLan, e);
        RowIpv6.Tapped += (_, e) => ToggleFromRow(SwitchIpv6, e);
        RowMux.Tapped += (_, e) => ToggleFromRow(SwitchMux, e);
        RowFragment.Tapped += (_, e) => ToggleFromRow(SwitchFragment, e);
        RowBoot.Tapped += (_, e) => ToggleFromRow(SwitchBoot, e);

        // Зависимая строка «Число соединений Mux» видна только при включённом Mux
        // (аналог Android rowMuxConcurrency.isVisible = muxOn). Чистая view-логика.
        SwitchMux.IsCheckedChanged += (_, _) => UpdateMuxDependentRows();
        UpdateMuxDependentRows();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnLocalProxyTapped(object? sender, TappedEventArgs e)
    {
        var open = !LocalProxyPanel.IsVisible;
        LocalProxyPanel.IsVisible = open;
        LocalProxyChevron.RenderTransform = new RotateTransform(open ? 90 : 0);

        // Сворачивание = коммит введённых значений (порт/логин/пароль → Inbound[0]).
        if (!open)
        {
            _ = Vm?.CommitLocalProxyAsync();
        }
    }

    private void OnProxyFieldCommit(object? sender, RoutedEventArgs e) => _ = Vm?.CommitLocalProxyAsync();

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
