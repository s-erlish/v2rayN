using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран прогрузки (screens.md «Экран прогрузки», motion.md «Поток добавления подписки») —
/// полноэкранный прозрачный слой, который держит кадр между «пользователь выбрал способ» и
/// «Главная собралась».
///
/// ==================== РАСПИСАНИЕ ====================
/// Ровно из motion.md: 0 → шаг 0 (18%), 1200 → шаг 1 (54%), 3000 → шаг 2 (86%),
/// 4600 → шаг 3 (100%, сонар + галочка + тост), 5900 → оверлей начинает таять (520 мс),
/// 6450 → оверлей снят, ТЕМ ЖЕ КАДРОМ пошла сборка Главной.
///
/// Расписание — ПОЛ, а не потолок: шаг 3 наступает не раньше 4600 мс И не раньше, чем реальная
/// работа закончилась. Реальный импорт обычно укладывается в 4.6 с, и тогда видно ровно
/// хореографию пакета; если он затянулся — экран честно стоит на шаге 2, а не врёт «Подписка
/// добавлена» и не дёргается вперёд. Обратное (проскочить шаги, потому что работа кончилась за
/// 300 мс) тоже запрещено: четыре шага объясняют, что именно произошло.
///
/// ==================== ОДНА ТРАНЗАКЦИЯ ====================
/// «Снимать оверлей и запускать сборку в одной транзакции — иначе главная мелькнёт целиком».
/// Поэтому <see cref="ShellHandoffRequested"/> поднимается СИНХРОННО, в том же обороте UI-потока,
/// в котором слой снимается: между снятием и стартом сборки нет ни await, ни Post, ни отдельного
/// кадра. Обработчик обязан в этом же вызове: (1) выставить сборке Главной ПРЕД-состояние,
/// (2) показать шелл, (3) запустить стаггер. Разложи это на два оборота — и Главная покажется
/// целиком на один кадр, ровно то, что запрещено.
///
/// ==================== ЧЕГО ЗДЕСЬ НЕТ ====================
/// Размытия нет — motion.md прямо запрещает («оно лагает»); уход = прозрачность + отдаление 1.06.
/// Имени подписки нет нигде: ни в заголовке, ни в пояснении, ни в тосте.
///
/// Ветка ошибки (SyncFailed) — не из прототипа, а из реального движка: колонка меняется на месте
/// на «Не удалось синхронизировать» с «Повторить» / «Войти заново». Без неё упавший импорт дал бы
/// вечный экран прогрузки.
/// </summary>
public partial class AccountSyncView : UserControl
{
    /// <summary>Набор текстов потока (screens.md, таблица «Экран прогрузки»).</summary>
    public enum FlowKind
    {
        /// <summary>Вход через Telegram (и через сайт — терминальный путь тот же).</summary>
        Telegram,

        /// <summary>Добавление подписки из буфера обмена.</summary>
        Clipboard,
    }

    // ==================== Расписание (motion.md) ====================
    private static readonly TimeSpan _stepAt1 = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan _stepAt2 = TimeSpan.FromMilliseconds(3000);
    private static readonly TimeSpan _stepAt3 = TimeSpan.FromMilliseconds(4600);
    // Отсчитываются от МОМЕНТА шага 3, а не от старта: если работа задержалась, вся концовка
    // сдвигается вместе с ней, сохраняя ритм 4600 → 5900 → 6450.
    private static readonly TimeSpan _dissolveAfterStep3 = TimeSpan.FromMilliseconds(1300);
    private static readonly TimeSpan _removeAfterStep3 = TimeSpan.FromMilliseconds(1850);
    private static readonly TimeSpan _dissolveOpacity = TimeSpan.FromMilliseconds(520);
    private static readonly TimeSpan _dissolveScale = TimeSpan.FromMilliseconds(600);

    // Кривая «смена экрана» cubic-bezier(0.33,0,0.2,1) (motion.md, «Кривые»). В GlobalResources
    // такого токена пока нет — заведён локально; при промоушене в Ease.* заменить ссылкой.
    private static readonly Easing _screenEase = new SplineEasing(0.33, 0, 0.2, 1);

    private static readonly ITransform _scale1 = TransformOperations.Parse("scale(1)");
    private static readonly ITransform _scale106 = TransformOperations.Parse("scale(1.06)");

    private static readonly double[] _progress = [0.18, 0.54, 0.86, 1.00];

    private readonly AccountViewModel? _vm;
    private readonly CompositeDisposable _subs = new();

