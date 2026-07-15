using v2rayN.Desktop.Account;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «О приложении». Real: shows the actual assembly version + runtime info, and opens the real
/// departament site / Telegram bot in the default browser. Nothing here touches the core.
/// </summary>
public partial class AboutSubWindow : Window
{
    public AboutSubWindow()
    {
        InitializeComponent();

        txtVersion.Text = "Версия " + Utils.GetVersionInfo();
        txtRuntime.Text = BuildRuntimeInfo();

        btnDone.Click += (_, _) => Close();
        btnSite.Click += (_, _) => OpenUrl(SiteUrl());
        btnTelegram.Click += (_, _) => OpenUrl($"https://t.me/{BackendConfig.BotUsername}");
        btnCopy.Click += async (_, _) =>
        {
            await AvaUtils.SetClipboardData(this, $"departament · Версия {Utils.GetVersionInfo()}\n{txtRuntime.Text}");
        };
    }

    private static string SiteUrl()
    {
        // BackendConfig.BaseUrl ends with /api — strip it to reach the site root.
        var b = BackendConfig.BaseUrl;
        var idx = b.IndexOf("/api", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? b[..idx] : b;
    }

    private static string BuildRuntimeInfo()
    {
        try
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
            var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
            return $"ОС: {os}\nАрхитектура: {arch}\n.NET: {Environment.Version}";
        }
        catch
        {
            return "—";
        }
    }

    private static void OpenUrl(string url)
    {
        try { ProcUtils.ProcessStart(url); }
        catch { }
    }
}
