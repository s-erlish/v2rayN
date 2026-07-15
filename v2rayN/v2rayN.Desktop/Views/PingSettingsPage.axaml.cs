namespace v2rayN.Desktop.Views;

/// <summary>
/// «Пинг» — in-app суб-страница (раньше отдельное окно). Desktop uses one latency method — a real-delay
/// probe through the core. There is no method enum to fake; instead this exposes the real, consumed
/// parameters of that probe: <c>SpeedTestItem.SpeedPingTestUrl</c> + <c>SpeedTestItem.SpeedTestTimeout</c>.
/// Persist only. Уход со страницы сохраняет и поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class PingSettingsPage : UserControl, ISubPage
{
    private readonly Config _config;
    private bool _saved;

    public event EventHandler? BackRequested;

    public PingSettingsPage()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        txtPingUrl.Text = _config.SpeedTestItem.SpeedPingTestUrl ?? string.Empty;
        txtTimeout.Text = _config.SpeedTestItem.SpeedTestTimeout > 0
            ? _config.SpeedTestItem.SpeedTestTimeout.ToString()
            : string.Empty;

        btnBack.Click += async (_, _) => await SaveAndBackAsync();
    }

    private async Task SaveAndBackAsync()
    {
        if (_saved)
        {
            return;
        }
        _saved = true;

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
        BackRequested?.Invoke(this, EventArgs.Empty);
    }
}
