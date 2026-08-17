using Avalonia.Animation;
using v2rayN.Desktop.Account;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>The compact tabs — the single source of truth for tab identity across both layouts.
/// There is no «Сервера» tab: the compact Home already lists servers in its single scroll.</summary>
public enum AppTab
{
    Home,
    Settings,
    Account,
}

/// <summary>
/// Bottom navigation for the compact (phone-like) layout (CA-2). Drives the SAME tab switching the
/// widescreen left rail does — it only raises <see cref="TabSelected"/>; the host (<c>MainWindow</c>)
/// decides how to show the tab, so tab + connection state survive a width change. «Аккаунт» appears
/// only while signed in (its column collapses to zero otherwise, keeping equal thirds).
/// </summary>
public partial class BottomNavBar : UserControl
{
    /// <summary>Raised when a tab is tapped.</summary>
    public event EventHandler<AppTab>? TabSelected;

    private AppTab _selected = AppTab.Home;
    private Action<AccountState>? _handler;

    // ==================== Путешествующий индикатор (P0-1) ====================
    // ОДНА акцентная полоса (BottomIndicator) физически СКОЛЬЗИТ по X к центру активной трети —
    // вместо трёх независимых пилюль, «мигавших» на месте. Центр берётся из ЖИВЫХ bounds активной
    // кнопки, поэтому корректен и в 3-пунктовом (вошёл), и в 2-пунктовом (без «Аккаунта») состоянии,
    // и пере-решается при ресайзе окна / смене числа колонок. Первый показ — мгновенно на активной
    // трети (без скольжения с X=0); дальше — переезд Motion.Dur.Nav 280мс ease-out-quart (motion.md
    // «Навигация»). Под lite / off-screen — мгновенно. Токен _indicatorAnim отменяет незавершённое
    // скольжение при новом тапе.
    private readonly TranslateTransform _indicatorTransform = new();
    private bool _indicatorSeeded;
    private double _lastTargetX = double.NaN;
    private CancellationTokenSource? _indicatorAnim;

    // Ширина полоски (tokens.md «Нижняя панель»: 30×3). Держать равной Width у BottomIndicator в
    // разметке — из неё считается «центр трети минус половина полоски».
    private const double IndicatorWidth = 30d;

