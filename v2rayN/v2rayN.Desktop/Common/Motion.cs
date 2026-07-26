using Avalonia.Animation.Easings;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Единый C#-каталог моушен-токенов. ЗЕРКАЛО Android-файла <c>res/values/motion.xml</c>: каждое
/// значение здесь сверено с одноимённым <c>&lt;integer&gt;</c> там, потому что «один темп на весь
/// продукт» проверяем только если обе платформы читают один список чисел, а не два похожих.
/// Одновременно зеркалит XAML-набор из <c>Assets/GlobalResources.axaml</c>: кривые = те же
/// <see cref="SplineEasing"/> контрольные точки, длительности = те же значения, что документированы
/// литералами в <c>Duration="…"</c> (XAML-компилятор Avalonia НЕ поддерживает интринсик
/// <c>x:TimeSpan</c>, поэтому C# держит длительности как <see cref="TimeSpan"/>).
///
/// Это ЕДИНСТВЕННЫЙ источник правды для C#-аниматоров (смена вкладок · кроссфейд оболочки · суб-страницы ·
/// success-момент входа · entrance chip · settle синхронизации). Раньше <c>MainWindow</c> пере-объявлял
/// <c>_easeOutQuint</c>/<c>_easeStandard</c> и магические константы длительностей — теперь они берутся
/// отсюда, чтобы XAML и C# не расходились. Дисциплина значений: ТОЛЬКО ease-out, выход быстрее входа,
/// без bounce/elastic (00-rules.md 8).
///
/// СООТВЕТСТВИЕ ANDROID (сверка по значениям, а не по названиям):
/// <list type="table">
/// <item><description><c>motion_press_in</c> 90 = <see cref="Dur.PressIn"/></description></item>
/// <item><description><c>motion_press_out</c> 160 = <see cref="Dur.PressOut"/></description></item>
/// <item><description><c>motion_hover</c> 150 = <see cref="Dur.Hover"/> (на Android — токен паритета: наведения там нет)</description></item>
/// <item><description><c>motion_state</c> 220 = <see cref="Dur.State"/></description></item>
/// <item><description><c>motion_state_exit</c> 165 = <see cref="Dur.StateExit"/></description></item>
/// <item><description><c>motion_reveal</c> 300 = <see cref="Dur.Reveal"/></description></item>
/// <item><description><c>motion_reveal_exit</c> 225 = <see cref="Dur.RevealExit"/></description></item>
/// <item><description><c>motion_slow</c> 450 = <see cref="Dur.Slow"/></description></item>
/// <item><description><c>motion_stagger</c> 40 = <see cref="Dur.Stagger"/></description></item>
/// <item><description><c>motion_emphasis</c> 600 = <see cref="Dur.Emphasis"/></description></item>
/// <item><description><c>motion_pulse</c> 1000 = <see cref="Dur.Pulse"/></description></item>
/// <item><description><c>motion_spin</c> 1100 = <see cref="Dur.Spin"/></description></item>
/// <item><description><c>input_debounce</c> 500 = <see cref="Dur.Debounce"/></description></item>
/// <item><description><c>press_scale</c> 97% = <see cref="PressScale"/></description></item>
/// </list>
///
/// ДВА РАСХОЖДЕНИЯ, оставленные СОЗНАТЕЛЬНО и названные вслух, чтобы они не притворялись паритетом:
/// <list type="bullet">
/// <item><description><see cref="Dur.Exit"/> 150 — исторический выход экрана десктопа. Android для той
/// же роли объявляет <c>motion_reveal_exit</c> 225 (75% от входа 300, как требует 8.5); 150 — это 50%.
/// Токен НЕ переопределён и НЕ удалён, потому что на нём висят живые вызовы, а тихая смена значения
/// поменяла бы темп оболочки без единой правки во вью. Новый код берёт <see cref="Dur.RevealExit"/>;
/// <see cref="Dur.Exit"/> — долг вывода.</description></item>
/// <item><description><see cref="Dur.Shell"/> 200 — кроссфейд 3-путёвого оверлея оболочки. На Android
/// пары нет: там нет самой оболочки с оверлеем. Разрешённая асимметрия платформы, а не дрейф.</description></item>
/// </list>
///
/// ЧТЕНИЕ РЕЖИМА ДВИЖЕНИЯ: аниматор обязан спрашивать <see cref="MotionState.IsLite"/> В МОМЕНТ
/// ВОСПРОИЗВЕДЕНИЯ (см. <see cref="Play"/>), а НЕ один раз в конструкторе. Прочитанный в конструкторе
/// флаг — это ровно тот баг, ради которого написан <see cref="MotionState"/>: переключатель
/// «Облегчённый режим» менял сохранённое значение, а щит продолжал крутиться до перезапуска.
/// </summary>
public static class Motion
{
    /// <summary>Длительности единой шкалы темпа (мс). Названия 1:1 с таблицей литералов в GlobalResources.</summary>
    public static class Dur
    {
        /// <summary>0 мс — lite / reduced-motion фолбэк: анимация мгновенно снапится в конечное состояние.</summary>
        public static readonly TimeSpan Instant = TimeSpan.Zero;

