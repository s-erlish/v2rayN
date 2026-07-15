using v2rayN.Desktop.Account;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Настройки провайдеров». Real, persisted:
///   • Автообновление + интервал → <c>GuiItem.AutoUpdateInterval</c> (0 = off; drives the updater);
///   • User-Agent → <c>CoreBasicItem.DefUserAgent</c> (the UA the core sends on outbounds);
///   • HWID → shows the real device id (<c>AuthTokenStore.DeviceId()</c>) with copy-to-clipboard.
/// No core start (settings-only); the interval feeds the background sub-updater on save.
/// </summary>
public partial class ProviderSettingsWindow : Window
{
    private static readonly int[] IntervalOptions = { 0, 6, 12, 24, 48 };

    private readonly Config _config;

    public ProviderSettingsWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        cmbInterval.ItemsSource = IntervalOptions.Select(h => h == 0 ? "Выкл" : $"{h} ч.").ToList();
        var cur = _config.GuiItem.AutoUpdateInterval;
        var idx = Array.IndexOf(IntervalOptions, cur);
        cmbInterval.SelectedIndex = idx < 0 ? 0 : idx;
        switchAutoUpdate.IsChecked = cur > 0;

        txtUserAgent.Text = _config.CoreBasicItem.DefUserAgent ?? string.Empty;
        txtHwid.Text = SafeDeviceId();

        switchAutoUpdate.IsCheckedChanged += (_, _) =>
        {
            // Toggling off zeroes the interval; toggling on restores a sane 24 ч if it was off.
            if (switchAutoUpdate.IsChecked == true && cmbInterval.SelectedIndex == 0)
            {
                cmbInterval.SelectedIndex = Array.IndexOf(IntervalOptions, 24);
            }
            else if (switchAutoUpdate.IsChecked == false)
            {
                cmbInterval.SelectedIndex = 0;
            }
        };
        cmbInterval.SelectionChanged += (_, _) =>
        {
            switchAutoUpdate.IsChecked = cmbInterval.SelectedIndex > 0;
        };

        btnCopyHwid.Click += async (_, _) =>
        {
            await AvaUtils.SetClipboardData(this, txtHwid.Text ?? string.Empty);
        };

        btnDone.Click += async (_, _) => await SaveAndCloseAsync();
    }

    private async Task SaveAndCloseAsync()
    {
        var i = cmbInterval.SelectedIndex;
        _config.GuiItem.AutoUpdateInterval = i >= 0 && i < IntervalOptions.Length ? IntervalOptions[i] : 0;

        var ua = txtUserAgent.Text?.Trim() ?? string.Empty;
        _config.CoreBasicItem.DefUserAgent = ua;

        await ConfigHandler.SaveConfig(_config);
        Close();
    }

    private static string SafeDeviceId()
    {
        try { return AuthTokenStore.DeviceId(); }
        catch { return "—"; }
    }
}
