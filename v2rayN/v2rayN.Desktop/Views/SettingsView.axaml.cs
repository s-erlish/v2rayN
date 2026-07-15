using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Настройки (вкладка «Настройки») — Incy секции-карточки со строками/тумблерами
/// (Подключение, Обход блокировок, Производительность, Интерфейс, Подписка, О приложении).
///
/// Значения/тумблеры биндятся к реальному <see cref="SettingsViewModel"/> (данные читаются из
/// живого <c>Config</c> и пишутся обратно через <c>ConfigHandler.SaveConfig</c>). Тумблеры —
/// two-way биндинги, строки-под-экраны (Режим/DNS/Маршрутизация) открываются из code-behind по
/// тапу. Sample-данные — только design-time (<c>Design.DataContext</c>).
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

        // Строки, которым нужен обработчик тапа (Border не имеет Command):
        //   • Режим  → переключить TUN ↔ Прокси (StatusBarViewModel.EnableTun — персист + reload сам);
        //   • DNS    → открыть реальный экран DNS-настроек;
        //   • Маршрутизация → открыть реальный экран маршрутизации.
        RowMode.Tapped += OnModeTapped;
        RowDns.Tapped += OnDnsTapped;
        RowRouting.Tapped += OnRoutingTapped;

        // Зависимая строка «Число соединений Mux» видна только при включённом Mux
        // (аналог Android rowMuxConcurrency.isVisible = muxOn). Чистая view-логика.
        SwitchMux.IsCheckedChanged += (_, _) => UpdateMuxDependentRows();
        UpdateMuxDependentRows();
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnModeTapped(object? sender, TappedEventArgs e) => Vm?.ToggleTun();

    private void OnDnsTapped(object? sender, TappedEventArgs e) => _ = Vm?.OpenDnsAsync();

    private void OnRoutingTapped(object? sender, TappedEventArgs e) => _ = Vm?.OpenRoutingAsync();

    private void UpdateMuxDependentRows()
    {
        var muxOn = SwitchMux.IsChecked == true;
        RowMuxConcurrency.IsVisible = muxOn;
        DividerConcurrency.IsVisible = muxOn;
    }
}
