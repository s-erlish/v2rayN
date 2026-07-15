using System.Reactive.Threading.Tasks;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Мета-бар подписки (под-план 2 / Ф-D4): заголовок + подзаголовок, действия (пинг/обновить/пин),
/// трафик-пилюля + expiry, announce, поддержка + Telegram.
///
/// Data-driven: поля привязаны в code-behind к ВЫБРАННОЙ подписке
/// (<c>ProfilesViewModel.SelectedSub</c>, наследуемый DataContext = <see cref="HomeViewModel"/>).
/// userinfo (трафик/срок/announce/support/web-page/title) заполняет движок —
/// <c>SubscriptionHandler</c> из заголовка <c>subscription-userinfo</c> и директив. Ничего не
/// выдумываем: пусто, пока не появится подписка с userinfo. Карта видна только когда выбрана
/// РЕАЛЬНАЯ подписка (непустой Id), а не псевдо-группа «Все серверы».
///
/// Образцы значений существуют ТОЛЬКО в <see cref="Design.IsDesignMode"/> (для превьюера).
/// </summary>
public partial class SubscriptionMetaView : UserControl
{
    //  Кэш-кисти повторяют токены Incy (тема одна, тёмная) — как в ConnectHeroView/MainWindow.
    private static readonly IBrush _accent = new SolidColorBrush(Color.Parse("#4C8DFF"));       // Brush.Accent
    private static readonly IBrush _muted = new SolidColorBrush(Color.Parse("#9BA1AD"));        // Brush.OnSurfaceVariant
    private static readonly IBrush _red = new SolidColorBrush(Color.Parse("#F04452"));          // Brush.Red (destructive)

    //  Ширина трек-пилюли = @dimen Size.TrafficPill (160): заливка = 160 * used/total.
    private const double TrafficPillWidth = 160d;

    private IDisposable? _binding;
    private ProfilesViewModel? _profiles;
    private ReactiveCommand<Unit, Unit>? _refreshCmd;
    private string _supportUrl = string.Empty;
    private string _webPageUrl = string.Empty;
    private bool _refreshing;

    public SubscriptionMetaView()
    {
        InitializeComponent();

        if (Design.IsDesignMode)
        {
            ApplyDesignSample();
            return;
        }

        PingButton.Click += OnPingClick;
        RefreshButton.Click += OnRefreshClick;
        SupportButton.Click += OnSupportClick;
        TelegramButton.Click += OnTelegramClick;

        // Re-bind whenever the inherited HomeViewModel arrives or changes.
        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) =>
        {
            _binding?.Dispose();
            _binding = null;
        };

