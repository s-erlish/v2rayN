using Avalonia.Data.Converters;

namespace v2rayN.Desktop.Converters;

/// <summary>
/// Раскрашивает значение задержки (пинга) ТЕМА-зависимыми токенами, а не жёсткими зелёным/красным:
/// хорошо (>0…≤500 мс) = <c>Brush.Green</c>, плохо/таймаут (≤0 или &gt;500 мс) = <c>Brush.Red</c>.
/// Кисти резолвим из ресурсов приложения по активному <see cref="ThemeVariant"/>, поэтому цвет
/// совпадает с текущей темой (Тёмная/Светлая) и с монохромным оверлеем (в mono Brush.Green
/// перекрашен в серый connected-токен — пинг читается серым, как остальной UI). Пороговые значения
/// good/bad/timeout сохранены. Если ресурсы недоступны — защитный откат к литеральным токенам Incy.
/// </summary>
public class DelayColorConverter : IValueConverter
{
    // Литеральный откат = базовые токены Incy (Dark), если ресурсы темы почему-то не резолвятся.
    private static readonly IBrush _goodFallback = new SolidColorBrush(Color.Parse("#22C55E")); // Brush.Green
    private static readonly IBrush _badFallback = new SolidColorBrush(Color.Parse("#F04452"));  // Brush.Red

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var delay = value.ToString().ToInt();

        // Пороги: таймаут/ошибка (≤0) и «медленно» (>500) → плохо (Red); (0…500] → хорошо (Green).
        var good = delay is > 0 and <= 500;
        return good ? Resolve("Brush.Green", _goodFallback) : Resolve("Brush.Red", _badFallback);
    }

    // Резолвит кисть темы по активному ThemeVariant (учитывает mono-оверлей). Защитно: любой сбой
    // доступа к ресурсам → литеральный откат, чтобы конвертер никогда не ронял привязку.
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
            // проваливаемся в откат
        }
        return fallback;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
