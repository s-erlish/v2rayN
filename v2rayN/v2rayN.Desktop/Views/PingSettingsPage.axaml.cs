namespace v2rayN.Desktop.Views;

/// <summary>
/// «Пинг» — подэкран настроек по единому лекалу (screens.md «Подэкраны»). Выбирает МЕТОД измерения
/// задержки серверов и хранит параметры проверки: адрес (<c>SpeedTestItem.SpeedPingTestUrl</c>)
/// и тайм-аут (<c>SpeedTestItem.SpeedTestTimeout</c>). Выбранный метод → <c>SpeedTestItem.PingMethod</c>.
///
/// Спецификация просит ЧЕТЫРЕ метода (реальная задержка · HTTP-запрос · TCP-соединение · ICMP), но
/// ядро умеет два: в <c>ESpeedActionType</c> нет ни Httping, ни Icmping, а <c>SpeedtestService</c>
/// сводит неизвестный метод к реальной задержке. Показать четыре строки, две из которых молча делают
/// одно и то же, — это ложный выбор, поэтому строк две, а сноска называет причину. Ранее сохранённое
/// Httping/Icmping (из легаси-окна) так же сводится к реальной задержке.
///
/// Persist only: смена метода не поднимает ядро. Уход со страницы сохраняет и поднимает
/// <see cref="BackRequested"/>.
/// </summary>
public partial class PingSettingsPage : UserControl, ISubPage
{
    // Ключи метода совпадают с именами ESpeedActionType — их читает ProfilesViewModel.ServerSpeedtest.
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

        _method = _config.SpeedTestItem.PingMethod == MethodTcp ? MethodTcp : MethodReal;

        txtPingUrl.Text = _config.SpeedTestItem.SpeedPingTestUrl ?? string.Empty;
        txtTimeout.Text = _config.SpeedTestItem.SpeedTestTimeout > 0
            ? _config.SpeedTestItem.SpeedTestTimeout.ToString()
            : string.Empty;

        RowReal.Tapped += (_, _) => SelectMethod(MethodReal);
        RowTcp.Tapped += (_, _) => SelectMethod(MethodTcp);

        UpdateSelection();

        btnBack.Click += async (_, _) => await SaveAndBackAsync();
    }

    private void SelectMethod(string method)
    {
        _method = method;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        SubPageUtil.SetClass(DotReal, "on", _method == MethodReal);
        SubPageUtil.SetClass(DotTcp, "on", _method == MethodTcp);
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
        // Мусорный тайм-аут молча НЕ пишем: лучше оставить прежнее рабочее значение, чем сохранить
        // ноль и получить проверку, которая никогда не дожидается ответа.
        if (int.TryParse(txtTimeout.Text?.Trim(), out var t) && t is > 0 and < 600)
        {
            _config.SpeedTestItem.SpeedTestTimeout = t;
        }
        await ConfigHandler.SaveConfig(_config);
        BackRequested?.Invoke(this, EventArgs.Empty);
    }
}
