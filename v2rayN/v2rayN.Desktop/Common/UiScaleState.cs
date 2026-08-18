namespace v2rayN.Desktop.Common;

/// <summary>
/// Runtime «Масштаб интерфейса» broadcast — единый источник истины по in-app zoom всего интерфейса,
/// чтобы изменение из настроек ИЛИ по горячим клавишам применялось МГНОВЕННО без перезапуска (тот же
/// приём, что <see cref="MotionState"/> для reduced-motion).
///
/// Фактора ДВА, и они перемножаются:
///   • <see cref="Auto"/> — подбор под МОНИТОР (см. <see cref="ResolveAuto"/>). Не выбирается человеком,
///     считается на старте из разрешения и системного масштаба экрана, на котором открывается окно;
///   • <see cref="Current"/> — «Масштаб интерфейса» из настроек (100% / 110% / 125% / 150%) и горячих
///     клавиш Ctrl +/Ctrl −/Ctrl 0. ЭТО и есть то, что персистится в <c>UiItem.UiScale</c> и переживает
///     перезапуск; 100% значит «как монитор просит», а не «1:1 с пикселем».
/// <see cref="Effective"/> = Auto × Current — фактор, который MainWindow кладёт в корневой ScaleTransform.
///
/// <see cref="MainWindow"/> оборачивает корневой контент в LayoutTransformControl со ScaleTransform и
/// подписывается на <see cref="Changed"/>, применяя фактор к трансформу + мин-размеру окна + брейкпоинту
/// раскладки (в координатах КОНТЕНТА). <see cref="SettingsViewModel"/> показывает и меняет ТОЛЬКО
/// <see cref="Current"/> — она не знает про монитор и знать не должна. Обе стороны читают
/// <see cref="Current"/> при позднем подключении; значение всегда уже КЛАМПНУТО в [<see cref="Min"/>,
/// <see cref="Max"/>].
/// </summary>
public static class UiScaleState
{
    // ==================== Масштаб и раскладка (tokens.md «Размеры окна и масштаб») ====================
    // Раскладка приложения ОДНА — логические ~1366×768. На больших мониторах пропорции НЕ пересчитываются,
    // меняется только масштаб:
    //
    //   монитор 1366×768 → 1.00 → логические 1366×768
    //   монитор 1920×1080 → 1.40 → логические 1371×771
    //   монитор 2560×1440 → 1.85 → логические 1384×778
    //
    // Смысл таблицы не в пяти зашитых пресетах, а в ПРАВИЛЕ: масштаб подбирается так, чтобы логический
    // размер держался около 1366×768. Отсюда формула ResolveAuto — она даёт ровно эти три числа и
    // осмысленно продолжается на любое другое разрешение (3840×2160 → 2.80, 1024×768 → 1.00).
    //
    // Пороги раскладки MainWindow читает в КООРДИНАТАХ КОНТЕНТА (Bounds.Width / Effective): при факторе
    // 1.85 окно шириной 777 физ. DIP даёт контенту 420 логических пикселей — ровно порог узкого режима.
    // Мин-размеры окна масштабируются тем же фактором (MainWindow.ApplyUiScaleToWindow), иначе на высоком
    // масштабе контенту не хватает места и он клиппится.

    /// <summary>Границы ПОЛЬЗОВАТЕЛЬСКОГО фактора. Шаг — для горячих клавиш Ctrl +/Ctrl −.</summary>
    public const double Min = 0.8;

    public const double Max = 2.0;
    public const double Step = 0.1;
    public const double Default = 1.0;

    // ==================== Подбор под монитор ====================
    // Целевая логическая раскладка (tokens.md). Ниже 1.0 не опускаемся НИКОГДА: на маленьком экране
    // раскладка и так уходит в компактную/узкую по живой ширине окна, а мельчить текст — только вредить.
    private const double TargetLogicalWidth = 1366.0;
    private const double TargetLogicalHeight = 768.0;

