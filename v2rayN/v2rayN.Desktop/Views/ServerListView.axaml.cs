using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Server row + list + group header + flags + selected pill. Fills the left panel of Home.
///
/// DATA-DRIVEN: the runtime DataContext is the real <see cref="HomeViewModel"/> (inherited from
/// <see cref="HomeView"/>), so rows bind to real <c>ProfileItemModel</c>s grouped by subscription:
///   name ← Remarks, protocol ← ConfigType (ConfigTypeToProtocol), transport ← Network·StreamSecurity
///   (ProfileTransport), selected ← IsActive. Sample rows exist ONLY at design time (DesignData.Home).
/// A row tap selects the server and connects it (HomeViewModel.SelectServer → SetDefaultServer).
/// </summary>
public partial class ServerListView : UserControl
{
    public ServerListView()
    {
        InitializeComponent();
        // NOTE: no runtime DataContext here — it inherits the real HomeViewModel from HomeView.
        // The XAML Design.DataContext (DesignData.Home) only feeds the previewer.
    }

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
}
