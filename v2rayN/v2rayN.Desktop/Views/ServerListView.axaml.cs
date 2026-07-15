using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Строка сервера + список + групп-хедер + флаги + selected-пилюля (под-план 3).
/// Заполняет левую панель «Главной» и вкладку «Сервера».
///
/// DATA-DRIVEN: шаблон строки биндится к модели (<see cref="ServerRowItem"/>), НЕ хардкодит строки.
/// Здесь — только дизайн-семпл, чтобы экран рендерился; реальные данные подставит
/// ProfilesViewModel.GetProfileItemsEx + FlagResolver (фаза данных, агент B):
///   имя ← ProfileItem.Remarks (без ведущего флага), протокол ← ConfigType.ToUpper(),
///   транспорт ← Network·StreamSecurity, флаг ← страна из remark, группа ← подписка.
/// </summary>
public partial class ServerListView : UserControl
{
    public ServerListView()
    {
        InitializeComponent();
        DataContext = ServerListModel.CreateSample();
    }

    // Групп-хедер: тап сворачивает/разворачивает группу (шеврон −90°, строки скрываются).
    private void OnGroupHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ServerGroupItem group })
        {
            group.IsExpanded = !group.IsExpanded;
        }
    }

    // «Свернуть все» в хедере: любая развёрнута → свернуть все; иначе развернуть все
    // (аналог MainRecyclerAdapter.toggleCollapseAll).
    private void OnCollapseAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ServerListModel model)
        {
            return;
        }

        var anyExpanded = model.Groups.Any(g => g.IsExpanded);
        foreach (var group in model.Groups)
        {
            group.IsExpanded = !anyExpanded;
        }
    }
}

/// <summary>Корневая модель экрана (дизайн-семпл). Реальную заменит ViewModel фазы данных.</summary>
public sealed class ServerListModel
{
    public string Title { get; init; } = "Сервера";
    public string SearchHint { get; init; } = "Поиск серверов…";
    public List<ServerGroupItem> Groups { get; init; } = new();

    // «N серверов · M провайдеров» — считается из данных (строки servers_count / providers_count).
    public string Subtitle
    {
        get
        {
            var servers = Groups.Sum(g => g.Count);
            var providers = Groups.Count;
            return $"{servers} серверов · {providers} провайдеров";
        }
    }

    /// <summary>
    /// Реальные стили remark из подписки (после StripLeadingFlag) + корректные протокол/транспорт
    /// со скриншота. Имена НЕ константы приложения — это примеры конкретной подписки.
    /// </summary>
    public static ServerListModel CreateSample()
    {
        return new ServerListModel
        {
            Groups = new List<ServerGroupItem>
            {
                new()
                {
                    Name = "import sub",
                    Servers = new List<ServerRowItem>
                    {
                        new() { Name = "Hybrid (Автовыбор)", Protocol = "VLESS", Transport = "TCP · REALITY", IsSelected = true, FlagBrush = Tint("#26467F") },
                        new() { Name = "Hybrid (gRPC, PC)", Protocol = "VLESS", Transport = "GRPC · REALITY", FlagBrush = Tint("#26467F") },
                        new() { Name = "Germany", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#8C3B3B") },
                        new() { Name = "Finland", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#3B6EA5") },
                        new() { Name = "Latvia", Protocol = "VLESS", Transport = "GRPC · REALITY", FlagBrush = Tint("#7A2434") },
                        new() { Name = "Russia (YT, TG, WA)", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#34477F") },
                        new() { Name = "↓ xHTTP (Non iOS) ↓", Protocol = "SHADOWSOCKS", Transport = "TCP · NONE", FlagBrush = Tint("#3A414D") },
                        new() { Name = "Germany xHTTP", Protocol = "VLESS", Transport = "XHTTP · REALITY", FlagBrush = Tint("#8C3B3B") },
                        new() { Name = "Netherlands xHTTP", Protocol = "VLESS", Transport = "XHTTP · REALITY", FlagBrush = Tint("#A0432E") },
                        new() { Name = "Netherlands", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#A0432E") },
                        new() { Name = "France", Protocol = "VLESS", Transport = "GRPC · REALITY", FlagBrush = Tint("#3452A0") },
                        new() { Name = "United Kingdom", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#2E3F6B") },
                        new() { Name = "Sweden", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#2F6EA8") },
                    },
                },
                new()
                {
                    Name = "departament • Premium",
                    Servers = new List<ServerRowItem>
                    {
                        new() { Name = "USA (Netflix)", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#34477F") },
                        new() { Name = "Japan", Protocol = "VLESS", Transport = "GRPC · REALITY", FlagBrush = Tint("#A03A46") },
                        new() { Name = "Singapore", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#B0433A") },
                        new() { Name = "Turkey", Protocol = "TROJAN", Transport = "TCP · TLS", FlagBrush = Tint("#9A2E36") },
                        new() { Name = "Poland", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#9A3F52") },
                        new() { Name = "Switzerland", Protocol = "VMESS", Transport = "WS · TLS", FlagBrush = Tint("#8C3B3B") },
                        new() { Name = "Canada", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#9A3F42") },
                        new() { Name = "Estonia", Protocol = "VLESS", Transport = "GRPC · REALITY", FlagBrush = Tint("#3B6EA5") },
                        new() { Name = "Lithuania", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#3E7A4A") },
                        new() { Name = "Spain", Protocol = "VLESS", Transport = "XHTTP · REALITY", FlagBrush = Tint("#A05A2E") },
                        new() { Name = "Italy", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#3E7A4A") },
                        new() { Name = "Austria", Protocol = "VLESS", Transport = "TCP · REALITY", FlagBrush = Tint("#8C3B3B") },
                        new() { Name = "Norway", Protocol = "VLESS", Transport = "GRPC · REALITY", FlagBrush = Tint("#34477F") },
                    },
                },
            },
        };
    }

    private static IBrush Tint(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// <summary>
/// Групп-хедер = подписка (Subid/SubRemarks). Сворачиваемая (default — развёрнута).
/// Реальные: имя ← SubRemarks, Count ← размер группы.
/// </summary>
public sealed class ServerGroupItem : INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public string Name { get; init; } = string.Empty;
    public List<ServerRowItem> Servers { get; init; } = new();

    public int Count => Servers.Count;
    public string CountText => Count.ToString();

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Одна строка сервера. Реальные источники (фаза данных):
///   Name ← FlagUtil.StripLeadingFlag(ProfileItem.Remarks),
///   Protocol ← EConfigType.ToString().ToUpper() (VLESS/SHADOWSOCKS/VMESS/TROJAN/Auto/Chain),
///   Transport ← Network.ToUpper() + " · " + StreamSecurity.ToUpper(),
///   IsSelected ← IndexId == активный, FlagBrush ← FlagResolver (сейчас — дизайн-тинт).
/// БЕЗ города, БЕЗ пинга (Android их в строке не показывает).
/// </summary>
public sealed class ServerRowItem
{
    public string Name { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Transport { get; init; } = string.Empty;
    public bool IsSelected { get; init; }
    public IBrush FlagBrush { get; init; } = Brushes.Gray;
}
