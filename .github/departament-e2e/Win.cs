using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace DpE2E;

internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public override string ToString() => $"({Left},{Top}) {Width}×{Height}";
}

internal sealed record WinInfo(nint Hwnd, string Class, string Title, bool Visible, Rect Rect, bool Cloaked, bool Iconic, bool Zoomed);

internal sealed record ToolbarProbe(string Where, bool Exists, int Buttons, bool Found, string? Note);

internal sealed record ProxyState(bool Enabled, string? Server, string? Bypass, string? AutoConfigUrl, int? RegProxyEnable, string? RegProxyServer)
{
    public override string ToString() =>
        $"WinHTTP: {(Enabled ? $"proxy={Server}" : "без прокси")}{(AutoConfigUrl is null ? "" : $", pac={AutoConfigUrl}")}; " +
        $"реестр: ProxyEnable={RegProxyEnable?.ToString() ?? "—"}, ProxyServer={RegProxyServer ?? "—"}";
}

/// <summary>
/// Всё, что стенд спрашивает у самой Windows: окна процесса, значок в области уведомлений (тремя
/// независимыми способами), снимки экрана, системный прокси, кеш DNS, лучший интерфейс до адреса.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win
{
    #region P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly Rect ToRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hWnd, ref POINT p);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string? cls, string? name);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    private static extern nint SendMessageTimeoutString(nint hWnd, uint msg, nint w, string l, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hdc);

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attr, out RECT value, int size);

    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern nint VirtualAllocEx(nint hProcess, nint addr, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(nint hProcess, nint addr, nuint size, uint type);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(nint hProcess, nint baseAddr, byte[] buffer, nint size, out nint read);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint h);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_CURRENT_USER_IE_PROXY_CONFIG
    {
        public int fAutoDetect;
        public nint lpszAutoConfigUrl;
        public nint lpszProxy;
        public nint lpszProxyBypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)] private static extern bool WinHttpGetIEProxyConfigForCurrentUser(ref WINHTTP_CURRENT_USER_IE_PROXY_CONFIG c);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint p);
    [DllImport("dnsapi.dll")] private static extern int DnsFlushResolverCache();
    [DllImport("iphlpapi.dll")] private static extern int GetBestInterface(uint destAddr, out uint index);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);

    private const uint SMTO_ABORTIFHUNG = 0x2;
    private const uint WM_USER = 0x400;
    private const uint TB_GETBUTTON = WM_USER + 23;
    private const uint TB_BUTTONCOUNT = WM_USER + 24;
    private const uint WM_SETTINGCHANGE = 0x1A;

    #endregion P/Invoke

    /// <summary>Координаты окон и снимков — в физических пикселях, как их видит приложение (per-monitor v2).</summary>
    public static void Prepare()
    {
        try { SetProcessDpiAwarenessContext(-4); } catch { }
        //  Шаг опроса 1–2 мс: по умолчанию Sleep(1) спит целый квант планировщика, 15,6 мс.
        try { timeBeginPeriod(1); } catch { }
    }

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    #region Окна

    public static List<WinInfo> WindowsOf(int pid)
    {
        var list = new List<WinInfo>();
        var sb = new StringBuilder(256);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == (uint)pid)
            {
                list.Add(Describe(h, sb));
            }
            return true;
        }, 0);
        return list;
    }

    public static WinInfo? Describe(nint hwnd)
    {
        return IsWindow(hwnd) ? Describe(hwnd, new StringBuilder(256)) : null;
    }

    private static WinInfo Describe(nint h, StringBuilder sb)
    {
        sb.Clear();
        GetClassName(h, sb, sb.Capacity);
        var cls = sb.ToString();
        sb.Clear();
        GetWindowText(h, sb, sb.Capacity);
        var title = sb.ToString();
        GetWindowRect(h, out var r);
        var cloaked = DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int c, sizeof(int)) == 0 && c != 0;
        return new WinInfo(h, cls, title, IsWindowVisible(h), r.ToRect(), cloaked, IsIconic(h), IsZoomed(h));
    }

    /// <summary>Главное окно приложения Avalonia: видимое, класса «Avalonia-…», самое большое.</summary>
    public static WinInfo? MainWindowOf(IEnumerable<WinInfo> windows) =>
        windows.Where(w => w.Visible && !w.Cloaked && w.Class.StartsWith("Avalonia-", StringComparison.Ordinal)
                           && w.Rect.Width >= 200 && w.Rect.Height >= 200)
               .MaxBy(w => w.Rect.Width * w.Rect.Height);

    /// <summary>
    /// Скрытое окно сообщений Avalonia: от его имени (hWnd) и с uID=1 Avalonia регистрирует первый значок
    /// в трее (Win32 TrayIconImpl: NOTIFYICONDATA.hWnd = Win32Platform.Handle, uID — порядковый номер).
    /// </summary>
    public static nint MessageWindowOf(IEnumerable<WinInfo> windows) =>
        windows.FirstOrDefault(w => w.Class.StartsWith("AvaloniaMessageWindow", StringComparison.Ordinal))?.Hwnd ?? 0;

    public static Rect? ClientRectOnScreen(nint hwnd)
    {
        if (!GetClientRect(hwnd, out var r))
        {
            return null;
        }
        var p = new POINT();
        if (!ClientToScreen(hwnd, ref p))
        {
            return null;
        }
        return new Rect(p.X, p.Y, p.X + r.Right - r.Left, p.Y + r.Bottom - r.Top);
    }

    public static Rect? ExtendedFrame(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>()) == 0 ? r.ToRect() : null;

    public static uint DpiOf(nint hwnd)
    {
        try { return GetDpiForWindow(hwnd); } catch { return 0; }
    }

    public static Rect VirtualScreen() =>
        new(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(76) + GetSystemMetrics(78), GetSystemMetrics(77) + GetSystemMetrics(79));

    public static Rect? TaskbarRect()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        return tray != 0 && GetWindowRect(tray, out var r) ? r.ToRect() : null;
    }

    #endregion Окна

    #region Трей

    /// <summary>
    /// Способ 1 — сама оболочка: Shell_NotifyIconGetRect по (hWnd, uID). S_OK значит, что Explorer знает
    /// этот значок и вернул его прямоугольник.
    /// </summary>
    public static (int Hr, Rect Rect) TrayIconRect(nint hwnd, uint uid)
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(), hWnd = hwnd, uID = uid };
        var hr = Shell_NotifyIconGetRect(ref id, out var r);
        return (hr, r.ToRect());
    }

    /// <summary>
    /// Способ 2 — классическая область уведомлений (Windows 10 / Server 2019–2022): кнопки
    /// ToolbarWindow32 в Shell_TrayWnd и в окне переполнения. Данные кнопки (TRAYDATA: hWnd, uID)
    /// лежат в памяти Explorer, поэтому читаются через буфер в его процессе. На оболочке Windows 11
    /// этих панелей нет — тогда ответ «панели нет», а не «значка нет».
    /// </summary>
    public static List<ToolbarProbe> TrayToolbars(nint hwnd, uint uid)
    {
        var result = new List<ToolbarProbe>();
        var tray = FindWindow("Shell_TrayWnd", null);
        var notify = tray == 0 ? 0 : FindWindowEx(tray, 0, "TrayNotifyWnd", null);
        var pager = notify == 0 ? 0 : FindWindowEx(notify, 0, "SysPager", null);
        var visibleTb = pager == 0 ? 0 : FindWindowEx(pager, 0, "ToolbarWindow32", null);
        var overflow = FindWindow("NotifyIconOverflowWindow", null);
        var overflowTb = overflow == 0 ? 0 : FindWindowEx(overflow, 0, "ToolbarWindow32", null);
        result.Add(ProbeToolbar("панель задач", visibleTb, hwnd, uid));
        result.Add(ProbeToolbar("переполнение", overflowTb, hwnd, uid));
        return result;
    }

    private static ToolbarProbe ProbeToolbar(string where, nint tb, nint hwnd, uint uid)
    {
        if (tb == 0)
        {
            return new ToolbarProbe(where, false, 0, false, null);
        }
        if (SendMessageTimeout(tb, TB_BUTTONCOUNT, 0, 0, SMTO_ABORTIFHUNG, 1000, out var countRes) == 0)
        {
            return new ToolbarProbe(where, true, 0, false, "TB_BUTTONCOUNT не ответил");
        }
        var count = (int)countRes;
        GetWindowThreadProcessId(tb, out var explorerPid);
        var proc = OpenProcess(0x8 | 0x10 | 0x20 | 0x400, false, explorerPid);
        if (proc == 0)
        {
            return new ToolbarProbe(where, true, count, false, $"OpenProcess(explorer) = {Marshal.GetLastPInvokeError()}");
        }
        var remote = VirtualAllocEx(proc, 0, 64, 0x3000 /* MEM_COMMIT | MEM_RESERVE */, 0x04 /* PAGE_READWRITE */);
        try
        {
            if (remote == 0)
            {
                return new ToolbarProbe(where, true, count, false, "VirtualAllocEx не удался");
            }
            var button = new byte[32];
            var trayData = new byte[16];
            for (var i = 0; i < count; i++)
            {
                if (SendMessageTimeout(tb, TB_GETBUTTON, i, remote, SMTO_ABORTIFHUNG, 1000, out var ok) == 0 || ok == 0)
                {
                    continue;
                }
                if (!ReadProcessMemory(proc, remote, button, button.Length, out _))
                {
                    continue;
                }
                //  TBBUTTON на x64: iBitmap(4) idCommand(4) fsState fsStyle bReserved[6] dwData(8) iString(8).
                var dwData = (nint)BitConverter.ToInt64(button, 16);
                if (dwData == 0 || !ReadProcessMemory(proc, dwData, trayData, trayData.Length, out _))
                {
                    continue;
                }
                var owner = (nint)BitConverter.ToInt64(trayData, 0);
                var id = BitConverter.ToUInt32(trayData, 8);
                if (owner == hwnd && id == uid)
                {
                    return new ToolbarProbe(where, true, count, true, $"кнопка {i + 1} из {count}");
                }
            }
            return new ToolbarProbe(where, true, count, false, null);
        }
        finally
        {
            if (remote != 0)
            {
                VirtualFreeEx(proc, remote, 0, 0x8000);
            }
            CloseHandle(proc);
        }
    }

    /// <summary>Перечитать настройки области уведомлений (после правки EnableAutoTray в реестре).</summary>
    public static void BroadcastTraySettingsChanged()
    {
        try { SendMessageTimeoutString(0xFFFF, WM_SETTINGCHANGE, 0, "TraySettings", SMTO_ABORTIFHUNG, 2000, out _); } catch { }
    }

    #endregion Трей

    #region Снимки экрана

    public static (byte[] Bgra, int Width, int Height)? Capture(Rect r)
    {
        if (r.IsEmpty)
        {
            return null;
        }
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bih = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = r.Width,
            biHeight = -r.Height, // сверху вниз
            biPlanes = 1,
            biBitCount = 32,
        };
        var bmp = CreateDIBSection(screen, ref bih, 0, out var bits, 0, 0);
        try
        {
            if (bmp == 0 || bits == 0)
            {
                return null;
            }
            var old = SelectObject(mem, bmp);
            //  CAPTUREBLT — вместе с многослойными окнами; экранный DC отдаёт уже собранный DWM кадр.
            BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000);
            SelectObject(mem, old);
            var data = new byte[r.Width * r.Height * 4];
            Marshal.Copy(bits, data, 0, data.Length);
            return (data, r.Width, r.Height);
        }
        finally
        {
            if (bmp != 0)
            {
                DeleteObject(bmp);
            }
            DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    public static bool SaveScreenshot(string path, Rect? region = null)
    {
        try
        {
            var r = region ?? VirtualScreen();
            if (Capture(r) is not { } shot)
            {
                return false;
            }
            Png.Save(path, shot.Bgra, shot.Width, shot.Height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion Снимки экрана

    #region Сеть

    public static ProxyState ReadSystemProxy()
    {
        var cfg = new WINHTTP_CURRENT_USER_IE_PROXY_CONFIG();
        string? server = null, bypass = null, pac = null;
        if (WinHttpGetIEProxyConfigForCurrentUser(ref cfg))
        {
            server = TakeString(cfg.lpszProxy);
            bypass = TakeString(cfg.lpszProxyBypass);
            pac = TakeString(cfg.lpszAutoConfigUrl);
        }
        int? regEnable = null;
        string? regServer = null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            regEnable = key?.GetValue("ProxyEnable") as int?;
            regServer = key?.GetValue("ProxyServer") as string;
        }
        catch
        {
        }
        return new ProxyState(!string.IsNullOrEmpty(server), server, bypass, pac, regEnable, regServer);
    }

    private static string? TakeString(nint p)
    {
        if (p == 0)
        {
            return null;
        }
        var s = Marshal.PtrToStringUni(p);
        GlobalFree(p);
        return s;
    }

    public static bool FlushDnsCache()
    {
        try { return DnsFlushResolverCache() != 0; } catch { return false; }
    }

    /// <summary>Через какой интерфейс Windows сейчас отправит пакет до адреса (таблица маршрутов).</summary>
    public static NetworkInterface? BestInterfaceFor(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (GetBestInterface(BitConverter.ToUInt32(bytes, 0), out var index) != 0)
        {
            return null;
        }
        return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
        {
            try { return n.GetIPProperties().GetIPv4Properties()?.Index == (int)index; } catch { return false; }
        });
    }

    #endregion Сеть
}
