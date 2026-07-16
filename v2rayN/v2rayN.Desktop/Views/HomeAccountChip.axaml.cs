using System.Reactive.Disposables;
using v2rayN.Desktop.Account;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Account chip shared by the widescreen <see cref="HomeView"/> and the compact
/// <see cref="CompactHomeView"/> (CA-1). Self-manages its visibility and identity from the
/// read-only <see cref="AccountSession"/>: shown only when signed in, filled with the
/// «@handle» / display name / email and a monogram avatar. Tap raises
/// <see cref="AccountRequested"/> — the host decides how to open the Account tab (rail vs
/// bottom nav) so this control stays layout-agnostic.
/// </summary>
public partial class HomeAccountChip : UserControl
{
    /// <summary>Chip tapped — host should open the Account tab.</summary>
    public event EventHandler? AccountRequested;

    private Action<AccountState>? _handler;

    public HomeAccountChip()
    {
        InitializeComponent();
        IsVisible = false;

        // Bind to the shared session while attached; drop the handler on detach so a swapped-out
        // (invisible) layout host does not leak or double-update.
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ApplyAccountState(AccountSession.State);
        _handler = state => Dispatcher.UIThread.Post(() => ApplyAccountState(state));
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

    // Data-driven from the shared session: shown only when logged in; name = @telegram / display /
    // email; avatar initial = its first letter. Never fabricated (hidden when logged out).
    private void ApplyAccountState(AccountState state)
    {
        if (state is AccountState.LoggedIn)
        {
            AccountName.Text = AccountSession.DisplayName;
            AccountInitial.Text = AccountSession.AvatarInitial;
            IsVisible = true;
        }
        else
        {
            IsVisible = false;
            AccountName.Text = string.Empty;
            AccountInitial.Text = string.Empty;
        }
    }

    private void OnChipTapped(object? sender, TappedEventArgs e) =>
        AccountRequested?.Invoke(this, EventArgs.Empty);

    private void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border b && !b.Classes.Contains("pressed"))
        {
            b.Classes.Add("pressed");
        }
    }

    private void OnChipReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border b)
        {
            b.Classes.Remove("pressed");
        }
    }

    private void OnChipExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border b)
        {
            b.Classes.Remove("pressed");
        }
    }
}
