using Avalonia.Animation;
using Avalonia.Controls.Shapes;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран «Аккаунт» (PREMIUM редизайн, Фаза C): двух-зонная hero-карта (идентичность над деньгами,
/// один волосок-шов), карусель health-богатых карточек подписок (перетаскивание · точки · стрелки ·
/// снап), секция «Управление» (строки 56dp, press-scale 0.99) и вынесенный тихий красно-текстовый
/// «Выйти». DATA-DRIVEN: всё биндится к <see cref="AccountViewModel"/> (departament-API).
///
/// В рантайме DataContext ставит MainWindow (ОБЩИЙ VM, тот же, что у суб-страницы «Вход»). Навигацию
/// отдаёт наружу событиями (<see cref="BuyRequested"/> / <see cref="DevicesRequested"/> /
/// <see cref="HistoryRequested"/> / <see cref="LoginRequested"/>); CTA карточек карусели идут через
/// intent-хуки общего VM и форвардятся сюда же — без изменения MainWindow.
/// </summary>
public partial class AccountView : UserControl
{
    /// <summary>Строка «Купить подписку» / CTA «Продлить» карточки — хост открывает Buy.</summary>
    public event EventHandler? BuyRequested;

    /// <summary>Строка «Устройства» / ссылка карточки — хост открывает Devices.</summary>
    public event EventHandler? DevicesRequested;

    /// <summary>Строка «История платежей» — хост открывает History.</summary>
    public event EventHandler? HistoryRequested;

    /// <summary>CTA входа (logged-out) — хост открывает суб-страницу «Вход».</summary>
    public event EventHandler? LoginRequested;

    // ── carousel geometry ──
    private const double CardPeek = 32.0;      // сколько соседней карточки видно (аффорданс «есть ещё»)
    private const double CardGap = 12.0;       // Spacing горизонтального StackPanel карточек
    private const double MinCardWidth = 240.0;
    private const double DragThreshold = 6.0;  // порог, после которого тап превращается в перетаскивание

    private AccountViewModel? _vm;
    private IDisposable? _cardsSub;
    private IDisposable? _balanceSub;

    // ── carousel drag / snap state ──
    private bool _pointerDown;
    private bool _dragging;
    private double _dragStartX;
    private double _dragStartOffset;
    private CancellationTokenSource? _snapCts;

    public AccountView()
    {
        InitializeComponent();

        // Только для превьювера: в рантайме общий VM приходит от MainWindow.
        if (Design.IsDesignMode)
        {
            DataContext = AccountViewModel.CreateDesign();
        }

        BuyRow.Tapped += (_, _) => BuyRequested?.Invoke(this, EventArgs.Empty);
        HistoryRow.Tapped += (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty);
        LoginSiteButton.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
        CopyReferralButton.Click += OnCopyReferral;
        // Пустое состояние «Выбрать тариф» ведёт в «Купить» (тот же путь навигации, что и строка «Купить»).
        EmptyBuyButton.Click += (_, _) => BuyRequested?.Invoke(this, EventArgs.Empty);
        // onError is MANDATORY here: Logout() awaits an unguarded DB teardown (AccountSession.Wipe →
        // RemoveAllManaged → ConfigHandler.DeleteSubItem), and a parameterless Subscribe() would rethrow
        // that fault into the dispatcher and kill the process mid-logout — after the VPN was stopped but
        // before the session was cleared.
        LogoutRow.Tapped += (_, _) =>
        {
            if (DataContext is AccountViewModel vm)
            {
                vm.LogoutCmd.Execute().Subscribe(_ => { }, vm.ReportCommandException);
            }
        };

        // Press-scale 0.99 (§0.1) на всех строках навигации: тап ощущается до выезда суб-страницы.
        WirePress(BuyRow);
        WirePress(HistoryRow);
        WirePress(LogoutRow);

        // Карусель: перетаскивание (tunnel, чтобы видеть жест поверх кнопок карточек), стрелки, пейджер.
        SubCarousel.AddHandler(PointerPressedEvent, OnCarouselPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SubCarousel.AddHandler(PointerMovedEvent, OnCarouselPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        SubCarousel.AddHandler(PointerReleasedEvent, OnCarouselPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        SubCarousel.PointerCaptureLost += (_, _) => EndDrag(snap: true);
        SubCarousel.KeyDown += OnCarouselKeyDown;
        SubCarousel.SizeChanged += (_, _) => ComputeCardWidth();
        SubCarousel.AddHandler(ScrollViewer.ScrollChangedEvent, OnCarouselScrollChanged);
        CarouselPrev.Click += (_, _) => PageBy(-1);
        CarouselNext.Click += (_, _) => PageBy(+1);

        DataContextChanged += (_, _) => HookVm();
        HookVm();
    }

    // ==================== VM wiring ====================

    private void HookVm()
    {
        _cardsSub?.Dispose();
        _cardsSub = null;
        _balanceSub?.Dispose();
        _balanceSub = null;
        if (_vm != null)
        {
            _vm.BuyIntentRequested -= OnBuyIntent;
            _vm.DevicesIntentRequested -= OnDevicesIntent;
            _vm.TopUpCheckoutOpened -= OnTopUpCheckoutOpened;
        }

        _vm = DataContext as AccountViewModel;
        if (_vm is null)
        {
            return;
        }

        // Закрываем флайаут пополнения ТОЛЬКО при успехе (открылся чекаут): невалидная сумма
        // оставляет флайаут открытым, чтобы показать инлайн-ошибку.
        _vm.TopUpCheckoutOpened += OnTopUpCheckoutOpened;
        _vm.BuyIntentRequested += OnBuyIntent;
        _vm.DevicesIntentRequested += OnDevicesIntent;

        // Пересборка списка подписок → перестроить точки + пересчитать ширину карточек.
        _cardsSub = _vm.WhenAnyValue(x => x.SubCards).Subscribe(_ => OnCardsChanged());
        // Изменение баланса (только РЕАЛЬНОЕ, не первичное) → кроссфейд суммы.
        _balanceSub = _vm.WhenAnyValue(x => x.BalanceAmountText).Skip(1).Subscribe(_ => OnBalanceChanged());
    }

    private void OnTopUpCheckoutOpened(object? sender, EventArgs e) => TopUpButton.Flyout?.Hide();

    private void OnBuyIntent(object? sender, EventArgs e) => BuyRequested?.Invoke(this, EventArgs.Empty);

    private void OnDevicesIntent(object? sender, EventArgs e) => DevicesRequested?.Invoke(this, EventArgs.Empty);

    // ==================== referral copy ====================

    private async void OnCopyReferral(object? sender, RoutedEventArgs e)
    {
        var code = (DataContext as AccountViewModel)?.ReferralCode;
        if (code.IsNullOrEmpty())
        {
            return;
        }
        await AvaUtils.SetClipboardData(this, code);
        AppEvents.SendSnackMsgRequested.Publish(L.T("Common_Copied"));
    }

    // ==================== row press-scale ====================

    private static void WirePress(Border row)
    {
        row.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (!row.Classes.Contains("pressed"))
            {
                row.Classes.Add("pressed");
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        void Release(object? s, EventArgs e) => row.Classes.Remove("pressed");
        row.PointerReleased += Release;
        row.PointerExited += Release;
        row.PointerCaptureLost += Release;
    }

    // ==================== entrance stagger (group 2) ====================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // MainWindow помечает ровно одну keep-alive вкладку hit-test'абельной при свопе: переход
        // false→true = «эта вкладка стала активной» → проигрываем внутренний стаггер группы 2.
        if (change.Property == IsHitTestVisibleProperty)
        {
            if (change.GetNewValue<bool>() && !change.GetOldValue<bool>())
            {
                PlayEntrance();
            }
            else if (!change.GetNewValue<bool>())
            {
                // Вкладка стала неактивной — гасим текущий snap-твин карусели, чтобы DispatcherTimer
                // не продолжал крутить SubCarousel.Offset за экраном (правило «one-shot гасится при скрытии»).
                _snapCts?.Cancel();
            }
        }
    }

    // Группа 2 (подписки + управление) приезжает на +40мс позже hero — ровно ОДИН стаггер-шаг,
    // внутри того же окна, что и подъём всей вкладки в оболочке. Под lite — мгновенно (не запускаем).
    private void PlayEntrance()
    {
        if (MotionState.IsLite || EntranceGroup2 is null)
        {
            return;
        }
        var anim = new Animation
        {
            Duration = Motion.Dur.Reveal,
            Delay = Motion.Dur.Stagger,
            Easing = Motion.Ease.OutQuint,
            FillMode = FillMode.Both,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 8d) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) },
                },
            },
        };
        _ = anim.RunAsync(EntranceGroup2);
    }

    // ==================== balance change crossfade ====================

    // Единственный анимированный момент hero: сумма «оседает» сверху вниз при РЕАЛЬНОЙ смене баланса
    // (пополнение долетело). Под lite — мгновенно (текст уже обновлён биндингом).
    private void OnBalanceChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnBalanceChanged);
            return;
        }
        // Не анимируем скрытую вкладку: пост-пополнение poll (RefreshProfile каждые 5с) может обновить
        // баланс, пока пользователь на другой вкладке — текст уже обновлён биндингом, крутить оседание
        // на невидимом TextBlock незачем. IsHitTestVisible = сигнал «эта вкладка активна» (оболочка).
        if (MotionState.IsLite || BalanceAmount is null || !IsHitTestVisible)
        {
            return;
        }
        var anim = new Animation
        {
            Duration = Motion.Dur.State,
            Easing = Motion.Ease.Standard,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(OpacityProperty, 0.25d), new Setter(TranslateTransform.YProperty, -6d) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) },
                },
            },
        };
        _ = anim.RunAsync(BalanceAmount);
    }

    // ==================== carousel ====================

    private int CardCount => _vm?.SubCards.Count ?? 0;

    private double Step()
    {
        var w = _vm?.CardWidth ?? 0;
        return w > 0 ? w + CardGap : 0;
    }

    private double MaxOffset() => Math.Max(0, SubCarousel.Extent.Width - SubCarousel.Viewport.Width);

    private void OnCardsChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnCardsChanged);
            return;
        }
        RebuildDots();
        ComputeCardWidth();
        // Держим текущую страницу выровненной после пересборки (без анимации).
        Dispatcher.UIThread.Post(() => AlignToIndex(_vm?.CarouselIndex ?? 0, animate: false), DispatcherPriority.Background);
    }

    private void ComputeCardWidth()
    {
        if (_vm is null)
        {
            return;
        }
        var count = _vm.SubCards.Count;
        if (count == 0)
        {
            return;
        }
        var viewport = SubCarousel.Bounds.Width;
        if (viewport <= 1)
        {
            return;
        }
        // Одна подписка → карточка во всю ширину (без пустого поля); 2+ → оставляем peek под аффорданс.
        var w = count <= 1 ? viewport : Math.Max(MinCardWidth, viewport - CardPeek);
        if (Math.Abs(w - _vm.CardWidth) < 0.5)
        {
            return;
        }
        _vm.CardWidth = w;
        foreach (var c in _vm.SubCards)
        {
            c.CardWidth = w;
        }
        Dispatcher.UIThread.Post(() => AlignToIndex(_vm.CarouselIndex, animate: false), DispatcherPriority.Background);
    }

    private void RebuildDots()
    {
        SubDots.Children.Clear();
        var count = CardCount;
        if (count <= 1)
        {
            return;
        }
        for (var i = 0; i < count; i++)
        {
            var dot = new Ellipse { Cursor = new Cursor(StandardCursorType.Hand), Tag = i };
            dot.Classes.Add("Dot");
            dot.PointerPressed += OnDotPressed;
            SubDots.Children.Add(dot);
        }
        UpdateActiveDot(_vm?.CarouselIndex ?? 0);
    }

    private void UpdateActiveDot(int index)
    {
        for (var i = 0; i < SubDots.Children.Count; i++)
        {
            if (SubDots.Children[i] is Ellipse dot)
            {
                if (i == index)
                {
                    if (!dot.Classes.Contains("active"))
                    {
                        dot.Classes.Add("active");
                    }
                }
                else
                {
                    dot.Classes.Remove("active");
                }
            }
        }
    }

    private void OnDotPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Ellipse { Tag: int index })
        {
            SnapToIndex(index);
            e.Handled = true;
        }
    }

    private void PageBy(int delta)
    {
        if (CardCount <= 1)
        {
            return;
        }
        var index = Math.Clamp((_vm?.CarouselIndex ?? 0) + delta, 0, CardCount - 1);
        SnapToIndex(index);
    }

    private void OnCarouselKeyDown(object? sender, KeyEventArgs e)
    {
        if (CardCount <= 1)
        {
            return;
        }
        if (e.Key == Key.Left)
        {
            PageBy(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            PageBy(+1);
            e.Handled = true;
        }
    }

    // ── pointer drag ──

    private void OnCarouselPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (CardCount <= 1 || !e.GetCurrentPoint(SubCarousel).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _pointerDown = true;
        _dragging = false;
        _dragStartX = e.GetPosition(SubCarousel).X;
        _dragStartOffset = SubCarousel.Offset.X;
    }

    private void OnCarouselPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }
        var dx = e.GetPosition(SubCarousel).X - _dragStartX;
        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThreshold)
            {
                return;
            }
            // Порог пройден — это перетаскивание: захватываем указатель (тап по кнопке карточки отменится).
            _dragging = true;
            _snapCts?.Cancel();
            e.Pointer.Capture(SubCarousel);
        }
        var target = Math.Clamp(_dragStartOffset - dx, 0, MaxOffset());
        SubCarousel.Offset = new Vector(target, SubCarousel.Offset.Y);
        e.Handled = true;
    }

    private void OnCarouselPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndDrag(snap: true);
    }

    private void EndDrag(bool snap)
    {
        var wasDragging = _dragging;
        _pointerDown = false;
        _dragging = false;
        if (wasDragging && snap)
        {
            SnapToNearest();
        }
    }

    private void SnapToNearest()
    {
        var step = Step();
        if (step <= 0)
        {
            return;
        }
        var index = Math.Clamp((int)Math.Round(SubCarousel.Offset.X / step), 0, Math.Max(0, CardCount - 1));
        SnapToIndex(index);
    }

    private void SnapToIndex(int index)
    {
        if (_vm != null)
        {
            _vm.CarouselIndex = index;
        }
        UpdateActiveDot(index);
        AlignToIndex(index, animate: true);
    }

    private void AlignToIndex(int index, bool animate)
    {
        var step = Step();
        if (step <= 0)
        {
            return;
        }
        var target = Math.Min(index * step, MaxOffset());
        if (!animate || MotionState.IsLite)
        {
            _snapCts?.Cancel();
            SubCarousel.Offset = new Vector(target, SubCarousel.Offset.Y);
            return;
        }
        SnapOffsetTo(target);
    }

    // Снап оффсета к цели: ручной твин по Ease.OutQuint (без bounce/overshoot), 300мс. Под lite/reduced —
    // мгновенно (см. AlignToIndex). ScrollViewer.Offset не поддаётся Transition, поэтому таймер-твин.
    private void SnapOffsetTo(double targetX)
    {
        _snapCts?.Cancel();
        var startX = SubCarousel.Offset.X;
        if (Math.Abs(targetX - startX) < 0.5)
        {
            SubCarousel.Offset = new Vector(targetX, SubCarousel.Offset.Y);
            return;
        }
        var cts = new CancellationTokenSource();
        _snapCts = cts;
        var start = DateTime.UtcNow;
        var dur = Motion.Dur.Reveal.TotalMilliseconds;
        var easing = Motion.Ease.OutQuint;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (cts.IsCancellationRequested)
            {
                timer.Stop();
                return;
            }
            var t = (DateTime.UtcNow - start).TotalMilliseconds / dur;
            if (t >= 1.0)
            {
                SubCarousel.Offset = new Vector(targetX, SubCarousel.Offset.Y);
                timer.Stop();
                return;
            }
            var eased = easing.Ease(t);
            var x = startX + (targetX - startX) * eased;
            SubCarousel.Offset = new Vector(x, SubCarousel.Offset.Y);
        };
        timer.Start();
    }

    private void OnCarouselScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var step = Step();
        if (step <= 0 || CardCount <= 1)
        {
            return;
        }
        var index = Math.Clamp((int)Math.Round(SubCarousel.Offset.X / step), 0, CardCount - 1);
        if (_vm != null && _vm.CarouselIndex != index)
        {
            _vm.CarouselIndex = index;
        }
        UpdateActiveDot(index);
    }
}
