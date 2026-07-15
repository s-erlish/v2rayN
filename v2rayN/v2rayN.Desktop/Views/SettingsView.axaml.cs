namespace v2rayN.Desktop.Views;

/// <summary>
/// Настройки (вкладка «Настройки») — 1:1 порт Android layout_settings_content.xml:
/// секции-карточки со строками/тумблерами (Подключение, Обход блокировок,
/// Производительность, Интерфейс, Подписка, О приложении). Тёмная Incy, один синий акцент.
/// Значения/тумблеры имеют x:Name и привязываются к настройкам ServiceLib на слое данных;
/// сейчас — design-time дефолты, соответствующие android_settings.jpg.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // Зависимая строка «Число соединений Mux» видна только при включённом Mux
        // (аналог Android rowMuxConcurrency.isVisible = muxOn). Чистая view-логика.
        SwitchMux.IsCheckedChanged += (_, _) => UpdateMuxDependentRows();
        UpdateMuxDependentRows();
    }

    private void UpdateMuxDependentRows()
    {
        var muxOn = SwitchMux.IsChecked == true;
        RowMuxConcurrency.IsVisible = muxOn;
        DividerConcurrency.IsVisible = muxOn;
    }
}
