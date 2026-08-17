using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Platform.Storage;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Прокси по приложениям» (split-tunnel) — подэкран настроек по единому лекалу. Real, working:
/// подбирает процессы Windows (по имени) или явный путь к .exe, сохраняет их И вставляет управляемое
/// правило маршрутизации в АКТИВНЫЙ <c>RoutingItem.RuleSet</c> через матчеры <c>process_name</c> /
/// <c>process_path</c>, которые движок уже умеет (SingboxRoutingService.GenRoutingUserRule). Два режима:
///   • «Кроме выбранных» (bypass)  → перечисленные идут НАПРЯМУЮ, остальное остаётся в туннеле;
///   • «Только выбранные» (include) → через прокси идут только перечисленные, остальное напрямую.
/// OFF-модель: изменение настройки НЕ поднимает ядро; вживую применяется, только если оно уже запущено.
///
/// Две вещи, которым научил тот же экран на Android-стороне продукта:
///   1. Строка «Режим» НЕ переключается по кругу. Значение, которое меняется местами по тапу, не
///      показывает набор целиком и заставляет угадывать следующий шаг. Здесь у строки каретка, и она
///      открывает ОБЩЕЕ «окошко у значения» (<see cref="ValuePopup"/>) — второй реализации выбора
///      в приложении не заводится.
///   2. Подпись строки не обрезается на полуслове. Обрезается ЗНАЧЕНИЕ справа (у него свой лимит),
///      а имя программы и имя файла живут в собственной колонке и переносятся/усекаются по своим
///      правилам.
///
/// Чего на экране НЕТ и почему: в прототипе есть тумблеры «Игры» и «Лаунчеры» (готовые наборы
/// программ). В ветке такого понятия не существует — ни списка категорий, ни признака у процесса.
/// Выдумывать их значило бы нарисовать переключатель, который ничего не делает, поэтому строки не
/// добавлены (вопрос вынесен в отчёт).
///
/// Уход со страницы (стрелка «назад») сохраняет и применяет, затем поднимает <see cref="BackRequested"/>.
/// </summary>
public partial class PerAppProxyPage : UserControl, ISubPage
{
    // Маркер на управляемом RulesItem — по нему находим и заменяем СВОИ правила, не трогая пользовательские.
    private const string PerAppMarkerBypass = "__departament_perapp_bypass";
    private const string PerAppMarkerInclude = "__departament_perapp_include";
    private const string PerAppMarkerCatchAll = "__departament_perapp_catchall";

    // Порядок пунктов окошка = порядок этих индексов. 0 — «Кроме выбранных» (bypass).
    private const int ModeExcept = 0;
    private const int ModeOnly = 1;

    private readonly Config _config;
    private readonly ObservableCollection<AppItem> _all = new();
    private bool _saved;

    public event EventHandler? BackRequested;

    public PerAppProxyPage()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        btnBack.Click += async (_, _) => await SaveAndBackAsync();
        RowRefresh.Tapped += (_, _) => LoadProcesses();
        RowAddExe.Tapped += async (_, _) => await AddExeAsync();
        txtFilter.GetObservable(TextBox.TextProperty).Subscribe(_ => ApplyFilter());

        switchEnabled.IsChecked = _config.UiItem.PerAppProxyEnabled;
        RowEnabled.Tapped += (_, e) =>
        {
            if (SubPageUtil.OriginatedIn<ToggleSwitch>(e.Source))
            {
                return;
            }
            switchEnabled.IsChecked = !(switchEnabled.IsChecked ?? false);
        };

        // ── Режим через общее «окошко у значения» ──
        ModePopup.Options = new[] { L.T("PerApp_ModeExcept"), L.T("PerApp_ModeOnly") };
        ModePopup.SelectedIndex = _config.UiItem.PerAppProxyBypass ? ModeExcept : ModeOnly;
        ModePopup.Picked += (_, _) => UpdateModeValue();
        // Каретка и приглушение значения ведутся ОТ состояния окошка, а не от тапа: окошко умеет
        // закрыться само (Esc, клик мимо, уход со страницы), и строка обязана это отражать.
        ModePopup.GetObservable(ValuePopup.IsOpenProperty).Subscribe(open =>
        {
            SubPageUtil.SetClass(ModeCaret, "open", open);
            SubPageUtil.SetClass(txtModeValue, "open", open);
        });
        RowMode.Tapped += (_, _) => ModePopup.Toggle();
        UpdateModeValue();

