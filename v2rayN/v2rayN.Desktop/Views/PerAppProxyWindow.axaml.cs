using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Platform.Storage;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Прокси по приложениям» (split-tunnel). Real, working: picks Windows processes (by name) or an
/// explicit .exe path, then persists them AND injects a managed routing rule into the ACTIVE
/// RoutingItem.RuleSet using the sing-box <c>process_name</c>/<c>process_path</c> matchers the engine
/// already builds (SingboxRoutingService.GenRoutingUserRule). Two modes:
///   • bypass  → listed apps go DIRECT (skip the VPN), everything else stays on the tunnel;
///   • include → only listed apps go through the proxy, everything else goes DIRECT.
/// OFF-model honored: a settings change never starts the core; it re-applies live only if running.
/// Process routing requires TUN mode + sing-box.
/// </summary>
public partial class PerAppProxyWindow : Window
{
    // Marker on the managed RulesItem so we can find/replace ours without touching the user's own rules.
    private const string PerAppMarkerBypass = "__departament_perapp_bypass";
    private const string PerAppMarkerInclude = "__departament_perapp_include";
    private const string PerAppMarkerCatchAll = "__departament_perapp_catchall";

    private readonly Config _config;
    private readonly ObservableCollection<AppItem> _all = new();

    public PerAppProxyWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        btnDone.Click += async (_, _) => await SaveAndCloseAsync();
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
                ? new[] { new FilePickerFileType("Программа") { Patterns = new[] { "*.exe" } } }
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

    private async Task SaveAndCloseAsync()
    {
        var enabled = switchEnabled.IsChecked == true;
        var bypass = rbBypass.IsChecked == true;
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
        Close();
    }

    /// <summary>Rewrite ONLY our managed rules in the active routing; user rules are untouched.</summary>
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
                // Listed apps go DIRECT (bypass the tunnel). Everything else follows the existing rules.
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
                // Only listed apps go through the proxy; everything else goes DIRECT (catch-all after).
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

    private sealed class AppItem
    {
        public string Identifier { get; set; } = string.Empty;
        public string? Display { get; set; }
        public string? Path { get; set; }
        public bool IsChecked { get; set; }
    }
}
