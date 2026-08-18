namespace v2rayN.Desktop.Common;

/// <summary>
/// Runtime «Масштаб интерфейса» broadcast — единый источник истины по in-app zoom всего интерфейса,
/// чтобы изменение из настроек ИЛИ по горячим клавишам применялось МГНОВЕННО без перезапуска (тот же
/// приём, что <see cref="MotionState"/> для reduced-motion).
///
/// Слой НЕЗАВИСИМ от OS DPI: физический масштаб задаёт ОС (per-monitor-v2), а этот фактор — дополнительный
/// zoom самого приложения. На 4K-мониторе при 100% OS-масштабе интерфейс физически крошечный — фактор > 1
/// увеличивает его целиком (layout+рендер), фактор &lt; 1 уменьшает.
///
/// <see cref="MainWindow"/> оборачивает корневой контент в LayoutTransformControl со ScaleTransform и
/// подписывается на <see cref="Changed"/>, применяя фактор к трансформу + мин-размеру окна + брейкпоинту
/// раскладки (760px в координатах КОНТЕНТА). <see cref="SettingsViewModel"/> показывает текущий % и толкает
/// новые значения сюда. Обе стороны читают <see cref="Current"/> при позднем подключении; значение всегда
/// уже КЛАМПНУТО в [<see cref="Min"/>, <see cref="Max"/>].
/// </summary>
public static class UiScaleState
{
    // ==================== Масштаб и раскладка (tokens.md «Размеры окна и масштаб») ====================
    // Раскладка приложения ОДНА — логические ~1366×768. На больших мониторах её увеличивает СИСТЕМНЫЙ
    // масштаб (1920×1080 → 1.4 → логические 1371×771; 2560×1440 → 1.85 → 1384×778), а пропорции не
    // пересчитываются: 1371 и 1384 попадают в тот же диапазон широкой раскладки, что и 1366, поэтому
    // на 1920 и 2560 экран совпадает с 1366×768 — только крупнее. Никакого отдельного брейкпоинта на
    // большие ширины нет и быть не должно.
    //
    // ЭТОТ фактор — НЕ системный масштаб, а дополнительный in-app zoom поверх него (для 4K при 100%
    // системном масштабе, где интерфейс физически крошечный). Поэтому пороги раскладки MainWindow
    // читает в КООРДИНАТАХ КОНТЕНТА (Bounds.Width / фактор): при zoom 1.5 окно 1366 даёт контенту 911
    // логических пикселей, и раскладка честно становится компактной — места действительно меньше.
    // Мин-размеры окна масштабируются тем же фактором (MainWindow.ApplyUiScaleToWindow), иначе на
    // высоком zoom контенту не хватает места и он клиппится.

    /// <summary>Границы in-app zoom. Шаг — для горячих клавиш Ctrl +/Ctrl −.</summary>
    public const double Min = 0.8;

    public const double Max = 2.0;
    public const double Step = 0.1;
    public const double Default = 1.0;

    private static double _current = Default;

    /// <summary>Текущий фактор масштаба (последнее переданное значение, всегда в [Min, Max]).</summary>
    public static double Current => _current;

    /// <summary>Срабатывает при реальном изменении фактора; аргумент = новое значение.</summary>
    public static event EventHandler<double>? Changed;

    /// <summary>Клампит любой ввод (в т.ч. 0 / NaN из старого или битого конфига) в допустимый диапазон.</summary>
    public static double Clamp(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return Default;
        }
        return Math.Clamp(value, Min, Max);
    }

    /// <summary>Засеять кэш при старте БЕЗ уведомления (первичная загрузка из конфига).</summary>
    public static void Initialize(double value) => _current = Clamp(value);

    /// <summary>Передать новый фактор; уведомляет подписчиков только при реальном изменении.</summary>
    public static void Set(double value)
    {
        var v = Clamp(value);
        if (Math.Abs(_current - v) < 0.0001)
        {
            return;
        }
        _current = v;
        Changed?.Invoke(null, v);
    }
}
