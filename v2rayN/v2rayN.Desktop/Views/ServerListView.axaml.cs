using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Data.Converters;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
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
                // G1: the reveal is one-shot PER VIEW-MODEL, not per view instance. Leaving Home and
                // returning tears down + re-creates this view (or re-attaches it), which used to reset
                // these instance fields and REPLAY the stagger on every show. The static weak table
                // remembers which VM instances have already played, so a re-shown / re-attached list
                // stays settled; only a genuinely new VM (e.g. after re-login) plays once more.
                var alreadyRevealed = DataContext is { } ctx && _revealedContexts.TryGetValue(ctx, out _);
                _revealStarted = alreadyRevealed;
                _revealFinished = alreadyRevealed;
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

    //  G1: which HomeViewModel instances have already played their one-shot reveal. STATIC so the
    //  guard survives this view being torn down / re-created / re-attached when the Home tab is
    //  re-shown (the source of the residual replay). Weak keys → a discarded VM is collected and a
    //  genuinely new VM (re-login) reveals once more; harmless if the view instance is reused.
    private static readonly ConditionalWeakTable<object, object> _revealedContexts = new();

    //  Latch the current VM as "revealed" so a later re-show / re-attach can't replay the stagger.
    private void MarkContextRevealed()
    {
        if (DataContext is { } ctx)
        {
            _revealedContexts.AddOrUpdate(ctx, ctx);
        }
    }

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
        // close the window so no row in this population animates. Latch the VM so re-enabling
        // motion + re-showing later doesn't retro-stagger an already-populated list.
        if (IsReducedMotion())
        {
            _revealFinished = true;
            MarkContextRevealed();
            return;
        }

        if (!_revealStarted)
        {
            _revealStarted = true;
            _revealIndex = 0;
            // Latch this VM as revealed so re-showing / re-attaching Home never replays the stagger (G1).
            MarkContextRevealed();
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

    #region Group expand/collapse reveal (smooth composited crossfade; instant under lite)

    //  The meta-bar chevron flips HomeServerGroup.IsExpanded. Each group's rows live in a clipped
    //  reveal Border. We do NOT animate the Border's layout Height per-frame — that thrashes layout
    //  (every tick re-measures/arranges the rows) and stutters badly. Instead the reveal is a SMOOTH,
    //  COMPOSITED crossfade: inside the ClipToBounds viewport we animate ONLY the inner rows'
    //  RenderTransform (a small translateY, TransformOperations — same vocabulary as the row press-scale
    //  and the list-reveal stagger) + Opacity, which run off the layout path and never jitter. The
    //  viewport Height is touched EXACTLY ONCE per toggle (reserve natural height on expand / snap to 0
    //  on collapse), so there is a single layout pass, not one per frame.
    //
    //  Under lite / reduced-motion the toggle is INSTANT (same gate as the list-reveal stagger).
    //  Rows stay UN-hosted while collapsed (IsVisible=false at rest), so a collapsed group never
    //  realizes its rows — this preserves the one-shot list-reveal stagger (it keys off row Loaded,
    //  which only fires for expanded groups) and the chevron rotate is unaffected. The outer groups
    //  ItemsControl uses a non-virtualizing StackPanel, so each reveal container is stable; we hook its
    //  group on Loaded, unhook on Unloaded, and cancel any in-flight reveal when a new toggle arrives.

    private const double RevealSlideMs = 300; //  deliberate reveal — OutQuint, both directions (was 190, too snappy)

    //  Small translateY (composited) — a subtle slide that reads as a reveal regardless of list length,
    //  paired with the opacity crossfade. Full-height slides would whoosh on long lists; 12px stays crisp.
    private static readonly ITransform _revealFrom = TransformOperations.Parse("translateY(-12px)");
    private static readonly ITransform _revealHome = TransformOperations.Parse("translateY(0px)");

    private readonly Dictionary<Border, (HomeServerGroup group, PropertyChangedEventHandler handler)> _revealHooks = new();
    private readonly Dictionary<Border, CancellationTokenSource> _revealCts = new();

    // Hook the group behind this reveal container and set its rest state (no animation on first bind).
    private void OnGroupRevealLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border reveal || reveal.DataContext is not HomeServerGroup group)
        {
            return;
        }

        // Already hooked to the SAME group (Loaded can re-fire) → just re-assert rest, don't double-subscribe.
        if (_revealHooks.TryGetValue(reveal, out var existing))
        {
            if (ReferenceEquals(existing.group, group))
            {
                ApplyRevealState(reveal, group.IsExpanded);
                return;
            }
            existing.group.PropertyChanged -= existing.handler;
            _revealHooks.Remove(reveal);
        }

        void Handler(object? _, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(HomeServerGroup.IsExpanded))
            {
                AnimateReveal(reveal, group.IsExpanded);
            }
        }

        group.PropertyChanged += Handler;
        _revealHooks[reveal] = (group, Handler);
        ApplyRevealState(reveal, group.IsExpanded);
    }

    private void OnGroupRevealUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border reveal)
        {
            return;
        }
        if (_revealHooks.TryGetValue(reveal, out var hook))
        {
            hook.group.PropertyChanged -= hook.handler;
            _revealHooks.Remove(reveal);
        }
        if (_revealCts.TryGetValue(reveal, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _revealCts.Remove(reveal);
        }
    }

    // Instant rest state: expanded → auto height, rows settled + visible; collapsed → height 0, hidden
    // (rows un-hosted). Also normalises the inner rows' transform/opacity so no reveal frame is stranded.
    private static void ApplyRevealState(Border reveal, bool expanded)
    {
        if (reveal.Child is Control inner)
        {
            inner.RenderTransform = null;
            inner.Opacity = 1;
        }

        if (expanded)
        {
            reveal.IsVisible = true;
            reveal.Height = double.NaN;
            reveal.Opacity = 1;
        }
        else
        {
            reveal.Height = 0;
            reveal.Opacity = 0;
            reveal.IsVisible = false;
        }
    }

    // Smooth reveal: the ClipToBounds viewport Height is set ONCE (reserve on expand / snap on collapse),
    // and only the inner rows' RenderTransform (translateY) + Opacity animate — composited, no per-frame
    // layout. Cancels any in-flight reveal on this container first. Instant under lite / reduced-motion.
    private async void AnimateReveal(Border reveal, bool expand)
    {
        //  CRASH-SAFETY: this is an `async void` handler fired straight off HomeServerGroup.IsExpanded,
        //  so ANY exception that escapes it (not just cancellation) is unobserved and tears the process
        //  down — that was the collapse crash: the reveal animation was run on the inner rows with only
        //  an OperationCanceledException catch, so a fault from RunAsync (inner detached / no live clock
        //  while the group is being hidden, a superseding toggle disposing the CTS, a null/again-detached
        //  child) propagated out and killed the app. Everything below is wrapped; on any failure we fall
        //  back to the correct INSTANT rest state so the group still ends up right, just without motion.
        try
        {
            if (_revealCts.TryGetValue(reveal, out var prev))
            {
                prev.Cancel();
                prev.Dispose();
                _revealCts.Remove(reveal);
            }

            //  Instant path (no animation possible / wanted): lite / reduced-motion, no inner rows, OR
            //  the container is no longer rooted. An Avalonia Animation needs a live visual root + clock;
            //  running it on a detached element throws — so a detached / being-torn-down reveal snaps to
            //  its rest state instead of animating.
            if (IsReducedMotion() || reveal.Child is not Control inner || TopLevel.GetTopLevel(reveal) is null)
            {
                ApplyRevealState(reveal, expand);
                return;
            }

            //  COLLAPSE-SMOOTHNESS: the viewport Height animates TOGETHER with the rows' composited
            //  opacity/translate (not touched once + snapped). Previously collapse could fall into an instant
            //  path (a non-concrete Bounds.Height → animateHeight=false → the finally slammed it shut) so the
            //  rows faded but the container jumped closed — a hard "snap", no smooth hide. Now BOTH directions
            //  always resolve a concrete height (expand: measured natural; collapse: live bounds else a fresh
            //  measure) and tween Height 0→natural / natural→0 over the same ~300ms OutQuint, so the list below
            //  slides open/closed in step with the fade. It is a single group's container height for ~300ms
            //  (bounded), and it degrades safely: a genuinely unmeasurable container skips the height tween
            //  and just fades the rows (auto height).
            double fromH = 0, toH = 0;
            bool animateHeight;
            if (expand)
            {
                reveal.IsVisible = true;
                reveal.Opacity = 1;
                var target = MeasureRevealHeight(reveal);
                if (target > 0)
                {
                    fromH = 0;
                    toH = target;
                    animateHeight = true;
                    reveal.Height = 0; //  start closed — the height tween opens it to `target`.
                }
                else
                {
                    animateHeight = false;
                    reveal.Height = double.NaN; //  can't measure → auto height, fade rows only.
                }
            }
            else
            {
                //  COLLAPSE must ALWAYS animate: get a concrete start height from the live bounds, and if
                //  those aren't realized at this instant (auto height / mid-layout) fall back to a fresh
                //  measure — the SAME source expand uses. Only a genuinely unmeasurable container (0) drops
                //  to the instant path; that is the crash-safe degrade, not the common case. This is what
                //  fixes «collapse doesn't animate»: previously a non-concrete Bounds.Height snapped it shut.
                var current = reveal.Bounds.Height;
                if (current <= 0)
                {
                    current = MeasureRevealHeight(reveal);
                }
                if (current > 0)
                {
                    fromH = current;
                    toH = 0;
                    animateHeight = true;
                    reveal.Height = current; //  freeze at the concrete height — the tween closes it to 0.
                }
                else
                {
                    animateHeight = false;
                    // Truly unmeasurable → leave as-is; the finally snaps it closed.
                }
            }

            var cts = new CancellationTokenSource();
            _revealCts[reveal] = cts;
            var token = cts.Token;

            var fromT = expand ? _revealFrom : _revealHome;
            var toT = expand ? _revealHome : _revealFrom;
            var fromO = expand ? 0d : 1d;
            var toO = expand ? 1d : 0d;

            // Base = start-state (matches keyframe 0) so nothing flashes before the clock ticks; FillMode.None
            // releases the properties on completion and the finally sets the resting base immediately after.
            inner.RenderTransform = fromT;
            inner.Opacity = fromO;

            var anim = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(RevealSlideMs),
                //  Ease.OutQuint (0.22,1,0.36,1) — the confident-reveal curve used across the app.
                Easing = new SplineEasing { X1 = 0.22, Y1 = 1, X2 = 0.36, Y2 = 1 },
                FillMode = FillMode.None,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter(Visual.RenderTransformProperty, fromT),
                            new Setter(Visual.OpacityProperty, fromO),
                        },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter(Visual.RenderTransformProperty, toT),
                            new Setter(Visual.OpacityProperty, toO),
                        },
                    },
                },
            };

            //  Height tween on the reveal container itself (Layoutable.Height), same ~300ms OutQuint curve,
            //  run CONCURRENTLY with the row crossfade off the SAME token so a superseding toggle cancels
            //  both together. FillMode.Forward holds the end height until the finally sets the resting state.
            Animation? heightAnim = animateHeight
                ? new Animation
                {
                    Duration = TimeSpan.FromMilliseconds(RevealSlideMs),
                    Easing = new SplineEasing { X1 = 0.22, Y1 = 1, X2 = 0.36, Y2 = 1 },
                    FillMode = FillMode.Forward,
                    Children =
                    {
                        new KeyFrame
                        {
                            Cue = new Cue(0d),
                            Setters = { new Setter(Control.HeightProperty, fromH) },
                        },
                        new KeyFrame
                        {
                            Cue = new Cue(1d),
                            Setters = { new Setter(Control.HeightProperty, toH) },
                        },
                    },
                }
                : null;

            try
            {
                if (heightAnim is null)
                {
                    await anim.RunAsync(inner, token);
                }
                else
                {
                    await Task.WhenAll(anim.RunAsync(inner, token), heightAnim.RunAsync(reveal, token));
                }
            }
            catch (OperationCanceledException)
            {
                //  Superseded by a newer toggle — it owns the resting state now.
            }
            finally
            {
                //  Only settle if THIS run still owns the container's CTS — a newer toggle may have
                //  replaced (and cancelled/disposed) it while we awaited, in which case that toggle owns
                //  the resting state and the dictionary entry. Dispose is idempotent, so a double-dispose
                //  from the superseding path can't throw. When we DO own it, ApplyRevealState ALWAYS runs
                //  (expand → Height=NaN; collapse → Height=0 + hidden), so the FillMode.Forward height
                //  tween can never strand a stale fixed Height on the container — that stale clip is what
                //  used to hide the last group/row (Bug 2). If we were cancelled we no longer own the CTS,
                //  so we correctly defer to the superseding toggle here.
                if (_revealCts.TryGetValue(reveal, out var mine) && ReferenceEquals(mine, cts))
                {
                    ApplyRevealState(reveal, expand);
                    _revealCts.Remove(reveal);
                }
                cts.Dispose();
            }
        }
        catch (Exception ex)
        {
            //  Last line of defence for the async-void handler: never let a reveal fault crash the app —
            //  log it and force the group to its correct instant rest state.
            Logging.SaveLog(_tag, ex);
            try
            {
                ApplyRevealState(reveal, expand);
            }
            catch
            {
                //  Even the instant fallback can't be allowed to escape the handler.
            }
        }
    }

    // Natural height of the reveal's rows at the current column width (measured with Height cleared).
    // Defensive: a manual Measure on a detached / mid-layout element can throw — any failure returns 0,
    // which the caller reads as "use auto height" (double.NaN), so expand still works, just without a
    // pre-reserved pixel height.
    private static double MeasureRevealHeight(Border reveal)
    {
        try
        {
            double width = 0;
            if (reveal.GetVisualParent() is Control parent && parent.Bounds.Width > 0)
            {
                width = parent.Bounds.Width;
            }
            else if (reveal.Bounds.Width > 0)
            {
                width = reveal.Bounds.Width;
            }

            reveal.Height = double.NaN;
            reveal.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
            return reveal.DesiredSize.Height;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return 0;
        }
    }

    #endregion Group expand/collapse reveal

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
    // probe. A8(b): the core's ESpeedActionType has NO Httping/Icmping probe — those two are not
    // implementable without a core change, so they are dead options. This resolver therefore only
    // ever yields the two working probes (Tcping / Realping); any other persisted value (incl. a
    // stale «Httping»/«Icmping» left by an earlier build) safely falls back to Realping so ping still
    // works. The dead HTTP/ICMP rows must be removed from the picker — see PingSettingsPage.axaml
    // (RowHttp/RowIcmp), which this wave does not own; flagged for the settings owner.
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

