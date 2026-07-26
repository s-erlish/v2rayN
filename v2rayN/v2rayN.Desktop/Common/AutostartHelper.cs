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

    /// <summary>
    /// Ключ, которым «Диспетчер задач → Автозагрузка» помечает запись включённой или ВЫКЛЮЧЕННОЙ.
    /// Пока здесь лежит флаг «выключено», Windows игнорирует Run-значение — сколько его ни перезаписывай.
    /// Это и есть самая частая причина «включил автозапуск, а он не работает».
    /// </summary>
    private const string StartupApprovedPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Первый байт значения в StartupApproved: чётный = включено, нечётный = выключено пользователем.</summary>
    private const byte ApprovedEnabledFlag = 0x02;

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
            // Снимаем возможный флаг «выключено» из «Диспетчера задач → Автозагрузка»: одного
            // Run-значения мало, если пользователь (или чистильщик автозагрузки) когда-то отключил
            // запись — Windows запоминает это отдельно и продолжает игнорировать значение.
            ClearStartupApprovedFlag();
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

    /// <summary>
    /// Работает ли автозапуск НА САМОМ ДЕЛЕ: есть непустое Run-значение «departament» И «Диспетчер
    /// задач» не пометил его выключенным. Именно это, а не сохранённый флаг в конфиге, должен
    /// показывать переключатель в настройках — иначе он врёт.
    /// </summary>
    public static bool IsEnabled()
    {
        if (!Utils.IsWindows())
        {
            return false;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if ((key?.GetValue(RunValueName) as string).IsNullOrEmpty())
            {
                return false;
            }
            return !IsDisabledInTaskManager();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Пометил ли пользователь запись выключенной в «Диспетчере задач → Автозагрузка».
    /// Значение — двоичное; выключено кодируется нечётным первым байтом.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsDisabledInTaskManager()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedPath);
            if (key?.GetValue(RunValueName) is not byte[] { Length: > 0 } flag)
            {
                return false;
            }
            return (flag[0] & 1) == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Вернуть записи состояние «включено» в «Диспетчере задач → Автозагрузка».</summary>
    [SupportedOSPlatform("windows")]
    private static void ClearStartupApprovedFlag()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedPath, writable: true);
            if (key is null || key.GetValue(RunValueName) is not byte[] { Length: > 0 } flag)
            {
                return;
            }
            if ((flag[0] & 1) == 0)
            {
                return;
            }
            var enabled = new byte[flag.Length];
            enabled[0] = ApprovedEnabledFlag;
            key.SetValue(RunValueName, enabled, RegistryValueKind.Binary);
        }
        catch
        {
            // Диспетчер задач переживёт отсутствие флага; терять из-за него включение автозапуска нельзя.
        }
    }

    /// <summary>Синхронизировать реестр с флагом: enabled → Set(), иначе Remove().</summary>
    public static bool Apply(bool enabled) => enabled ? Set() : Remove();

    /// <summary>
    /// Привести реальное состояние к сохранённому намерению при старте приложения.
    ///
    /// Раньше настройка и реестр могли разойтись навсегда: конфиг говорил «автозапуск включён», а
    /// Run-значения не было — потому что его писала прежняя реализация под другим именем, или под
    /// правами администратора вместо реестра создавалась ЗАДАЧА планировщика (её «Диспетчер задач →
    /// Автозагрузка» не показывает вовсе), или пользователь отключил запись, или конфиг приехал из
    /// резервной копии. Переключатель при этом стоял «включено», а приложение не стартовало,
    /// и починить это из интерфейса было нельзя.
    ///
    /// Возвращает фактическое состояние после сверки.
    /// </summary>
    public static bool Reconcile(bool intended)
    {
        if (!Utils.IsWindows())
        {
            return intended;
        }
        if (intended == IsEnabled())
        {
            return intended;
        }
        Apply(intended);
        return IsEnabled();
    }
}