    private CancellationTokenSource? _flowCts;      // весь поток целиком
    private CancellationTokenSource? _sonarAnim;
    private CancellationTokenSource? _checkAnim;
    private CancellationTokenSource? _toastAnim;
    private CancellationTokenSource? _columnAnim;

    private FlowKind _kind = FlowKind.Telegram;
    private int _step = -1;
    private bool _showingError;
    private bool _workDone;
    private bool _prevSyncing;

    // Сигнал «работа кончилась», которого ждёт шаг 3. Заводится на каждый поток заново.
    private TaskCompletionSource? _work;

    /// <summary>Поток идёт (от <see cref="RunFlow"/> до снятия слоя). MainWindow держит оверлей поднятым, пока это true.</summary>
    public bool FlowRunning { get; private set; }

    /// <summary>
    /// Слой снят — кадр отдан сборке «Главной». Поднимается СИНХРОННО в момент снятия (см. «одна
    /// транзакция» в шапке класса). Обработчик MainWindow обязан в этом же вызове выставить
    /// пред-состояние сборки, показать шелл и запустить стаггер.
    /// </summary>
    public event EventHandler? ShellHandoffRequested;

    public AccountSyncView()
    {
        InitializeComponent();

        _vm = Design.IsDesignMode ? null : AccountViewModel.Shared;
        DataContext = _vm;

        ProgressTrack.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
            {
                ApplyProgress(animate: false);
            }
        };

        // Цвет кометы — из темы (motion.md: синий в тёмной и светлой, белый в чёрно-белой). Берётся
        // из ТОКЕНА Brush.Accent, а не из литерала; конический градиент требует альфу НА КАЖДОЙ
        // ступени рампы, поэтому кисть собирается кодом, а не {DynamicResource} на одном свойстве.
        ApplyCometColor();
        ActualThemeVariantChanged += (_, _) => ApplyCometColor();

        if (Design.IsDesignMode)
        {
            SetStepTexts(0);
            return;
        }

