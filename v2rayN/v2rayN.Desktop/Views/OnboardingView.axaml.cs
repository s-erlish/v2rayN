using Avalonia.Animation;
using Avalonia.Media.Transformation;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Первый запуск (нет подписок): экран заведения доступа на всю ширину окна под chrome — без рейла, без
/// списка серверов, без connect-щита. MainWindow показывает эту вью, пока
/// <see cref="HomeViewModel.IsEmpty"/> = true, и прячет её (открывая обычный шелл), как только подписка
/// добавлена. Это буквально первый кадр нового пользователя.
///
/// «Добавить по QR-коду» / «из буфера обмена» бьют в реальный движок через HomeViewModel (тот же путь,
/// что онбординг-CTA в HomeView). «Войти через Telegram» / «через сайт» СРАЗУ стартуют соответствующую
/// авторизацию (без промежуточного выбора метода): Telegram открывает deep link и переходит в ожидание,
/// сайт открывает форму email/пароля. Шелл скрыт, пока пусто, поэтому вход показываем оверлеем; «назад»
/// возвращает к онбордингу.
///
/// Движение (§6/P1): один хореографированный entrance-стаггер при первом показе — щит scale 0.92→1,
/// остальное rise translateY 8→0 + fade, 300мс OutQuint, шаг 40мс сверху вниз (~620мс), затем ПОЛНАЯ
/// статика (product-register: без ambient-петель). Императивно в code-behind, чтобы под reduced-motion
/// (<see cref="MotionState.IsLite"/>) / preview (PREVIEW_VIEW) / дизайн-режимом сразу отдать
/// полностью-видимый дефолт (reveal ОБЯЗАН улучшать уже-видимое; headless/preview не должен быть пустым).
/// Хит-тест НИКОГДА не гейтится анимацией — кнопки кликабельны всё время (только opacity/transform).
/// </summary>
public partial class OnboardingView : UserControl
{
    // Моушен-трансформы: TransformOperations композируются чисто с анимацией RenderTransform (приём
    // LoginView). Масштаб щита центрируется по RenderTransformOrigin="50%,50%" плитки.
    private static readonly ITransform _rise8 = TransformOperations.Parse("translateY(8px)");
    private static readonly ITransform _rise0 = TransformOperations.Parse("translateY(0px)");
    private static readonly ITransform _scale092 = TransformOperations.Parse("scale(0.92)");
    private static readonly ITransform _scale1 = TransformOperations.Parse("scale(1)");

    // Колонка пред-скрыта в ctor под entrance-стаггер — стаггер ещё не сыгран.
    private bool _entryPending;

    public OnboardingView()
    {
        InitializeComponent();

        AddQrButton.Click += OnAddQr;
        AddClipboardButton.Click += OnAddClipboard;
        LoginTelegramButton.Click += OnLoginTelegram;
        LoginSiteButton.Click += OnLoginSite;

        // Entrance-стаггер: пред-скрываем детей колонки (opacity 0), чтобы раскрыть их сверху вниз без
        // пред-вспышки (приём LoginView). ТОЛЬКО при включённом движении; под lite/preview/дизайн —
        // не трогаем (остаются видимыми), стаггер не запускаем: reveal улучшает уже-видимый дефолт.
        if (!IsReducedMotion())
        {
            foreach (var child in Column.Children)
            {
                child.Opacity = 0;
            }
            _entryPending = true;
        }

        Loaded += OnFirstLoaded;
    }

    // ── Первая раскладка: entrance-стаггер (один раз) ────────────────────────
    private void OnFirstLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;

        if (!_entryPending)
        {
            return;
        }
        _entryPending = false;

        // Движение выключили между ctor и первым кадром (живой lite-тумблер) — просто возвращаем видимость.
        if (IsReducedMotion())
        {
            RestoreChildren();
            return;
        }
        PlayEntryStagger();
    }

    // ── Действия (проводка CTA по DataContext) ──────────────────────────────

    // Добавить по QR-коду → скан экрана (MainWindowViewModel.AddServerViaScanAsync).
    private void OnAddQr(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm)
        {
            _ = vm.AddViaQr();
        }
    }

    // Добавить из буфера обмена → импорт из clipboard (MainWindowViewModel.AddServerViaClipboardAsync).
    private void OnAddClipboard(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm)
        {
            _ = vm.AddViaClipboard();
        }
    }

    // Войти через Telegram → сразу стартуем Telegram-авторизацию (открывает Telegram), LoginView
    // показывает состояние ожидания подтверждения — без повторного выбора метода.
    private void OnLoginTelegram(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenLoginTelegram();
    }

    // Войти через сайт → открываем LoginView прямо на форме входа по email/паролю.
    private void OnLoginSite(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenLoginSite();
    }

    // ── Entrance-стаггер (§6) ────────────────────────────────────────────────

    /// <summary>
    /// Раскрывает колонку сверху вниз: щит (первый) — scale 0.92→1, остальное — rise translateY 8→0;
    /// оба + fade, 300мс OutQuint, шаг 40мс (Motion.Dur.Stagger). Один проход, затем статика.
    /// </summary>
    private void PlayEntryStagger()
    {
        var children = Column.Children;
        for (var i = 0; i < children.Count; i++)
        {
            var delay = (int)(Motion.Dur.Stagger.TotalMilliseconds * i);
            var from = i == 0 ? _scale092 : _rise8;
            var to = i == 0 ? _scale1 : _rise0;
            _ = PlayReveal((Control)children[i], delay, from, to);
        }
    }

    /// <summary>
    /// Раскрывает элемент: opacity 0→1 + RenderTransform from→to, 300мс OutQuint, с задержкой стаггера.
    /// FillMode.None + восстановление базы — чтобы не затенять :pressed-scale кнопок. Предохранитель
    /// (таймер) гарантированно возвращает полную видимость, если анимация прервана отсоединением.
    /// </summary>
    private static async Task PlayReveal(Control el, int delayMs, ITransform from, ITransform to)
    {
        el.Opacity = 0;
        var anim = new Animation
        {
            Duration = Motion.Dur.Reveal,
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = Motion.Ease.OutQuint,
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(Visual.RenderTransformProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(Visual.RenderTransformProperty, to) } },
            },
        };

        var cts = new CancellationTokenSource();
        var safety = DispatcherTimer.RunOnce(
            () =>
            {
                cts.Cancel();
                el.Opacity = 1;
                el.RenderTransform = null;
            },
            TimeSpan.FromMilliseconds(delayMs + Motion.Dur.Reveal.TotalMilliseconds + 250));
        try
        {
            await anim.RunAsync(el, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            safety.Dispose();
            el.Opacity = 1;
            el.RenderTransform = null;
            cts.Dispose();
        }
    }

    /// <summary>Возвращает колонку полностью видимой (когда стаггер пропущен: lite/preview).</summary>
    private void RestoreChildren()
    {
        foreach (var child in Column.Children)
        {
            child.Opacity = 1;
            child.RenderTransform = null;
        }
    }

    /// <summary>reduced-motion: превью-хук (PREVIEW_VIEW), дизайн-режим ИЛИ live lite (MotionState).</summary>
    private static bool IsReducedMotion()
        => Design.IsDesignMode
           || Environment.GetEnvironmentVariable("PREVIEW_VIEW") is not null
           || MotionState.IsLite;
}
