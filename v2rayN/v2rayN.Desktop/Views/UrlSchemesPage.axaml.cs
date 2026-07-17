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

    // Browser→app SSO return scheme. Distinct from the tunnel-action scheme «depv» because the site's
    // safe-return allowlist only accepts a «departament…»-prefixed scheme (^departament[a-z0-9]*$), which
    // «depv» does not match. Registered alongside «depv» so «Войти через сайт» can round-trip back.
    private const string AuthScheme = "departamentvpn";

    public event EventHandler? BackRequested;

    public UrlSchemesPage()
    {
        InitializeComponent();

        listSchemes.ItemsSource = new List<SchemeRow>
        {
            new("depv://connect", L.T("UrlSchemes_StartTunnel")),
            new("depv://open", L.T("UrlSchemes_OpenApp")),
            new("depv://disconnect", L.T("UrlSchemes_Stop")),
            new("depv://close", L.T("UrlSchemes_Stop")),
            new("depv://toggle", L.T("UrlSchemes_Toggle")),
            new("depv://import/{base64}", L.T("UrlSchemes_Import")),
            new("depv://add/{url}", L.T("UrlSchemes_AddByUrl")),
            new("departamentvpn://auth", L.T("Common_SignInWebsite")),
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
            txtStatus.Text = L.T("UrlSchemes_WindowsOnly");
            btnRegister.IsEnabled = false;
            btnUnregister.IsEnabled = false;
            return;
        }
        txtStatus.Text = IsRegistered()
            ? L.T("UrlSchemes_Registered")
            : L.T("UrlSchemes_NotRegistered");
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
                txtStatus.Text = L.T("UrlSchemes_NoPath");
                return;
            }
            // Register both the tunnel-action scheme and the browser→app sign-in return scheme.
            RegisterScheme(Scheme, exe!);
            RegisterScheme(AuthScheme, exe!);
            txtStatus.Text = L.T("UrlSchemes_Registered");
        }
        catch (Exception ex)
        {
            txtStatus.Text = L.T("UrlSchemes_RegisterFailed") + ex.Message;
        }
        RefreshStatusButtons();
    }

    private static void RegisterScheme(string scheme, string exe)
    {
        using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
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
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{AuthScheme}", throwOnMissingSubKey: false);
            txtStatus.Text = L.T("UrlSchemes_RemovedOk");
        }
        catch (Exception ex)
        {
            txtStatus.Text = L.T("UrlSchemes_RemoveFailed") + ex.Message;
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
