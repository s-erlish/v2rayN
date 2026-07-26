namespace v2rayN.Desktop.Views;

/// <summary>
/// «DNS» — новая Incy in-app суб-страница (заменяет легаси англоязычное окно <c>DNSSettingWindow</c>).
/// Русская, в стиле Incy: чипы-пресеты провайдера (По умолчанию / Cloudflare / Google / AdGuard / Свой)
/// + поле своего DoH-адреса + переключатель FakeIP. Пишет напрямую в РЕАЛЬНЫЙ
/// <see cref="SimpleDNSItem"/> того же конфига, что сохраняло старое окно:
///   • выбранный провайдер → <c>SimpleDNSItem.RemoteDNS</c> (пусто = встроенный резолвер по умолчанию;
///     это же значение показывает строка «DNS» в списке настроек);
///   • FakeIP → <c>SimpleDNSItem.FakeIP</c>.
/// OFF-модель: правка применяется вживую только если ядро уже запущено. Уход со страницы (стрелка
/// «назад») сохраняет и поднимает <see cref="BackRequested"/>; никакого отдельного окна.
/// </summary>
public partial class DnsSubView : UserControl, ISubPage
{
    // Пресеты remote-DNS (значения совместимы с Global.DomainRemoteDNSAddress / движком).
    private const string CloudflareDoh = "https://cloudflare-dns.com/dns-query";
    private const string GoogleDoh = "https://dns.google/dns-query";
    private const string AdGuardDoh = "https://dns.adguard-dns.com/dns-query";

    private readonly Config _config;
    private bool _saved;

    // Текущий выбранный чип: "default" | "cloudflare" | "google" | "adguard" | "custom".
    private string _selected = "default";

    public event EventHandler? BackRequested;

    public DnsSubView()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        var item = _config.SimpleDNSItem ?? new SimpleDNSItem();
        var remote = item.RemoteDNS?.Trim() ?? string.Empty;

        // Определяем выбранный пресет по текущему значению; неизвестное непустое → «Свой».
        _selected = remote switch
        {
            "" => "default",
            CloudflareDoh => "cloudflare",
            GoogleDoh => "google",
            AdGuardDoh => "adguard",
            _ => "custom",
        };
        txtCustomDoh.Text = _selected == "custom" ? remote : string.Empty;
        switchFakeIp.IsChecked = item.FakeIP == true;

        chipDefault.Tapped += (_, _) => Select("default");
        chipCloudflare.Tapped += (_, _) => Select("cloudflare");
        chipGoogle.Tapped += (_, _) => Select("google");
        chipAdGuard.Tapped += (_, _) => Select("adguard");
        chipCustom.Tapped += (_, _) => Select("custom");

        btnBack.Click += async (_, _) => await SaveAndBackAsync();

        UpdateChips();
    }

    private void Select(string key)
    {
        _selected = key;
        UpdateChips();
        if (key == "custom")
        {
            txtCustomDoh.Focus();
        }
    }

    private void UpdateChips()
    {
        SetChip(chipDefault, _selected == "default");
        SetChip(chipCloudflare, _selected == "cloudflare");
        SetChip(chipGoogle, _selected == "google");
        SetChip(chipAdGuard, _selected == "adguard");
        SetChip(chipCustom, _selected == "custom");
        customPanel.IsVisible = _selected == "custom";
    }

    private static void SetChip(Border chip, bool selected)
    {
        if (selected)
        {
            if (!chip.Classes.Contains("selected"))
            {
                chip.Classes.Add("selected");
            }
        }
        else
        {
            chip.Classes.Remove("selected");
        }
    }

    private string ResolveRemote() => _selected switch
    {
        "cloudflare" => CloudflareDoh,
        "google" => GoogleDoh,
        "adguard" => AdGuardDoh,
        "custom" => txtCustomDoh.Text?.Trim() ?? string.Empty,
        _ => string.Empty, // default → пусто (встроенный резолвер)
    };

    private async Task SaveAndBackAsync()
    {
        if (_saved)
        {
            return;
        }
        _saved = true;

        _config.SimpleDNSItem ??= new SimpleDNSItem();
        _config.SimpleDNSItem.RemoteDNS = ResolveRemote();
        _config.SimpleDNSItem.FakeIP = switchFakeIp.IsChecked == true;

        await ConfigHandler.SaveConfig(_config);

        // Применяем вживую только если ядро уже запущено (consumer-VPN OFF-модель).
        if (IsCoreRunning())
        {
            StatusBarViewModel.Instance.ReloadRequested.Publish();
        }

        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);
}
