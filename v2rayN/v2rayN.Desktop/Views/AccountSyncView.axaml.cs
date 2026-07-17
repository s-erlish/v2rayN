using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран синхронизации аккаунта (E3 + Фаза 3): полноэкранный оверлей «Добавляем аккаунт», который держится,
/// пока идёт пост-логин импорт (<see cref="AccountViewModel.IsImportingAccount"/>) или холодный старт
/// (<see cref="AccountViewModel.IsStartupLoading"/>). Видимостью управляет MainWindow.ApplyShellVisibility
/// (3-way gate) — оверлей ПОСТОЯННО живёт в дереве shell, поэтому вся анимация навешивается ТОЛЬКО пока
/// оверлей виден: дуга не тикает за кадром (RAM/CPU-регресс, с которым борется репозиторий).
///
/// Разметка держит две перекрывающиеся колонки (загрузка / ошибка); этот code-behind ведёт:
///   • DataContext = <see cref="AccountViewModel.Shared"/> — оверлей статичен в shell и не наследует путь к
///     этому VM (без правок MainWindow);
///   • .spinning на дуге ТОЛЬКО пока (виден И нет ошибки) — переиспользует IsVisible-gated приём;
///   • живую строку стадии (кроссфейд при смене реальной фазы; под .lite мгновенно);
///   • ошибку (SyncFailed): кроссфейд колонок НА МЕСТЕ — оверлей остаётся поднятым, гейт shell не трогаем;
///   • success-хэндофф (IsImportingAccount → false без ошибки): дуга стоп + одноразовый settle щита
///     (scale 1.0→1.04→1.0), затем существующий кроссфейд shell на «Главную».
/// Каждая анимация имеет мгновенный фолбэк под <see cref="MotionState.IsLite"/>.
/// </summary>
public partial class AccountSyncView : UserControl
{
    private readonly AccountViewModel? _vm;
    private readonly CompositeDisposable _subs = new();

    private CancellationTokenSource? _columnAnim;   // кроссфейд загрузка↔ошибка
    private CancellationTokenSource? _settleAnim;    // одноразовый settle-поп щита (успех)
    private CancellationTokenSource? _stageAnim;     // дип-кроссфейд строки стадии

    private bool _showingError;
    private bool _prevSyncing;

    public AccountSyncView()
    {
        InitializeComponent();

        _vm = Design.IsDesignMode ? null : AccountViewModel.Shared;
        DataContext = _vm;

        if (_vm is null)
        {
            // Дизайн/превью (или VM ещё не сконструирован): статичная колонка загрузки.
            StageLine.Text = L.T("Account_SyncStageAccount");
            UpdateSpinner();
            return;
        }

        // Стартовое состояние — колонка загрузки + живая стадия.
        StageLine.Text = _vm.SyncStageText;
        ShowLoadingColumn(animate: false);

        // Живая строка стадии: кроссфейд подписи по мере продвижения реальной фазы (под .lite мгновенно).
        _subs.Add(_vm.WhenAnyValue(x => x.SyncStageText)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(SetStage));

        // Поверхность ошибки: своп колонок НА МЕСТЕ (под .lite мгновенно). Оверлей остаётся поднятым.
        _subs.Add(_vm.WhenAnyValue(x => x.SyncFailed)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSyncFailedChanged));

