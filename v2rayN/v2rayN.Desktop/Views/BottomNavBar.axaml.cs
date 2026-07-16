using v2rayN.Desktop.Account;

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

    public BottomNavBar()
    {
        InitializeComponent();

        ItemHome.Click += (_, _) => Raise(AppTab.Home);
        ItemSettings.Click += (_, _) => Raise(AppTab.Settings);
        ItemAccount.Click += (_, _) => Raise(AppTab.Account);

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;

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
    }

    private void Raise(AppTab tab)
    {
        SetSelected(tab);
        TabSelected?.Invoke(this, tab);
    }

    /// <summary>Reflect the active tab without raising the event (host-driven, e.g. on layout swap).</summary>
    public void SetSelected(AppTab tab)
    {
        _selected = tab;
        SetItemState(ItemHome, tab == AppTab.Home);
        SetItemState(ItemSettings, tab == AppTab.Settings);
        SetItemState(ItemAccount, tab == AppTab.Account);
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
    }
}
