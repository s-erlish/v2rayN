using ServiceLib.Services;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Файлы ресурсов» — in-app суб-страница (раньше отдельное окно). Real: downloads geoip.dat +
/// geosite.dat (and sing-box .srs rulesets) through the engine's <see cref="UpdateService"/>, streaming
/// per-file progress into the status line and refreshing the on-disk file info. Pure asset update —
/// never touches the core. Стрелка «назад» поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class GeoFilesPage : UserControl, ISubPage
{
    private readonly Config _config;
    private bool _busy;

    public event EventHandler? BackRequested;

    public GeoFilesPage()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        btnBack.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);

        // Движок качает обе базы одним вызовом UpdateGeoFileAll — раздельного обновления в ветке
        // нет. Поэтому тап по любой из строк запускает одно и то же, а «Обновление…» загорается
        // сразу в обеих: обещать построчное обновление там, где его нет, — врать интерфейсом.
        RowGeosite.Tapped += async (_, _) => await UpdateAsync();
        RowGeoip.Tapped += async (_, _) => await UpdateAsync();

        txtSource.Text = DescribeSource();
        RefreshFileInfo();
    }

    private void RefreshFileInfo()
    {
        txtGeoip.Text = DescribeFile("geoip.dat");
        txtGeosite.Text = DescribeFile("geosite.dat");
    }

    /// <summary>
    /// Источник показан именем, а не полным адресом: строка узкая, а адрес — шаблон с «{0}».
    /// По умолчанию это релизы Loyalsoldier (<see cref="Global.GeoUrl"/>); если владелец задал
    /// свой адрес в конфиге — показываем его хост.
    /// </summary>
    private string DescribeSource()
    {
        var custom = _config.ConstItem.GeoSourceUrl;
        if (custom.IsNullOrEmpty())
        {
            return "Loyalsoldier";
        }
        return Uri.TryCreate(custom, UriKind.Absolute, out var uri) ? uri.Host : custom;
    }

    private static string DescribeFile(string name)
    {
        try
        {
            var path = Utils.GetBinPath(name);
            if (!File.Exists(path))
            {
                return L.T("Geo_NotDownloaded");
            }
            var fi = new FileInfo(path);
            var mb = fi.Length / 1024d / 1024d;
            return L.F("Geo_SizeUpdated", mb.ToString("0.0", CultureInfo.CurrentUICulture), fi.LastWriteTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentUICulture));
        }
        catch
        {
            return "—";
        }
    }

    private async Task UpdateAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        SetActionText(L.T("Geo_Updating"), busy: true);
        txtStatus.Text = L.T("Geo_Downloading");

        try
        {
            var svc = new UpdateService(_config, (success, msg) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (msg.IsNotEmpty())
                    {
                        txtStatus.Text = msg;
                    }
                    if (success)
                    {
                        RefreshFileInfo();
                    }
                });
                return Task.CompletedTask;
            });
            await svc.UpdateGeoFileAll();
            txtStatus.Text = L.T("Geo_Done");
        }
        catch (Exception ex)
        {
            txtStatus.Text = L.T("Geo_Failed") + ex.Message;
        }
        finally
        {
            RefreshFileInfo();
            SetActionText(L.T("Geo_Update"), busy: false);
            _busy = false;
        }
    }

    /// <summary>
    /// Пока идёт загрузка, действие теряет акцент и перестаёт откликаться на тап: акцентный текст
    /// читается как «нажми», а нажимать уже нечего.
    /// </summary>
    private void SetActionText(string text, bool busy)
    {
        foreach (var (action, row) in new[] { (txtGeositeAction, RowGeosite), (txtGeoipAction, RowGeoip) })
        {
            action.Text = text;
            action.Classes.Set("accent", !busy);
            row.Classes.Set("tap", !busy);
        }
    }
}
