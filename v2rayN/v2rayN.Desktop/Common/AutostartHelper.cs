using Microsoft.Win32;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Запуск при загрузке (Windows). Пишет per-user значение в
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> с именем «departament» → путь к exe,
/// так что departament стартует при входе пользователя в систему. Никаких прав администратора —
/// ровно тот же per-user реестровый подход, что и регистрация схемы depv:// в UrlSchemesPage.
///
/// Всё Windows-огорожено (<see cref="Utils.IsWindows"/>): на Linux/macOS автозапуск живёт в
/// файлах autostart/LaunchAgent (см. <c>AutoStartupHandler</c>), а этот хелпер там — no-op.
/// </summary>
public static class AutostartHelper
{
    // Человекочитаемое имя Run-значения (по просьбе владельца — «departament», а не хэш).
    private const string RunValueName = "departament";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Включить автозапуск: добавляет Run-значение «departament» → "путь\к\exe".
    /// Возвращает false, если платформа не Windows или путь к exe определить не удалось.</summary>
    public static bool Set()
    {
        if (!Utils.IsWindows())
        {
            return false;
        }
        try
        {
            var exe = Environment.ProcessPath;
            if (exe.IsNullOrEmpty())
            {
                return false;
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            // Кавычки вокруг пути — на случай пробелов в каталоге установки.
            key.SetValue(RunValueName, $"\"{exe}\"");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Выключить автозапуск: удаляет Run-значение «departament» (если есть).</summary>
    public static bool Remove()
    {
        if (!Utils.IsWindows())
        {
            return false;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Включён ли автозапуск сейчас (есть непустое Run-значение «departament»).</summary>
    public static bool IsEnabled()
    {
        if (!Utils.IsWindows())
        {
            return false;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return (key?.GetValue(RunValueName) as string).IsNotEmpty();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Синхронизировать реестр с флагом: enabled → Set(), иначе Remove().</summary>
    public static bool Apply(bool enabled) => enabled ? Set() : Remove();
}
