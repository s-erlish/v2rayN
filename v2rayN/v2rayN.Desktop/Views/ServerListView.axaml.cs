using Avalonia.Data.Converters;
using DialogHostAvalonia;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Server row + list + group header + flags + selected pill + actions. Fills the left panel of Home
/// (and the full-width Servers tab). 1:1 port of the Android servers screen (layout_servers_header +
/// item_section_header + item_recycler_main + layout_servers_empty).
///
/// DATA-DRIVEN: the runtime DataContext is the real <see cref="HomeViewModel"/> (inherited from
/// <see cref="HomeView"/>), so rows bind to real <c>ProfileItemModel</c>s grouped by subscription:
///   name ← Remarks (StripLeadingFlag), protocol ← ConfigType (ConfigTypeToProtocol), transport ←
///   Network·StreamSecurity (ProfileTransport), ping ← DelayVal, selected ← IsActive. Sample rows
///   exist ONLY at design time (DesignData.Home).
///
/// Interactions: a row tap selects + connects the server; the group header collapses/expands;
/// header actions collapse-all / refresh / ping-all / add-menu; a right-click context menu exposes
/// the §2.13 server actions (make default / ping / edit / duplicate / share / delete) against the
/// shared <see cref="ProfilesViewModel"/>. Because that VM raises its confirm / share / clipboard
/// results through ReactiveUI interactions (normally handled by ProfilesView, which is not in this
/// two-panel Home), those three interaction handlers are registered here so the actions work.
/// </summary>
public partial class ServerListView : UserControl
{
    private static readonly string _tag = "ServerListView";
    private readonly List<IDisposable> _interactionHandlers = new();

    // The row a context menu was opened on (captured on ContextRequested, before the menu shows) —
    // robust across Avalonia versions vs. relying on the MenuItem's inherited DataContext.
    private ProfileItemModel? _actionTarget;

    public ServerListView()
    {
        InitializeComponent();
        // NOTE: no runtime DataContext here — it inherits the real HomeViewModel from HomeView.
        // The XAML Design.DataContext (DesignData.Home) only feeds the previewer.
        DataContextChanged += (_, _) => RegisterInteractions();
    }

