using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Compact (phone-like) Home — the single-scroll re-host of the SAME pieces the widescreen Home uses
/// (CA-3). It reuses <see cref="ConnectHeroView"/>, <see cref="HomeAccountChip"/> and
/// <see cref="ServerListView"/>, all bound to the ONE <see cref="HomeViewModel"/> the host supplies
/// via DataContext. The hero is wired through the shared <see cref="HomeHeroPresenter"/> so the
/// connect pipeline is identical to widescreen.
///
/// The reused <see cref="ConnectHeroView"/> now carries the speed/uptime stats row UNDER the shield
/// (moved off the top, where it looked crooked on compact startup), so this compact tree no longer
/// drives its own top stats row — it only keeps the «+» add affordance in the top-right header. The
/// hero's own corner «+» is hidden here to avoid a duplicate (compact uses the header «+»).
/// </summary>
public partial class CompactHomeView : UserControl
{
    /// <summary>Account chip tapped — host should open the Account tab.</summary>
    public event EventHandler? AccountRequested;

    private IDisposable? _heroBinding;

    public CompactHomeView()
    {
        InitializeComponent();

        AccountChip.AccountRequested += (_, _) => AccountRequested?.Invoke(this, EventArgs.Empty);

        DataContextChanged += OnDataContextChanged;
        // Compact uses its own header «+», so hide the hero's corner «+» (widescreen keeps it).
        AttachedToVisualTree += (_, _) => ConnectHero.SetCornerAddVisible(false);
        DetachedFromVisualTree += (_, _) => DisposeBinding();
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