        /// <summary>90 мс — палец-вниз (press-in), кривая <see cref="Ease.OutQuart"/>. Android <c>motion_press_in</c>.</summary>
        public static readonly TimeSpan PressIn = TimeSpan.FromMilliseconds(90);

        /// <summary>160 мс — отпускание / малое оседание (press-out · quick), кривая <see cref="Ease.OutQuint"/>. Android <c>motion_press_out</c>.</summary>
        public static readonly TimeSpan PressOut = TimeSpan.FromMilliseconds(160);

        /// <summary>
        /// 150 мс — наведение курсора (<c>:pointerover</c>), кривая <see cref="Ease.Standard"/>.
        /// Android держит <c>motion_hover</c> 150 как ТОКЕН ПАРИТЕТА: наведения на телефоне не
        /// существует и проектировать его там запрещено — значение живёт только чтобы две шкалы
        /// оставались одним списком чисел.
        /// </summary>
        public static readonly TimeSpan Hover = TimeSpan.FromMilliseconds(150);

        /// <summary>220 мс — смена состояния / tint-crossfade (двусторонняя), кривая <see cref="Ease.Standard"/>. Android <c>motion_state</c>.</summary>
        public static readonly TimeSpan State = TimeSpan.FromMilliseconds(220);

        /// <summary>165 мс — реверс <see cref="State"/> (75% от 220, правило «выход = 75% входа»). Android <c>motion_state_exit</c>.</summary>
        public static readonly TimeSpan StateExit = TimeSpan.FromMilliseconds(165);

        /// <summary>300 мс — раскрытие: вход экрана, entrance chip, settle синхронизации, кривая <see cref="Ease.OutQuint"/>. Android <c>motion_reveal</c>.</summary>
        public static readonly TimeSpan Reveal = TimeSpan.FromMilliseconds(300);

        /// <summary>
        /// 225 мс — реверс <see cref="Reveal"/> (75% от 300), кривая <see cref="Ease.Standard"/>.
        /// Android <c>motion_reveal_exit</c>. ЭТО канонический выход экрана / суб-страницы; новый код
        /// берёт его, а не <see cref="Exit"/>.
        /// </summary>
        public static readonly TimeSpan RevealExit = TimeSpan.FromMilliseconds(225);

        /// <summary>
        /// 150 мс — исторический выход экрана / суб-страницы десктопа, кривая <see cref="Ease.Standard"/>.
        /// РАСХОЖДЕНИЕ С ANDROID: там ту же роль несёт <c>motion_reveal_exit</c> 225 (75% от входа);
        /// 150 — это 50%. Значение оставлено живым, потому что на нём висят вызовы оболочки, и тихая
        /// правка сместила бы темп без единого изменения во вью. Новый код: <see cref="RevealExit"/>.
        /// </summary>
        public static readonly TimeSpan Exit = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// 200 мс — кроссфейд 3-путёвого оверлея оболочки, кривая <see cref="Ease.Standard"/>.
        /// Пары на Android НЕТ (там нет оболочки с оверлеем) — разрешённая асимметрия платформы.
        /// </summary>
        public static readonly TimeSpan Shell = TimeSpan.FromMilliseconds(200);

        /// <summary>450 мс — ОДИН решительный hand-off auth→home, кривая <see cref="Ease.OutExpo"/>. Android <c>motion_slow</c>.</summary>
        public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(450);

        /// <summary>600 мс — ОДИН hero-момент: connect-сонар, кривая <see cref="Ease.OutQuint"/>. Android <c>motion_emphasis</c>. Хром 600 не получает НИКОГДА.</summary>
        public static readonly TimeSpan Emphasis = TimeSpan.FromMilliseconds(600);

        /// <summary>40 мс — стаггер списка / задержка entrance chip. Android <c>motion_stagger</c>. Суммарный стаггер не превышает 400мс — значит не более 10 элементов, дальше появляются вместе.</summary>
        public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(40);

        /// <summary>
        /// 1000 мс — пульс скелета, непрозрачность 0.45↔1.0, бесконечный реверс, кривая
        /// <see cref="Ease.Standard"/>. Android <c>motion_pulse</c>. Это ОБРАТНАЯ СВЯЗЬ ЗАГРУЗКИ
        /// (её 8.1 разрешает), а не hero-момент: на 1000мс пульс МЕДЛЕННЕЕ геройских 600 и потому
        /// не спорит с ними за внимание. Заменяет внемасштабные 900мс, которые жили и на Android,
        /// и в десктопном SkeletonPulse. Под lite скелет статичен на 0.7.
        /// </summary>
        public static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(1000);

