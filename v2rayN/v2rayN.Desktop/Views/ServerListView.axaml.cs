using Avalonia.Data.Converters;
using DialogHostAvalonia;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// The unified left-column list of Home: one collapsible section per subscription, each headed by its
/// rich meta-bar (<see cref="SubscriptionMetaView"/> = the section header) flowing directly into its
/// server rows. There is NO separate «Сервера» header / toolbar / top-right «+» — those belonged to
/// the standalone Servers tab (which desktop has no rail entry for); the subscription meta-bar's own
/// actions + its «+» are the single source of add / refresh / ping / pin / collapse (owner demand).
///
/// DATA-DRIVEN: the runtime DataContext is the real <see cref="HomeViewModel"/> (inherited from
/// <see cref="HomeView"/>), so sections bind to real <c>ProfileItemModel</c>s grouped by subscription:
///   name ← Remarks (StripLeadingFlag), protocol ← ConfigType (ConfigTypeToProtocol), transport ←
///   Network·StreamSecurity (ProfileTransport), ping ← DelayVal, selected ← IsActive. Sample rows
///   exist ONLY at design time (DesignData.Home).
///
/// Interactions: a row tap selects + connects the server; collapse/pin/refresh/ping/add live on the
/// per-section meta-bar; a right-click context menu exposes the §2.13 server actions (make default /
/// ping / edit / duplicate / share / delete) against the shared <see cref="ProfilesViewModel"/>.
/// Because that VM raises its confirm / share / clipboard results through ReactiveUI interactions
/// (normally handled by ProfilesView, which is not in this two-panel Home), those three interaction
/// handlers are registered here so the actions work.
/// </summary>
public partial class ServerListView : UserControl
{
    private static readonly string _tag = "ServerListView";
    private readonly List<IDisposable> _interactionHandlers = new();

    // The row a context menu was opened on (captured on ContextRequested, before the menu shows) —
    // robust across Avalonia versions vs. relying on the MenuItem's inherited DataContext.
    private ProfileItemModel? _actionTarget;

    public ServerListView()
    {
        InitializeComponent();
        // NOTE: no runtime DataContext here — it inherits the real HomeViewModel from HomeView.
        // The XAML Design.DataContext (DesignData.Home) only feeds the previewer.
        DataContextChanged += (_, _) => RegisterInteractions();
    }

