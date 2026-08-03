using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Platform.Storage;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Прокси по приложениям» (split-tunnel) — in-app суб-страница (раньше отдельное окно). Real, working:
/// picks Windows processes (by name) or an explicit .exe path, then persists them AND injects a managed
/// routing rule into the ACTIVE RoutingItem.RuleSet using the sing-box <c>process_name</c>/<c>process_path</c>
/// matchers the engine already builds (SingboxRoutingService.GenRoutingUserRule). Two modes:
///   • bypass  → listed apps go DIRECT (skip the VPN), everything else stays on the tunnel;
///   • include → only listed apps go through the proxy, everything else goes DIRECT.
/// OFF-model honored: a settings change never starts the core; it re-applies live only if running.
/// Уход со страницы (стрелка «назад») сохраняет и применяет, затем поднимает <see cref="BackRequested"/>.
///
/// ПЕРЕЗАПУСК — ТОЛЬКО НА ИЗМЕНЕНИЕ, НИКОГДА НА ОТКРЫТИЕ. Раньше выход отсюда безусловно писал конфиг,
/// переписывал правила маршрутизации и публиковал Reload, поэтому простой заход на страницу рвал
/// туннель: правило чеканилось с НОВЫМ GUID на каждом сохранении, а список приложений собирался в
/// порядке пересортированного списка — набор правил отличался от сохранённого всегда, даже когда
/// человек не тронул ни одной галочки. Теперь снимок состояния на входе + сравнение готового набора
/// правил с сохранённым решают за все три записи; см. <see cref="SaveAndBackAsync"/>.
/// </summary>
public partial class PerAppProxyPage : UserControl, ISubPage
{
    // Marker on the managed RulesItem so we can find/replace ours without touching the user's own rules.
    private const string PerAppMarkerBypass = "__departament_perapp_bypass";
    private const string PerAppMarkerInclude = "__departament_perapp_include";
    private const string PerAppMarkerCatchAll = "__departament_perapp_catchall";

    private readonly Config _config;
    private readonly ObservableCollection<AppItem> _all = new();
    private bool _saved;

    // ── Снимок состояния НА ВХОДЕ. Без него уход со страницы был безусловной записью: конфиг,
    //    перезапись правил маршрутизации и Reload ядра выполнялись даже когда человек ничего не
    //    трогал — владелец: «баг при нажатии прокси по приложениям впн перезапускается, хотя этого
    //    быть не должно, так как я даже процессы никакие не выбрал». Снимок делает разницу
    //    вычислимой: нет разницы — нет записи и нет перезапуска.
    private readonly bool _initialEnabled;
    private readonly bool _initialBypass;
    private readonly List<string> _initialSelection;
    private readonly HashSet<string> _initialSelectionSet;

    public event EventHandler? BackRequested;

    public PerAppProxyPage()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        _initialEnabled = _config.UiItem.PerAppProxyEnabled;
        _initialBypass = _config.UiItem.PerAppProxyBypass;
        _initialSelection = [.. _config.UiItem.PerAppProxyList ?? []];
        _initialSelectionSet = new HashSet<string>(_initialSelection, StringComparer.OrdinalIgnoreCase);

        btnBack.Click += async (_, _) => await SaveAndBackAsync();
        btnRefresh.Click += (_, _) => LoadProcesses();
        btnAddExe.Click += async (_, _) => await AddExeAsync();
        txtFilter.GetObservable(TextBox.TextProperty).Subscribe(_ => ApplyFilter());

        switchEnabled.IsChecked = _config.UiItem.PerAppProxyEnabled;
        rbBypass.IsChecked = _config.UiItem.PerAppProxyBypass;
        rbInclude.IsChecked = !_config.UiItem.PerAppProxyBypass;

