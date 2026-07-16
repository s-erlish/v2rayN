using Avalonia.VisualTree;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Compact (phone-like) Home — the single-scroll re-host of the SAME pieces the widescreen Home uses
/// (CA-3). It reuses <see cref="ConnectHeroView"/>, <see cref="HomeAccountChip"/> and
/// <see cref="ServerListView"/>, all bound to the ONE <see cref="HomeViewModel"/> the host supplies
/// via DataContext. The hero is wired through the shared <see cref="HomeHeroPresenter"/> so the
/// connect pipeline is identical to widescreen.
///
/// The reused <see cref="ConnectHeroView"/> already carries its own stats row; here we drive a
/// dedicated top stats row (with the «+»), so the hero's internal one is hidden to avoid duplication.
/// </summary>
public partial class CompactHomeView : UserControl
{
    /// <summary>Account chip tapped — host should open the Account tab.</summary>
    public event EventHandler? AccountRequested;

    private IDisposable? _heroBinding;

    private bool _statsHidden;

    public CompactHomeView()
    {
        InitializeComponent();

        AccountChip.AccountRequested += (_, _) => AccountRequested?.Invoke(this, EventArgs.Empty);

        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => HideHeroStatsRow();
        DetachedFromVisualTree += (_, _) => DisposeBinding();
    }

    // The compact top stats row (bound to the VM, with the «+») replaces the hero's own identical
    // row — hide the duplicate inside the reused ConnectHeroView. Done once on attach so its inner
    // named element is resolvable.
    private void HideHeroStatsRow()
    {
        if (_statsHidden)
        {
            return;
        }
        var innerStats = ConnectHero.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.Name == "StatsRow");
        if (innerStats is not null)
        {
            innerStats.IsVisible = false;
            _statsHidden = true;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DisposeBinding();
        if (DataContext is HomeViewModel vm)
        {
            _heroBinding = HomeHeroPresenter.Bind(ConnectHero, vm);
        }
    }

    private void DisposeBinding()
    {
        _heroBinding?.Dispose();
        _heroBinding = null;
    }
}
