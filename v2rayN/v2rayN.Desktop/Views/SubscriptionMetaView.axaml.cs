using System.ComponentModel;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using ServiceLib.Helper;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Мета-бар подписки = ЗАГОЛОВОК её группы серверов (под-план 2 / Ф-D4). Владелец потребовал, чтобы
/// карточка подписки сверху бесшовно продолжалась в свои серверы одной секцией — поэтому этот вид
/// живёт КАК header каждой группы внутри <see cref="ServerListView"/> (никакого отдельного
/// «Сервера»-заголовка). Показывает: заголовок/подзаголовок, действия (пинг/обновить/пин/«+»),
/// трафик-пилюлю + expiry, announce, поддержку + Telegram. Шеврон сворачивает серверы ЭТОЙ подписки.
///
/// Data-driven: <see cref="Control.DataContext"/> = <see cref="HomeServerGroup"/> (наследуется из
/// шаблона группы). Реальную <see cref="SubItem"/> резолвим по <c>Subid</c> первого сервера группы
/// (<c>AppManager.GetSubItem</c>) — userinfo (трафик/срок/announce/support/title) заполняет движок из
/// заголовка <c>subscription-userinfo</c>. Ничего не выдумываем: без реальной подписки тело скрыто,
/// виден только заголовок группы (имя + сворачивание). Общий движок (пинг/обновление/добавление)
/// достаём из <see cref="MainWindowViewModel"/> хост-окна. Образцы — ТОЛЬКО в <see cref="Design.IsDesignMode"/>.
/// </summary>
public partial class SubscriptionMetaView : UserControl
{
    //  Кэш-кисти повторяют токены Incy (тема одна, тёмная) — как в ConnectHeroView/MainWindow.
    private static readonly IBrush _accent = new SolidColorBrush(Color.Parse("#4C8DFF"));       // Brush.Accent
    private static readonly IBrush _muted = new SolidColorBrush(Color.Parse("#9BA1AD"));        // Brush.OnSurfaceVariant
    private static readonly IBrush _red = new SolidColorBrush(Color.Parse("#F04452"));          // Brush.Red (destructive)

    //  Ширина трек-пилюли = @dimen Size.TrafficPill (160): заливка = 160 * used/total.
    private const double TrafficPillWidth = 160d;

    private HomeServerGroup? _group;
    private string _supportUrl = string.Empty;
    private string _webPageUrl = string.Empty;
    private string _currentSubId = string.Empty;
    private bool _refreshing;

    //  Шеврон сворачивания: явный RotateTransform, угол меняем плавно через собственный переход
    //  (0° раскрыто / −90° свёрнуто). Центр вращения задаётся РОВНО ОДИН раз — через
    //  RenderTransformOrigin="50%,50%" на самом CollapseIcon (ОТНОСИТЕЛЬНЫЙ центр = 11,11 у 22px-глифа).
    //  ВАЖНО «50%,50%», а НЕ «0.5,0.5»: последнее = 0.5 ПИКСЕЛЯ ≈ угол → шеврон облетал бы орбитой.
    //  CenterX/CenterY НАМЕРЕННО оставлены нулевыми, чтобы центр НЕ удвоился (origin 11,11 + center 0 =
    //  11,11 = центр глифа): удвоение унесло бы центр в (22,22) — дальний угол бокса.
    private readonly RotateTransform _chevronRotate = new() { Angle = 0 };

