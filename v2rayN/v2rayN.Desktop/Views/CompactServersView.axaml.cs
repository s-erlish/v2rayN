namespace v2rayN.Desktop.Views;

/// <summary>
/// Compact «Сервера» tab (CA-4): the servers header (title + count + refresh/ping/add + search)
/// over a reused <see cref="ServerListView"/>. Purely a re-host — it binds to the same
/// <see cref="ViewModels.HomeViewModel"/> the host supplies via DataContext; no state of its own.
/// </summary>
public partial class CompactServersView : UserControl
{
    public CompactServersView()
    {
        InitializeComponent();
    }
}