    public BottomNavBar()
    {
        InitializeComponent();

        BottomIndicator.RenderTransform = _indicatorTransform;

        ItemHome.Click += (_, _) => Raise(AppTab.Home);
        ItemSettings.Click += (_, _) => Raise(AppTab.Settings);
        ItemAccount.Click += (_, _) => Raise(AppTab.Account);

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;

        // Пере-ставим индикатор на активную треть после КАЖДОГО прохода раскладки (первый layout, ресайз
        // окна в компакте, смена числа колонок при входе/выходе). LayoutUpdated (а не Bounds одной кнопки)
        // гарантирует, что ВСЕ bounds пунктов уже финальные — иначе при активной не-первой трети коллбэк
        // мог сработать до арранжа ItemSettings/ItemAccount и увидеть нулевую ширину. Мгновенно, без
        // скольжения; _lastTargetX-guard гасит холостые повторы и не рвёт идущее скольжение по тапу.
        LayoutUpdated += (_, _) => PositionIndicator(animate: false);

        SetSelected(AppTab.Home);
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ApplyAccountVisibility();
        _handler = _ => Dispatcher.UIThread.Post(ApplyAccountVisibility);
        AccountSession.StateChanged += _handler;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_handler is not null)
        {
            AccountSession.StateChanged -= _handler;
            _handler = null;
        }
        _indicatorAnim?.Cancel();
    }

    private void Raise(AppTab tab)
    {
        SetSelected(tab);
        TabSelected?.Invoke(this, tab);
    }

    /// <summary>Reflect the active tab without raising the event (host-driven, e.g. on layout swap).</summary>
    public void SetSelected(AppTab tab)
    {
        var changed = _selected != tab;
        _selected = tab;
        SetItemState(ItemHome, tab == AppTab.Home);
        SetItemState(ItemSettings, tab == AppTab.Settings);
        SetItemState(ItemAccount, tab == AppTab.Account);
        // Скольжение только на реальную смену трети; повторный host-вызов той же вкладки (Raise + ShowTab
        // на один тап) не рвёт уже идущее скольжение — см. _lastTargetX-guard в PositionIndicator.
        PositionIndicator(animate: changed);
    }

    private static void SetItemState(Button item, bool selected)
    {
        if (selected)
        {
            if (!item.Classes.Contains("sel"))
            {
                item.Classes.Add("sel");
            }
        }
        else
        {
            item.Classes.Remove("sel");
        }
    }

    // Активная кнопка (для позиции индикатора). Её ЖИВЫЕ bounds дают центр активной трети — верно и
    // для 2-пунктового logged-out (Account сворачивается, Home/Settings делят пополам).
    private Button ActiveItem => _selected switch
    {
        AppTab.Settings => ItemSettings,
        AppTab.Account => ItemAccount,
        _ => ItemHome,
    };

    private void PositionIndicator(bool animate)
    {
        var item = ActiveItem;
        var w = item.Bounds.Width;
        if (w <= 0)
        {
            return;   // ещё не разложено — переставит следующий Bounds-тик ItemHome
        }
        var targetX = item.Bounds.X + (w / 2d) - (IndicatorWidth / 2d);

        // Тот же слот, что и прошлый запрос → ничего не делаем: не рвём уже идущее скольжение к нему
        // (страхует от двойного SetSelected — Raise + host ShowTab дают два вызова на один тап) и
        // не дёргаем позицию на «пустых» Bounds-тиках без изменения геометрии.
        if (_indicatorSeeded && !double.IsNaN(_lastTargetX) && Math.Abs(targetX - _lastTargetX) < 0.5)
        {
            return;
        }
        _lastTargetX = targetX;

        var instant = !animate || !_indicatorSeeded || MotionState.IsLite || !IsWindowLive();
        //  Текущее X (в т.ч. на СЕРЕДИНЕ идущего скольжения) ловим ДО Cancel: отмена ревертит свойство к
        //  базе, поэтому чтение внутри аниматора давало «откат-кадр» при быстрых тапах трёх вкладок.
        var fromX = _indicatorTransform.X;
        _indicatorAnim?.Cancel();
        if (instant)
        {
            _indicatorTransform.X = targetX;
            _indicatorSeeded = true;
            return;
        }

        var cts = new CancellationTokenSource();
        _indicatorAnim = cts;
        AnimateIndicator(fromX, targetX, cts.Token);
    }

    private async void AnimateIndicator(double from, double targetX, CancellationToken ct)
    {
        //  motion.md «Навигация»: переезд 280мс ease-out-quart. Полоска ОДНА и физически едет —
        //  не гаснет и зажигается, поэтому кривая должна быть «доезжающей», а не двусторонней.
        var anim = new Animation
        {
            Duration = Motion.Dur.Nav,
            Easing = Motion.Ease.OutQuart,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(TranslateTransform.XProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(TranslateTransform.XProperty, targetX) } },
            },
        };
        try { await anim.RunAsync(_indicatorTransform, ct); }
        catch { }
        if (!ct.IsCancellationRequested)
        {
            _indicatorTransform.X = targetX;
        }
    }

    // Off-screen-guard: анимируем только когда окно реально видно (не в трее / не свёрнуто), иначе
    // индикатор тикал бы за экраном (правило «нет off-screen циклов»).
    private bool IsWindowLive()
        => TopLevel.GetTopLevel(this) is Window w && w.IsVisible && w.WindowState != WindowState.Minimized;

    // «Аккаунт» виден только при входе; его столбец сворачивается до 0, чтобы 2 остальных
    // (Главная · Настройки) держали равные половины (Android nav_account weighted collapse).
    private void ApplyAccountVisibility()
    {
        var logged = AccountSession.IsLoggedIn();
        ItemAccount.IsVisible = logged;
        NavGrid.ColumnDefinitions[2].Width = logged
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        // Signed out while on the Account tab → fall back to Home so no dead selection lingers.
        if (!logged && _selected == AppTab.Account)
        {
            Raise(AppTab.Home);
        }
        // Смена числа колонок сдвигает центры Home/Settings — ItemHome-bounds пере-ляжет и Bounds-тик
        // мгновенно пере-решит позицию индикатора (см. подписку в ctor).
    }
}
