using Avalonia.VisualTree;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Настройки (вкладка «Настройки») — Incy секции-карточки со строками/тумблерами
/// (Подключение, Обход блокировок, Производительность, Интерфейс, Подписка, О приложении).
///
/// Значения/тумблеры биндятся к реальному <see cref="SettingsViewModel"/> (данные читаются из
/// живого <c>Config</c> и пишутся обратно через <c>ConfigHandler.SaveConfig</c>). КАЖДАЯ строка —
/// реальная рабочая функция, ни одного мёртвого affordance:
///   • переключают/открывают диалог: Режим (TUN), DNS, Маршрутизация;
///   • открывают реальный суб-экран: Прокси по приложениям, Пинг, Файлы ресурсов,
///     Настройки провайдеров, О приложении, Резервное копирование, Схемы URL-адресов;
///   • раскрывают инлайн-поля: Локальный прокси;
///   • циклят реальное значение: Число Mux, Язык, Автообновление, Оформление;
///   • тумблеры (Обход сети, IPv6, Mux, Фрагментация, Облегчённый режим, Запуск при загрузке)
///     переключаются тапом по всей строке (56 px), не только по 52×32 тумблеру.
/// OFF-модель: ни одна строка не запускает ядро — изменения применяются вживую лишь если ядро
/// уже запущено. Sample-данные — только design-time (<c>Design.DataContext</c>).
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

        // Циклящиеся значения (тап продвигает реальное значение на месте).
        RowMuxConcurrency.Tapped += (_, _) => _ = Vm?.CycleMuxConcurrencyAsync();
        RowLanguage.Tapped += (_, _) => _ = Vm?.CycleLanguageAsync();
        RowSubAutoUpdate.Tapped += (_, _) => _ = Vm?.CycleAutoUpdateAsync();
        RowAppearance.Tapped += (_, _) => _ = Vm?.CycleAppearanceAsync();

        // --- Строки, открывающие реальные суб-экраны (диалоговые окна Incy) ---
        RowPerApp.Tapped += async (_, _) => await OpenDialogAsync(new PerAppProxyWindow(), refresh: true);
        RowPingMethod.Tapped += async (_, _) => await OpenDialogAsync(new PingSettingsWindow());
        RowAssets.Tapped += async (_, _) => await OpenDialogAsync(new GeoFilesWindow());
        RowProvider.Tapped += async (_, _) => await OpenDialogAsync(new ProviderSettingsWindow(), refresh: true);
        RowAbout.Tapped += async (_, _) => await OpenDialogAsync(new AboutSubWindow());
        RowBackup.Tapped += async (_, _) => await OpenDialogAsync(new BackupSubWindow());
        RowUrlScheme.Tapped += async (_, _) => await OpenDialogAsync(new UrlSchemesWindow());

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

    /// <summary>Open an Incy sub-screen as a modal dialog over the main window, then optionally re-read
    /// row values it may have changed.</summary>
    private async Task OpenDialogAsync(Window window, bool refresh = false)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
        if (refresh)
        {
            Vm?.RefreshDisplayValues();
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