        LoadProcesses();
    }

    private void UpdateModeValue() =>
        txtModeValue.Text = ModePopup.SelectedIndex == ModeOnly
            ? L.T("PerApp_ModeOnly")
            : L.T("PerApp_ModeExcept");

    private void LoadProcesses()
    {
        var selected = new HashSet<string>(
            _config.UiItem.PerAppProxyList ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        // Сначала добавленные вручную / ранее выбранные — так путь переживает то, что программа
        // сейчас не запущена и в списке процессов её нет.
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
        var shown = q.IsNullOrEmpty()
            ? _all.ToList()
            : _all.Where(x => (x.Display?.Contains(q!, StringComparison.OrdinalIgnoreCase) ?? false)
                           || (x.Identifier?.Contains(q!, StringComparison.OrdinalIgnoreCase) ?? false))
                  .ToList();

        // Разделитель рисует сама строка, поэтому у ПЕРВОЙ его быть не должно — иначе под шапкой
        // карточки появляется лишняя линия.
        for (var i = 0; i < shown.Count; i++)
        {
            shown[i].ShowDivider = i > 0;
        }

        listApps.ItemsSource = shown;
        AppsCard.IsVisible = shown.Count > 0;
        AppsEmpty.IsVisible = shown.Count == 0;
        txtProgramsLabel.Text = $"{L.T("PerApp_Programs")} · {L.F("PerApp_Chosen", _all.Count(x => x.IsChecked))}";
    }

    private void OnAppRowTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AppItem item)
        {
            return;
        }
        item.IsChecked = !item.IsChecked;
        txtProgramsLabel.Text = $"{L.T("PerApp_Programs")} · {L.F("PerApp_Chosen", _all.Count(x => x.IsChecked))}";
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

    private async Task SaveAndBackAsync()
    {
        if (_saved)
        {
            return;
        }
        _saved = true;

        var enabled = switchEnabled.IsChecked == true;
        var bypass = ModePopup.SelectedIndex != ModeOnly;
        var chosen = _all.Where(x => x.IsChecked && x.Identifier.IsNotEmpty())
                         .Select(x => x.Identifier!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

        _config.UiItem.PerAppProxyEnabled = enabled;
        _config.UiItem.PerAppProxyBypass = bypass;
        _config.UiItem.PerAppProxyList = chosen;
        await ConfigHandler.SaveConfig(_config);

        await ApplyToRoutingAsync(enabled && chosen.Count > 0, bypass, chosen);

        if (IsCoreRunning())
        {
            StatusBarViewModel.Instance.ReloadRequested.Publish();
        }
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Переписывает ТОЛЬКО наши управляемые правила; пользовательские не трогаются.</summary>
    private async Task ApplyToRoutingAsync(bool active, bool bypass, List<string> apps)
    {
        var routing = await ConfigHandler.GetDefaultRouting(_config);
        if (routing is null)
        {
            return;
        }

        var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet) ?? new List<RulesItem>();
        rules.RemoveAll(r => r.Remarks is PerAppMarkerBypass or PerAppMarkerInclude or PerAppMarkerCatchAll);

        if (active)
        {
            if (bypass)
            {
                // Перечисленные — НАПРЯМУЮ (мимо туннеля). Остальное живёт по существующим правилам.
                rules.Insert(0, new RulesItem
                {
                    Id = Utils.GetGuid(false),
                    Remarks = PerAppMarkerBypass,
                    OutboundTag = Global.DirectTag,
                    Process = apps,
                    Enabled = true,
                });
            }
            else
            {
                // Через прокси идут только перечисленные; всё остальное — напрямую (catch-all в конце).
                rules.Insert(0, new RulesItem
                {
                    Id = Utils.GetGuid(false),
                    Remarks = PerAppMarkerInclude,
                    OutboundTag = Global.ProxyTag,
                    Process = apps,
                    Enabled = true,
                });
                rules.Add(new RulesItem
                {
                    Id = Utils.GetGuid(false),
                    Remarks = PerAppMarkerCatchAll,
                    OutboundTag = Global.DirectTag,
                    Network = "tcp,udp",
                    Enabled = true,
                });
            }
        }

        routing.RuleSet = JsonUtils.Serialize(rules, false);
        routing.RuleNum = rules.Count;
        await ConfigHandler.SaveRoutingItem(_config, routing);
    }

    private static bool IsPathLike(string s) => s.Contains('/') || s.Contains('\\');

    private static bool IsCoreRunning() =>
        AppManager.Instance.IsRunningCore(ECoreType.Xray) || AppManager.Instance.IsRunningCore(ECoreType.sing_box);

    /// <summary>Строка списка программ. Уведомляет об изменениях, потому что галочку и разделитель
    /// строки ведёт разметка через <c>Classes.on</c> / <c>IsVisible</c>, а не код-behind по имени.</summary>
    public sealed class AppItem : INotifyPropertyChanged
    {
        private bool _isChecked;
        private bool _showDivider;

        public string Identifier { get; set; } = string.Empty;
        public string? Display { get; set; }
        public string? Path { get; set; }

        /// <summary>Инициал для плитки. Пустое имя даёт «?», а не пустой квадрат.</summary>
        public string Letter =>
            Display.IsNullOrEmpty() ? "?" : Display!.Trim()[..1].ToUpperInvariant();

        public bool IsChecked
        {
            get => _isChecked;
            set => Set(ref _isChecked, value);
        }

        public bool ShowDivider
        {
            get => _showDivider;
            set => Set(ref _showDivider, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
        {
            if (field == value)
            {
                return;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
