using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DpE2E;

internal sealed class Options
{
    public string App { get; set; } = "";
    public string ServerXray { get; set; } = "";
    public string Out { get; set; } = "e2e-out";
    public string Work { get; set; } = "";
    public HashSet<string> Phases { get; set; } = ["startup", "tray", "proxy", "tun", "custom", "update", "installer"];
    public int WebPort { get; set; } = 18080;
    public int ServerPort { get; set; } = 24443;
    public int DestPort { get; set; } = 24444;
    public int ClientPort { get; set; } = 31080;
    public int LocalPort { get; set; } = 10808;
    public string? ServerHost { get; set; }
    public string Target1 { get; set; } = "https://www.gstatic.com/generate_204";
    public string Target2 { get; set; } = "https://www.example.com/";
    public bool NoBind { get; set; }
    public string? XrayCandidate { get; set; }

    //  Чистая копия сборки (как её выложил branch build): из неё собирается пакет самообновления и с ней
    //  сверяется установленное. Запуски идут из --app, куда программа пишет свои настройки и журналы.
    public string? Dist { get; set; }
    public string? ExpectedVersion { get; set; }
    public string? ReleaseZip { get; set; }
    public string? ReleaseSha256 { get; set; }
    public string? Setup { get; set; }

    //  Копии установщика без skipifsilent (шаг workflow): с «runascurrentuser shellexec», как в ветке,
    //  и без них — контроль, что проверка запуска в конце установки ловит ошибку 740.
    public string? SetupPostinstall { get; set; }
    public string? SetupBaseline { get; set; }
    public bool DefenderRealtime { get; set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]}: нет значения");
            switch (args[i])
            {
                case "--app": o.App = Next(); break;
                case "--server-xray": o.ServerXray = Next(); break;
                case "--out": o.Out = Next(); break;
                case "--work": o.Work = Next(); break;
                case "--phases": o.Phases = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(); break;
                case "--web-port": o.WebPort = int.Parse(Next()); break;
                case "--server-port": o.ServerPort = int.Parse(Next()); break;
                case "--dest-port": o.DestPort = int.Parse(Next()); break;
                case "--client-port": o.ClientPort = int.Parse(Next()); break;
                case "--local-port": o.LocalPort = int.Parse(Next()); break;
                case "--server-host": o.ServerHost = Next(); break;
                case "--target1": o.Target1 = Next(); break;
                case "--target2": o.Target2 = Next(); break;
                case "--no-bind": o.NoBind = true; break;
                case "--xray-candidate": o.XrayCandidate = Next(); break;
                case "--dist": o.Dist = Path.GetFullPath(Next()); break;
                case "--expected-version": o.ExpectedVersion = Next() is { Length: > 0 } v ? v : null; break;
                case "--release-zip": o.ReleaseZip = Next() is { Length: > 0 } z ? Path.GetFullPath(z) : null; break;
                case "--release-sha256": o.ReleaseSha256 = Next() is { Length: > 0 } h ? Path.GetFullPath(h) : null; break;
                case "--setup": o.Setup = Next() is { Length: > 0 } x ? Path.GetFullPath(x) : null; break;
                case "--setup-postinstall": o.SetupPostinstall = Next() is { Length: > 0 } sp ? Path.GetFullPath(sp) : null; break;
                case "--setup-baseline": o.SetupBaseline = Next() is { Length: > 0 } sb ? Path.GetFullPath(sb) : null; break;
                case "--defender-realtime": o.DefenderRealtime = true; break;
                default: throw new ArgumentException($"неизвестный аргумент {args[i]}");
            }
        }
        if (o.App.Length == 0 || o.ServerXray.Length == 0)
        {
            throw new ArgumentException("нужны --app <папка сборки> и --server-xray <xray для тестового сервера>");
        }
        o.Out = Path.GetFullPath(o.Out);
        o.Work = Path.GetFullPath(o.Work.Length > 0 ? o.Work : Path.Combine(o.Out, "work"));
        return o;
    }
}

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Options o;
        try
        {
            o = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        Directory.CreateDirectory(o.Out);
        Directory.CreateDirectory(o.Work);
        Log.To(Path.Combine(o.Out, "harness.log"));
        var report = new Report();
        var e2e = new E2E(o, report);
        try
        {
            e2e.Run();
        }
        catch (Exception ex)
        {
            report.Add("harness", "Стенд").Set(Status.Fail, "стенд упал: " + ex);
        }
        finally
        {
            e2e.Cleanup();
            report.Write(o.Out);
        }
        var fails = report.Checks.Count(c => c.FailsBuild);
        Log.Info(fails == 0 ? "Отказов нет." : $"Отказов: {fails}.");
        return fails == 0 ? 0 : 1;
    }
}

internal sealed partial class E2E(Options o, Report report)
{
    private readonly AppDriver _app = new(o.App);
    private readonly string _shots = Path.Combine(o.Out, "screens");
    private readonly string _timelines = Path.Combine(o.Out, "timelines");
    private readonly string _serverDir = Path.Combine(o.Out, "server");
    private SubServer? _sub;
    private CoreProcess? _server;
    private TailFile? _access;
    private string _host = "";
    private bool _hostIsDomain;
    private bool _serverOk;
    private bool _serverBound = true;
    private readonly List<string> _directDns = [];
    private int _defaultSysProxyType;
    private ProfileRow? _vless;
    private ProfileRow? _custom;

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    private static bool IsWin => OperatingSystem.IsWindows();

    private static string HostPort(string url)
    {
        var u = new Uri(url);
        return $"{u.Host}:{u.Port}";
    }

    public void Run()
    {
        Directory.CreateDirectory(_shots);
        Directory.CreateDirectory(_timelines);
        Directory.CreateDirectory(_serverDir);
        if (IsWin)
        {
            Win.Prepare();
        }
        DescribeMachine();
        //  Стенд и первый запуск нужны всем фазам, кроме самообновления (оно работает с копиями сборки).
        if (o.Phases.Overlaps(["startup", "tray", "proxy", "tun", "custom", "installer"]))
        {
            Phase("стенд", StartFixtures);
            Phase("запуск", PhaseStartup);
        }
        if (o.Phases.Contains("tray"))
        {
            Phase("трей", PhaseTrayCycle);
        }
        if (o.Phases.Contains("proxy"))
        {
            Phase("системный прокси", PhaseProxy);
        }
        if (o.Phases.Contains("tun"))
        {
            Phase("TUN, обычный узел", () => PhaseTun(customNode: false));
        }
        if (o.Phases.Contains("custom"))
        {
            Phase("TUN, узел Custom", () => PhaseTun(customNode: true));
        }
        if (o.XrayCandidate is not null && o.Phases.Contains("tun"))
        {
            Phase("TUN, обычный узел с другим Xray", PhaseXrayCandidate);
        }
        //  Последними: обе фазы запускают программу из других папок и трогают Defender, запись в
        //  «Приложениях», автозапуск — ничто из этого не должно попасть в замеры первого запуска.
        if (o.Phases.Contains("update"))
        {
            Phase("самообновление", PhaseUpdate);
        }
        if (o.Phases.Contains("installer"))
        {
            Phase("установщик", PhaseInstaller);
        }
    }

    /// <summary>Упавшая фаза — отдельный отказ с причиной; следующие фазы всё равно идут.</summary>
    private void Phase(string name, Action body)
    {
        Log.Info($"── фаза: {name}");
        try
        {
            body();
        }
        catch (Exception ex)
        {
            report.Add("phase." + name, $"Фаза «{name}» не доработала").Set(Status.Fail, ex.GetType().Name + ": " + ex.Message);
            Log.Info(ex.ToString());
            try { _app.KillEverything(); } catch { }
        }
    }