        LoadProcesses();
    }

    private void LoadProcesses()
    {
        var selected = new HashSet<string>(
            _config.UiItem.PerAppProxyList ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        // Manually-added / previously-selected entries first (so paths survive even if not running).
        foreach (var id in selected)
        {
            items[id] = new AppItem
            {
                Identifier = id,
                Display = IsPathLike(id) ? Path.GetFileName(id) : id,
                IsChecked = true,
            };
        }

        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (name.IsNullOrEmpty() || items.ContainsKey(name))
                    {
                        continue;
                    }
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    items[name] = new AppItem
                    {
                        Identifier = name,
                        Display = name,
                        Path = path,
                        IsChecked = selected.Contains(name),
                    };
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        _all.Clear();
        foreach (var it in items.Values.OrderByDescending(x => x.IsChecked).ThenBy(x => x.Display, StringComparer.OrdinalIgnoreCase))
        {
            _all.Add(it);
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = txtFilter.Text?.Trim();
        if (q.IsNullOrEmpty())
        {
            listApps.ItemsSource = _all.ToList();
            return;
        }
        listApps.ItemsSource = _all
            .Where(x => (x.Display?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                     || (x.Identifier?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }

    private async Task AddExeAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = Utils.IsWindows()
                ? new[] { new FilePickerFileType(L.T("PerApp_ProgramFileType")) { Patterns = new[] { "*.exe" } } }
                : null,
        });
        var path = files?.FirstOrDefault()?.TryGetLocalPath();
        if (path.IsNullOrEmpty())
        {
            return;
        }
        if (_all.Any(x => string.Equals(x.Identifier, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        _all.Insert(0, new AppItem { Identifier = path!, Display = Path.GetFileName(path), IsChecked = true });
        ApplyFilter();
    }

    /// <summary>
    /// Уход со страницы. ЗАПИСЫВАЕТ И ПЕРЕЗАПУСКАЕТ ТОЛЬКО ПО РЕАЛЬНОМУ ИЗМЕНЕНИЮ — три независимых
    /// решения вместо одного безусловного:
    ///   • конфиг сохраняется, если изменилось хоть одно хранимое поле (тумблер, режим, набор);
    ///   • правила маршрутизации переписываются, только если новый набор правил ОТЛИЧАЕТСЯ от
    ///     сохранённого (см. <see cref="ApplyToRoutingAsync"/>);
    ///   • ядро перезагружается, только если правила действительно переписаны И оно запущено.
    /// Открыть страницу и выйти, ничего не тронув, — теперь ноль записей и ноль перезапусков.
    /// </summary>
    private async Task SaveAndBackAsync()
    {
        if (_saved)
        {
            return;
        }
        _saved = true;

        var enabled = switchEnabled.IsChecked == true;
        var bypass = rbBypass.IsChecked == true;
        var chosen = CollectSelection();

        // Хранимое изменилось? Набор сравнивается как МНОЖЕСТВО: список строится в прежнем порядке
        // (CollectSelection), но сравнение по множеству не зависит от того, как список пересобрался.
        var storedChanged = enabled != _initialEnabled
            || bypass != _initialBypass
            || !new HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase).SetEquals(_initialSelectionSet);

        if (storedChanged)
        {
            _config.UiItem.PerAppProxyEnabled = enabled;
            _config.UiItem.PerAppProxyBypass = bypass;
            _config.UiItem.PerAppProxyList = chosen;
            await ConfigHandler.SaveConfig(_config);
        }

        // Правила считаем всегда (это чтение + сериализация, без записи), а ПИШЕМ и перезапускаем
        // только при фактическом отличии: так лечится и случай, когда правила разошлись с настройкой.
        var routingChanged = await ApplyToRoutingAsync(enabled && chosen.Count > 0, bypass, chosen);

        if (routingChanged && IsCoreRunning())
        {
            StatusBarViewModel.Instance.ReloadRequested.Publish();
        }
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Отмеченные приложения В ПРЕЖНЕМ ПОРЯДКЕ: сперва те, что уже были сохранены (в сохранённом
    /// порядке), затем вновь отмеченные — в порядке списка. Порядок здесь не декоративный: он входит
    /// в правило маршрутизации, а значит и в сравнение «изменилось ли». Список <see cref="_all"/>
    /// пересортирован (отмеченные сверху, дальше по алфавиту), поэтому наивный проход по нему давал бы
    /// другой порядок на каждом открытии — и «ничего не менял» выглядело бы как изменение.
    /// </summary>
    private List<string> CollectSelection()
    {
        var picked = new HashSet<string>(
            _all.Where(x => x.IsChecked && x.Identifier.IsNotEmpty()).Select(x => x.Identifier!),
            StringComparer.OrdinalIgnoreCase);

        var ordered = new List<string>();
        foreach (var id in _initialSelection)
        {
            if (picked.Contains(id))
            {
                ordered.Add(id);
            }
        }
        foreach (var item in _all)
        {
            if (item.IsChecked && item.Identifier.IsNotEmpty() && !_initialSelectionSet.Contains(item.Identifier))
            {
                ordered.Add(item.Identifier);
            }
        }
        return ordered.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Rewrite ONLY our managed rules in the active routing; user rules are untouched. Returns
    /// <c>true</c> when the rule set actually changed and was persisted, <c>false</c> when it is
    /// byte-for-byte what was already stored — the caller uses that to decide whether the core has
    /// anything to reload.
    /// </summary>
    private async Task<bool> ApplyToRoutingAsync(bool active, bool bypass, List<string> apps)
    {
        var routing = await ConfigHandler.GetDefaultRouting(_config);
        if (routing is null)
        {
            return false;
        }

        var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? new List<RulesItem>();
        // Сравниваем с ТЕМ ЖЕ круговым проходом (десериализация → сериализация), иначе разница
        // форматирования читалась бы как изменение правил.
        var before = JsonUtils.Serialize(rules, false);

        // Id ранее выставленных наших правил ПЕРЕИСПОЛЬЗУЕМ. Раньше каждое сохранение чеканило новый
        // GUID, поэтому набор правил отличался ВСЕГДА — даже при том же самом выборе, — и этого одного
        // хватало, чтобы ядро перезапускалось на каждом выходе со страницы.
        var previous = rules
            .Where(r => r.Remarks is PerAppMarkerBypass or PerAppMarkerInclude or PerAppMarkerCatchAll)
            .ToList();
        string IdFor(string marker) => previous.FirstOrDefault(r => r.Remarks == marker)?.Id ?? Utils.GetGuid(false);

        rules.RemoveAll(r => r.Remarks is PerAppMarkerBypass or PerAppMarkerInclude or PerAppMarkerCatchAll);

        if (active)
        {
            if (bypass)
            {
                // Listed apps go DIRECT (bypass the tunnel). Everything else follows the existing rules.
                rules.Insert(0, new RulesItem
                {
                    Id = IdFor(PerAppMarkerBypass),
                    Remarks = PerAppMarkerBypass,
                    OutboundTag = Global.DirectTag,
                    Process = apps,
                    Enabled = true,
                });
            }
            else
            {
                // Only listed apps go through the proxy; everything else goes DIRECT (catch-all after).
                rules.Insert(0, new RulesItem
                {
                    Id = IdFor(PerAppMarkerInclude),
                    Remarks = PerAppMarkerInclude,
                    OutboundTag = Global.ProxyTag,
                    Process = apps,
                    Enabled = true,
                });
                rules.Add(new RulesItem
                {
                    Id = IdFor(PerAppMarkerCatchAll),
                    Remarks = PerAppMarkerCatchAll,
                    OutboundTag = Global.DirectTag,
                    Network = "tcp,udp",
                    Enabled = true,
                });
            }
        }

        var after = JsonUtils.Serialize(rules, false);
        if (after == before)
        {
            return false;
        }

        routing.RuleSet = after;
        routing.RuleNum = rules.Count;
        await ConfigHandler.SaveRoutingItem(_config, routing);
        return true;
    }

    private static bool IsPathLike(string s) => s.Contains('/') || s.Contains('\\');

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);

    public sealed class AppItem
    {
        public string Identifier { get; set; } = string.Empty;
        public string? Display { get; set; }
        public string? Path { get; set; }
        public bool IsChecked { get; set; }
    }
}
