using System.Reactive.Disposables;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Converters;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Главная — двухпанельная. Левая колонка (мета-бар + список серверов) биндится к реальному
/// <see cref="HomeViewModel"/> через наследуемый DataContext; правая (connect-щит) управляется
/// императивно из этого code-behind по реактивному состоянию VM.
///
/// Здесь — только проводка «состояние ↔ вид»: события щита → команды VM, а реактивные свойства VM
/// (IsConnected/IsConnecting/HasServers/скорости/аптайм/выбранный сервер) → методы ConnectHeroView.
/// Никаких данных не хардкодим: пусто по умолчанию, ядро стартует только по действию пользователя.
/// </summary>
public partial class HomeView : ReactiveUserControl<HomeViewModel>
{
    private static readonly RemarkToFlagConverter _flag = new();
    private ConnectHeroView.ConnectVisualState _lastState = ConnectHeroView.ConnectVisualState.Idle;

    public HomeView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            var vm = ViewModel;
            if (vm is null)
            {
                return;
            }

            // ── Connect-hero events → VM commands ──────────────────────────────
            void OnToggle(object? s, EventArgs e) => vm.ConnectToggle();
            void OnAdd(object? s, EventArgs e) => _ = vm.AddViaClipboard();
            void OnAddQr(object? s, EventArgs e) => _ = vm.AddViaQr();
            void OnAddClip(object? s, EventArgs e) => _ = vm.AddViaClipboard();

            ConnectHero.ConnectToggleRequested += OnToggle;
            ConnectHero.AddRequested += OnAdd;
            ConnectHero.AddByQrRequested += OnAddQr;
            ConnectHero.AddFromClipboardRequested += OnAddClip;
            Disposable.Create(() =>
            {
                ConnectHero.ConnectToggleRequested -= OnToggle;
                ConnectHero.AddRequested -= OnAdd;
                ConnectHero.AddByQrRequested -= OnAddQr;
                ConnectHero.AddFromClipboardRequested -= OnAddClip;
            }).DisposeWith(disposables);

            // ── Empty / onboarding: hero shows onboarding CTAs, meta-bar hides ─
            vm.WhenAnyValue(x => x.IsEmpty)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(empty =>
                {
                    ConnectHero.ShowEmptyState(empty);
                    SubMeta.IsVisible = !empty;
                })
                .DisposeWith(disposables);

            // ── Connect visual state (idle / connecting / connected) ───────────
            vm.WhenAnyValue(x => x.IsConnected, x => x.IsConnecting, x => x.HasServers)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => ApplyConnectState(vm))
                .DisposeWith(disposables);

            // ── Live speed + uptime ────────────────────────────────────────────
            vm.WhenAnyValue(x => x.UpSpeed, x => x.DownSpeed)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => ConnectHero.SetSpeeds(vm.UpSpeed, vm.DownSpeed))
                .DisposeWith(disposables);
            vm.WhenAnyValue(x => x.Uptime)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(u => ConnectHero.SetUptime(u))
                .DisposeWith(disposables);

            // ── Active server identity under the shield ────────────────────────
            if (vm.Profiles is not null)
            {
                vm.Profiles.WhenAnyValue(x => x.SelectedProfile)
                    .ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Subscribe(_ => ApplyServerInfo(vm))
                    .DisposeWith(disposables);

                // The active row is flagged during a list rebuild (IsActive), which may not move
                // SelectedProfile — re-resolve identity whenever the collection itself changes.
                void OnItemsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
                    Dispatcher.UIThread.Post(() => ApplyServerInfo(vm));
                vm.Profiles.ProfileItems.CollectionChanged += OnItemsChanged;
                Disposable.Create(() => vm.Profiles.ProfileItems.CollectionChanged -= OnItemsChanged)
                    .DisposeWith(disposables);
            }

            ConnectHero.ShowEmptyState(vm.IsEmpty);
            SubMeta.IsVisible = !vm.IsEmpty;
            ApplyConnectState(vm);
            ApplyServerInfo(vm);
        });
    }

    private void ApplyConnectState(HomeViewModel vm)
    {
        var state = vm.IsConnected
            ? ConnectHeroView.ConnectVisualState.Connected
            : vm.IsConnecting
                ? ConnectHeroView.ConnectVisualState.Connecting
                : ConnectHeroView.ConnectVisualState.Idle;

        // Play the connect-confirm sonar only on the transition INTO connected.
        var animate = state == ConnectHeroView.ConnectVisualState.Connected
                      && _lastState != ConnectHeroView.ConnectVisualState.Connected;

        ConnectHero.SetConnectState(state, hasServer: vm.HasServers, animate: animate);
        _lastState = state;
    }

    private void ApplyServerInfo(HomeViewModel vm)
    {
        // Follow the ACTIVE / default server (the row that will actually connect), not the first
        // list row. The engine marks it IsActive (IndexId == _config.IndexId); fall back to the
        // config's IndexId, then to the selected row. Data-driven — read-only over ProfileItems.
        var p = ResolveActiveProfile(vm);
        if (p is null || p.IndexId.IsNullOrEmpty())
        {
            return;
        }

        var flag = _flag.Convert(p.Remarks, typeof(IImage), null, CultureInfo.CurrentCulture) as IImage;
        ConnectHero.SetServerInfo(
            // Strip the leading flag emoji — the flag renders in its own tile, and Windows draws
            // emoji flags as tofu boxes otherwise.
            FlagResolver.StripLeadingFlag(p.Remarks) ?? string.Empty,
            ProfileDisplay.Protocol(p.ConfigType),
            ProfileDisplay.Transport(p.Network, p.StreamSecurity),
            flag);
    }

    private static ProfileItemModel? ResolveActiveProfile(HomeViewModel vm)
    {
        var items = vm.Profiles?.ProfileItems;
        if (items is null || items.Count == 0)
        {
            return vm.Profiles?.SelectedProfile;
        }

        var active = items.FirstOrDefault(t => t.IsActive);
        if (active is not null)
        {
            return active;
        }

        var indexId = AppManager.Instance.Config?.IndexId;
        if (indexId.IsNotEmpty())
        {
            var match = items.FirstOrDefault(t => t.IndexId == indexId);
            if (match is not null)
            {
                return match;
            }
        }

        return vm.Profiles?.SelectedProfile;
    }
}
