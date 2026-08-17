using Avalonia.Animation;
using Avalonia.Animation.Easings;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Отклик на нажатие (motion.md «Отклик на нажатие») для контролов, у которых НЕТ псевдокласса
/// <c>:pressed</c> — то есть для <see cref="Border"/>-строк и карточек. У кнопок псевдокласс есть,
/// им хватает стилей в <c>GlobalStyles.axaml</c>; сюда они приходят только за
/// <see cref="CompositedProperty"/>.
///
/// Пакет: никакой подсветки и никакого ripple. Под курсором — шаг фона, под нажатием — прогиб
/// масштабом. Вход 70 мс (<c>cubic-bezier(0.4,0,0.6,1)</c>), возврат 200 мс с лёгким отскоком
/// (<c>cubic-bezier(0.34,1.25,0.64,1)</c>). Целочисленный сдвиг по Y НЕ добавляется — только масштаб.
///
/// ==================== ПОЧЕМУ ПРОГИБ ЖИВЁТ НА РЕБЁНКЕ, А НЕ НА САМОЙ СТРОКЕ ====================
/// Коммит 1e884ad9 «Settings rows: remove press-scale, fix every-other-tap» снял прежний прогиб строк
/// настроек, и снял по делу: класс <c>.pressed</c> вешал <c>RenderTransform</c> на САМУ строку. В
/// Avalonia render-transform участвует в hit-test, поэтому строка физически уезжала из-под курсора:
/// у краёв указатель оказывался ВНЕ ужатых границ → прилетал <c>PointerExited</c> → обработчик снимал
/// <c>.pressed</c> → строка возвращалась под курсор → <c>PointerEntered</c> … и на этом дребезге жест
/// <c>Tapped</c> отменялся. Отсюда и симптом «тап срабатывает через раз».
///
/// Здесь прогиб применяется к СОДЕРЖИМОМУ строки (<c>Border.Child</c>), а сама строка остаётся
/// нетронутой. Её границы, а значит и hit-test, и <c>IsPointerOver</c>, и жест <c>Tapped</c>, при
/// нажатии не меняются вообще — дребезг невозможен по построению, а не «починен» подбором таймингов.
/// Обработчики подписаны пассивно: <c>Handled</c> не ставится, указатель не захватывается, поэтому
/// ни тап, ни протяжка-скролл в <see cref="ScrollViewer"/> не ломаются. Заодно ховер-заливка строки
/// (её рисует сам <see cref="Border"/>) при нажатии стоит на месте, а не съёживается вместе с текстом.
///
/// ==================== ПОЧЕМУ ТЕКСТ НЕ ДЁРГАЕТСЯ ====================
/// См. <see cref="CompositedProperty"/>.
/// </summary>
public static class PressFeedback
{
    //  Тайминги и кривые прототипа (.row/.btn/.ico): вход 70мс cubic-bezier(0.4,0,0.6,1),
    //  возврат 200мс cubic-bezier(0.34,1.25,0.64,1). Зеркалит Ease.PressIn/Ease.PressBack из
    //  GlobalStyles.axaml — держать синхронно.
    private static readonly TimeSpan InDuration = TimeSpan.FromMilliseconds(70);
    private static readonly TimeSpan BackDuration = TimeSpan.FromMilliseconds(200);
    private static readonly Easing InEase = new SplineEasing(0.4, 0, 0.6, 1);
    private static readonly Easing BackEase = new SplineEasing(0.34, 1.25, 0.64, 1);

