namespace v2rayN.Desktop.Views;

/// <summary>
/// «Пинг» — in-app суб-страница (раньше отдельное окно). Экран выбирает МЕТОД измерения задержки
/// серверов и хранит параметры проверки: адрес (<c>SpeedTestItem.SpeedPingTestUrl</c>) +
/// тайм-аут (<c>SpeedTestItem.SpeedTestTimeout</c>).
/// Ядро поддерживает только реальную задержку (Realping) и TCP (Tcping); прочие методы (Httping/Icmping)
/// в движке отсутствуют, поэтому не предлагаются, а ранее сохранённое значение сводится к Realping.
/// Выбранный метод пишется в <c>SpeedTestItem.PingMethod</c>. Persist only.
/// Уход со страницы сохраняет и поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class PingSettingsPage : UserControl, ISubPage
{
    // Ключи метода, совпадающие с ESpeedActionType. Ядро поддерживает только Realping/Tcping.
    private const string MethodReal = "Realping";
    private const string MethodTcp = "Tcping";

    private readonly Config _config;
    private string _method = MethodReal;
    private bool _saved;

    public event EventHandler? BackRequested;

    public PingSettingsPage()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        // Только Realping/Tcping реально измеряются; всё остальное (в т.ч. старые Httping/Icmping)
        // сводим к Realping — движок и так мапит неподдержанные методы на реальную задержку.
        _method = _config.SpeedTestItem.PingMethod == MethodTcp
            ? MethodTcp
            : MethodReal;

        txtPingUrl.Text = _config.SpeedTestItem.SpeedPingTestUrl ?? string.Empty;
        txtTimeout.Text = _config.SpeedTestItem.SpeedTestTimeout > 0
            ? _config.SpeedTestItem.SpeedTestTimeout.ToString()
            : string.Empty;

        RowReal.Tapped += (_, _) => SelectMethod(MethodReal);
        RowTcp.Tapped += (_, _) => SelectMethod(MethodTcp);

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
