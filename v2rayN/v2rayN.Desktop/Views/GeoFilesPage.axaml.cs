using ServiceLib.Services;

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
                return "Не загружен";
            }
            var fi = new FileInfo(path);
            var mb = fi.Length / 1024d / 1024d;
            return $"{mb:0.0} МБ · обновлён {fi.LastWriteTime:dd.MM.yyyy HH:mm}";
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
        btnUpdate.Content = "Обновление…";
        txtStatus.Text = "Загрузка баз…";

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
            txtStatus.Text = "Готово — базы обновлены.";
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Не удалось обновить: " + ex.Message;
        }
        finally
        {
            RefreshFileInfo();
            btnUpdate.Content = "Обновить сейчас";
            btnUpdate.IsEnabled = true;
            _busy = false;
        }
    }
}