        // Поток можно ЗАПУСТИТЬ извне (RunFlow с начального экрана), а можно не запускать вовсе —
        // тогда экран сам поднимается на сигналах VM: пост-логин импорт и холодный старт с
        // сохранённой сессией это тот же «Войти через Telegram», только пользователь его не нажимал.
        _subs.Add(_vm!.WhenAnyValue(x => x.IsImportingAccount, x => x.IsStartupLoading, (a, b) => a || b)
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSyncingChanged));

        _subs.Add(_vm.WhenAnyValue(x => x.SyncFailed)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(failed =>
            {
                if (failed)
                {
                    SignalWorkFailed();
                }
                else if (_showingError)
                {
                    // «Повторить» — возвращаемся к потоку с начала.
                    RunFlow(_kind, null);
                }
            }));

        DetachedFromVisualTree += (_, _) =>
        {
            CancelFlow();
            _subs.Dispose();
        };
    }

    // ==================== Публичный вход ====================

    /// <summary>
    /// Запускает поток по расписанию motion.md.
    /// </summary>
    /// <param name="kind">Какой набор текстов показывать.</param>
    /// <param name="work">
    /// Реальная работа под потоком. Завершилась — шаг 3 разрешён; упала — ветка ошибки.
    /// <c>null</c> — работу ведёт не вызывающий (вход через Telegram доводит AccountViewModel), и
    /// сигналом служат его же флаги импорта.
    /// </param>
    public void RunFlow(FlowKind kind, Task? work)
    {
        CancelFlow();

        _kind = kind;
        _step = -1;
        _workDone = false;
        _showingError = false;
        FlowRunning = true;

        ShowFlowColumn(animate: false);
        ResetFlowVisuals();

        _work = new TaskCompletionSource();
        if (work is not null)
        {
            _ = TrackWork(work);
        }

        var cts = new CancellationTokenSource();
        _flowCts = cts;
        _ = RunSchedule(cts.Token);
    }

    /// <summary>Реальная работа завершилась успешно — шаг 3 разрешён (не раньше 4600 мс от старта).</summary>
    public void SignalWorkDone()
    {
        _workDone = true;
        _work?.TrySetResult();
    }

    /// <summary>Реальная работа упала — поток прерывается, колонка меняется на ветку ошибки.</summary>
    public void SignalWorkFailed()
    {
        if (!FlowRunning && _showingError)
        {
            return;
        }
        CancelFlow();
        FlowRunning = false;
        _showingError = true;
        SetComet(false);
        ShowErrorColumn(animate: !IsReducedMotion() && IsVisible);
    }

    private async Task TrackWork(Task work)
    {
        try
        {
            await work;
            SignalWorkDone();
        }
        catch
        {
            SignalWorkFailed();
        }
    }

    // ==================== Расписание ====================

    private async Task RunSchedule(CancellationToken ct)
    {
        try
        {
            SetStep(0);
            await Task.Delay(_stepAt1, ct);
            SetStep(1);
            await Task.Delay(_stepAt2 - _stepAt1, ct);
            SetStep(2);
            await Task.Delay(_stepAt3 - _stepAt2, ct);

            // Пол расписания пройден — ждём реальную работу, если она ещё идёт. Экран стоит на
            // шаге 2 («Проверяем сервера» / «Почти готово») сколько нужно: это правда.
            if (!_workDone && _work is not null)
            {
                await _work.Task.WaitAsync(ct);
            }

            SetStep(3);
            PlayConfirmation();

            await Task.Delay(_dissolveAfterStep3, ct);
            StartDissolve();

            await Task.Delay(_removeAfterStep3 - _dissolveAfterStep3, ct);
            ct.ThrowIfCancellationRequested();
            Handoff();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// ОДНА ТРАНЗАКЦИЯ: слой снимается и кадр отдаётся сборке «Главной» в одном обороте UI-потока.
    /// Ни await, ни Dispatcher.Post между этими двумя действиями — иначе между кадрами успеет
    /// показаться собранная Главная целиком.
    /// </summary>
    private void Handoff()
    {
        FlowRunning = false;
        _flowCts = null;

        // Готовим себя к следующему разу ДО передачи кадра — после неё мы уже не на экране.
        SetComet(false);
        FlowRoot.Opacity = 1;
        FlowRoot.RenderTransform = null;

        var handler = ShellHandoffRequested;
        if (handler is null)
        {
            // Никто не слушает (проводка в MainWindow ещё не сделана): просто уходим, дальше кадр
            // ведёт штатный 3-way гейт шелла. Хореографии сборки в этом случае не будет.
            IsVisible = false;
            return;
        }
        handler(this, EventArgs.Empty);
    }

    private void CancelFlow()
    {
        _flowCts?.Cancel();
        _flowCts = null;
        _work?.TrySetCanceled();
        _work = null;
        _sonarAnim?.Cancel();
        _checkAnim?.Cancel();
        _toastAnim?.Cancel();
        FlowRunning = false;
    }

    // ==================== Шаги ====================

    private void SetStep(int step)
    {
        if (_step == step)
        {
            return;
        }
        _step = step;

        SetStepTexts(step);
        ApplyProgress(animate: true);

        var busy = step < 3;
        SetComet(busy);
        // Глиф Telegram объясняет ожидание на шагах 0–2 потока входа; в потоке буфера кольцо
        // остаётся пустым — там объясняет текст, а не глиф.
        TelegramGlyph.IsVisible = busy && _kind == FlowKind.Telegram;
    }

    private void SetStepTexts(int step)
    {
        var prefix = _kind == FlowKind.Telegram ? "Flow_Tg" : "Flow_Clip";
        FlowTitle.Text = L.T($"{prefix}Title{step}");
        FlowNote.Text = L.T($"{prefix}Note{step}");
    }

    // Ширина заливки = доля живой ширины дорожки: процентов в раскладке Avalonia нет, а привязка
    // к Bounds через Binding не даёт нужного перехода — ведём кодом, переход живёт в разметке.
    private void ApplyProgress(bool animate)
    {
        if (_step < 0)
        {
            return;
        }
        var track = ProgressTrack.Bounds.Width;
        if (track <= 0)
        {
            return;
        }
        var target = track * _progress[Math.Clamp(_step, 0, _progress.Length - 1)];
        if (animate && !IsReducedMotion())
        {
            ProgressFill.Width = target;
            return;
        }
        // Мгновенно: снимаем переход на время присвоения, иначе значение всё равно поедет.
        var transitions = ProgressFill.Transitions;
        ProgressFill.Transitions = null;
        ProgressFill.Width = target;
        ProgressFill.Transitions = transitions;
    }

    // ==================== Подтверждение (шаг 3) ====================

    private void PlayConfirmation()
    {
        DoneRing.IsVisible = true;
        CheckGlyph.IsVisible = true;
        FlowToastText.Text = L.T(_kind == FlowKind.Telegram ? "Flow_ToastTg" : "Flow_ToastClip");
        FlowToast.IsVisible = true;

        if (IsReducedMotion())
        {
            CheckGlyph.Opacity = 1;
            FlowToast.Opacity = 1;
            return;
        }

        _ = PlaySonar();
        _ = PlayCheckPop();
        _ = PlayToast();
    }

    // Сонар: кольцо расходится до 1.55× и гаснет, 600 мс, ОДИН раз (motion.md «Сонар»).
    private async Task PlaySonar()
    {
        _sonarAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _sonarAnim = cts;

        Sonar.IsVisible = true;
        var anim = new Animation
        {
            Duration = Motion.Dur.Emphasis,
            Easing = Motion.Ease.OutQuint,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(Visual.RenderTransformProperty, _scale1),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(Visual.RenderTransformProperty, TransformOperations.Parse("scale(1.55)")),
                    },
                },
            },
        };
        try { await anim.RunAsync(Sonar, cts.Token); }
        catch { }
        if (!cts.IsCancellationRequested)
        {
            Sonar.IsVisible = false;
            Sonar.Opacity = 0;
            Sonar.RenderTransform = null;
        }
    }

    // Галочка: pop 0.6 → 1.06 (70%) → 1 + проявление, 320 мс ease-out-quart.
    private async Task PlayCheckPop()
    {
        _checkAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _checkAnim = cts;

        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(320),
            Easing = Motion.Ease.OutQuart,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(Visual.RenderTransformProperty, TransformOperations.Parse("scale(0.6)")),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(0.7d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(Visual.RenderTransformProperty, _scale106),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(Visual.RenderTransformProperty, _scale1),
                    },
                },
            },
        };
        try { await anim.RunAsync(CheckGlyph, cts.Token); }
        catch { }
        if (!cts.IsCancellationRequested)
        {
            CheckGlyph.Opacity = 1;
            CheckGlyph.RenderTransform = null;
        }
    }

    // Тост выезжает снизу 280 мс (motion.md «Тост»). Сам он не уходит: через 550 мс после него
    // растворяется весь оверлей — отдельное затухание тоста было бы вторым, лишним движением.
    private async Task PlayToast()
    {
        _toastAnim?.Cancel();
        var cts = new CancellationTokenSource();
        _toastAnim = cts;

        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(280),
            Easing = Motion.Ease.OutQuint,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(Visual.RenderTransformProperty, TransformOperations.Parse("translateY(16px)")),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(Visual.RenderTransformProperty, TransformOperations.Parse("translateY(0px)")),
                    },
                },
            },
        };
        try { await anim.RunAsync(FlowToast, cts.Token); }
        catch { }
        if (!cts.IsCancellationRequested)
        {
            FlowToast.Opacity = 1;
            FlowToast.RenderTransform = null;
        }
    }

    // ==================== Уход оверлея ====================

    // Прозрачность в 0 (520 мс) плюс отдаление до 1.06 (600 мс), кривая «смена экрана».
    // РАЗМЫТИЯ НЕТ — motion.md запрещает прямым текстом.
    private void StartDissolve()
    {
        if (IsReducedMotion())
        {
            FlowRoot.Opacity = 0;
            return;
        }

        var fade = new Animation
        {
            Duration = _dissolveOpacity,
            Easing = _screenEase,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
            },
        };
        var zoom = new Animation
        {
            Duration = _dissolveScale,
            Easing = _screenEase,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.RenderTransformProperty, _scale1) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.RenderTransformProperty, _scale106) } },
            },
        };
        _ = fade.RunAsync(FlowRoot);
        _ = zoom.RunAsync(FlowRoot);
    }

    // ==================== Комета ====================

    private void SetComet(bool on)
    {
        // За кадром и под «Облегчённым режимом» комета не крутится: анимация, которую никто не
        // видит, всё равно тикает и жжёт кадры.
        var live = on && IsVisible && !IsReducedMotion();
        Comet.IsVisible = on;
        if (live)
        {
            if (!Comet.Classes.Contains("spinning"))
            {
                Comet.Classes.Add("spinning");
            }
        }
        else
        {
            Comet.Classes.Remove("spinning");
        }
    }

    private void ApplyCometColor()
    {
        if (this.TryFindResource("Brush.Accent", out var res) && res is ISolidColorBrush brush)
        {
            Comet.RingColor = brush.Color;
        }
    }

    // ==================== Колонки: поток ↔ ошибка ====================

    private void ShowFlowColumn(bool animate)
    {
        _columnAnim?.Cancel();
        FlowColumn.IsVisible = true;
        ProgressTrack.IsVisible = true;
        if (!animate)
        {
            FlowColumn.Opacity = 1;
            ErrorColumn.Opacity = 0;
            ErrorColumn.IsVisible = false;
            return;
        }
        var cts = new CancellationTokenSource();
        _columnAnim = cts;
        _ = CrossfadeColumns(FlowColumn, ErrorColumn, cts.Token);
    }

    private void ShowErrorColumn(bool animate)
    {
        _columnAnim?.Cancel();
        ErrorColumn.IsVisible = true;
        FlowToast.IsVisible = false;
        if (!animate)
        {
            ErrorColumn.Opacity = 1;
            FlowColumn.Opacity = 0;
            FlowColumn.IsVisible = false;
            return;
        }
        var cts = new CancellationTokenSource();
        _columnAnim = cts;
        _ = CrossfadeColumns(ErrorColumn, FlowColumn, cts.Token);
    }

    private static async Task CrossfadeColumns(Control incoming, Control outgoing, CancellationToken ct)
    {
        incoming.IsVisible = true;
        var inTask = RunOpacity(incoming, incoming.Opacity, 1d, Motion.Dur.Exit, Motion.Ease.Standard, ct);
        var outTask = RunOpacity(outgoing, outgoing.Opacity, 0d, Motion.Dur.Exit, Motion.Ease.Standard, ct);
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

    private static Task RunOpacity(Visual target, double from, double to, TimeSpan duration, Easing easing, CancellationToken ct)
    {
        var anim = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, to) } },
            },
        };
        return anim.RunAsync(target, ct);
    }

    // ==================== Самостоятельный подъём по сигналам VM ====================

    // Пост-логин импорт и холодный старт с сохранённой сессией — тот же поток «через Telegram»,
    // просто запущенный не с начального экрана. Если RunFlow уже позвали снаружи, ничего не
    // перезапускаем: внешний вызов знает набор текстов точнее.
    private void OnSyncingChanged(bool syncing)
    {
        var was = _prevSyncing;
        _prevSyncing = syncing;

        if (syncing)
        {
            if (!FlowRunning)
            {
                RunFlow(FlowKind.Telegram, null);
            }
            return;
        }

        // Импорт закончился успешно (не ошибка, всё ещё вошли) — разрешаем шаг 3.
        if (was && _vm is { SyncFailed: false, IsLoggedIn: true })
        {
            SignalWorkDone();
        }
    }

    // ==================== Жизненный цикл на экране ====================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || Comet is null)
        {
            return;
        }

        if (IsVisible)
        {
            // Показали заново — цвет кометы мог устареть (тему меняли, пока слоя не было).
            ApplyCometColor();
            ApplyProgress(animate: false);
            SetComet(_step is >= 0 and < 3);
        }
        else
        {
            // Слой сняли извне (штатный гейт шелла увёл кадр на Главную раньше, чем поток
            // доиграл) — отменяем всё, чтобы за кадром ничего не тикало и хэндофф не выстрелил
            // в пустоту. Регресса это не даёт: кадр уже ведёт шелл.
            CancelFlow();
            SetComet(false);
            ResetFlowVisuals();
        }
    }

    private void ResetFlowVisuals()
    {
        _sonarAnim?.Cancel();
        _checkAnim?.Cancel();
        _toastAnim?.Cancel();

        Sonar.IsVisible = false;
        Sonar.Opacity = 0;
        Sonar.RenderTransform = null;
        DoneRing.IsVisible = false;
        CheckGlyph.IsVisible = false;
        CheckGlyph.Opacity = 0;
        CheckGlyph.RenderTransform = null;
        TelegramGlyph.IsVisible = false;
        FlowToast.IsVisible = false;
        FlowToast.Opacity = 0;
        FlowToast.RenderTransform = null;
        FlowRoot.Opacity = 1;
        FlowRoot.RenderTransform = null;

        var transitions = ProgressFill.Transitions;
        ProgressFill.Transitions = null;
        ProgressFill.Width = 0;
        ProgressFill.Transitions = transitions;
    }

    /// <summary>«Облегчённый режим» / дизайн-режим: движения нет, состояния переключаются мгновенно.</summary>
    private static bool IsReducedMotion() => Design.IsDesignMode || MotionState.IsLite;
}