    /// <summary>
    /// «Аппаратный слой» на время анимации из motion.md (<c>will-change: transform</c> в прототипе,
    /// <c>setLayerType(LAYER_TYPE_HARDWARE)</c> в андроидной формулировке) — в переводе на Avalonia.
    ///
    /// Avalonia НЕ кеширует поддерево в текстуру, она переигрывает список отрисовки под матрицей
    /// трансформа, то есть глифы РАСТРИРУЮТСЯ ЗАНОВО на каждом кадре масштаба. Дёргается текст не от
    /// самого пересчёта, а от двух квантователей, которые на последнем кадре включаются обратно:
    ///
    ///  1. <b>Привязка базовой линии к пиксельной сетке.</b> Пока идёт анимация, базовая линия строки
    ///     стоит на дробной координате; на возврате она снапится в целый пиксель — это и есть тот
    ///     самый рывок «текст снапится по пикселям» из motion.md. Лечится
    ///     <see cref="BaselinePixelAlignment.Unaligned"/>.
    ///  2. <b>Субпиксельное (LCD) сглаживание.</b> Skia отключает его, когда в матрице есть масштаб,
    ///     и включает обратно, когда трансформ уходит — глифы за один кадр меняют насыщенность и
    ///     цветную бахрому. Лечится жёстко серым <see cref="TextRenderingMode.Antialias"/>: раствор
    ///     глифа одинаков и в покое, и под прогибом.
    ///
    /// Оба поля живут в <see cref="Visual.TextOptions"/> и применяются рендером ко ВСЕМУ поддереву
    /// (<c>DrawingContext.PushTextOptions</c>), поэтому достаточно выставить их на самом нажимаемом
    /// элементе — подписи и значения внутри наследуют их на отрисовке. AvaloniaProperty у них нет,
    /// из <c>Setter</c> в стиле их не выставить — только кодом, ради чего это свойство и заведено.
    ///
    /// Выставляется ОДИН раз и навсегда, а не на время анимации: ровно так это сделано в прототипе
    /// (<c>will-change:transform</c> объявлен статически на <c>.row/.btn/.nav/.ico</c>, а не
    /// переключается на каждый тап). Постоянное «повышение» ещё и убирает саму границу перехода —
    /// снапу неоткуда взяться, потому что режим растеризации глифов не меняется НИКОГДА.
    /// </summary>
    public static readonly AttachedProperty<bool> CompositedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Composited", typeof(PressFeedback));

    /// <summary>
    /// Целевой масштаб прогиба по лестнице motion.md (строка 0.985, карточка/кнопка/чип 0.975,
    /// иконка 0.90, пункт навигации 0.92). <see cref="double.NaN"/> — отклик выключен.
    /// Выставляется селектором из <c>GlobalStyles.axaml</c>, поэтому потребителям (SettingsView,
    /// BuyView …) не нужно ни строчки кода — это важно: прежняя реализация требовала правки в
    /// code-behind КАЖДОГО экрана, и именно там расползались варианты обработки указателя.
    /// </summary>
    public static readonly AttachedProperty<double> ScaleProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Scale", typeof(PressFeedback), double.NaN);

    //  Трансформ прогиба, посаженный на РЕБЁНКА строки. Заодно метка «уже подписаны».
    private static readonly AttachedProperty<ScaleTransform?> DipProperty =
        AvaloniaProperty.RegisterAttached<Control, ScaleTransform?>("Dip", typeof(PressFeedback));

    //  Токен отмены незавершённого прогиба/возврата: быстрый повторный тап не должен догонять
    //  предыдущую анимацию (та же дисциплина, что у путешествующего индикатора навигации).
    private static readonly AttachedProperty<CancellationTokenSource?> DipAnimProperty =
        AvaloniaProperty.RegisterAttached<Control, CancellationTokenSource?>("DipAnim", typeof(PressFeedback));

    static PressFeedback()
    {
        CompositedProperty.Changed.AddClassHandler<Control, bool>(OnCompositedChanged);
        ScaleProperty.Changed.AddClassHandler<Control, double>(OnScaleChanged);
    }

    public static bool GetComposited(Control c) => c.GetValue(CompositedProperty);

    public static void SetComposited(Control c, bool v) => c.SetValue(CompositedProperty, v);

    public static double GetScale(Control c) => c.GetValue(ScaleProperty);

    public static void SetScale(Control c, double v) => c.SetValue(ScaleProperty, v);

    // ==================== «Аппаратный слой» ====================

    private static void OnCompositedChanged(Control c, AvaloniaPropertyChangedEventArgs<bool> e)
    {
        if (e.NewValue.GetValueOrDefault())
        {
            TextOptions.SetTextRenderingMode(c, TextRenderingMode.Antialias);
            TextOptions.SetBaselinePixelAlignment(c, BaselinePixelAlignment.Unaligned);
        }
        else
        {
            TextOptions.SetTextRenderingMode(c, TextRenderingMode.Unspecified);
            TextOptions.SetBaselinePixelAlignment(c, BaselinePixelAlignment.Unspecified);
        }
    }

    // ==================== Прогиб ====================

