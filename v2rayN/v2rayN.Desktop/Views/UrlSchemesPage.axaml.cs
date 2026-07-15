using Microsoft.Win32;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Схемы URL-адресов» — in-app суб-страница (раньше отдельное окно). Real on Windows: registers/unregisters
/// the <c>depv://</c> protocol under HKCU\Software\Classes (per-user, no admin) so the OS launches
/// departament for those links, and shows live registration status. Each scheme row copies to the clipboard.
/// No core interaction. Стрелка «назад» поднимает <see cref="BackRequested"/>.
/// NOTE: dispatching the launched command into the running app is handled by the app-startup layer
/// (argument parsing) — this screen owns the OS-level protocol registration + discovery.
/// </summary>
public partial class UrlSchemesPage : UserControl, ISubPage
{
    private const string Scheme = "depv";

    public event EventHandler? BackRequested;

    public UrlSchemesPage()
    {
        InitializeComponent();

        listSchemes.ItemsSource = new List<SchemeRow>
        {
            new("depv://connect", "Запустить туннель"),
            new("depv://open", "Открыть приложение"),
            new("depv://disconnect", "Остановить соединение"),
            new("depv://close", "Остановить соединение"),
            new("depv://toggle", "Переключить соединение"),
            new("depv://import/{base64}", "Импорт (автоопределение типа)"),
            new("depv://add/{url}", "Добавить по URL"),
        };

        btnBack.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        btnRegister.Click += (_, _) => Register();
        btnUnregister.Click += (_, _) => Unregister();

        RefreshStatus();
    }

    private void OnCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string scheme })
        {
            _ = CopyAsync(scheme);
        }
    }

    private async Task CopyAsync(string text)
    {
        await AvaUtils.SetClipboardData(this, text);
    }

    private void RefreshStatus()
    {
        if (!Utils.IsWindows())
        {
            txtStatus.Text = "Регистрация схемы доступна только на Windows.";
            btnRegister.IsEnabled = false;
            btnUnregister.IsEnabled = false;
            return;
        }
        txtStatus.Text = IsRegistered()
            ? "Схема зарегистрирована — ссылки depv:// открывают departament."
            : "Схема не зарегистрирована.";
    }

    private static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Scheme}\shell\open\command");
            var cmd = key?.GetValue(null) as string;
            return cmd.IsNotEmpty();
        }
        catch
        {
            return false;
        }
    }

    private void Register()
    {
        if (!Utils.IsWindows())
        {
            return;
        }
        try
        {
            var exe = Environment.ProcessPath;
            if (exe.IsNullOrEmpty())
            {
                txtStatus.Text = "Не удалось определить путь к программе.";
                return;
            }
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
            root.SetValue(null, "URL:departament protocol");
            root.SetValue("URL Protocol", string.Empty);
            using (var icon = root.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, $"\"{exe}\",0");
            }
            using (var cmd = root.CreateSubKey(@"shell\open\command"))
            {
                cmd.SetValue(null, $"\"{exe}\" \"%1\"");
            }
            txtStatus.Text = "Схема зарегистрирована — ссылки depv:// открывают departament.";
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Не удалось зарегистрировать: " + ex.Message;
        }
        RefreshStatusButtons();
    }

    private void Unregister()
    {
        if (!Utils.IsWindows())
        {
            return;
        }
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Scheme}", throwOnMissingSubKey: false);
            txtStatus.Text = "Схема удалена.";
        }
        catch (Exception ex)
        {
            txtStatus.Text = "Не удалось удалить: " + ex.Message;
        }
        RefreshStatusButtons();
    }

    private void RefreshStatusButtons()
    {
        var reg = IsRegistered();
        btnRegister.IsEnabled = !reg;
        btnUnregister.IsEnabled = reg;
    }

    public sealed record SchemeRow(string Scheme, string Hint);
}
