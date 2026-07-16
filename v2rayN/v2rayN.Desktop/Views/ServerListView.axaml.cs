using System.Runtime.InteropServices;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Data.Converters;
using Avalonia.Media.Transformation;
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
        DataContextChanged += (_, _) =>
        {
            RegisterInteractions();
            // Re-arm the one-shot list-reveal stagger only when a genuinely NEW view-model is bound
            // (identity change), so the reveal plays once per bind — never again on scroll/refresh
            // (those keep the same VM instance and only mutate its ServerGroups collection).
            if (!ReferenceEquals(DataContext, _revealBoundContext))
            {
                _revealBoundContext = DataContext;
                _revealStarted = false;
                _revealFinished = false;
                _revealIndex = 0;
            }
        };
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

    #region List-reveal stagger (§A.4 — first-population only; Lite / reduced-motion disables it)

    //  Android parity: on the FIRST population of the server list the first ≤8 rows RISE
    //  (translateY 12→0) + fade (0→1) with a per-row delay of index×40ms over ~300ms OutQuint;
    //  rows beyond the first 8 just appear. This is a one-shot per bind — refresh / collapse /
    //  scroll re-run Loaded on fresh containers but are suppressed by _revealFinished.
    //
    //  ROBUSTNESS: rows are visible by DEFAULT (their XAML rest is Opacity 1, no transform). The
    //  reveal only ENHANCES that — it sets the transient hidden start state as part of actually
    //  running, and a safety timer + finally ALWAYS restore rest, so a row can never be stranded
    //  hidden if the animation clock never ticks (headless / inactive render).

    private const double RevealMs = 300; //  Dur.Reveal (rise = translateY 12 → 0, see _riseFrom)
    private const int StaggerMs = 40; //  Dur.Stagger (per-row delay)
    private const int MaxStaggerRows = 8; //  cap: only the first ≤8 rows stagger in

    private object? _revealBoundContext;
    private bool _revealStarted; //  first-population window has opened (rows attaching)
    private bool _revealFinished; //  first population done → no further reveals this bind
    private int _revealIndex; //  running per-row index within the first-population batch

    // Each server row raises Loaded when its container is realized. On the FIRST population we
    // stagger the first ≤8 rows in; every later realization (refresh / re-expand) is skipped so
    // rows simply appear at their visible rest.
    private void OnServerRowLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border row)
        {
            return;
        }

        // Previewer: never animate — the design surface shows the final rest frame.
        if (Design.IsDesignMode)
        {
            return;
        }

        // One-shot: refresh / collapse / scroll re-run Loaded on new containers → leave them at
        // their visible XAML rest (no stagger, no flash).
        if (_revealFinished)
        {
            return;
        }

        // Lite / reduced-motion: no stagger. Rows are visible by default, so nothing to do but
        // close the window so no row in this population animates.
        if (IsReducedMotion())
        {
            _revealFinished = true;
            return;
        }

        if (!_revealStarted)
        {
            _revealStarted = true;
            _revealIndex = 0;
            // Close the first-population window once this layout batch drains. Loaded callbacks run
            // at a higher dispatcher priority than Background, so every row realized in this pass is
            // indexed before the window closes; later (refresh) rows fall under _revealFinished.
            Dispatcher.UIThread.Post(() => _revealFinished = true, DispatcherPriority.Background);
        }

        var index = _revealIndex++;
        // Rows past the first 8 just appear — keeps the reveal snappy on long lists.
        if (index >= MaxStaggerRows)
        {
            return;
        }

        _ = PlayRowReveal(row, index * StaggerMs);
    }

    //  Rise expressed as TransformOperations (translateY), the SAME transform vocabulary the row's
    //  own press-scale uses (Border.ServerRow `scale(0.96)` + TransformOperationsTransition). Driving
    //  RenderTransform with TransformOperations composes cleanly with that subsystem — a raw
    //  TranslateTransform would clash with the style's TransformOperationsTransition on RenderTransform.
    private static readonly ITransform _riseFrom = TransformOperations.Parse("translateY(12px)");
    private static readonly ITransform _riseTo = TransformOperations.Parse("translateY(0px)");

    private async Task PlayRowReveal(Border row, int delayMs)
    {
        // Transient hidden start — set ONLY as part of running the reveal (never a persistent gate).
        // During the stagger delay the animation has not started, so this base Opacity holds the row
        // hidden (no pre-delay flash) until its turn; the rise is carried entirely by the keyframes.
        row.Opacity = 0;

        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(RevealMs),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            //  Ease.OutQuint (0.22,1,0.36,1) — the confident-reveal curve (matches GlobalResources).
            Easing = new SplineEasing { X1 = 0.22, Y1 = 1, X2 = 0.36, Y2 = 1 },
            //  None (NOT Forward): on completion the animation RELEASES RenderTransform / Opacity back
            //  to the control's base — so it never keeps ownership at Animation priority and can't
            //  shadow the row's `:pressed` scale-0.96. RestoreRow then defines the visible rest.
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(Visual.RenderTransformProperty, _riseFrom),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(Visual.RenderTransformProperty, _riseTo),
                    },
                },
            },
        };

        // SAFETY (mirrors ConnectHeroView's pre-hide + DispatcherTimer.RunOnce EnsureHeroVisible):
        // guarantee the row reaches visible rest even if the animation clock never advances (headless
        // / inactive render). Cancelling first makes the (possibly stuck) animation relinquish the
        // property so the base values RestoreRow writes actually take effect. Margin past delay+dur.
        var cts = new CancellationTokenSource();
        var safety = DispatcherTimer.RunOnce(
            () =>
            {
                cts.Cancel();
                RestoreRow(row);
            },
            TimeSpan.FromMilliseconds(delayMs + RevealMs + 250));

        try
        {
            await anim.RunAsync(row, cts.Token);
        }
        catch (OperationCanceledException)
        {
            //  Safety path already restored the row — nothing more to do.
        }
        finally
        {
            safety.Dispose(); //  cancel the pending safety callback when the reveal ended normally
            RestoreRow(row);
            cts.Dispose();
        }
    }

    // Idempotent rest = fully visible, no reveal transform. Safe to call from the safety timer and
    // finally. RenderTransform → null returns the row to the style's identity, so the `:pressed`
    // scale-0.96 keeps working on later taps.
    private static void RestoreRow(Control row)
    {
        row.Opacity = 1;
        row.RenderTransform = null;
    }

    // Reduced-motion decision — same signal the rest of the app gates on (ConnectHeroView): the
    // live `.lite` window class (set from UiItem.LiteMode), the persisted LiteMode flag, and the
    // Windows "show animations" system preference. Any one true → rows appear instantly.
    private bool IsReducedMotion()
    {
        if (Design.IsDesignMode)
        {
            return false;
        }

        // Live lite state: MainWindow carries the `.lite` class whenever LiteMode is on.
        if (TopLevel.GetTopLevel(this) is { } top && top.Classes.Contains("lite"))
        {
            return true;
        }

        try
        {
            if (AppManager.Instance.Config.UiItem.LiteMode)
            {
                return true;
            }
        }
        catch
        {
            //  Config not ready → treat motion as enabled.
        }

        return !SystemAnimationsEnabled();
    }

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref bool pvParam, uint fWinIni);

    private static bool SystemAnimationsEnabled()
    {
        //  No direct signal off Windows → assume motion is enabled.
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var enabled = true;
            return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0) || enabled;
        }
        catch
        {
            return true;
        }
    }

    #endregion List-reveal stagger

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
            _ = profiles.ServerSpeedtest(ResolvePingAction());
        }
    }

    // Map the user-selected ping method (Настройки → Пинг → SpeedTestItem.PingMethod) to the engine
    // probe. The core supports Tcping + Realping; Httping/Icmping (Android parity) fall back to the
    // real latency probe until the engine gains those probes.
    private static ESpeedActionType ResolvePingAction()
        => AppManager.Instance.Config.SpeedTestItem.PingMethod == "Tcping"
            ? ESpeedActionType.Tcping
            : ESpeedActionType.Realping;

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