        Rebind();
    }

    /// <summary>Resolve the shared engine VM from the inherited DataContext and follow SelectedSub.</summary>
    private void Rebind()
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        _binding?.Dispose();
        _binding = null;

        var vm = DataContext as HomeViewModel;
        _profiles = vm?.Profiles;
        _refreshCmd = vm?.RefreshSubscriptionCmd;

        if (_profiles is null)
        {
            BindSub(null);
            return;
        }

        // WhenAnyValue emits the current SelectedSub immediately, then on every change (incl. the
        // reassignment RefreshSubscriptions() does after an update — which carries fresh userinfo).
        _binding = _profiles
            .WhenAnyValue(x => x.SelectedSub)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(sub => BindSub(sub));
    }

    /// <summary>Project a real subscription (with userinfo) onto the meta-bar fields.</summary>
    private void BindSub(SubItem? sub)
    {
        // The bar represents ONE subscription. The "Все серверы" pseudo-group has an empty Id and
        // no userinfo -> hide the card (data-driven: no fabricated aggregate).
        if (sub is null || sub.Id.IsNullOrEmpty())
        {
            MetaCard.IsVisible = false;
            return;
        }
        MetaCard.IsVisible = true;

        // Title: profile-title -> remarks (never fabricated).
        TitleText.Text = sub.ProfileTitle.IsNotEmpty() ? sub.ProfileTitle : (sub.Remarks ?? string.Empty);

        // Subtitle: last-update time + auto-update interval.
        SubtitleText.Text = FormatSubtitle(sub);

        // Traffic pill: used = upload + download; total <= 0 => unlimited ("∞").
        var used = sub.UploadUsed + sub.DownloadUsed;
        var total = sub.TotalTraffic;
        var unlimited = total <= 0;
        TrafficText.Text = unlimited
            ? $"{Utils.HumanFy(used)} / ∞"
            : $"{Utils.HumanFy(used)} / {Utils.HumanFy(total)}";
        // Fill width = 160 * used/total; unlimited => empty track (0), like the reference.
        TrafficFill.Width = unlimited ? 0d : TrafficPillWidth * Math.Clamp((double)used / total, 0d, 1d);

        // Expiry: "∞" / "до dd.MM.yyyy" / "Просрочено" (red).
        ApplyExpiry(sub.Expire);

        // Announce banner (collapse when the provider sent none).
        var announce = sub.Announce ?? string.Empty;
        AnnounceText.Text = announce;
        AnnounceText.IsVisible = announce.IsNotEmpty();

        // Pin state tint (display only — Accent when pinned, OnSurfaceVariant otherwise).
        PinIcon.Foreground = sub.Pinned ? _accent : _muted;

        // Support / Telegram: shown only when the provider sent the matching URL.
        _supportUrl = sub.SupportUrl ?? string.Empty;
        _webPageUrl = sub.WebPageUrl ?? string.Empty;
        SupportButton.IsVisible = _supportUrl.IsNotEmpty();
        TelegramButton.IsVisible = _webPageUrl.IsNotEmpty();
        ActionRow.IsVisible = _supportUrl.IsNotEmpty() || _webPageUrl.IsNotEmpty();
    }

    private void ApplyExpiry(long expire)
    {
        if (expire <= 0)
        {
            ExpiryText.Text = "∞";
            ExpiryText.Foreground = _muted;
            return;
        }

        // `expire` is epoch SECONDS (Remnawave/Happ), matching the header semantics.
        if (expire < DateTimeOffset.Now.ToUnixTimeSeconds())
        {
            ExpiryText.Text = "Просрочено";
            ExpiryText.Foreground = _red;
            return;
        }

        var date = DateTimeOffset.FromUnixTimeSeconds(expire).LocalDateTime;
        ExpiryText.Text = $"до {date:dd.MM.yyyy}";
        ExpiryText.Foreground = _muted;
    }

    private static string FormatSubtitle(SubItem sub)
    {
        var parts = new List<string>();
        if (sub.UpdateTime > 0)
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(sub.UpdateTime).LocalDateTime;
            parts.Add(dt.ToString("dd.MM.yyyy HH:mm"));
        }
        if (sub.AutoUpdateInterval > 0)
        {
            parts.Add($"Автообновление — {FormatInterval(sub.AutoUpdateInterval)}");
        }
        return string.Join(" · ", parts);
    }

    private static string FormatInterval(int minutes)
    {
        return minutes % 60 == 0 ? $"{minutes / 60} ч." : $"{minutes} мин.";
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    private void OnPingClick(object? sender, RoutedEventArgs e)
    {
        // Real-delay test of the selected subscription's servers (per-row delays update live).
        _profiles?.FastRealPingCmd.Execute().Subscribe(_ => { }, _ => { });
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        var subId = _profiles?.SelectedSub?.Id;
        if (subId.IsNullOrEmpty())
        {
            return;
        }

        _refreshing = true;
        ActionProgress.IsVisible = true;
        try
        {
            // Refresh THIS subscription only, via the real MainWindowViewModel (reached through the
            // host window's DataContext). Re-downloads the sub; the engine now also persists its
            // userinfo/directives. Fall back to the Home "refresh all" command if unreachable.
            var mainVm = TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;
            if (mainVm is not null)
            {
                await mainVm.UpdateSubscriptionProcess(subId, false);
            }
            else if (_refreshCmd is not null)
            {
                await _refreshCmd.Execute().ToTask();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SubscriptionMetaView.Refresh", ex);
        }
        finally
        {
            ActionProgress.IsVisible = false;
            _refreshing = false;
        }

        // Re-read the freshly persisted row so the pill/expiry/announce update immediately.
        try
        {
            var fresh = await AppManager.Instance.GetSubItem(subId);
            if (fresh is not null)
            {
                BindSub(fresh);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SubscriptionMetaView.Rebind", ex);
        }
    }

    private void OnSupportClick(object? sender, RoutedEventArgs e)
    {
        if (_supportUrl.IsNotEmpty())
        {
            ProcUtils.ProcessStart(_supportUrl);
        }
    }

    private void OnTelegramClick(object? sender, RoutedEventArgs e)
    {
        if (_webPageUrl.IsNotEmpty())
        {
            ProcUtils.ProcessStart(_webPageUrl);
        }
    }

    // ── Design-time only ────────────────────────────────────────────────────

    /// <summary>Representative sample for the Avalonia previewer. Never runs at runtime.</summary>
    private void ApplyDesignSample()
    {
        MetaCard.IsVisible = true;
        TitleText.Text = "erlish";
        SubtitleText.Text = "10.07.2026 17:17 · Автообновление — 1 ч.";
        TrafficText.Text = "1.7 TB / ∞";
        TrafficFill.Width = 69;
        ExpiryText.Text = "до 12.08.2026";
        ExpiryText.Foreground = _muted;
        AnnounceText.Text = "Без рекламы на YouTube: Hybrid, Russia\nЕсли не работает, обновите подписку\n@departamentvpn";
        AnnounceText.IsVisible = true;
        PinIcon.Foreground = _muted;
        ActionRow.IsVisible = true;
        SupportButton.IsVisible = true;
        TelegramButton.IsVisible = true;
    }
}