/// <summary>
/// Ping display text (A8): a real reading renders its millisecond number; a failed / timed-out probe
/// (the core writes «-1») renders an em-dash «—», never the raw «-1». Only reached for numeric results
/// (visibility is gated by <see cref="DelayResultConverter"/>; a test in flight shows the spinner), so
/// this converter only decides the failure marker vs. the number. Local to ServerListView.
/// </summary>
public sealed class DelayDisplayConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        if (int.TryParse(s, out var ms) && ms <= 0)
        {
            return "—"; //  timeout / failure — no number, no latency ink (see DelayInkConverter)
        }
        return s;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>
/// Ping value ink (A8): a real reading uses the theme reading-ink (blue on light / white on dark, the
/// same single tone as the old DelayColorConverter — no green/red good-bad signal); a failed / timed-out
/// probe (<c>Delay &lt;= 0</c>) renders its em-dash in the MUTED variant tone so it reads as «no result»,
/// not as a latency. Theme-resolved via the active <see cref="ThemeVariant"/> (honours the mono overlay),
/// with literal Incy-token fallbacks so the binding never drops. Local to ServerListView.
/// </summary>
public sealed class DelayInkConverter : IValueConverter
{
    private static readonly IBrush _mutedFallback = new SolidColorBrush(Color.Parse("#9BA1AD")); // Brush.OnSurfaceVariant
    private static readonly IBrush _blueFallback = new SolidColorBrush(Color.Parse("#4C8DFF"));   // Brush.Accent (Light)
    private static readonly IBrush _whiteFallback = new SolidColorBrush(Color.Parse("#F2F4F8"));  // Brush.OnSurface (Dark)

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Failed / timeout → muted «no result» ink (not a latency colour).
        if (value is int ms && ms <= 0)
        {
            return Resolve("Brush.OnSurfaceVariant", _mutedFallback);
        }
        // Real reading → single theme ink: blue on light, white on dark (mono maps via tokens).
        var light = Application.Current?.ActualThemeVariant == ThemeVariant.Light;
        return light
            ? Resolve("Brush.Accent", _blueFallback)
            : Resolve("Brush.OnSurface", _whiteFallback);
    }

    private static IBrush Resolve(string key, IBrush fallback)
    {
        try
        {
            var app = Application.Current;
            if (app is not null
                && app.TryFindResource(key, app.ActualThemeVariant, out var res)
                && res is IBrush brush)
            {
                return brush;
            }
        }
        catch
        {
            //  fall through to the literal token fallback
        }
        return fallback;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