    #region Server-action interaction handlers (mirror ProfilesView, so share/delete work here)

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RegisterInteractions();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var handler in _interactionHandlers)
        {
            handler.Dispose();
        }
        _interactionHandlers.Clear();
    }

    // Registered once against the shared ProfilesViewModel. Idempotent; disposed on detach.
    private void RegisterInteractions()
    {
        if (_interactionHandlers.Count > 0)
        {
            return;
        }
        if (DataContext is not HomeViewModel { Profiles: { } profiles })
        {
            return;
        }

        // Delete confirmation ("Удалить сервер?" → yes/no).
        _interactionHandlers.Add(profiles.ShowYesNoInteraction.RegisterHandler(async interaction =>
        {
            var result = await UI.ShowYesNo(interaction.Input);
            interaction.SetOutput(result == ButtonResult.Yes);
        }));

        // Share via QR-code (dialog hosted by MainWindow's DialogHost).
        _interactionHandlers.Add(profiles.ShareServerInteraction.RegisterHandler(async interaction =>
        {
            var url = interaction.Input;
            if (url.IsNotEmpty())
            {
                try
                {
                    await DialogHost.Show(new QrcodeView(url));
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(_tag, ex);
                }
            }
            interaction.SetOutput(Unit.Default);
        }));

        // Share via clipboard (copy share-url / export).
        _interactionHandlers.Add(profiles.SetClipboardDataInteraction.RegisterHandler(async interaction =>
        {
            await AvaUtils.SetClipboardData(this, interaction.Input);
            interaction.SetOutput(Unit.Default);
        }));
    }

    #endregion Server-action interaction handlers

    #region Row selection

    //  Manual press/release selection (NOT Tapped): the rows live inside a ScrollViewer, and
    //  Avalonia cancels the Tapped gesture on the slightest pointer movement / scroll-drag, so
    //  a click frequently never selected. Mirroring the ConnectHero disc (_pressing flag), we
    //  track press → release on the row Border itself: on press we remember the target row; on
    //  release, if we are still pressing AND the pointer is still over that same row, we select.
    //  A drag that starts a scroll makes the ScrollViewer capture the pointer, which fires
    //  PointerCaptureLost on the Border and cancels the press — so a scroll-drag never selects.
    private bool _rowPressing;
    private ProfileItemModel? _rowPressTarget;

    // Row press feedback: subtle scale (Border.ServerRow.pressed), no ripple/glow (§0.6), and
    // arm selection for the row under the pointer (left button only).
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b)
        {
            return;
        }

        if (!b.Classes.Contains("pressed"))
        {
            b.Classes.Add("pressed");
        }

        if (e.GetCurrentPoint(b).Properties.IsLeftButtonPressed && b.DataContext is ProfileItemModel item)
        {
            _rowPressing = true;
            _rowPressTarget = item;
        }
        else
        {
            ClearRowPress();
        }
    }

    // Server row select + connect (make default server → engine reloads the core). Fires only
    // when the press completes over the same row it started on — reliable inside the ScrollViewer.
    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Border b)
        {
            return;
        }

        b.Classes.Remove("pressed");

        if (_rowPressing
            && e.InitialPressMouseButton == MouseButton.Left
            && _rowPressTarget is { } item
            && b.IsPointerOver
            && DataContext is HomeViewModel vm)
        {
            _ = vm.SelectServer(item.IndexId);
        }

        ClearRowPress();
    }

    private void OnRowPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border b)
        {
            b.Classes.Remove("pressed");
        }
        ClearRowPress();
    }

    // Scroll-drag steals pointer capture → cancel the pending selection (no accidental switch).
    private void OnRowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is Border b)
        {
            b.Classes.Remove("pressed");
        }
        ClearRowPress();
    }

    private void ClearRowPress()
    {
        _rowPressing = false;
        _rowPressTarget = null;
    }

    #endregion Row selection

    #region Server-row context actions (§2.13)

    // Capture the right-clicked row before its context menu opens.
    private void OnRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control { DataContext: ProfileItemModel item })
        {
            _actionTarget = item;
        }
    }

    // Point the shared VM's selection at the captured row, then return the VM so an action can run.
    private ProfilesViewModel? SelectTargetRow()
    {
        if (_actionTarget is { } item && DataContext is HomeViewModel { Profiles: { } profiles })
        {
            profiles.SelectedProfile = item;
            profiles.SelectedProfiles = new List<ProfileItemModel> { item };
            return profiles;
        }
        return null;
    }

    private void OnRowMakeDefault(object? sender, RoutedEventArgs e)
    {
        if (_actionTarget is { } item && DataContext is HomeViewModel { Profiles: { } profiles })
        {
            _ = profiles.SetDefaultServer(item.IndexId);
        }
    }

    private void OnRowPing(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.ServerSpeedtest(ESpeedActionType.Realping);
        }
    }

    private void OnRowEdit(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.EditServerAsync();
        }
    }

    private void OnRowDuplicate(object? sender, RoutedEventArgs e)
    {
        // CopyServer is private on the VM; go through the command (selection sets its canExecute).
        if (SelectTargetRow() is { } profiles)
        {
            profiles.CopyServerCmd.Execute().Subscribe(static _ => { }, static _ => { });
        }
    }

    private void OnRowShareQr(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.ShareServerAsync();
        }
    }

    private void OnRowShareLink(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.Export2ShareUrlAsync(false);
        }
    }

    private void OnRowDelete(object? sender, RoutedEventArgs e)
    {
        if (SelectTargetRow() is { } profiles)
        {
            _ = profiles.RemoveServerAsync();
        }
    }

    #endregion Server-row context actions
}

/// <summary>
/// Row name converter: strips a leading flag emoji from the remark (the flag already shows in its
/// tile) via <see cref="FlagResolver.StripLeadingFlag"/>. Instantiated locally in ServerListView.axaml
/// (kept out of GlobalResources by design — it is only used by this view).
/// </summary>
public sealed class StripLeadingFlagConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => FlagResolver.StripLeadingFlag(value?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>
/// True once a latency test produced a real result — the bound <c>DelayVal</c> is a numeric
/// millisecond string (e.g. "123", "-1"). Empty (never tested) and the engine's non-numeric
/// "Testing…" placeholder both read false, so the coloured ms value shows only for real results.
/// Local to ServerListView (kept out of GlobalResources).
/// </summary>
public sealed class DelayResultConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        return !string.IsNullOrEmpty(s) && int.TryParse(s, out _);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>
/// True while a latency test is in flight for the row: <c>DelayVal</c> is populated with the engine's
/// non-numeric "Testing…" placeholder (never a parseable millisecond value). Drives the ping-slot
/// spinner so a testing row shows a spinner instead of any "Testing…" / "тест" text. Local to
/// ServerListView (kept out of GlobalResources).
/// </summary>
public sealed class DelayTestingConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        return !string.IsNullOrEmpty(s) && !int.TryParse(s, out _);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