        // Success-хэндофф: когда «идёт синхронизация» падает true→false БЕЗ ошибки (и мы всё ещё вошли) —
        // щит делает settle. Комбинируем оба гейта (пост-логин + холодный старт) в один сигнал.
        _subs.Add(_vm.WhenAnyValue(x => x.IsImportingAccount, x => x.IsStartupLoading, (a, b) => a || b)
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSyncingChanged));

        DetachedFromVisualTree += (_, _) => _subs.Dispose();
        UpdateSpinner();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || SyncSpinner is null)
        {
            return;
        }

        if (IsVisible)
        {
            // Стал виден: сбрасываем к текущему состоянию VM (свежая синхронизация → загрузка; если уже
            // ошибка — сразу колонка ошибки). Никаких анимаций входа: shell сам кроссфейдит оверлей.
            SyncShield.RenderTransform = null;
            if (_vm is { SyncFailed: true })
            {
                _showingError = true;
                ShowErrorColumn(animate: false);
            }
            else
            {
                _showingError = false;
                StageLine.Text = _vm?.SyncStageText ?? StageLine.Text;
                ShowLoadingColumn(animate: false);
            }
        }
        else
        {
            // Уходит с экрана: гасим любую незавершённую анимацию, чтобы ничего не тикало за кадром.
            _settleAnim?.Cancel();
            _stageAnim?.Cancel();
            _columnAnim?.Cancel();
            SyncShield.RenderTransform = null;
        }

        UpdateSpinner();
    }

    // Дуга крутится ТОЛЬКО пока оверлей виден и нет состояния ошибки (в ошибке кольцо «спокойное»).
    // Под .lite сам keyframe не аттачится (глобальный селектор :is(Window):not(.lite)) — здесь не дублируем.
    private void UpdateSpinner()
    {
        if (SyncSpinner is null)
        {
            return;
        }
        if (IsVisible && !_showingError)
        {
            if (!SyncSpinner.Classes.Contains("spinning"))
            {
                SyncSpinner.Classes.Add("spinning");
            }
        }
        else
        {
            SyncSpinner.Classes.Remove("spinning");
        }
    }

    // ==================== Живая строка стадии (дип-кроссфейд) ====================
    private void SetStage(string? text)
    {
        var next = text ?? string.Empty;
        if (StageLine.Text == next)
        {
            return;
        }
        _stageAnim?.Cancel();
        if (MotionState.IsLite || !IsVisible || _showingError)
        {
            StageLine.Text = next;
            StageLine.Opacity = 1;
            return;
        }
        var cts = new CancellationTokenSource();
        _stageAnim = cts;
        _ = RunStageDip(next, cts.Token);
    }

    private async Task RunStageDip(string next, CancellationToken ct)
    {
        try { await RunOpacity(StageLine, StageLine.Opacity, 0d, TimeSpan.FromMilliseconds(75), Motion.Ease.Standard, ct); }
        catch { }
        if (ct.IsCancellationRequested)
        {
            return;
        }
        StageLine.Text = next;
        try { await RunOpacity(StageLine, 0d, 1d, TimeSpan.FromMilliseconds(75), Motion.Ease.Standard, ct); }
        catch { }
        if (!ct.IsCancellationRequested)
        {
            StageLine.Opacity = 1;
        }
    }

    // ==================== Ошибка ↔ загрузка (кроссфейд НА МЕСТЕ) ====================
    private void OnSyncFailedChanged(bool failed)
    {
        if (failed == _showingError)
        {
            return;
        }
        _showingError = failed;
        UpdateSpinner();   // дуга крутится только вне ошибки
        var animate = !MotionState.IsLite && IsVisible;
        if (failed)
        {
            ShowErrorColumn(animate);
        }
        else
        {
            // Повтор: вернулись к загрузке — обновляем стадию под текущую фазу VM.
            StageLine.Text = _vm?.SyncStageText ?? StageLine.Text;
            ShowLoadingColumn(animate);
        }
    }

    private void ShowErrorColumn(bool animate)
    {
        _columnAnim?.Cancel();
        ErrorColumn.IsVisible = true;
        if (!animate)
        {
            ErrorColumn.Opacity = 1;
            LoadingColumn.Opacity = 0;
            LoadingColumn.IsVisible = false;
            return;
        }
        var cts = new CancellationTokenSource();
        _columnAnim = cts;
        _ = CrossfadeColumns(ErrorColumn, LoadingColumn, cts.Token);
    }

    private void ShowLoadingColumn(bool animate)
    {
        _columnAnim?.Cancel();
        LoadingColumn.IsVisible = true;
        StageLine.Opacity = 1;
        if (!animate)
        {
            LoadingColumn.Opacity = 1;
            ErrorColumn.Opacity = 0;
            ErrorColumn.IsVisible = false;
            return;
        }
        var cts = new CancellationTokenSource();
        _columnAnim = cts;
        _ = CrossfadeColumns(LoadingColumn, ErrorColumn, cts.Token);
    }

    private async Task CrossfadeColumns(Control incoming, Control outgoing, CancellationToken ct)
    {
        incoming.IsVisible = true;
        var inTask = RunOpacity(incoming, incoming.Opacity, 1d, TimeSpan.FromMilliseconds(150), Motion.Ease.Standard, ct);
        var outTask = RunOpacity(outgoing, outgoing.Opacity, 0d, TimeSpan.FromMilliseconds(150), Motion.Ease.Standard, ct);
        try { await Task.WhenAll(inTask, outTask); }
        catch { }
        if (ct.IsCancellationRequested)
        {
            return;
        }
        incoming.Opacity = 1;
        outgoing.Opacity = 0;
        outgoing.IsVisible = false;
    }

    // ==================== Success settle (дуга стоп + поп щита) ====================
    private void OnSyncingChanged(bool syncing)
    {
        var was = _prevSyncing;
        _prevSyncing = syncing;
        // Настоящее завершение успехом: «шло → перестало», без ошибки, всё ещё вошли (re-login уходит в
        // logged-out, поэтому settle там не срабатывает — оверлей уходит на онбординг, а не на «Главную»).
        if (was && !syncing && _vm is { SyncFailed: false, IsLoggedIn: true })
        {
            RunSettle();
        }
    }

    private void RunSettle()
    {
        // Дуга завершает и ОСТАНАВЛИВАЕТСЯ (не резкий обрыв — просто снимаем вращение).
        SyncSpinner.Classes.Remove("spinning");
        if (MotionState.IsLite || !IsVisible)
        {
            return;   // reduced-motion / за кадром: мгновенно, без scale
        }
        _settleAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _settleAnim = cts;
        _ = RunSettlePop(cts.Token);
    }

    private async Task RunSettlePop(CancellationToken ct)
    {
        // Уверенный «готово»-бит: scale 1.0→1.04→1.0, Reveal 300 OutQuint, только transform (центр 50%,50%).
        var anim = new Animation
        {
            Duration = Motion.Dur.Reveal,
            Easing = Motion.Ease.OutQuint,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Avalonia.Styling.Setter(ScaleTransform.ScaleXProperty, 1.0), new Avalonia.Styling.Setter(ScaleTransform.ScaleYProperty, 1.0) },
                },
                new KeyFrame
                {
                    Cue = new Cue(0.5d),
                    Setters = { new Avalonia.Styling.Setter(ScaleTransform.ScaleXProperty, 1.04), new Avalonia.Styling.Setter(ScaleTransform.ScaleYProperty, 1.04) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Avalonia.Styling.Setter(ScaleTransform.ScaleXProperty, 1.0), new Avalonia.Styling.Setter(ScaleTransform.ScaleYProperty, 1.0) },
                },
            },
        };
        try { await anim.RunAsync(SyncShield, ct); }
        catch { }
        if (!ct.IsCancellationRequested)
        {
            SyncShield.RenderTransform = null;
        }
    }

    // Чистый opacity-аниматор (compositor-only), зеркалит MainWindow.RunFade. FillMode.Forward держит кадр.
    private static Task RunOpacity(Visual target, double from, double to, TimeSpan duration, Easing easing, CancellationToken ct)
    {
        var anim = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Avalonia.Styling.Setter(Visual.OpacityProperty, to) } },
            },
        };
        return anim.RunAsync(target, ct);
    }
}
