namespace v2rayN.Desktop.Views;

/// <summary>
/// «DNS» — подэкран настроек по единому лекалу (screens.md «Подэкраны»): назад 40 → заголовок 22/700 →
/// пояснение → группы карточек.
///
/// Состав по спецификации — раздел «Пресеты» с РАДИО-выбором: Cloudflare · Google · Cloudflare + Google ·
/// AdGuard · Quad9 · Свой сервер. Раньше это были чипы в WrapPanel: чип — это фильтр («и то, и это»),
/// а здесь выбор взаимоисключающий, поэтому радио честнее. Геометрия строки НЕ меняется между выбранным
/// и невыбранным состоянием (кружок всегда занимает место) — то же правило, что у списка серверов.
///
/// Пишет напрямую в РЕАЛЬНЫЙ <see cref="SimpleDNSItem"/> того же конфига, что сохраняло легаси-окно:
///   • выбранный пресет → <c>SimpleDNSItem.RemoteDNS</c> (это же значение показывает строка «DNS»
///     в списке настроек);
///   • FakeIP → <c>SimpleDNSItem.FakeIP</c>.
///
/// Значения пресетов — DoH-адреса, а не голые IP: ровно тот формат, которым оперирует движок
/// (<c>Global.DomainRemoteDNSAddress</c>), включая комбинацию через запятую. Подпись строки показывает
/// узнаваемый адрес резолвера (1.1.1.1 и т.п.) — так строка читается с одного взгляда, а в конфиг
/// уходит рабочее значение.
///
/// OFF-модель: правка применяется вживую только если ядро уже запущено. Уход со страницы (стрелка
/// «назад») сохраняет и поднимает <see cref="BackRequested"/>; никакого отдельного окна.
///
/// ВХОДА У ЭТОГО ЭКРАНА НЕТ — и это решение, а не недоделка. DNS в настройках выбирается ОКОШКОМ
/// у значения (владелец: «днс правильно что вылезает менюшка»), пункта «Ещё…» рядом с ним не будет.
/// Файл оставлен целиком: правило проекта — уточнять, а не удалять, и экран приведён к общему
/// лекалу, чтобы день, когда пресетам понадобится своя страница, не начинался с переписывания.
/// </summary>
public partial class DnsSubView : UserControl, ISubPage
{
    // Пресеты remote-DNS. Формат — DoH-URL, как в Global.DomainRemoteDNSAddress; комбинация через
    // запятую там же есть штатным пресетом, поэтому «Cloudflare + Google» — не самодеятельность.
    private const string CloudflareDoh = "https://cloudflare-dns.com/dns-query";
    private const string GoogleDoh = "https://dns.google/dns-query";
    private const string BothDoh = CloudflareDoh + "," + GoogleDoh;
    private const string AdGuardDoh = "https://dns.adguard-dns.com/dns-query";
    private const string Quad9Doh = "https://dns.quad9.net/dns-query";

    private readonly Config _config;
    private bool _saved;

    // Текущий выбор: "cloudflare" | "google" | "both" | "adguard" | "quad9" | "custom".
    private string _selected = "cloudflare";

    public event EventHandler? BackRequested;

    public DnsSubView()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        var item = _config.SimpleDNSItem ?? new SimpleDNSItem();
        var remote = item.RemoteDNS?.Trim() ?? string.Empty;

        // Определяем выбранный пресет по текущему значению. Пусто = встроенный резолвер по умолчанию,
        // а он и есть Cloudflare (первый в Global.DomainRemoteDNSAddress) — поэтому пустое значение
        // подсвечивает Cloudflare, а не оставляет экран без выбранной строки.
        _selected = remote switch
        {
            "" => "cloudflare",
            CloudflareDoh => "cloudflare",
            GoogleDoh => "google",
            BothDoh => "both",
            AdGuardDoh => "adguard",
            Quad9Doh => "quad9",
            _ => "custom",
        };
        txtCustomDoh.Text = _selected == "custom" ? remote : string.Empty;
        switchFakeIp.IsChecked = item.FakeIP == true;

        RowCloudflare.Tapped += (_, _) => Select("cloudflare");
        RowGoogle.Tapped += (_, _) => Select("google");
        RowBoth.Tapped += (_, _) => Select("both");
        RowAdGuard.Tapped += (_, _) => Select("adguard");
        RowQuad9.Tapped += (_, _) => Select("quad9");
        RowCustom.Tapped += (_, _) => Select("custom");

        // Тап по всей строке FakeIP переключает тумблер — но не когда источником тапа был сам тумблер
        // (он уже переключился) и не когда тап пришёл с уже гашенной строки.
        RowFakeIp.Tapped += (_, e) =>
        {
            if (SubPageUtil.OriginatedIn<ToggleSwitch>(e.Source))
            {
                return;
            }
            switchFakeIp.IsChecked = !(switchFakeIp.IsChecked ?? false);
        };

        btnBack.Click += async (_, _) => await SaveAndBackAsync();

        UpdateSelection();
    }

    private void Select(string key)
    {
        _selected = key;
        UpdateSelection();
        if (key == "custom")
        {
            txtCustomDoh.Focus();
        }
    }

    private void UpdateSelection()
    {
        SetDot(DotCloudflare, _selected == "cloudflare");
        SetDot(DotGoogle, _selected == "google");
        SetDot(DotBoth, _selected == "both");
        SetDot(DotAdGuard, _selected == "adguard");
        SetDot(DotQuad9, _selected == "quad9");
        SetDot(DotCustom, _selected == "custom");
        customPanel.IsVisible = _selected == "custom";
    }

    private static void SetDot(Border dot, bool on) => SubPageUtil.SetClass(dot, "on", on);

    private string ResolveRemote() => _selected switch
    {
        "google" => GoogleDoh,
        "both" => BothDoh,
        "adguard" => AdGuardDoh,
        "quad9" => Quad9Doh,
        // Пустой «свой» адрес — это НЕ «свой сервер», а «ничего не задано»: пишем пусто, движок
        // возьмёт встроенный резолвер, и строка настроек не покажет ложный «Свой».
        "custom" => txtCustomDoh.Text?.Trim() ?? string.Empty,
        _ => CloudflareDoh,
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