    /// <summary>Шаг округления подбора: 0.05 — это ровно 1.40 на 1920 и 1.85 на 2560 из tokens.md.</summary>
    private const double AutoStep = 0.05;

    private const double AutoMin = 1.0;
    private const double AutoMax = 3.0;

    /// <summary>Границы ИТОГОВОГО фактора (Auto × Current): 4K при 150% из настроек = 2.8 × 1.5.</summary>
    private const double EffectiveMin = 0.5;

    private const double EffectiveMax = 4.0;

    private static double _current = Default;
    private static double _auto = 1.0;

    /// <summary>Пользовательский фактор из настроек/клавиш (персистится), всегда в [Min, Max].</summary>
    public static double Current => _current;

    /// <summary>Фактор подбора под монитор (не персистится — пересчитывается на каждом старте).</summary>
    public static double Auto => _auto;

    /// <summary>Фактор, который реально уходит в корневой ScaleTransform окна.</summary>
    public static double Effective => ClampEffective(_auto * _current);

    /// <summary>Срабатывает при реальном изменении ПОЛЬЗОВАТЕЛЬСКОГО фактора; аргумент = новое значение.</summary>
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

    private static double ClampEffective(double value)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return Default;
        }
        return Math.Clamp(value, EffectiveMin, EffectiveMax);
    }

    /// <summary>Засеять кэш при старте БЕЗ уведомления (первичная загрузка из конфига).</summary>
    public static void Initialize(double value) => _current = Clamp(value);

    /// <summary>
    /// Подбор масштаба под МОНИТОР по его разрешению и системному масштабу.
    ///
    /// <paramref name="osScaling"/> — масштаб, который ОС УЖЕ применила (Avalonia Screen.Scaling: 1.0 при
    /// 100%, 1.5 при 150%). Делим на него, поэтому системный масштаб НЕ умножается на наш дважды: на
    /// 2560×1440 при 150% ОС отдаёт приложению 1706×960 логических DIP, подбор даёт 1.25, итог по пикселям
    /// 1.5 × 1.25 = 1.875 — то же, что 1.85 при 100%.
    ///
    /// Берём МИНИМУМ из отношений по ширине и по высоте, иначе ультраширокий монитор (3440×1440) раздул бы
    /// интерфейс так, что по вертикали он перестал бы помещаться.
    /// </summary>
    public static double ResolveAuto(double screenPixelWidth, double screenPixelHeight, double osScaling)
    {
        if (osScaling <= 0 || double.IsNaN(osScaling))
        {
            osScaling = 1.0;
        }

        var dipWidth = screenPixelWidth / osScaling;
        var dipHeight = screenPixelHeight / osScaling;
        if (dipWidth <= 0 || dipHeight <= 0 || double.IsNaN(dipWidth) || double.IsNaN(dipHeight))
        {
            return AutoMin;
        }

        var raw = Math.Min(dipWidth / TargetLogicalWidth, dipHeight / TargetLogicalHeight);
        var stepped = Math.Round(raw / AutoStep, MidpointRounding.AwayFromZero) * AutoStep;
        return Math.Clamp(stepped, AutoMin, AutoMax);
    }

    /// <summary>
    /// Записать подобранный под монитор фактор. Возвращает true, если он изменился (значит окну надо
    /// переприменить трансформ/мин-размеры). Событие НЕ шлёт: <see cref="Changed"/> — про строку настроек,
    /// а подбор под монитор в ней не отражается и отражаться не должен.
    /// </summary>
    public static bool SetAuto(double value)
    {
        var v = double.IsNaN(value) || value <= 0 ? AutoMin : Math.Clamp(value, AutoMin, AutoMax);
        if (Math.Abs(_auto - v) < 0.0001)
        {
            return false;
        }
        _auto = v;
        return true;
    }

    /// <summary>Передать новый ПОЛЬЗОВАТЕЛЬСКИЙ фактор; уведомляет подписчиков только при реальном изменении.</summary>
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
