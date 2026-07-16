using Avalonia.VisualTree;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Главная — двухпанельная (широкая раскладка). Левая колонка (чип аккаунта сверху + единый список
/// подписок→серверов) биндится к реальному <see cref="HomeViewModel"/> через наследуемый DataContext;
/// правая (connect-щит) связывается общим <see cref="HomeHeroPresenter"/> — той же проводкой, что
/// использует компактная раскладка, поэтому connect-состояние идентично при любой ширине окна.
///
/// Чип аккаунта — общий <see cref="HomeAccountChip"/> (сам показывает/прячет себя по
/// <see cref="Account.AccountSession"/>); его тап здесь превращается в открытие вкладки «Аккаунт»
/// через кнопку рейла (тот же путь, что и раньше).
/// </summary>
public partial class HomeView : ReactiveUserControl<HomeViewModel>
{
    private IDisposable? _heroBinding;
    private bool _attached;

    public HomeView()
    {
        InitializeComponent();

        // ── Account chip tap → open the Account tab (rail button, unchanged path) ──
        //  Independent of the ViewModel, so wire it once, unconditionally.
        AccountChip.AccountRequested += (_, _) => OpenAccountTab();

        // ── Connect-hero binding: mirror CompactHomeView EXACTLY (attach + DataContext driven) ──────
        //  The old wiring lived inside ReactiveUI `WhenActivated`, whose Avalonia activation for a
        //  Control is raised off the `Loaded`/`Unloaded` events (AvaloniaActivationForViewFetcher.
        //  GetActivationForControl). In this keep-alive shell the wide Home is a permanent child whose
        //  ancestor `bodyRoot` starts IsVisible=false and is toggled by CrossfadeShellTo, and whose own
        //  visibility is driven by Opacity — so its `Loaded` activation was not reliably raised/kept,
        //  and the hero binding (shield tap, corner-«+», empty/connect state) never got wired → the
        //  widescreen shield and corner-«+» were dead. CompactHomeView never had this problem because
        //  it binds on `AttachedToVisualTree` + `DataContextChanged`, both of which fire independent of
        //  layout/visibility. We now do the same here: bind whenever this view is attached AND the host
        //  has assigned the shared HomeViewModel as DataContext, tearing down on detach. The host
        //  (MainWindow.BindActiveHome) assigns the VM to ONLY the active-layout Home, so exactly one
        //  Home holds the live pipeline — identical, reliable connect behaviour at any width.
        DataContextChanged += (_, _) => BindHero();
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            BindHero();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            DisposeBinding();
        };
    }

    // (Re)create the connect-hero binding for the current DataContext; a null/foreign DataContext
    // (the host unbinds the INACTIVE layout to release its rows) leaves the hero unbound.
    private void BindHero()
    {
        DisposeBinding();
        if (_attached && DataContext is HomeViewModel vm)
        {
            _heroBinding = HomeHeroPresenter.Bind(ConnectHero, vm);
        }
    }

    private void DisposeBinding()
    {
        _heroBinding?.Dispose();
        _heroBinding = null;
    }

    // Chip tap → open the Account tab: raise a click on the nav-rail's «Аккаунт» button (the same
    // path the rail uses). Read-only reach into the host window; no MainWindow edits.
    private void OpenAccountTab()
    {
        var nav = TopLevel.GetTopLevel(this)?
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(b => b.Name == "navAccount");
        nav?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