    public SubscriptionMetaView()
    {
        InitializeComponent();

        _chevronRotate.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = RotateTransform.AngleProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Easing = new SplineEasing(0.2, 0, 0, 1), // Ease.Standard
            },
        };
        CollapseIcon.RenderTransform = _chevronRotate;

        if (Design.IsDesignMode)
        {
            ApplyDesignSample();
            return;
        }

        PingButton.Click += OnPingClick;
        RefreshButton.Click += OnRefreshClick;
        SupportButton.Click += OnSupportClick;
        TelegramButton.Click += OnTelegramClick;
        PinButton.Click += OnPinClick;

        DataContextChanged += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unhook();

        Rebind();
    }

    // ── Bind the owning group ────────────────────────────────────────────────

    private void Unhook()
    {
        if (_group is not null)
        {
            _group.PropertyChanged -= OnGroupPropertyChanged;
        }
    }

    private void Rebind()
    {
        if (Design.IsDesignMode)
        {
            return;
        }

        Unhook();
        _group = DataContext as HomeServerGroup;

        if (_group is null)
        {
            MetaCard.IsVisible = false;
            return;
        }

        _group.PropertyChanged += OnGroupPropertyChanged;
        MetaCard.IsVisible = true;
        SyncCollapsed();

        // Header baseline from the group itself (always available, even for a manual no-sub group).
        _currentSubId = string.Empty;
        TitleText.Text = _group.Name;
        SubtitleText.Text = string.Empty;
        SubtitleText.IsVisible = false;
        MetaBody.IsVisible = false;
        RefreshButton.IsVisible = false;
        PinButton.IsVisible = false;

        // Resolve the real subscription (with userinfo) behind this group, if any.
        _ = ResolveAndBindSub();
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeServerGroup.IsExpanded))
        {
            SyncCollapsed();
        }
    }

    // Reflect the group's collapsed state on the chevron (−90° collapsed). Rotates in place about
    // its own centre (CollapseIcon.RenderTransformOrigin=50%,50%); the transition animates the angle.
    private void SyncCollapsed()
    {
        var collapsed = _group is { IsExpanded: false };
        _chevronRotate.Angle = collapsed ? -90 : 0;
    }

    // The group's Subid → real SubItem. Data-driven: null when the group has no real subscription.
    private async Task ResolveAndBindSub()
    {
        var subid = _group?.Servers.FirstOrDefault(s => s is not null && s.Subid.IsNotEmpty())?.Subid;
        if (subid.IsNullOrEmpty())
        {
            return;
        }

        SubItem? sub = null;
        try
        {
            sub = await AppManager.Instance.GetSubItem(subid);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SubscriptionMetaView.Resolve", ex);
        }

        // The group may have been recycled onto another subscription while we awaited.
        if (_group is null || subid != _group.Servers.FirstOrDefault(s => s is not null && s.Subid.IsNotEmpty())?.Subid)
        {
            return;
        }
        if (sub is not null)
        {
            BindSub(sub);
        }
    }

    /// <summary>Project a real subscription (with userinfo) onto the meta-bar fields.</summary>
    private void BindSub(SubItem? sub)
    {
        if (sub is null || sub.Id.IsNullOrEmpty())
        {
            return;
        }
        _currentSubId = sub.Id;

        // Title: profile-title -> remarks -> group name (never fabricated).
        var title = sub.ProfileTitle.IsNotEmpty() ? sub.ProfileTitle : (sub.Remarks ?? string.Empty);
        TitleText.Text = title.IsNotEmpty() ? title : (_group?.Name ?? string.Empty);

        // Subtitle: last-update time + auto-update interval.
        var subtitle = FormatSubtitle(sub);
        SubtitleText.Text = subtitle;
        SubtitleText.IsVisible = subtitle.IsNotEmpty();

        // Body + subscription-only actions become available.
        MetaBody.IsVisible = true;
        RefreshButton.IsVisible = true;
        PinButton.IsVisible = true;

        // Traffic pill: used = upload + download; total <= 0 => unlimited ("∞").
        var used = sub.UploadUsed + sub.DownloadUsed;
        var total = sub.TotalTraffic;
        var unlimited = total <= 0;
        TrafficText.Text = unlimited
            ? $"{FormatBytesRu(used)} / ∞"
            : $"{FormatBytesRu(used)} / {FormatBytesRu(total)}";
        // Fill width = 160 * used/total; unlimited => empty track (0), like the reference.
        TrafficFill.Width = unlimited ? 0d : TrafficPillWidth * Math.Clamp((double)used / total, 0d, 1d);

        // Expiry: "∞" / "до dd.MM.yyyy" / "Просрочено" (red).
        ApplyExpiry(sub.Expire);

        // Announce banner (collapse when the provider sent none).
        var announce = sub.Announce ?? string.Empty;
        AnnounceText.Text = announce;
        AnnounceText.IsVisible = announce.IsNotEmpty();

        // Pin state tint (Accent when pinned, OnSurfaceVariant otherwise).
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

    //  RU byte formatter to match the reference pill «1,7 ТБ / ∞» (comma decimal + Cyrillic units),
    //  since Utils.HumanFy is EN-invariant («1.7 TB»). Base 1024; 1 decimal from КБ up, trimmed.
    private static readonly string[] _ruUnits = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ" };

    private static string FormatBytesRu(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 Б";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < _ruUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var digits = unit == 0 ? 0 : 1;
        var text = value.ToString("N" + digits, ru);
        if (digits == 1 && text.EndsWith(",0", StringComparison.Ordinal))
        {
            text = text[..^2];
        }
        return $"{text} {_ruUnits[unit]}";
    }

    // ── Engine access (shared VM reached through the host window) ─────────────

    private MainWindowViewModel? MainVm => TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;

    private ProfilesViewModel? Profiles => MainVm?.ProfilesViewModel;

    // ── Actions ─────────────────────────────────────────────────────────────

    // Collapse chevron: fold / unfold THIS subscription's server rows (group.IsExpanded).
    private void OnCollapseClick(object? sender, RoutedEventArgs e)
    {
        if (_group is not null)
        {
            _group.IsExpanded = !_group.IsExpanded;
        }
    }

    private void OnPingClick(object? sender, RoutedEventArgs e)
    {
        // Real-delay test of the shown servers (per-row delays update live).
        Profiles?.FastRealPingCmd.Execute().Subscribe(_ => { }, _ => { });
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        var subId = _currentSubId;
        if (subId.IsNullOrEmpty())
        {
            return;
        }

        _refreshing = true;
        // In-place busy indicator: swap the refresh glyph for a spinner in the SAME 40px slot so the
        // action row never grows / shifts (no progress-bar animation). Nothing around it moves.
        RefreshIcon.IsVisible = false;
        RefreshSpinner.IsVisible = true;
        RefreshSpinner.Classes.Add("spinning");
        try
        {
            // Refresh THIS subscription only, via the real MainWindowViewModel. Re-downloads the sub;
            // the engine now also persists its userinfo/directives.
            var mainVm = MainVm;
            if (mainVm is not null)
            {
                await mainVm.UpdateSubscriptionProcess(subId, false);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SubscriptionMetaView.Refresh", ex);
        }
        finally
        {
            RefreshSpinner.Classes.Remove("spinning");
            RefreshSpinner.IsVisible = false;
            RefreshIcon.IsVisible = true;
            _refreshing = false;
        }

        // Re-read the freshly persisted row so the pill/expiry/announce update immediately.
        try
        {
            var fresh = await AppManager.Instance.GetSubItem(subId);
            if (fresh is not null && fresh.Id == _currentSubId)
            {
                BindSub(fresh);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SubscriptionMetaView.Rebind", ex);
        }
    }

    // Pin toggle: flip SubItem.Pinned and persist it (pinned subs sort first / become default tab).
    private async void OnPinClick(object? sender, RoutedEventArgs e)
    {
        var subId = _currentSubId;
        if (subId.IsNullOrEmpty())
        {
            return;
        }
        try
        {
            var sub = await AppManager.Instance.GetSubItem(subId);
            if (sub is null)
            {
                return;
            }
            sub.Pinned = !sub.Pinned;
            await SQLiteHelper.Instance.UpdateAsync(sub);
            // Optimistic tint update (still the same sub in view).
            if (_currentSubId == subId)
            {
                PinIcon.Foreground = sub.Pinned ? _accent : _muted;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SubscriptionMetaView.Pin", ex);
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

    // «+» add-another-subscription menu (clipboard / QR / file) → shared MainWindowViewModel.
    private void OnImportClipboard(object? sender, RoutedEventArgs e) => _ = MainVm?.AddServerViaClipboardAsync(null);

    private void OnImportQr(object? sender, RoutedEventArgs e) => _ = MainVm?.AddServerViaScanAsync();

    private void OnImportFile(object? sender, RoutedEventArgs e) => _ = MainVm?.AddServerViaImageAsync();

    // ── Design-time only ────────────────────────────────────────────────────

    /// <summary>Representative sample for the Avalonia previewer. Never runs at runtime.</summary>
    private void ApplyDesignSample()
    {
        MetaCard.IsVisible = true;
        RefreshButton.IsVisible = true;
        PinButton.IsVisible = true;
        TitleText.Text = "erlish";
        SubtitleText.Text = "10.07.2026 17:17 · Автообновление — 1 ч.";
        SubtitleText.IsVisible = true;
        MetaBody.IsVisible = true;
        TrafficText.Text = "1,7 ТБ / ∞";
        TrafficFill.Width = 0;
        ExpiryText.Text = "∞";
        ExpiryText.Foreground = _muted;
        AnnounceText.Text = "Без рекламы на YouTube: Hybrid, Russia\nЕсли не работает, обновите подписку\n@departamentvpn";
        AnnounceText.IsVisible = true;
        PinIcon.Foreground = _muted;
        ActionRow.IsVisible = true;
        SupportButton.IsVisible = true;
        TelegramButton.IsVisible = true;
    }
}