    #region Машина и стенд

    private void DescribeMachine()
    {
        var env = report.Environment;
        env.Add($"ОС: {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}");
        if (IsWin)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                env.Add($"Windows: {k?.GetValue("ProductName")} {k?.GetValue("DisplayVersion")} (сборка {k?.GetValue("CurrentBuild")}.{k?.GetValue("UBR")}), {k?.GetValue("EditionID")}");
            }
            catch
            {
            }
            env.Add($"Права администратора у стенда: {(Win.IsAdmin() ? "да" : "НЕТ")}");
            var (lua, consent) = UacState();
            env.Add($"UAC: EnableLUA={lua?.ToString() ?? "—"}, ConsentPromptBehaviorAdmin={consent?.ToString() ?? "—"} (0 — повышение без запроса)");
            var explorers = Process.GetProcessesByName("explorer");
            env.Add($"Explorer: {(explorers.Length > 0 ? $"запущен (pid {string.Join(", ", explorers.Select(p => p.Id))})" : "НЕ запущен")}; панель задач: {Win.TaskbarRect()?.ToString() ?? "нет Shell_TrayWnd"}");
            env.Add($"Экран (виртуальный): {Win.VirtualScreen()}");
            if (Win.BestInterfaceFor(IPAddress.Parse("8.8.8.8")) is { } nic)
            {
                env.Add($"Выход в интернет: {Net.DescribeAdapter(nic)}");
            }
            //  Новые значки Windows по умолчанию прячет в переполнение. «Показывать все значки» (Windows 10)
            //  делает значок видимым на панели — так его видно на снимке и в UIA без открытия переполнения.
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer");
                var before = k.GetValue("EnableAutoTray");
                k.SetValue("EnableAutoTray", 0, Microsoft.Win32.RegistryValueKind.DWord);
                Win.BroadcastTraySettingsChanged();
                env.Add($"EnableAutoTray: было {before ?? "не задано"}, поставлено 0 (показывать все значки)");
            }
            catch (Exception ex)
            {
                env.Add("EnableAutoTray не записан: " + ex.Message);
            }
            env.Add($"Системный прокси до проверок: {Win.ReadSystemProxy()}");
        }
        if (File.Exists(_app.ExePath))
        {
            var v = FileVersionInfo.GetVersionInfo(_app.ExePath);
            env.Add($"Приложение: {_app.ExePath}, версия файла {v.FileVersion}, продукт {v.ProductVersion}, {new FileInfo(_app.ExePath).Length / 1048576.0:F1} МБ");
        }
        else
        {
            env.Add($"Приложение НЕ найдено: {_app.ExePath}");
        }
        foreach (var core in new[] { "xray", "sing-box" })
        {
            if (_app.CoreExe(core) is { } exe)
            {
                var (_, so, _, _) = Net.Run(exe, ["version"], TimeSpan.FromSeconds(15));
                env.Add($"Ядро в поставке: {Path.GetRelativePath(_app.Dir, exe)} — {so.Split('\n')[0].Trim()}");
            }
            else
            {
                env.Add($"Ядро {core} в поставке НЕ найдено");
            }
        }
        var (_, sv, _, _) = Net.Run(o.ServerXray, ["version"], TimeSpan.FromSeconds(15));
        env.Add($"Тестовый сервер: {sv.Split('\n')[0].Trim()}");
        foreach (var e in env)
        {
            Log.Info(e);
        }
    }

    private void StartFixtures()
    {
        var check = report.Add("fixture.server", "Стенд: тестовый сервер VLESS+REALITY (Xray 25.9.11) пропускает трафик");

        //  Адрес узла — домен, который публичный DNS разрешает в 127.0.0.1: тогда в режиме TUN есть что
        //  разрешать (и это разрешение не должно уйти в туннель), а сам сервер живёт на этой же машине.
        var candidates = o.ServerHost is { } forced ? [forced] : new[] { "vpn-e2e.localtest.me", "127.0.0.1.nip.io", "127-0-0-1.sslip.io" };
        foreach (var c in candidates)
        {
            var sys = Net.ResolveSystem(c);
            var line = $"{c}: система → [{string.Join(", ", sys)}]";
            Log.Info("Адрес сервера-кандидат " + line);
            check.Evidence.Add(line);
            if (sys.Contains("127.0.0.1"))
            {
                _host = c;
                break;
            }
        }
        if (_host.Length == 0)
        {
            _host = "127.0.0.1";
            report.Notes.Add("Ни один домен-кандидат не разрешился в 127.0.0.1: узлы смотрят на 127.0.0.1, и проверки «домен сервера мимо туннеля» не наблюдаемы.");
        }
        _hostIsDomain = !IPAddress.TryParse(_host, out _);

        //  Подписки раздаются независимо от сервера: импорт проверяется и без него.
        var vlessLink = Fixtures.VlessLink(_host, o.ServerPort);
        var customJson = Fixtures.CustomSubscription(_host, o.ServerPort);
        var userInfo = new Dictionary<string, string>
        {
            ["profile-title"] = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("E2E провайдер")),
            ["subscription-userinfo"] = "upload=0; download=1073741824; total=107374182400; expire=1893456000",
        };
        _sub = new SubServer(o.WebPort, path =>
        {
            if (path.StartsWith("/subjson", StringComparison.Ordinal))
            {
                return new SubServer.Response(200, "application/json", Encoding.UTF8.GetBytes(customJson), userInfo);
            }
            if (path.StartsWith("/sub", StringComparison.Ordinal))
            {
                return new SubServer.Response(200, "text/plain", Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(vlessLink + "\n"))), userInfo);
            }
            if (path.StartsWith("/probe", StringComparison.Ordinal))
            {
                return new SubServer.Response(200, "text/plain", Encoding.ASCII.GetBytes("E2E-OK " + path));
            }
            return new SubServer.Response(404, "text/plain", Encoding.ASCII.GetBytes("not found"));
        }, Path.Combine(o.Out, "web.log"));
        _sub.Start();
        Log.Info($"Подписки: http://127.0.0.1:{o.WebPort}/sub (VLESS {_host}:{o.ServerPort}), /subjson (XRAY_JSON)");

        //  Цели запросов сервер разрешает сам заранее (hosts): в режиме TUN его собственный DNS не нужен.
        var hosts = new Dictionary<string, string>();
        foreach (var url in new[] { o.Target1, o.Target2 })
        {
            var h = new Uri(url).Host;
            if (!IPAddress.TryParse(h, out _) && Net.ResolveSystem(h).FirstOrDefault() is { } ip)
            {
                hosts[h] = ip;
            }
        }

        string? bind = null;
        if (IsWin && !o.NoBind)
        {
            bind = Win.BestInterfaceFor(IPAddress.Parse("8.8.8.8"))?.Name;
        }

        var (certCode, certJson, certErr, _) = Net.Run(o.ServerXray, ["tls", "cert", "--domain=" + Fixtures.RealitySni, "--expire=48h"], TimeSpan.FromSeconds(20));
        if (certCode != 0 || !certJson.TrimStart().StartsWith('{') || JsonNode.Parse(certJson) is not JsonObject cert)
        {
            check.Set(Status.Fail, $"xray tls cert не выдал сертификат для цели REALITY: {certErr}");
            return;
        }

        if (_app.CoreExe("xray") is not { } clientXray)
        {
            check.Set(Status.Fail, "в поставке нет xray — клиент проверить нечем");
            return;
        }
        var ipv6 = false;
        try
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.IPv6Loopback, 0);
            probe.Start();
            probe.Stop();
            ipv6 = true;
        }
        catch
        {
        }

        var (ok, detail) = StartServerAndSelfTest(clientXray, cert, bind, hosts, ipv6, check);
        if (!ok && bind != null)
        {
            //  Привязка к интерфейсу нужна только режиму TUN. Если сервер с ней не выходит в сеть, проверки
            //  прокси всё равно имеют смысл, а TUN на одной машине — нет: ответы сервера ушли бы в туннель.
            var (okNoBind, detailNoBind) = StartServerAndSelfTest(clientXray, cert, null, hosts, ipv6, check);
            if (okNoBind)
            {
                _serverBound = false;
                report.Notes.Add($"С привязкой freedom к интерфейсу «{bind}» тестовый сервер не выходил в сеть ({detail}); без привязки работает. Проверки TUN на одной машине в таком виде не показательны.");
                ok = true;
                detail = detailNoBind + $" (без привязки к «{bind}»: с ней не работал)";
            }
        }
        _serverOk = ok;
        check.Set(ok ? Status.Pass : Status.Fail, ok ? detail : detail + ". Проверки подключения ниже — про стенд, не про приложение");
    }

    /// <summary>
    /// Поднять тестовый сервер и проверить его ДО приложения: ядро из поставки как ручной клиент. Если не
    /// проходит эта проверка, отказы подключения ниже — про стенд, а не про приложение.
    /// </summary>
    private (bool Ok, string Detail) StartServerAndSelfTest(string clientXray, JsonObject cert, string? bind,
        Dictionary<string, string> hosts, bool ipv6, CheckResult check)
    {
        _server?.Dispose();
        Net.WaitUntil(() => !Net.IsListening(o.ServerPort), TimeSpan.FromSeconds(5));
        var accessLog = Path.Combine(_serverDir, "server-access.log");
        var serverConfig = Fixtures.ServerConfig(o.ServerPort, o.DestPort, accessLog, Path.Combine(_serverDir, "server-error.log"), cert, bind, hosts, ipv6);
        var serverConfigPath = Path.Combine(o.Work, "server.json");
        File.WriteAllText(serverConfigPath, serverConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _access = new TailFile(accessLog);
        _server = CoreProcess.Start(o.ServerXray, $"run -c \"{serverConfigPath}\"", Path.Combine(_serverDir, "server-stdout.log"));
        var listening = Net.WaitUntil(() => Net.IsListening(o.ServerPort) && Net.IsListening(o.DestPort), TimeSpan.FromSeconds(15));
        check.Evidence.Add($"сервер: 127.0.0.1:{o.ServerPort}{(ipv6 ? $" и [::1]:{o.ServerPort}" : "")} (REALITY → 127.0.0.1:{o.DestPort}), freedom привязан к интерфейсу: {bind ?? "нет"}, hosts: {string.Join(", ", hosts.Select(kv => kv.Key + "=" + kv.Value))}");
        if (listening is null || !_server.Alive)
        {
            return (false, "тестовый сервер не поднялся (порты не слушаются) — см. server/server-stdout.log");
        }

        var clientConfigPath = Path.Combine(o.Work, "manual-client.json");
        File.WriteAllText(clientConfigPath, Fixtures.ManualClientConfig(o.ClientPort, o.ServerPort).ToJsonString());
        using var client = CoreProcess.Start(clientXray, $"run -c \"{clientConfigPath}\"", Path.Combine(_serverDir, "manual-client.log"));
        Net.WaitUntil(() => Net.IsListening(o.ClientPort), TimeSpan.FromSeconds(10));
        var mark = _access.Mark();
        var fetch = Net.Curl(o.Target1, proxy: $"socks5h://127.0.0.1:{o.ClientPort}");
        var hits = _access.WaitFor(mark, [HostPort(o.Target1)], TimeSpan.FromSeconds(5));
        check.Evidence.Add($"curl через ручной клиент: {fetch}");
        check.Evidence.AddRange(hits.Take(3));
        var ok = fetch.Ok && hits.Count > 0;
        return (ok, ok
            ? $"ручной клиент (xray из поставки) → сервер → {HostPort(o.Target1)}: {fetch}; сервер записал соединение"
            : $"ручной клиент не прошёл: {fetch}; строк в журнале сервера: {hits.Count}");
    }

    #endregion Машина и стенд

    #region Запуск и импорт подписок

    private Dictionary<string, string> BaseEnv(int exitAfterMs) => new() { ["DP_EXIT_AFTER_MS"] = exitAfterMs.ToString() };

    private StartupRun LaunchObserved(string name, int exitAfterMs, bool uia)
    {
        var timeline = Path.Combine(_timelines, $"timeline-{name}.txt");
        if (IsWin)
        {
            return Observers.Startup(_app, name, BaseEnv(exitAfterMs), _shots, timeline, TimeSpan.FromSeconds(40), run =>
            {
                if (!uia || !IsWin)
                {
                    return null;
                }
                var script = Path.Combine(AppContext.BaseDirectory, "tray-uia.ps1");
                run.TrayUia = Observers.TrayUia(script, Path.Combine(o.Out, $"tray-uia-{name}.json"), Path.Combine(_shots, $"startup-{name}-5-overflow.png"), openOverflow: true);
                return null;
            });
        }

        //  Linux: окно и трей здесь не наблюдаются (для них есть стенд Xvfb), только вехи изнутри.
        var run = new StartupRun { Name = name };
        File.Delete(timeline);
        var env = BaseEnv(exitAfterMs);
        env["DP_TIMELINE"] = timeline;
        var p = _app.Launch(env);
        run.Exited = AppDriver.WaitExit(p, TimeSpan.FromMilliseconds(exitAfterMs + 30000));
        if (!run.Exited)
        {
            _app.KillEverything();
        }
        run.ReadTimeline(timeline);
        return run;
    }

    private void PhaseStartup()
    {
        var window = report.Add("startup.window", "Запуск: окно появляется");
        var tray = report.Add("startup.tray", "Запуск: значок в трее появляется не раньше окна");
        var import = report.Add("import", "Подписки скачиваются и импортируются при запуске (VLESS и XRAY_JSON → узел Custom)");
        var runs = new List<StartupRun>();
        try
        {
            RunStartups(import, runs);
        }
        finally
        {
            JudgeStartups(window, tray, runs);
        }
    }

    private void RunStartups(CheckResult import, List<StartupRun> runs)
    {
        if (Directory.Exists(_app.ConfigDir))
        {
            report.Notes.Add("guiConfigs уже был до первого запуска — запуск A не «первый в жизни».");
        }

        if (IsWin)
        {
            report.Environment.Add("Фон под окном: " + Win.PrepareDesktop(Environment.ProcessId));
        }

        //  A — первый запуск: пустая папка, конфиг и база создаются при нём.
        var runA = LaunchObserved("A-first", 12000, uia: true);
        runs.Add(runA);
        report.StartupRuns.Add(runA);
        if (!File.Exists(_app.DbPath) || !File.Exists(_app.ConfigPath))
        {
            import.Set(Status.Fail, $"после первого запуска нет {(File.Exists(_app.DbPath) ? "guiNConfig.json" : "guiNDB.db")} — засеять подписки некуда");
            return;
        }

        CheckDirectDns();
        _defaultSysProxyType = _app.ReadConfig()["SystemProxyItem"]?["SysProxyType"] is JsonValue v && v.TryGetValue<int>(out var t) ? t : 0;

        _app.AddSubscription("e2e-sub-vless", "E2E VLESS", $"http://127.0.0.1:{o.WebPort}/sub", 1);
        _app.AddSubscription("e2e-sub-json", "E2E XRAY_JSON", $"http://127.0.0.1:{o.WebPort}/subjson", 2);
        _app.PatchConfig(root =>
        {
            var core = AppDriver.Obj(root, "CoreBasicItem");
            core["LogEnabled"] = true;
            core["Loglevel"] = "debug";
            AppDriver.Obj(root, "TunModeItem")["EnableTun"] = false;
            if (o.LocalPort != 10808 && root["Inbound"] is JsonArray { Count: > 0 } inbound && inbound[0] is JsonObject first)
            {
                first["LocalPort"] = o.LocalPort;
            }
        });
        if (IsWin)
        {
            PromoteTrayIcon();
        }

        //  B — второй запуск: приложение само скачивает обе подписки (сведения «не приходили ни разу»).
        var runB = LaunchObserved("B-import", 16000, uia: true);
        runs.Add(runB);
        report.StartupRuns.Add(runB);

        var profiles = _app.Profiles();
        _vless = profiles.FirstOrDefault(p => p.ConfigType == 5 && p.Address == _host);
        _custom = profiles.FirstOrDefault(p => p.ConfigType == 2);
        import.Evidence.AddRange(_sub?.Requests ?? []);
        import.Evidence.AddRange(_app.SubscriptionRows());
        import.Evidence.AddRange(profiles.Select(p => $"узел {p.IndexId}: тип {p.ConfigType}, ядро {p.CoreType}, «{p.Remarks}», {p.Address}:{p.Port}, подписка {p.Subid}"));
        var customFile = _custom?.Address is { } a ? Path.Combine(_app.ConfigDir, a) : null;
        var customFileOk = customFile != null && File.Exists(customFile);
        if (_vless != null && _custom != null && customFileOk)
        {
            import.Set(Status.Pass, $"VLESS «{_vless.Remarks}» → {_vless.Address}:{_vless.Port}; Custom «{_custom.Remarks}» → файл {_custom.Address}");
        }
        else
        {
            import.Set(Status.Fail, $"после запуска с подписками: VLESS {(_vless is null ? "нет" : "есть")}, Custom {(_custom is null ? "нет" : customFileOk ? "есть" : "есть, но файла конфига нет")}; запросов к подпискам: {_sub?.Requests.Count ?? 0}");
        }
        _app.FreezeSubscriptions();

        //  C — обычный повторный запуск со списком серверов.
        var runC = LaunchObserved("C-warm", 12000, uia: false);
        runs.Add(runC);
        report.StartupRuns.Add(runC);
    }

    /// <summary>
    /// Прямой DNS приложения (SimpleDNSItem.DirectDNS, умолчание первого запуска) с этой машины: им в режиме
    /// TUN разрешается домен VPN-сервера. Не отвечает он — проверки «мимо туннеля» упадут не по вине кода.
    /// </summary>
    private void CheckDirectDns()
    {
        try
        {
            var dns = _app.ReadConfig()["SimpleDNSItem"] as JsonObject;
            foreach (var key in new[] { "DirectDNS", "BootstrapDNS" })
            {
                var value = dns?[key]?.GetValue<string>() ?? "";
                foreach (var entry in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!IPAddress.TryParse(entry, out _))
                    {
                        _directDns.Add($"{key} {entry}: не IP, напрямую не опрашивается");
                        continue;
                    }
                    var (ips, err) = _hostIsDomain ? Net.QueryA(entry, _host) : ([], "узел без домена");
                    _directDns.Add($"{key} {entry}: {_host} → [{string.Join(", ", ips)}]{(err is null ? "" : " (" + err + ")")}");
                }
            }
        }
        catch (Exception ex)
        {
            _directDns.Add("прямой DNS из guiNConfig.json не прочитан: " + ex.Message);
        }
        foreach (var line in _directDns)
        {
            report.Environment.Add("Прямой DNS приложения: " + line);
            Log.Info("Прямой DNS приложения: " + line);
        }
    }

    private void JudgeStartups(CheckResult window, CheckResult tray, List<StartupRun> runs)
    {
        if (!o.Phases.Contains("startup"))
        {
            window.Set(Status.Skip, "фаза startup не запрошена");
            tray.Set(Status.Skip, "фаза startup не запрошена");
            return;
        }
        if (runs.Count == 0)
        {
            window.Set(Status.Fail, "ни одного запуска не состоялось");
            tray.Set(Status.Skip, "ни одного запуска не состоялось");
            return;
        }
        if (!IsWin)
        {
            window.Set(Status.NotObservable, "окно наблюдается только на Windows; вехи изнутри: " + string.Join(" | ", runs.Select(r => $"{r.Name}: {string.Join(", ", r.Timeline.Select(kv => $"{kv.Key} {kv.Value:F0}"))}")));
            tray.Set(Status.NotObservable, "трей наблюдается только на Windows");
            return;
        }

        string Fmt(double? v) => v is { } x ? $"{x:F0}" : "—";
        var noWindow = runs.Where(r => r.WindowVisibleMs is null).ToList();
        window.Set(noWindow.Count == 0 ? Status.Pass : Status.Fail,
            string.Join("; ", runs.Select(r => $"{r.Name}: видно на {Fmt(r.WindowVisibleMs)} мс, первый кадр на {Fmt(r.WindowDrawnMs)} мс, содержимое на {Fmt(r.WindowPaintedMs)} мс")) +
            (noWindow.Count == 0 ? "" : $". Окно не появилось: {string.Join(", ", noWindow.Select(r => r.Name))}"));
        foreach (var r in runs)
        {
            window.Evidence.Add($"{r.Name}: {r.MainWindow}; вехи: {string.Join(", ", r.Timeline.Select(kv => $"{kv.Key} {kv.Value:F0}"))}; {string.Join("; ", r.Notes)}");
        }

        //  Сравнение — с первым кадром окна на экране: до него окно прозрачно, и человек его не видит.
        var observed = runs.Where(r => r.TrayObservedMs.HasValue && r.WindowShownMs.HasValue).ToList();
        var early = observed.Where(r => r.TrayObservedMs < r.WindowShownMs).ToList();
        foreach (var r in runs)
        {
            tray.Evidence.Add($"{r.Name}: Shell_NotifyIconGetRect {Fmt(r.TrayShellMs)} мс {r.TrayShellRect}; панели трея: {r.TrayToolbar}; UIA: {r.TrayUia ?? "—"}; изнутри tray.created {(r.Timeline.TryGetValue("tray.created", out var tc) ? $"{tc:F0}" : "—")}, window.frame {(r.Timeline.TryGetValue("window.frame", out var wf) ? $"{wf:F0}" : "—")}");
        }
        string Ref(StartupRun r) => r.WindowDrawnMs.HasValue ? "первый кадр окна" : "окно видно";
        if (early.Count > 0)
        {
            tray.Set(Status.Fail, "значок раньше окна: " + string.Join("; ", early.Select(r => $"{r.Name}: значок {Fmt(r.TrayObservedMs)} мс, {Ref(r)} {Fmt(r.WindowShownMs)} мс")));
        }
        else if (observed.Count > 0)
        {
            tray.Set(Status.Pass, string.Join("; ", observed.Select(r => $"{r.Name}: {Ref(r)} {Fmt(r.WindowShownMs)} → значок {Fmt(r.TrayObservedMs)} мс (+{r.TrayObservedMs - r.WindowShownMs:F0})")) +
                (observed.Count < runs.Count ? $"; в {runs.Count - observed.Count} запусках значок снаружи не увиден" : ""));
        }
        else
        {
            var inside = runs.Where(r => r.Timeline.ContainsKey("tray.created") && r.Timeline.ContainsKey("window.frame")).ToList();
            tray.Set(Status.NotObservable, "область уведомлений на раннере значок не показала ни одним способом" +
                (inside.Count > 0 ? "; изнутри: " + string.Join("; ", inside.Select(r => $"{r.Name}: кадр {r.Timeline["window.frame"]:F0} → значок {r.Timeline["tray.created"]:F0} мс")) : ""));
        }
    }

    /// <summary>
    /// Windows 11: у каждого значка своя запись в HKCU\Control Panel\NotifyIconSettings; IsPromoted=1 —
    /// «показывать на панели задач», а не в переполнении. Запись появляется после первого показа значка.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void PromoteTrayIcon()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (root is null)
            {
                report.Environment.Add("NotifyIconSettings: раздела нет (оболочка не Windows 11)");
                return;
            }
            var promoted = 0;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name, writable: true);
                if (k?.GetValue("ExecutablePath") is string exe && exe.EndsWith("departament.exe", StringComparison.OrdinalIgnoreCase))
                {
                    k.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    promoted++;
                }
            }
            report.Environment.Add($"NotifyIconSettings: записей departament.exe — {promoted}, всем IsPromoted=1");
        }
        catch (Exception ex)
        {
            report.Environment.Add("NotifyIconSettings не записаны: " + ex.Message);
        }
    }

    #endregion Запуск и импорт подписок

    #region Трей: уход и возврат

    private void PhaseTrayCycle()
    {
        var check = report.Add("tray.cycle", "Уход в трей и возврат (DP_TRAY_CYCLE): тот же размер и положение");
        if (!IsWin)
        {
            check.Set(Status.NotObservable, "окна наблюдаются только на Windows");
            return;
        }
        var env = BaseEnv(14000);
        env["DP_SETPOS"] = "48,32";
        env["DP_TRAY_CYCLE"] = "5000:3000";
        env["DP_TIMELINE"] = Path.Combine(_timelines, "timeline-tray-cycle.txt");
        var r = Observers.TrayCycle(_app, env, _shots, TimeSpan.FromSeconds(30));
        check.Evidence.AddRange(r.Notes);
        if (r.Before is null || r.HiddenAtMs is null)
        {
            check.Set(Status.Fail, "окно не ушло в трей за отведённое время" + (r.Notes.Count > 0 ? ": " + string.Join("; ", r.Notes) : ""));
            return;
        }
        if (r.ShownAtMs is null || r.After is null)
        {
            check.Set(Status.Fail, $"окно ушло в трей на {r.HiddenAtMs:F0} мс и не вернулось");
            return;
        }
        var b = r.Before;
        var a = r.After;
        var same = b.Rect == a.Rect && b.Frame == a.Frame;
        var normal = a.Visible && !a.Zoomed && !a.Iconic;
        var details = $"до: {b.Rect} (рамка DWM {b.Frame}); после: {a.Rect} (рамка DWM {a.Frame}){(a.Zoomed ? ", РАЗВЁРНУТО" : "")}{(a.Iconic ? ", СВЁРНУТО" : "")}; " +
                      $"скрыто {r.HiddenAtMs:F0}→{r.ShownAtMs:F0} мс от запуска; значок, пока окна нет: {(r.TrayPresentWhileHidden ? "есть" : "не увиден")} ({r.TrayWhileHiddenNote ?? "—"})";
        check.Set(same && normal ? Status.Pass : Status.Fail, details);
    }

    #endregion Трей: уход и возврат

    #region Подключение: системный прокси

    private void PhaseProxy()
    {
        var byDefault = report.Add("proxy.default", "«Только прокси» с настройками по умолчанию: системный прокси включается при подключении");
        var set = report.Add("proxy.set", "Системный прокси (режим «Изменить системный прокси»): включается при подключении");
        var early = report.Add("proxy.early", "Системный прокси не включается раньше, чем ядро слушает порт");
        var traffic = report.Add("proxy.traffic", "Системный прокси: трафик программ идёт через VPN-сервер");
        var cleared = report.Add("proxy.cleared", "Системный прокси: снимается при выходе");
        if (_vless is null || !_serverOk)
        {
            foreach (var c in new[] { byDefault, set, early, traffic, cleared })
            {
                c.Set(Status.Skip, _vless is null ? "нет импортированного узла VLESS" : "тестовый сервер не прошёл самопроверку");
            }
            return;
        }
        var expected = $"127.0.0.1:{o.LocalPort}";

        //  1. Как у человека: режим «Только прокси», тип системного прокси — тот, что приложение само
        //  записало в новый конфиг (в интерфейсе ПК его не выставить: переключатель — в скрытом StatusBarView).
        _app.PatchConfig(root =>
        {
            root["IndexId"] = _vless.IndexId;
            AppDriver.Obj(root, "TunModeItem")["EnableTun"] = false;
            AppDriver.Obj(root, "SystemProxyItem")["SysProxyType"] = _defaultSysProxyType;
        });
        if (IsWin)
        {
            var envDefault = BaseEnv(14000);
            envDefault["DP_CONNECT_AFTER_MS"] = "500";
            var pDefault = _app.Launch(envDefault);
            var listening = Net.WaitUntil(() => Net.IsListening(o.LocalPort), TimeSpan.FromSeconds(20));
            var enabled = listening.HasValue ? Net.WaitUntil(() => IsWin && Win.ReadSystemProxy().Enabled, TimeSpan.FromSeconds(4), 50) : null;
            var state = Win.ReadSystemProxy();
            if (!AppDriver.WaitExit(pDefault, TimeSpan.FromSeconds(45)))
            {
                _app.KillEverything();
            }
            byDefault.Set(listening is null ? Status.Fail : enabled.HasValue ? Status.Pass : Status.Warn,
                listening is null ? "ядро так и не стало слушать порт — подключения не было"
                : enabled.HasValue ? $"SysProxyType по умолчанию = {_defaultSysProxyType}: после подключения {state}"
                : $"SysProxyType по умолчанию = {_defaultSysProxyType} (ForcedClear): подключение в режиме «Только прокси» системный прокси Windows НЕ включает ({state}) — браузеры и другие программы идут мимо VPN, а главная пишет «Через системный прокси». Выставить режим в интерфейсе нельзя: переключатель в скрытом StatusBarView");
        }
        else
        {
            byDefault.Set(Status.NotObservable, "системный прокси Windows проверяется только на Windows");
        }

        //  2. Режим «Изменить системный прокси» (SysProxyType=1): Windows направляет программы на вход
        //  приложения, их трафик идёт через VPN-сервер, выход прокси снимает.
        _app.PatchConfig(root => AppDriver.Obj(root, "SystemProxyItem")["SysProxyType"] = 1);
        var mark = _access!.Mark();
        var env = BaseEnv(30000);
        env["DP_CONNECT_AFTER_MS"] = "500";
        var timeline = Path.Combine(_timelines, "timeline-proxy.txt");
        File.Delete(timeline);
        env["DP_TIMELINE"] = timeline;
        var p = _app.Launch(env);
        DateTime? processStart = null;
        try { processStart = p.StartTime; } catch { }
        double? enabledAtMs = null;
        try
        {
            if (IsWin)
            {
                //  Частый опрос: момент включения сверяется с вехой core.up изнутри.
                var tSet = Net.WaitUntil(() => IsWin && Win.ReadSystemProxy() is { Enabled: true } s && s.Server!.Contains(expected), TimeSpan.FromSeconds(25), 25);
                if (tSet.HasValue && processStart is { } ps)
                {
                    enabledAtMs = (DateTime.Now - ps).TotalMilliseconds;
                }
                var state = Win.ReadSystemProxy();
                set.Set(tSet.HasValue ? Status.Pass : Status.Fail, tSet.HasValue
                    ? $"на {enabledAtMs:F0} мс от старта процесса: {state}"
                    : $"за 25 с системный прокси не стал {expected}: {state}");
            }
            else
            {
                set.Set(Status.NotObservable, "системный прокси Windows проверяется только на Windows");
            }
            var tListen = Net.WaitUntil(() => Net.IsListening(o.LocalPort), TimeSpan.FromSeconds(20));
            traffic.Evidence.Add($"вход приложения 127.0.0.1:{o.LocalPort} слушается: {(tListen.HasValue ? "да" : "НЕТ")}; ядра: {string.Join(", ", _app.RunningCores())}");
            var fetch = IsWin ? Net.SystemProxyFetch(o.Target1) : Net.Curl(o.Target1, proxy: $"http://{expected}");
            var hits = _access.WaitFor(mark, [HostPort(o.Target1)], TimeSpan.FromSeconds(5));
            traffic.Evidence.Add((IsWin ? "Windows PowerShell (WinINet, системный прокси): " : "curl через вход приложения: ") + fetch.Output.Replace("\r", "").Replace("\n", "; ") + (fetch.Error.Length > 0 ? " | " + fetch.Error : ""));
            traffic.Evidence.AddRange(hits.Take(5));
            traffic.Set(fetch.Ok && hits.Count > 0 ? Status.Pass : Status.Fail,
                fetch.Ok && hits.Count > 0
                    ? $"{HostPort(o.Target1)}: HTTP {fetch.Code} за {fetch.ElapsedMs} мс, сервер записал: {Shorten(hits[0])}"
                    : $"запрос: HTTP {fetch.Code}{(fetch.Error.Length > 0 ? " (" + fetch.Error.Trim() + ")" : "")}; строк про {HostPort(o.Target1)} в журнале сервера: {hits.Count}");
            if (IsWin)
            {
                //  Что в это время показывает само приложение: щит, режим, скорость.
                Win.SaveScreenshot(Path.Combine(_shots, "proxy-connected.png"));
            }
        }
        finally
        {
            var exited = AppDriver.WaitExit(p, TimeSpan.FromSeconds(60));
            if (!exited)
            {
                _app.KillEverything();
            }
            var tl = new StartupRun();
            tl.ReadTimeline(timeline);
            traffic.Evidence.Add("вехи: " + string.Join(", ", tl.Timeline.Select(kv => $"{kv.Key} {kv.Value:F0}")));
            if (!IsWin)
            {
                early.Set(Status.NotObservable, "системный прокси Windows проверяется только на Windows");
            }
            else if (enabledAtMs is null || !tl.Timeline.TryGetValue("core.up", out var coreUp))
            {
                early.Set(Status.NotObservable, $"нечего сравнить: прокси включён на {(enabledAtMs is { } e ? $"{e:F0}" : "—")} мс, core.up {(tl.Timeline.ContainsKey("core.up") ? "есть" : "нет")}");
            }
            else
            {
                //  Допуск 100 мс: опрос реестра идёт с шагом 25 мс, веха пишется из потока ядра.
                var lead = coreUp - enabledAtMs.Value;
                var tap = tl.Timeline.TryGetValue("connect.tap", out var t) ? t : (double?)null;
                early.Set(lead > 100 ? Status.Fail : Status.Pass, lead > 100
                    ? $"прокси включён на {enabledAtMs:F0} мс, а ядро слушает с {coreUp:F0} мс (нажатие «Подключить» — на {(tap is { } x ? $"{x:F0}" : "—")} мс): {lead:F0} мс программы Windows смотрели в порт, где никого нет"
                    : $"прокси включён на {enabledAtMs:F0} мс, ядро слушает с {coreUp:F0} мс, «Подключить» — на {(tap is { } x2 ? $"{x2:F0}" : "—")} мс");
            }
            if (IsWin)
            {
                var after = Win.ReadSystemProxy();
                cleared.Set(exited && !after.Enabled && after.RegProxyEnable is null or 0 ? Status.Pass : Status.Fail,
                    $"{(exited ? "вышло штатно" : "НЕ вышло само, добито")}; после выхода: {after}");
            }
            else
            {
                cleared.Set(Status.NotObservable, "системный прокси Windows проверяется только на Windows");
            }
        }
    }

    #endregion Подключение: системный прокси

    #region Подключение: TUN

    /// <param name="candidate">Версия подменённого ядра Xray для диагностического прогона (null — ядро из поставки).</param>
    private void PhaseTun(bool customNode, string? candidate = null)
    {
        var kind = customNode ? "узел Custom (XRAY_JSON)" : "обычный узел";
        var prefix = customNode ? "tun.custom" : candidate is null ? "tun.xray" : "tun.xray-candidate";
        var lead = candidate is null ? "TUN" : $"Диагностика, TUN с {candidate}";
        var diagnostic = candidate is not null;
        var adapterName = customNode ? "singbox_tun" : "xray_tun";
        var up = report.Add($"{prefix}.up", customNode
            ? "TUN, узел Custom: sing-box поднимает singbox_tun и отдаёт трафик в Xray 127.0.0.1:" + o.LocalPort
            : $"{lead}, обычный узел: Xray поднимает xray_tun с адресом и маршрутом (wintun.dll рядом с xray.exe)", diagnostic);
        var traffic = report.Add($"{prefix}.traffic", $"{lead}, {kind}: трафик без настроек прокси идёт через VPN-сервер", diagnostic);
        var dns = report.Add($"{prefix}.dns", $"{lead}, {kind}: домен VPN-сервера разрешается мимо туннеля", diagnostic);
        var down = report.Add($"{prefix}.down", $"{lead}, {kind}: после выхода адаптера нет и сеть прямая", diagnostic);
        var all = new[] { up, traffic, dns, down };
        var node = customNode ? _custom : _vless;
        if (!IsWin)
        {
            foreach (var c in all)
            {
                c.Set(Status.NotObservable, "TUN проверяется только на Windows");
            }
            return;
        }
        if (node is null || !_serverOk || !_serverBound || !Win.IsAdmin())
        {
            foreach (var c in all)
            {
                c.Set(Status.Skip, node is null ? "нет импортированного узла"
                    : !_serverOk ? "тестовый сервер не прошёл самопроверку"
                    : !_serverBound ? "тестовый сервер не привязан к интерфейсу: его ответы ушли бы в туннель"
                    : "у стенда нет прав администратора");
            }
            return;
        }
        if (!customNode)
        {
            var xray = _app.CoreExe("xray");
            var wintun = xray is null ? null : Path.Combine(Path.GetDirectoryName(xray)!, "wintun.dll");
            up.Evidence.Add($"wintun.dll рядом с xray.exe: {(wintun != null && File.Exists(wintun) ? wintun : "НЕТ")}");
        }
        if (Net.Adapter(adapterName) is { } stale)
        {
            up.Evidence.Add("адаптер уже был до запуска: " + Net.DescribeAdapter(stale));
        }

        _app.PatchConfig(root =>
        {
            root["IndexId"] = node.IndexId;
            AppDriver.Obj(root, "TunModeItem")["EnableTun"] = true;
            AppDriver.Obj(root, "SystemProxyItem")["SysProxyType"] = 0; // ForcedClear: никакого системного прокси
        });
        var logMarks = LogMarks();
        var env = BaseEnv(45000);
        env["DP_CONNECT_AFTER_MS"] = "500";
        var timeline = Path.Combine(_timelines, $"timeline-{prefix}.txt");
        File.Delete(timeline);
        env["DP_TIMELINE"] = timeline;
        var p = _app.Launch(env);
        var exited = false;
        try
        {
            var tUp = Net.WaitUntil(() => Net.Adapter(adapterName) is { OperationalStatus: System.Net.NetworkInformation.OperationalStatus.Up }, TimeSpan.FromSeconds(40), 200);
            var cores = _app.RunningCores();
            up.Evidence.Add("ядра: " + string.Join(", ", cores));
            if (tUp is null)
            {
                up.Set(Status.Fail, $"за 40 с адаптер {adapterName} не поднялся; ядра: {string.Join(", ", cores)}; см. guiLogs и binConfigs");
                traffic.Set(Status.Skip, "туннеля нет");
                dns.Set(Status.Skip, "туннеля нет");
                return;
            }
            //  Поднятый адаптер — ещё не туннель: ядро должно дать ему адрес и маршрут. Адрес 169.254.x.x
            //  (APIPA) Windows ставит сама, когда этого никто не сделал.
            var tAddr = Net.WaitUntil(() => Net.Adapter(adapterName) is { } a && Net.HasRealIPv4(a), TimeSpan.FromSeconds(10), 200);
            //  Маршруты встают следом за адресом.
            Thread.Sleep(2000);
            var adapter = Net.Adapter(adapterName);
            var best = Win.BestInterfaceFor(IPAddress.Parse("8.8.8.8"));
            var coresOk = !customNode || (cores.Any(c => c.StartsWith("sing-box", StringComparison.Ordinal)) && cores.Any(c => c.StartsWith("xray", StringComparison.Ordinal)) && Net.IsListening(o.LocalPort));
            var routed = best != null && adapter != null && best.Id == adapter.Id;
            var upOk = coresOk && tAddr.HasValue && routed;
            up.Set(upOk ? Status.Pass : Status.Fail,
                (upOk ? $"через {tUp:F0} мс: " : tAddr is null ? "адаптер поднят, но без IPv4-адреса (только APIPA) и без маршрута: ядро не настроило интерфейс; " : !routed ? $"адрес есть, но маршрут до 8.8.8.8 идёт через {best?.Name ?? "?"}, а не через туннель; " : "") +
                $"{(adapter is null ? adapterName + " пропал" : Net.DescribeAdapter(adapter))}; маршрут до 8.8.8.8 через {best?.Name ?? "?"}; ядра: {string.Join(", ", cores)}" +
                (customNode ? $"; Xray слушает 127.0.0.1:{o.LocalPort}: {(Net.IsListening(o.LocalPort) ? "да" : "НЕТ")}" : ""));
            var proxy = Win.ReadSystemProxy();
            traffic.Evidence.Add($"маршрут до 8.8.8.8 через: {best?.Name ?? "?"}; системный прокси: {proxy}");
            var (_, routes, _, _) = Net.Run("route", ["print", "-4"], TimeSpan.FromSeconds(10));
            File.WriteAllText(Path.Combine(o.Out, $"routes-{prefix}.txt"), routes);

            var mark1 = _access!.Mark();
            var f1 = Net.Curl(o.Target1);
            var hits1 = _access.WaitFor(mark1, TargetKeys(o.Target1, f1), TimeSpan.FromSeconds(5));
            traffic.Evidence.Add($"curl без прокси {o.Target1}: {f1} (remote_ip {f1.Output.Split(' ').ElementAtOrDefault(1)})");
            traffic.Evidence.AddRange(hits1.Take(5));
            var viaTunnel = f1.Ok && hits1.Count > 0 && !proxy.Enabled;
            traffic.Set(viaTunnel ? Status.Pass : Status.Fail, viaTunnel
                ? $"{HostPort(o.Target1)}: HTTP {f1.Code} за {f1.ElapsedMs} мс без прокси (системный прокси выключен), сервер записал: {Shorten(hits1[0])}; маршрут по умолчанию через {best?.Name}"
                : $"curl без прокси: {f1}; строк в журнале сервера: {hits1.Count}; системный прокси: {(proxy.Enabled ? "ВКЛЮЧЁН" : "выключен")}; маршрут через {best?.Name}");
            Win.SaveScreenshot(Path.Combine(_shots, $"{prefix}-connected.png"));
            //  Конфиги ядер этой фазы: следующая фаза их перезапишет.
            CopyDir(_app.BinConfigsDir, Path.Combine(o.Out, "app", $"binConfigs-{prefix}"));

            //  Домен сервера: кеш DNS системы сбрасывается, и следующее соединение ядра с сервером
            //  вынуждено разрешить его заново. Уйди это разрешение в туннель — оно ждало бы само себя.
            Win.FlushDnsCache();
            Net.Run("ipconfig", ["/flushdns"], TimeSpan.FromSeconds(10));
            var mark2 = _access.Mark();
            var f2 = Net.Curl(o.Target2);
            var hits2 = _access.WaitFor(mark2, TargetKeys(o.Target2, f2), TimeSpan.FromSeconds(5));
            dns.Evidence.Add($"после сброса кеша DNS: curl без прокси {o.Target2}: {f2}");
            dns.Evidence.AddRange(hits2.Take(3));
            var (staticOk, staticDetail) = customNode ? SingboxProtect() : XrayProtect();
            dns.Evidence.Add("конфиг: " + staticDetail);
            dns.Evidence.AddRange(_directDns.Select(l => "прямой DNS до туннеля: " + l));
            var (logLines, contradiction) = DnsLogEvidence(logMarks, customNode);
            dns.Evidence.AddRange(logLines.Take(20));
            if (!_hostIsDomain)
            {
                dns.Set(Status.NotObservable, "узел смотрит на IP (домен-кандидат не разрешился) — разрешать нечего");
            }
            else
            {
                var functional = f2.Ok && hits2.Count > 0;
                var ok = staticOk && functional && contradiction is null;
                dns.Set(ok ? Status.Pass : Status.Fail,
                    $"{_host}: {staticDetail}; после сброса кеша DNS новое соединение {(functional ? $"прошло (HTTP {f2.Code}, сервер записал {HostPort(o.Target2)})" : $"НЕ прошло ({f2})")}" +
                    (contradiction is null ? $"; строк журнала ядра про домен: {logLines.Count}" : $"; ЖУРНАЛ ЯДРА ПРОТИВ: {contradiction}"));
            }
        }
        finally
        {
            exited = AppDriver.WaitExit(p, TimeSpan.FromSeconds(75));
            if (!exited)
            {
                _app.KillEverything();
            }
            var tl = new StartupRun();
            tl.ReadTimeline(timeline);
            up.Evidence.Add("вехи: " + string.Join(", ", tl.Timeline.Select(kv => $"{kv.Key} {kv.Value:F0}")));

            {
                var gone = Net.WaitUntil(() => Net.Adapter(adapterName) is null or { OperationalStatus: not System.Net.NetworkInformation.OperationalStatus.Up }, TimeSpan.FromSeconds(15), 250);
                var mark3 = _access!.Mark();
                var f3 = Net.Curl(o.Target1);
                var hits3 = _access.WaitFor(mark3, [HostPort(o.Target1)], TimeSpan.FromSeconds(2));
                var bestAfter = Win.BestInterfaceFor(IPAddress.Parse("8.8.8.8"))?.Name;
                var ok = exited && gone.HasValue && f3.Ok && hits3.Count == 0;
                down.Set(ok ? Status.Pass : Status.Fail,
                    $"{(exited ? "вышло штатно" : "НЕ вышло само, добито")}; адаптер {adapterName}: {(gone.HasValue ? "снят" : "ОСТАЛСЯ")}; маршрут до 8.8.8.8 через {bestAfter}; прямой запрос: {f3}{(hits3.Count > 0 ? ", но он ПРОШЁЛ ЧЕРЕЗ СЕРВЕР" : "")}");
                if (!gone.HasValue)
                {
                    //  Оставшийся туннель отрезал бы следующие проверки и загрузку артефактов.
                    Net.Run("powershell.exe", ["-NoProfile", "-Command", $"Disable-NetAdapter -Name '{adapterName}' -Confirm:$false"], TimeSpan.FromSeconds(30));
                }
            }
        }
    }

    /// <summary>
    /// Диагностика: тот же TUN обычного узла, но с другим ядром Xray вместо закреплённого в сборке. Идёт,
    /// только если с ядром из поставки туннель не заработал, — чтобы было видно, чинит ли дело смена ядра.
    /// Отказы здесь сборку не валят; ядро из поставки возвращается на место в любом случае.
    /// </summary>
    private void PhaseXrayCandidate()
    {
        var shippedFailed = report.Checks.Any(c => c.Id is "tun.xray.up" or "tun.xray.traffic" && c.Status == Status.Fail);
        if (!shippedFailed)
        {
            report.Notes.Add("TUN обычного узла с ядром из поставки прошёл — прогон с другим Xray не нужен.");
            return;
        }
        if (!IsWin || _app.CoreExe("xray") is not { } shipped || !File.Exists(o.XrayCandidate))
        {
            report.Notes.Add("Прогон с другим Xray не состоялся: нет ядра из поставки или подменного ядра.");
            return;
        }
        _app.KillEverything();
        var backup = shipped + ".shipped";
        File.Copy(shipped, backup, overwrite: true);
        try
        {
            File.Copy(o.XrayCandidate!, shipped, overwrite: true);
            var (_, version, _, _) = Net.Run(shipped, ["version"], TimeSpan.FromSeconds(15));
            var name = string.Join(' ', version.Split('\n')[0].Trim().Split(' ').Take(2));
            PhaseTun(customNode: false, candidate: name.Length > 0 ? name : "другим Xray");
        }
        finally
        {
            _app.KillEverything();
            File.Copy(backup, shipped, overwrite: true);
            File.Delete(backup);
        }
    }

    private static IReadOnlyCollection<string> TargetKeys(string url, FetchResult f)
    {
        var keys = new List<string> { HostPort(url) };
        var ip = f.Output.Split(' ').ElementAtOrDefault(1);
        if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
        {
            keys.Add($"{ip}:{new Uri(url).Port}");
        }
        return keys;
    }

    private Dictionary<string, long> LogMarks()
    {
        var marks = new Dictionary<string, long>();
        if (Directory.Exists(_app.LogsDir))
        {
            foreach (var f in Directory.EnumerateFiles(_app.LogsDir))
            {
                marks[f] = new FileInfo(f).Length;
            }
        }
        return marks;
    }

    /// <summary>
    /// Строки журналов ядер про домен сервера за время фазы. У sing-box строки одного DNS-запроса
    /// связаны номером [id]: по нему видно правило, которое этот запрос выбрало (match[…] => route(…)).
    /// Правило с remote_dns значит, что домен сервера ушёл в туннель, — это прямое опровержение.
    /// </summary>
    private (List<string> Lines, string? Contradiction) DnsLogEvidence(Dictionary<string, long> marks, bool customNode)
    {
        var lines = new List<string>();
        string? contradiction = null;
        if (!Directory.Exists(_app.LogsDir) || !_hostIsDomain)
        {
            return (lines, null);
        }
        var pattern = customNode ? "sbox_*.txt" : "Verror_*.txt";
        foreach (var file in Directory.EnumerateFiles(_app.LogsDir, pattern))
        {
            var fresh = new TailFile(file).LinesSince(marks.GetValueOrDefault(file));
            if (customNode)
            {
                var ids = fresh.Where(l => l.Contains(_host, StringComparison.OrdinalIgnoreCase))
                    .Select(l => Regex.Match(l, @"\[(\d+) [^\]]*\]"))
                    .Where(m => m.Success)
                    .Select(m => m.Groups[1].Value)
                    .ToHashSet();
                foreach (var l in fresh.Where(l => ids.Any(id => l.Contains($"[{id} ", StringComparison.Ordinal)) && (l.Contains("dns", StringComparison.Ordinal) || l.Contains(_host, StringComparison.OrdinalIgnoreCase))))
                {
                    lines.Add(Path.GetFileName(file) + ": " + l);
                    if (l.Contains("route(remote_dns)", StringComparison.Ordinal))
                    {
                        contradiction ??= l;
                    }
                }
            }
            else
            {
                lines.AddRange(fresh.Where(l => l.Contains(_host, StringComparison.OrdinalIgnoreCase)).Select(l => Path.GetFileName(file) + ": " + l));
            }
        }
        return (lines, contradiction);
    }

    /// <summary>binConfigs/config.json (Xray с собственным TUN): домен сервера — среди domains у прямого DNS.</summary>
    private (bool Ok, string Detail) XrayProtect()
    {
        var path = Path.Combine(_app.BinConfigsDir, "config.json");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["dns"]?["servers"] is not JsonArray servers)
            {
                return (false, "в config.json нет dns.servers");
            }
            foreach (var s in servers.OfType<JsonObject>())
            {
                if (s["domains"] is JsonArray domains && domains.Any(d => MatchesHost(d?.GetValue<string>())))
                {
                    var tag = s["tag"]?.GetValue<string>() ?? "";
                    var address = s["address"]?.ToString();
                    return (tag.StartsWith("direct-dns", StringComparison.Ordinal), $"config.json: {_host} → DNS {address} (tag {tag})");
                }
            }
            return (false, $"config.json: {_host} нет ни в одном dns.servers[].domains — разрешается общим порядком");
        }
        catch (Exception ex)
        {
            return (false, $"config.json не прочитан: {ex.Message}");
        }
    }

    /// <summary>binConfigs/configPre.json (sing-box, владелец TUN): правило DNS для домена сервера ведёт на прямой DNS.</summary>
    private (bool Ok, string Detail) SingboxProtect()
    {
        var path = Path.Combine(_app.BinConfigsDir, "configPre.json");
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["dns"]?["rules"] is not JsonArray rules)
            {
                return (false, "в configPre.json нет dns.rules");
            }
            var index = 0;
            foreach (var r in rules.OfType<JsonObject>())
            {
                var domains = r["domain"] switch
                {
                    JsonArray arr => arr.Select(d => d?.GetValue<string>()).ToList(),
                    JsonValue v => [v.GetValue<string>()],
                    _ => [],
                };
                if (domains.Any(MatchesHost))
                {
                    var server = r["server"]?.GetValue<string>() ?? r["action"]?.GetValue<string>() ?? "?";
                    return (server is "direct_dns" or "local_local" or "hosts_dns", $"configPre.json: dns.rules[{index}] {_host} → {server}");
                }
                index++;
            }
            return (false, $"configPre.json: правила для {_host} нет — он уйдёт на удалённый DNS через туннель");
        }
        catch (Exception ex)
        {
            return (false, $"configPre.json не прочитан: {ex.Message}");
        }
    }

    private bool MatchesHost(string? d) =>
        d is not null && (string.Equals(d, _host, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(d, "full:" + _host, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(d, "domain:" + _host, StringComparison.OrdinalIgnoreCase));

    private static string Shorten(string s) => s.Length > 160 ? s[..160] + "…" : s;

    #endregion Подключение: TUN

    public void Cleanup()
    {
        DefenderDetections();
        try
        {
            var killed = _app.KillEverything();
            if (killed.Count > 0)
            {
                report.Notes.Add("В конце добиты: " + string.Join(", ", killed));
            }
        }
        catch
        {
        }
        _server?.Dispose();
        _sub?.Dispose();
        CopyDir(_app.LogsDir, Path.Combine(o.Out, "app", "guiLogs"));
        CopyDir(_app.BinConfigsDir, Path.Combine(o.Out, "app", "binConfigs"));
        CopyDir(_app.ConfigDir, Path.Combine(o.Out, "app", "guiConfigs"));
    }

    private static void CopyDir(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                //  Хранилище входа аккаунта в артефакты не кладём, даже пустое.
                if (Path.GetFileName(file).Contains("_auth", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        catch
        {
        }
    }
}
