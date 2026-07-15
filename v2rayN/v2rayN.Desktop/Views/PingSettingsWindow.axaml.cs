namespace v2rayN.Desktop.Views;

/// <summary>
/// «Пинг». Desktop uses one latency method — a real-delay probe through the core. There is no
/// method enum to fake; instead this exposes the real, consumed parameters of that probe:
/// <c>SpeedTestItem.SpeedPingTestUrl</c> + <c>SpeedTestItem.SpeedTestTimeout</c>. Persist only.
/// </summary>
public partial class PingSettingsWindow : Window
{
    private readonly Config _config;

    public PingSettingsWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        txtPingUrl.Text = _config.SpeedTestItem.SpeedPingTestUrl ?? string.Empty;
        txtTimeout.Text = _config.SpeedTestItem.SpeedTestTimeout > 0
            ? _config.SpeedTestItem.SpeedTestTimeout.ToString()
            : string.Empty;

        btnDone.Click += async (_, _) => await SaveAndCloseAsync();
    }

    private async Task SaveAndCloseAsync()
    {
        var url = txtPingUrl.Text?.Trim();
        if (url.IsNotEmpty())
        {
            _config.SpeedTestItem.SpeedPingTestUrl = url;
        }
        if (int.TryParse(txtTimeout.Text?.Trim(), out var t) && t is > 0 and < 600)
        {
            _config.SpeedTestItem.SpeedTestTimeout = t;
        }
        await ConfigHandler.SaveConfig(_config);
        Close();
    }
}
