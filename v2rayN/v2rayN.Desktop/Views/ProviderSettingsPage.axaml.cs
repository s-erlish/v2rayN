using v2rayN.Desktop.Account;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Настройки подписок» — подэкран настроек по единому лекалу (screens.md «Подэкраны»):
/// «Обновление» (автообновление + зависимая строка интервала) → «Сеть» (идентификатор устройства,
/// User-Agent).
///
/// Этого экрана в дизайн-пакете нет вовсе — это пробел пакета, а не указание его убрать: всё, что
/// он показывает, реально и сохраняется:
///   • Автообновление + интервал → <c>GuiItem.AutoUpdateInterval</c> (0 = выключено; им живёт
///     фоновый обновлятель подписок);
///   • User-Agent → <c>CoreBasicItem.DefUserAgent</c> (его ядро шлёт на исходящих);
///   • Идентификатор устройства → настоящий <c>AuthTokenStore.DeviceId()</c> с копированием.
/// Ядро не запускается (экран только настроек); интервал подхватывается обновлятором при
/// сохранении. Уход со страницы сохраняет и поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class ProviderSettingsPage : UserControl, ISubPage
{
    // 0 = выключено; остальное — часы. Первый пункт окошка соответствует «выключено», поэтому при
    // включённом тумблере он недостижим: тумблер и есть выключатель.
    private static readonly int[] IntervalOptions = { 6, 12, 24, 48 };
    private const int DefaultInterval = 24;

    private readonly Config _config;
    private bool _saved;

    public event EventHandler? BackRequested;

    public ProviderSettingsPage()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        var cur = _config.GuiItem.AutoUpdateInterval;
        var idx = Array.IndexOf(IntervalOptions, cur);

        IntervalPopup.Options = IntervalOptions.Select(h => L.F("Common_HoursShort", h)).ToList();
        IntervalPopup.SelectedIndex = idx < 0 ? Array.IndexOf(IntervalOptions, DefaultInterval) : idx;
        IntervalPopup.Picked += (_, _) => UpdateIntervalValue();
        IntervalPopup.GetObservable(ValuePopup.IsOpenProperty).Subscribe(open =>
        {
            SubPageUtil.SetClass(IntervalCaret, "open", open);
            SubPageUtil.SetClass(txtIntervalValue, "open", open);
        });
        RowInterval.Tapped += (_, _) => IntervalPopup.Toggle();
        UpdateIntervalValue();

        switchAutoUpdate.IsChecked = cur > 0;
        SetIntervalVisible(cur > 0);
        switchAutoUpdate.IsCheckedChanged += (_, _) => SetIntervalVisible(switchAutoUpdate.IsChecked == true);

        // Тап по строке-тумблеру переключает тумблер — но не когда источником тапа был он сам.
        RowAutoUpdate.Tapped += (_, e) =>
        {
            if (!SubPageUtil.OriginatedIn<ToggleSwitch>(e.Source))
            {
                switchAutoUpdate.IsChecked = switchAutoUpdate.IsChecked != true;
            }
        };

        txtUserAgent.Text = _config.CoreBasicItem.DefUserAgent ?? string.Empty;
        txtHwid.Text = SafeDeviceId();
        btnCopyHwid.Click += async (_, _) => await SubPageUtil.CopyAsync(this, txtHwid.Text);

        btnBack.Click += async (_, _) => await SaveAndBackAsync();
    }

    private void UpdateIntervalValue()
    {
        var i = IntervalPopup.SelectedIndex;
        txtIntervalValue.Text = i >= 0 && i < IntervalOptions.Length ? L.F("Common_HoursShort", IntervalOptions[i]) : string.Empty;
    }

    /// <summary>Строка интервала существует только при включённом автообновлении. Закрываем окошко
    /// при скрытии: иначе оно осталось бы висеть над исчезнувшей строкой.</summary>
    private void SetIntervalVisible(bool on)
    {
        if (!on)
        {
            IntervalPopup.Close();
        }
        intervalRow.IsVisible = on;
    }

    private async Task SaveAndBackAsync()
    {
        if (_saved)
        {
            return;
        }
        _saved = true;

        var i = IntervalPopup.SelectedIndex;
        var hours = i >= 0 && i < IntervalOptions.Length ? IntervalOptions[i] : DefaultInterval;
        _config.GuiItem.AutoUpdateInterval = switchAutoUpdate.IsChecked == true ? hours : 0;

        _config.CoreBasicItem.DefUserAgent = txtUserAgent.Text?.Trim() ?? string.Empty;

        await ConfigHandler.SaveConfig(_config);
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string SafeDeviceId()
    {
        try { return AuthTokenStore.DeviceId(); }
        catch { return "—"; }
    }
}
