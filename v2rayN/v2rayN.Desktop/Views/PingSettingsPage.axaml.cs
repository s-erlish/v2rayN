namespace v2rayN.Desktop.Views;

/// <summary>
/// «Пинг» — in-app суб-страница (раньше отдельное окно). Экран выбирает МЕТОД измерения задержки
/// серверов (как в Android: реальная задержка через ядро / TCP / HTTP / ICMP) и хранит параметры
/// проверки: адрес (<c>SpeedTestItem.SpeedPingTestUrl</c>) + тайм-аут (<c>SpeedTestItem.SpeedTestTimeout</c>).
/// Выбранный метод пишется в <c>SpeedTestItem.PingMethod</c>. Persist only.
/// Уход со страницы сохраняет и поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class PingSettingsPage : UserControl, ISubPage
{
    // Ключи метода, совпадающие с ESpeedActionType там, где ядро их поддерживает (Realping/Tcping);
    // Httping/Icmping — паритет с Android (движок honorит их по мере поддержки).
    private const string MethodReal = "Realping";
    private const string MethodTcp = "Tcping";
    private const string MethodHttp = "Httping";
    private const string MethodIcmp = "Icmping";

    private readonly Config _config;
    private string _method = MethodReal;
    private bool _saved;

    public event EventHandler? BackRequested;

    public PingSettingsPage()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        _method = _config.SpeedTestItem.PingMethod.IsNullOrEmpty()
            ? MethodReal
            : _config.SpeedTestItem.PingMethod!;

        txtPingUrl.Text = _config.SpeedTestItem.SpeedPingTestUrl ?? string.Empty;
        txtTimeout.Text = _config.SpeedTestItem.SpeedTestTimeout > 0
            ? _config.SpeedTestItem.SpeedTestTimeout.ToString()
            : string.Empty;

        RowReal.Tapped += (_, _) => SelectMethod(MethodReal);
        RowTcp.Tapped += (_, _) => SelectMethod(MethodTcp);
        RowHttp.Tapped += (_, _) => SelectMethod(MethodHttp);
        RowIcmp.Tapped += (_, _) => SelectMethod(MethodIcmp);

        UpdateChecks();

        btnBack.Click += async (_, _) => await SaveAndBackAsync();
    }

    private void SelectMethod(string method)
    {
        _method = method;
        UpdateChecks();
    }

    private void UpdateChecks()
    {
        CheckReal.IsVisible = _method == MethodReal;
        CheckTcp.IsVisible = _method == MethodTcp;
        CheckHttp.IsVisible = _method == MethodHttp;
        CheckIcmp.IsVisible = _method == MethodIcmp;
    }

    private async Task SaveAndBackAsync()
    {
        if (_saved)
        {
            return;
        }
        _saved = true;

        _config.SpeedTestItem.PingMethod = _method;

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
