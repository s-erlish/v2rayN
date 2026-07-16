using System.Reactive.Disposables;
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
    public HomeView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            // ── Account chip tap → open the Account tab (rail button, unchanged path) ──
            //  Independent of the ViewModel, so wire it unconditionally — never gated behind a
            //  (possibly-null-at-activation) ViewModel the way it used to be.
            void OnAccountRequested(object? s, EventArgs e) => OpenAccountTab();
            AccountChip.AccountRequested += OnAccountRequested;
            Disposable.Create(() => AccountChip.AccountRequested -= OnAccountRequested).DisposeWith(disposables);

            // ── Connect-hero: (re)bind on ViewModel AVAILABILITY (mirrors CompactHomeView) ──────
            //  In the keep-alive model this permanent tab view is activated once — often BEFORE the
            //  host assigns DataContext/ViewModel — so the old `var vm = ViewModel; if (vm is null)
            //  return;` guard could skip the connect wiring forever, leaving the widescreen shield /
            //  server rows dead. Subscribing to ViewModel and rebinding whenever it becomes non-null
            //  (and re-binding if it swaps) keeps the widescreen connect pipeline wired regardless of
            //  activation/DataContext ordering, exactly like compact.
            var heroBinding = new SerialDisposable().DisposeWith(disposables);
            this.WhenAnyValue(x => x.ViewModel)
                .Where(vm => vm is not null)
                .Subscribe(vm => heroBinding.Disposable = HomeHeroPresenter.Bind(ConnectHero, vm!))
                .DisposeWith(disposables);
        });
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