    #region Server-action interaction handlers (mirror ProfilesView, so share/delete work here)

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RegisterInteractions();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var handler in _interactionHandlers)
        {
            handler.Dispose();
        }
        _interactionHandlers.Clear();
    }

    // Registered once against the shared ProfilesViewModel. Idempotent; disposed on detach.
    private void RegisterInteractions()
    {
        if (_interactionHandlers.Count > 0)
        {
            return;
        }
        if (DataContext is not HomeViewModel { Profiles: { } profiles })
        {
            return;
        }

        // Delete confirmation ("Удалить сервер?" → yes/no).
        _interactionHandlers.Add(profiles.ShowYesNoInteraction.RegisterHandler(async interaction =>
        {
            var result = await UI.ShowYesNo(interaction.Input);
            interaction.SetOutput(result == ButtonResult.Yes);
        }));

        // Share via QR-code (dialog hosted by MainWindow's DialogHost).
        _interactionHandlers.Add(profiles.ShareServerInteraction.RegisterHandler(async interaction =>
        {
            var url = interaction.Input;
            if (url.IsNotEmpty())
            {
                try
                {
                    await DialogHost.Show(new QrcodeView(url));
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
            }
            interaction.SetOutput(Unit.Default);
        }));

        // Share via clipboard (copy share-url / export).
        _interactionHandlers.Add(profiles.SetClipboardDataInteraction.RegisterHandler(async interaction =>
        {
            await AvaUtils.SetClipboardData(this, interaction.Input);
            interaction.SetOutput(Unit.Default);
        }));
    }

    #endregion Server-action interaction handlers

    #region Group + row selection

    // Group header: tap collapses / expands the group (chevron −90°, rows hidden).
    private void OnGroupHeaderTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: HomeServerGroup group })
        {
            group.IsExpanded = !group.IsExpanded;
        }
    }

    // «Collapse all» in the header: any expanded → collapse all; otherwise expand all.
    private void OnCollapseAllClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not HomeViewModel vm)
        {
            return;
        }

        var anyExpanded = vm.ServerGroups.Any(g => g.IsExpanded);
        foreach (var group in vm.ServerGroups)
        {
            group.IsExpanded = !anyExpanded;
        }
    }

    // Server row tap: select + connect (make default server → engine reloads the core).
    private void OnServerRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ProfileItemModel item } && DataContext is HomeViewModel vm)
        {
            _ = vm.SelectServer(item.IndexId);
        }
    }

    // Row press feedback: subtle scale 0.96 (Border.ServerRow.pressed), no ripple/glow (§0.6).
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border b && !b.Classes.Contains("pressed"))
        {
            b.Classes.Add("pressed");
        }
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border b)
        {
            b.Classes.Remove("pressed");
        }
    }

    private void OnRowPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border b)
        {
            b.Classes.Remove("pressed");
        }
    }

    // Search: Enter re-runs the filter (matches ProfilesView.TxtServerFilter_KeyDown).
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && DataContext is HomeViewModel { Profiles: { } profiles })
        {
            _ = profiles.RefreshServers();
        }
    }

    #endregion Group + row selection

    #region «+» add-menu (§2.10)

    // Import from clipboard / QR are reachable through the HomeViewModel DataContext (they wrap
    // MainWindowViewModel.AddServerViaClipboardAsync / AddServerViaScanAsync).
    private void OnImportClipboard(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm)
        {
            _ = vm.AddViaClipboard();
        }
    }

    private void OnImportQr(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm)
        {
            _ = vm.AddViaQr();
        }
    }

    private void OnImportFile(object? sender, RoutedEventArgs e)
    {
        // TODO(add-menu): import a config from a file. Not reachable via the HomeViewModel DataContext;
        // wire to MainWindowViewModel.AddServerViaImageCmd (import a QR image) or a dedicated
        // file-import flow once MainWindowViewModel is exposed to this view.
    }

    private void OnAddTyped(object? sender, RoutedEventArgs e)
    {
        // TODO(add-menu): add a typed server; the EConfigType name is in ((Control)sender).Tag.
        // Not reachable via the HomeViewModel DataContext; wire to the matching
        // MainWindowViewModel.Add{VMess|VLESS|Shadowsocks|SOCKS|HTTP|Trojan|WireGuard|Hysteria2}ServerCmd
        // once MainWindowViewModel is exposed to this view.
    }

    #endregion «+» add-menu

    #region Server-row context actions (§2.13)

    // Capture the right-clicked row before its context menu opens.
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: ProfileItemModel item })
        {
            _actionTarget = item;
        }
    }

    // Point the shared VM's selection at the captured row, then return the VM so an action can run.
    private ProfilesViewModel? SelectTargetRow()
    {
        if (_actionTarget is { } item && DataContext is HomeViewModel { Profiles: { } profiles })
        {
            profiles.SelectedProfile = item;
            profiles.SelectedProfiles = new List<ProfileItemModel> { item };
            return profiles;
        }
        return null;
    }

    private void OnRowMakeDefault(object? sender, RoutedEventArgs e)
    {
        if (_actionTarget is { } item && DataContext is HomeViewModel { Profiles: { } profiles })
        {
            _ = profiles.SetDefaultServer(item.IndexId);
        }
    }

    private void OnRowPing(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.ServerSpeedtest(ESpeedActionType.Realping);
        }
    }

    private void OnRowEdit(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.EditServerAsync();
        }
    }

    private void OnRowDuplicate(object? sender, RoutedEventArgs e)
    {
        // CopyServer is private on the VM; go through the command (selection sets its canExecute).
        if (SelectTargetRow() is { } profiles)
        {
            profiles.CopyServerCmd.Execute().Subscribe(static _ => { }, static _ => { });
        }
    }

    private void OnRowShareQr(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.ShareServerAsync();
        }
    }

    private void OnRowShareLink(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.Export2ShareUrlAsync(false);
        }
    }

    private void OnRowDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.RemoveServerAsync();
        }
    }

    #endregion Server-row context actions
}

/// <summary>
/// Row name converter: strips a leading flag emoji from the remark (the flag already shows in its
/// tile) via <see cref="FlagResolver.StripLeadingFlag"/>. Instantiated locally in ServerListView.axaml
/// (kept out of GlobalResources by design — it is only used by this view).
/// </summary>
public sealed class StripLeadingFlagConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => FlagResolver.StripLeadingFlag(value?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>
/// True when the bound string is non-empty. Used to reveal a row's ping value only after a latency
/// test has a result. Local to ServerListView (kept out of GlobalResources).
/// </summary>
public sealed class NotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrEmpty(value?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