        /// <summary>
        /// 1100 мс — ОДИН оборот инлайн-дуги индикатора (20px, штрих 2, сектор 90°, круглые торцы).
        /// Android <c>motion_spin</c>. ЕДИНСТВЕННАЯ линейная анимация продукта, и это НЕ нарушение
        /// запрета 8.3: запрет касается переходов между двумя состояниями, а непрерывное вращение
        /// конечного состояния не имеет и при любом easing видимо запинается раз в оборот.
        /// </summary>
        public static readonly TimeSpan Spin = TimeSpan.FromMilliseconds(1100);

        /// <summary>
        /// 500 мс — НЕ анимация: окно повторного входа, которое делает двойное нажатие невозможным
        /// ПО УСТРОЙСТВУ там, где кнопка не закрыта командой с собственным in-flight состоянием.
        /// Android <c>input_debounce</c>. Живёт здесь потому, что это длительность, а длительности
        /// живут здесь.
        /// </summary>
        public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    }

    /// <summary>
    /// 0.97 — ЕДИНСТВЕННЫЙ масштаб нажатия во всём продукте (Android <c>@fraction/press_scale</c> 97%).
    /// Вход за <see cref="Dur.PressIn"/> 90мс по <see cref="Ease.OutQuart"/>, возврат за
    /// <see cref="Dur.PressOut"/> 160мс по <see cref="Ease.OutQuint"/>.
    /// СТРОКИ НЕ МАСШТАБИРУЮТСЯ, масштабируются ОБЪЕКТЫ: строка — срез поверхности, и её масштаб
    /// рвёт волосяные линии сверху и снизу. Строка отвечает шагом фона на ступень вверх.
    /// </summary>
    public const double PressScale = 0.97;

    /// <summary>
    /// Кривые = 1:1 с XAML <c>Ease.*</c> из GlobalResources и с <c>res/interpolator/*</c> на Android.
    /// НЕ встроенные QuarticEaseOut/QuinticEaseOut (у них другие контрольные точки). Типизированы как
    /// базовый <see cref="Easing"/>, чтобы напрямую подставляться в <c>Animation.Easing</c> и вызывать
    /// <c>.Ease(t)</c>.
    /// </summary>
    public static class Ease
    {
        /// <summary>ease_out_quart (0.25,1,0.5,1) — press-feedback, малые оседания.</summary>
        public static readonly Easing OutQuart = new SplineEasing(0.25, 1, 0.5, 1);

        /// <summary>ease_out_quint (0.22,1,0.36,1) — уверенные reveal / settle, glow, сонар.</summary>
        public static readonly Easing OutQuint = new SplineEasing(0.22, 1, 0.36, 1);

        /// <summary>ease_standard (0.2,0,0,1) — tint / crossfade, двусторонние смены состояния, реверс.</summary>
        public static readonly Easing Standard = new SplineEasing(0.2, 0, 0, 1);

        /// <summary>ease_out_expo (0.16,1,0.3,1) — самый решительный ease-out, ЗАРЕЗЕРВИРОВАН под hand-off auth→home.</summary>
        public static readonly Easing OutExpo = new SplineEasing(0.16, 1, 0.3, 1);
    }

    /// <summary>
    /// Длительность, ПРОПУЩЕННАЯ ЧЕРЕЗ РЕЖИМ ДВИЖЕНИЯ. Вызывать НА КАЖДОМ воспроизведении:
    /// <c>Duration = Motion.Play(Motion.Dur.Reveal)</c>. Под «Облегчённым режимом» вернёт
    /// <see cref="Dur.Instant"/>, и анимация окажется в конечном состоянии сразу.
    ///
    /// Смысл существования метода — сделать правильный вызов КОРОЧЕ неправильного. Прочитать
    /// <see cref="MotionState.IsLite"/> один раз в конструкторе и запомнить — это в точности тот
    /// баг, ради которого <c>MotionState</c> и написан: тумблер переключался, флаг сохранялся,
    /// а щит крутился до следующего запуска. Здесь состояние читается в момент вызова, поэтому
    /// переключение действует на СЛЕДУЮЩЕМ же кадре без подписок и без перезапуска.
    /// </summary>
    public static TimeSpan Play(TimeSpan duration) => MotionState.IsLite ? Dur.Instant : duration;

    /// <summary>
    /// Задержка стаггера для элемента с индексом <paramref name="index"/>, ограниченная так, чтобы
    /// суммарный стаггер никогда не превышал 400мс (8.6): первые десять элементов расходятся по
    /// 40мс, остальные появляются вместе с десятым. Под lite задержки нет вовсе.
    /// </summary>
    public static TimeSpan StaggerFor(int index)
    {
        if (MotionState.IsLite || index <= 0)
        {
            return Dur.Instant;
        }
        var capped = index > 10 ? 10 : index;
        return TimeSpan.FromTicks(Dur.Stagger.Ticks * capped);
    }
}
