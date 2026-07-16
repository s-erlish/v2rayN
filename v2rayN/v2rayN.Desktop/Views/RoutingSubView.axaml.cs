using Avalonia.Data.Converters;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Маршрутизация» — новая Incy in-app суб-страница (заменяет легаси Semi-окно
/// <c>RoutingSettingWindow</c>). Русская, в стиле Incy: список наборов правил с отметкой активного
/// (тап = сделать активным), выбор стратегии доменов и сброс к стандартным правилам.
///
/// Логика НЕ дублируется: DataContext — тот же движковый <see cref="RoutingSettingViewModel"/>, что
/// стоял за старым окном, поэтому все изменения пишутся в тот же реальный конфиг
/// (SetDefaultRouting / InitRouting / DomainStrategy → ConfigHandler.SaveConfig). Редактирование
/// отдельных правил (что открывало ещё одно окно) здесь намеренно не показываем — на суб-странице
/// живут только основные, самодостаточные элементы; никаких отдельных окон.
///
/// OFF-модель: при уходе со страницы перестраиваем маршруты и меню, и применяем вживую только если
/// ядро уже запущено. Стрелка «назад» поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class RoutingSubView : UserControl, ISubPage
{
    // Технические значения Xray-стратегии → дружелюбные подписи (значение хранится «как есть»).
    // Подпись берётся из локали (L.T) — суб-страница строится заново при каждом открытии, поэтому
    // язык, выбранный в SettingsView, применяется к комбо на следующем входе.
    private static readonly (string Value, string LabelKey)[] StrategyOptions =
    [
        ("AsIs", "Routing_DsAsIs"),
        ("IPIfNonMatch", "Routing_DsIpIfNonMatch"),
        ("IPOnDemand", "Routing_DsIpOnDemand"),
    ];

    private readonly Config _config;
    private readonly RoutingSettingViewModel _vm;
    private bool _suppressStrategy = true;
    private bool _saved;

    public event EventHandler? BackRequested;

    public RoutingSubView()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;
        _vm = new RoutingSettingViewModel();
        DataContext = _vm;

        listRoutings.ItemsSource = _vm.RoutingItems;

        cmbStrategy.ItemsSource = StrategyOptions.Select(x => L.T(x.LabelKey)).ToList();
        var curIdx = Array.FindIndex(StrategyOptions, x => x.Value == _vm.DomainStrategy);
        cmbStrategy.SelectedIndex = curIdx < 0 ? 0 : curIdx;
        _suppressStrategy = false;
        cmbStrategy.SelectionChanged += OnStrategyChanged;

        btnResetRules.Click += async (_, _) => await ResetRulesAsync();
        btnBack.Click += async (_, _) => await BackAsync();
    }

    /// <summary>Тап по строке набора → сделать его активным по умолчанию (движковый SetDefaultRouting).</summary>
    private async void OnRoutingRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: RoutingItemModel item })
        {
            if (item.IsActive)
            {
                return;
            }
            _vm.SelectedSource = item;
            await _vm.RoutingAdvancedSetDefault();
        }
    }

    private void OnStrategyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressStrategy)
        {
            return;
        }
        var idx = cmbStrategy.SelectedIndex;
        if (idx >= 0 && idx < StrategyOptions.Length)
        {
            // Присваивание Reactive-свойству VM само сохраняет конфиг (его подписка на DomainStrategy).
            _vm.DomainStrategy = StrategyOptions[idx].Value;
        }
    }

    private async Task ResetRulesAsync()
    {
        btnResetRules.IsEnabled = false;
        try
        {
            // Пересоздаёт встроенные наборы правил и обновляет список (движковая команда).
            await _vm.RoutingAdvancedImportRulesCmd.Execute();
        }
        finally
        {
            btnResetRules.IsEnabled = true;
        }
    }

    private async Task BackAsync()
    {
        if (_saved)
        {
            return;
        }
        _saved = true;

        // Как в прежнем OpenRoutingAsync: перестраиваем встроенные маршруты и меню статуса, применяем
        // вживую только если ядро уже запущено.
        if (_vm.IsModified)
        {
            await ConfigHandler.InitBuiltinRouting(_config);
            await StatusBarViewModel.Instance.RefreshRoutingsMenu();
            if (IsCoreRunning())
            {
                StatusBarViewModel.Instance.ReloadRequested.Publish();
            }
        }

        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);
}

/// <summary>Форматирует число правил через язык-зависимый шаблон «{0} правил» / «{0} rules» (L.F).
/// Нужен, потому что StringFormat в XAML статичен и не переключается при смене языка.</summary>
public sealed class RuleCountConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value is int i ? i : 0;
        return L.F("Routing_RulesCount", n);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
