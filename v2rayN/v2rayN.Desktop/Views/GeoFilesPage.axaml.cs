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
        btnUpdate.Click += async (_, _) => await UpdateAsync();

        RefreshFileInfo();
    }

    private void RefreshFileInfo()
    {
        txtGeoip.Text = DescribeFile("geoip.dat");
        txtGeosite.Text = DescribeFile("geosite.dat");
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
        btnUpdate.IsEnabled = false;
        btnUpdate.Content = L.T("Geo_Updating");
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
            btnUpdate.Content = L.T("Geo_UpdateNow");
            btnUpdate.IsEnabled = true;
            _busy = false;
        }
    }
}
