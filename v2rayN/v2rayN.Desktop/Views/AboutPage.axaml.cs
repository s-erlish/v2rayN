using v2rayN.Desktop.Account;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «О приложении» — подэкран настроек по единому лекалу (screens.md «Подэкраны»):
/// «Приложение» (факты с кнопкой копирования) → «Ссылки и документы» (строки с шевроном).
///
/// Всё показанное — настоящее: версия берётся из сборки, строка системы — из
/// <see cref="System.Runtime.InteropServices.RuntimeInformation"/>, адреса — из
/// <see cref="BackendConfig"/>. Ни одной декоративной строки: у каждой есть адрес или экран.
/// Из списка прототипа не перенесены «Исходный код», «Лицензии открытого ПО», «Канал в Telegram» и
/// «Политика конфиденциальности»: под них в ветке нет ни адреса, ни экрана, а шеврон в никуда хуже
/// отсутствующей строки. Решено владельцем — три рабочие строки, отдельного экрана лицензий нет.
/// Строка «Идентификатор» из прототипа заменена на «Систему» (ОС · разрядность): андроидного
/// идентификатора пакета на ПК не существует, и выдумывать его незачем.
///
/// «Проверить обновления» кладёт <see cref="CheckUpdateView"/> на ТОТ ЖЕ стек оболочки — подэкран
/// поверх подэкрана, без единого отдельного окна.
/// Стрелка «назад» поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class AboutPage : UserControl, ISubPage
{
    public event EventHandler? BackRequested;

    public AboutPage()
    {
        InitializeComponent();

        txtVersion.Text = Utils.GetVersionInfo();
        txtSystem.Text = SystemLine();

        btnBack.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);

        // Копируем не то, что видно в срезанной строке, а полную запись: копию несут в поддержку.
        btnCopyVersion.Click += async (_, _) => await CopyAsync(L.F("About_TitleVersion", Utils.GetVersionInfo()));
        btnCopySystem.Click += async (_, _) => await CopyAsync(SystemDetails());

        RowSite.Tapped += (_, _) => OpenUrl(SiteUrl());
        RowFeedback.Tapped += (_, _) => OpenUrl($"https://t.me/{BackendConfig.BotUsername}");
        RowCheckUpdate.Tapped += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is MainWindow main)
            {
                main.OpenSubPage(new CheckUpdateView());
            }
        };
    }

    private async Task CopyAsync(string text)
    {
        await SubPageUtil.CopyAsync(this, text);
        txtCopyState.Text = L.T("About_Copied");
    }

    private static string SiteUrl()
    {
        // BackendConfig.BaseUrl оканчивается на /api — отрезаем, чтобы попасть в корень сайта.
        var b = BackendConfig.BaseUrl;
        var idx = b.IndexOf("/api", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? b[..idx] : b;
    }

    /// <summary>Одна строка для значения справа: ОС и разрядность. Длинное описание ОС уедет в
    /// многоточие — за полной записью есть кнопка копирования.</summary>
    private static string SystemLine()
    {
        try
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
            var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();
            return $"{os} · {arch}";
        }
        catch
        {
            return "—";
        }
    }

    private static string SystemDetails()
    {
        try
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
            var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription.Trim();
            return L.F("About_SystemInfo", os, arch, Environment.Version);
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
