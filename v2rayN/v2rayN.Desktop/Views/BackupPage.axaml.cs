using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Резервное копирование» — in-app суб-страница (раньше отдельное окно). Real: exports the whole config
/// directory to a .zip and restores from one, reusing the engine's <see cref="BackupAndRestoreViewModel"/>
/// (LocalBackup / LocalRestore). Restore makes its own safety backup first, then relaunches the app. No
/// core interaction. Стрелка «назад» поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class BackupPage : UserControl, ISubPage
{
    private readonly BackupAndRestoreViewModel _vm = new();
    private bool _busy;

    public event EventHandler? BackRequested;

    public BackupPage()
    {
        InitializeComponent();

        btnBack.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        btnExport.Click += async (_, _) => await ExportAsync();
        btnImport.Click += async (_, _) => await ImportAsync();
    }

    private async Task ExportAsync()
    {
        if (_busy)
        {
            return;
        }
        var fileName = await UI.SaveFileDialog("Zip|*.zip");
        if (fileName.IsNullOrEmpty())
        {
            return;
        }
        if (!fileName!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".zip";
        }
        _busy = true;
        txtStatus.Text = "Сохранение…";
        try
        {
            var ok = await _vm.LocalBackup(fileName);
            txtStatus.Text = ok ? $"Копия сохранена: {fileName}" : (_vm.OperationMsg.IsNotEmpty() ? _vm.OperationMsg : "Не удалось сохранить копию.");
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Ошибка экспорта: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ImportAsync()
    {
        if (_busy)
        {
            return;
        }
        var fileName = await UI.OpenFileDialog(null);
        if (fileName.IsNullOrEmpty())
        {
            return;
        }
        _busy = true;
        txtStatus.Text = "Восстановление… Приложение перезапустится.";
        try
        {
            // LocalRestore validates the zip, makes a safety backup, then extracts and reboots the app.
            await _vm.LocalRestore(fileName!);
            // If it returns without rebooting, surface any message.
            if (_vm.OperationMsg.IsNotEmpty())
            {
                txtStatus.Text = _vm.OperationMsg;
            }
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Ошибка импорта: " + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }
}