    private static void OnScaleChanged(Control c, AvaloniaPropertyChangedEventArgs<double> e)
    {
        var wanted = e.NewValue.GetValueOrDefault();
        var active = IsUsable(wanted);

        //  Прогиб = масштаб, значит текст под ним растрируется заново → нужен тот же «аппаратный
        //  слой», что и у кнопок. Ставим его вместе с прогибом, чтобы потребитель не мог забыть.
        SetComposited(c, active);

        if (!active)
        {
            Unwire(c);
            return;
        }

        if (c.GetValue(DipProperty) is not null)
        {
            return;   // уже подписаны; целевой масштаб читается на нажатии, менять подписку не нужно
        }

        c.SetValue(DipProperty, new ScaleTransform(1d, 1d));
        //  Пассивная подписка: Handled НЕ ставим и указатель НЕ захватываем — жест Tapped
        //  потребителя и протяжка-скролл ScrollViewer остаются нетронутыми.
        c.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        c.AddHandler(InputElement.PointerReleasedEvent, OnPointerUp, RoutingStrategies.Bubble, handledEventsToo: true);
        c.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerUp, RoutingStrategies.Bubble, handledEventsToo: true);
        //  PointerExited здесь БЕЗОПАСЕН (в отличие от прежней реализации): границы самой строки
        //  под нажатием не двигаются, поэтому событие приходит только когда указатель реально ушёл.
        c.AddHandler(InputElement.PointerExitedEvent, OnPointerUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void Unwire(Control c)
    {
        if (c.GetValue(DipProperty) is null)
        {
            return;
        }
        c.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        c.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerUp);
        c.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerUp);
        c.RemoveHandler(InputElement.PointerExitedEvent, OnPointerUp);
        c.GetValue(DipAnimProperty)?.Cancel();
        c.SetValue(DipAnimProperty, null);
        c.SetValue(DipProperty, null);
        c.Classes.Remove("pressed");
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c)
        {
            return;
        }
        if (!e.GetCurrentPoint(c).Properties.IsLeftButtonPressed)
        {
            return;   // правый/средний клик прогиб не даёт — это не «нажатие» в смысле пакета
        }
        if (!c.Classes.Contains("pressed"))
        {
            //  Класс несёт ТОЛЬКО фон нажатия (Brush.PressBg из прототипа .row:active).
            //  Масштаб он не трогает — иначе трансформ снова сел бы на саму строку.
            c.Classes.Add("pressed");
        }
        Dip(c, GetScale(c));
    }

    private static void OnPointerUp(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c)
        {
            return;
        }
        c.Classes.Remove("pressed");
        Dip(c, 1d);
    }

    private static void Dip(Control c, double to)
    {
        if (!IsUsable(to) && to != 1d)
        {
            return;
        }
        if (c.GetValue(DipProperty) is not { } dip)
        {
            return;
        }
        //  Цель прогиба — СОДЕРЖИМОЕ, а не сам контрол: см. развёрнутое объяснение в шапке класса.
        //  Если содержимого ещё нет (строку наполняют позже) — просто ничего не делаем.
        if (ResolveTarget(c) is not { } target)
        {
            return;
        }
        if (!ReferenceEquals(target.RenderTransform, dip))
        {
            target.RenderTransformOrigin = new RelativePoint(0.5d, 0.5d, RelativeUnit.Relative);
            target.RenderTransform = dip;
        }

        c.GetValue(DipAnimProperty)?.Cancel();

        //  «Облегчённый режим» гасит ВСЁ движение (motion.md): прогиб становится мгновенным
        //  переключением, но не исчезает — фон нажатия и снап масштаба остаются как отклик.
        if (MotionState.IsLite)
        {
            dip.ScaleX = dip.ScaleY = to;
            c.SetValue(DipAnimProperty, null);
            return;
        }

        var cts = new CancellationTokenSource();
        c.SetValue(DipAnimProperty, cts);
        RunDip(dip, to, cts.Token);
    }

    private static async void RunDip(ScaleTransform dip, double to, CancellationToken ct)
    {
        var pressingIn = to < 1d;
        //  Стартуем от ЖИВОГО масштаба (в т.ч. с середины идущего возврата), иначе быстрый повторный
        //  тап дал бы кадр-откат — та же дисциплина, что у индикатора навигации.
        var from = dip.ScaleX;
        var anim = new Animation
        {
            Duration = pressingIn ? InDuration : BackDuration,
            Easing = pressingIn ? InEase : BackEase,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(ScaleTransform.ScaleXProperty, from), new Setter(ScaleTransform.ScaleYProperty, from) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(ScaleTransform.ScaleXProperty, to), new Setter(ScaleTransform.ScaleYProperty, to) } },
            },
        };
        try
        {
            await anim.RunAsync(dip, ct);
        }
        catch
        {
            // отменили новым нажатием — конечное значение поставит уже он
        }
        if (!ct.IsCancellationRequested)
        {
            dip.ScaleX = dip.ScaleY = to;
        }
    }

    /// <summary>Содержимое нажимаемого контрола — то, что физически прогибается.</summary>
    private static Visual? ResolveTarget(Control c) => c switch
    {
        Border b => b.Child,
        Decorator d => d.Child,
        ContentControl { Presenter: { } p } => p,
        _ => null,
    };

    private static bool IsUsable(double scale) => !double.IsNaN(scale) && scale > 0d && scale < 1d;
}
