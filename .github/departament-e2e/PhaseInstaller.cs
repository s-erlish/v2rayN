using System.Diagnostics;
using Microsoft.Win32;

namespace DpE2E;

/// <summary>
/// Установщик Inno Setup (installer/departament.iss) на настоящей Windows: установка в Program Files,
/// установка поверх запущенной подключённой программы, «departament.exe --quit», самообновление уже
/// установленной программы и удаление.
/// </summary>
internal sealed partial class E2E
{
    //  AppId из departament.iss: по нему Windows узнаёт установленную программу.
    private const string InstallerAppId = "{60C53B95-8ECB-4307-A76B-756BEAC48B89}";
    private const string UninstallKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + InstallerAppId + "_is1";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SchemeKeyPath = @"Software\Classes\departamentvpn";
    private const string AutostartName = "departament";

    private bool CanConnect => _vless is not null && _serverOk;

    private void PhaseInstaller()
    {
        var install = report.Add("installer.install", "Установщик: тихая установка в Program Files — файлы, ярлык в «Пуске», запись в «Приложениях» с версией");
        var launch = report.Add("installer.launch", "Установленная программа запускается из Program Files и сама выравнивает версию в «Приложениях»");
        var upgrade = report.Add("installer.upgrade", "Установщик поверх запущенной подключённой программы: штатный выход, прокси снят, файлы заменены");
        var quit = report.Add("installer.quit", "departament.exe --quit: запущенная программа и ядро выходят за секунды, прокси снят");
        var selfUpdate = report.Add("installer.selfupdate", "Самообновление установленной программы: выход по --quit, замена в Program Files");
        var selfRestart = report.Add("installer.selfupdate-restart", "После самообновления в Program Files запущена новая версия, версия в «Приложениях» верна");
        var reconnect = report.Add("installer.selfupdate-reconnect", "После самообновления подключение возвращается само: системный прокси снова включён, трафик идёт через сервер");
        var uninstall = report.Add("installer.uninstall", "Удаление: программа закрыта, файлы убраны, настройки на месте; автозапуск, ссылки departamentvpn:// и запись убраны");
        var leftovers = report.Add("installer.leftovers", "После удаления в папке программы не остаётся её файлов");
        var postinstall = report.Add("installer.postinstall", "Запуск программы в конце установки: установщик запущен без прав администратора, как двойным щелчком, — программа открылась, ошибки 740 нет");
        var baseline = report.Add("installer.postinstall-baseline", "Контроль: без «runascurrentuser shellexec» тот же запуск падает с кодом 740 — проверка его ловит", diagnostic: true);
        var all = new[] { install, launch, upgrade, quit, selfUpdate, selfRestart, reconnect, uninstall, leftovers, postinstall, baseline };
        if (!IsWin)
        {
            foreach (var c in all)
            {
                c.Set(Status.NotObservable, "установщик — только Windows");
            }
            return;
        }
        if (o.Setup is null || !File.Exists(o.Setup) || o.Dist is null)
        {
            foreach (var c in all)
            {
                c.Set(Status.Skip, o.Dist is null ? "нет чистой копии сборки (--dist)" : "нет departament-setup.exe (артефакт departament-setup сборки ветки)");
            }
            return;
        }
        _app.KillEverything();
        DefenderRealtime();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "departament");
        var inst = new AppDriver(dir);
        var logs = Path.Combine(o.Out, "installer");
        Directory.CreateDirectory(logs);
        try
        {
            if (!InstallerInstall(install, inst, logs))
            {
                foreach (var c in all.Skip(1))
                {
                    c.Set(Status.Skip, "установка не удалась");
                }
                return;
            }
            InstallerLaunch(launch, inst);
            InstallerUpgrade(upgrade, inst, logs);
            InstallerQuit(quit, inst);
            InstallerSelfUpdate(selfUpdate, selfRestart, reconnect, inst);
            InstallerUninstall(uninstall, leftovers, inst, logs);
            InstallerPostinstall(postinstall, baseline, inst, logs);
        }
        finally
        {
            inst.KillEverything();
            CopyDir(inst.LogsDir, Path.Combine(logs, "guiLogs"));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool InstallerInstall(CheckResult c, AppDriver inst, string logs)
    {
        if (Directory.Exists(inst.Dir))
        {
            c.Evidence.Add($"{inst.Dir} уже был до установки");
        }
        var (code, ms, _) = RunSetup(Path.Combine(logs, "1-install.log"));
        var problems = new List<string>();
        var sv = FileVersionInfo.GetVersionInfo(o.Setup!);
        c.Evidence.Add($"departament-setup.exe ({new FileInfo(o.Setup!).Length / 1048576.0:F1} МБ, «{sv.ProductName}» {sv.ProductVersion}) /VERYSILENT /SUPPRESSMSGBOXES /NORESTART: код {code} за {ms} мс");
        if (code != 0)
        {
            problems.Add($"код выхода установщика {code}");
        }
        var diff = Diff(DistHashes, HashTree(inst.Dir)).Where(d => !d.StartsWith("появился", StringComparison.Ordinal)).ToList();
        c.Evidence.Add(diff.Count == 0 ? $"все {DistHashes.Count} файлов сборки на месте, SHA-256 совпадают" : "расхождения со сборкой: " + string.Join("; ", diff.Take(6)));
        if (diff.Count > 0)
        {
            problems.Add($"в {inst.Dir} не то, что в сборке: {string.Join("; ", diff.Take(3))}");
        }
        if (!File.Exists(Path.Combine(inst.Dir, "unins000.exe")))
        {
            problems.Add("нет unins000.exe");
        }

        var lnk = StartMenuShortcut;
        var target = File.Exists(lnk) ? ShortcutTarget(lnk) : null;
        c.Evidence.Add($"ярлык в «Пуске»: {lnk} → {target ?? "НЕТ ярлыка"}");
        if (target is null || !SamePath(target, inst.ExePath))
        {
            problems.Add(target is null ? "нет ярлыка в «Пуске»" : $"ярлык ведёт в {target}");
        }
        c.Evidence.Add($"ярлык на рабочем столе (галочка по умолчанию стоит): {(File.Exists(DesktopShortcut) ? "есть" : "нет")}");

        using (var key = UninstallKey())
        {
            if (key is null)
            {
                problems.Add($"нет записи HKLM\\{UninstallKeyPath}");
            }
            else
            {
                var dv = key.GetValue("DisplayVersion") as string;
                var loc = key.GetValue("InstallLocation") as string;
                c.Evidence.Add($"HKLM\\{UninstallKeyPath}: DisplayName «{key.GetValue("DisplayName")}», DisplayVersion «{dv}», Publisher «{key.GetValue("Publisher")}», InstallLocation «{loc}», UninstallString «{key.GetValue("UninstallString")}»");
                if (o.ExpectedVersion is { } v && dv != v)
                {
                    problems.Add($"DisplayVersion «{dv}», ждали {v}");
                }
                if (loc is null || !SamePath(loc, inst.Dir))
                {
                    problems.Add($"InstallLocation «{loc}»");
                }
            }
        }
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"за {ms} мс: {DistHashes.Count} файлов в {inst.Dir} совпадают со сборкой, ярлык «Пуска» ведёт на departament.exe, запись «departament VPN» с версией {o.ExpectedVersion}"
            : string.Join("; ", problems));
        return code == 0 && File.Exists(inst.ExePath);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void InstallerLaunch(CheckResult c, AppDriver inst)
    {
        //  Версию в записи портим сами: программа должна вернуть свою (App.SyncInstalledVersion) — иначе
        //  после самообновления «Приложения» показывали бы версию первой установки.
        using (var key = UninstallKey(writable: true))
        {
            key?.SetValue("DisplayVersion", "0.0.1-e2e");
        }
        var timeline = Path.Combine(_timelines, "timeline-installed.txt");
        File.Delete(timeline);
        using var p = inst.Launch(new Dictionary<string, string> { ["DP_EXIT_AFTER_MS"] = "15000", ["DP_TIMELINE"] = timeline });
        var shown = Net.WaitUntil(() => IsWin && Win.MainWindowOf(Win.WindowsOf(p.Id)) is not null, TimeSpan.FromSeconds(25), 50);
        if (shown.HasValue)
        {
            Thread.Sleep(1000);
            Win.SaveScreenshot(Path.Combine(_shots, "installer-launch.png"));
        }
        var expected = o.ExpectedVersion;
        var synced = Net.WaitUntil(() => UninstallKey()?.GetValue("DisplayVersion") is string dv && dv != "0.0.1-e2e" && (expected is null || dv == expected), TimeSpan.FromSeconds(20), 200);
        var exited = AppDriver.WaitExit(p, TimeSpan.FromSeconds(45));
        if (!exited)
        {
            inst.KillEverything();
        }
        var tl = new StartupRun();
        tl.ReadTimeline(timeline);
        c.Evidence.Add($"из {inst.ExePath}: окно {(shown is { } s ? $"видно через {s:F0} мс" : "НЕ появилось за 25 с")}; вехи: {string.Join(", ", tl.Timeline.Select(kv => $"{kv.Key} {kv.Value:F0}"))}");
        c.Evidence.Add($"настройки легли рядом с exe: {(File.Exists(inst.ConfigPath) ? inst.ConfigPath : "НЕТ guiNConfig.json")}; ссылки departamentvpn:// (HKCU\\{SchemeKeyPath}): {(Registry.CurrentUser.OpenSubKey(SchemeKeyPath) is { } k ? $"записаны → {k.OpenSubKey(@"shell\open\command")?.GetValue(null)}" : "нет")}");
        c.Evidence.Add($"DisplayVersion: поставлено «0.0.1-e2e», программа вернула «{UninstallKey()?.GetValue("DisplayVersion")}» {(synced is { } y ? $"через {y:F0} мс" : "— НЕ вернула")}");
        var problems = new List<string>();
        if (shown is null)
        {
            problems.Add("окно не появилось");
        }
        if (synced is null)
        {
            problems.Add("версию в «Приложениях» программа не выровняла");
        }
        if (!exited)
        {
            problems.Add("не вышла сама по DP_EXIT_AFTER_MS");
        }
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"окно через {shown:F0} мс от старта; DisplayVersion «0.0.1-e2e» → «{UninstallKey()?.GetValue("DisplayVersion")}» за {synced:F0} мс; штатный выход"
            : string.Join("; ", problems));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void InstallerUpgrade(CheckResult c, AppDriver inst, string logs)
    {
        PrepareConnectedConfig(inst);
        //  Установщик обязан перезаписать файлы: у AmazTool.exe хвост, после установки его быть не должно.
        using (var fs = new FileStream(Path.Combine(inst.Dir, "AmazTool.exe"), FileMode.Append, FileAccess.Write))
        {
            fs.Write("e2e: must be overwritten by the installer"u8);
        }
        using var p = inst.Launch(new Dictionary<string, string> { ["DP_CONNECT_AFTER_MS"] = "500" });
        var connected = WaitConnected(c, inst, "перед установкой");

        //  Когда программа вышла и когда снят прокси — пока идёт установщик.
        var sw = Stopwatch.StartNew();
        long? appGone = null, proxyOff = null;
        using var stop = new CancellationTokenSource();
        var watch = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (appGone is null && p.HasExited)
                {
                    appGone = sw.ElapsedMilliseconds;
                }
                if (proxyOff is null && IsWin && !Win.ReadSystemProxy().Enabled)
                {
                    proxyOff = sw.ElapsedMilliseconds;
                }
                Thread.Sleep(50);
            }
        });
        var log = Path.Combine(logs, "2-upgrade-over-running.log");
        var (code, ms, _) = RunSetup(log);
        stop.Cancel();
        watch.Wait();
        var (lines, suspicious) = InnoLog(log);
        var cores = inst.RunningCores();
        var proxy = Win.ReadSystemProxy();
        var diff = Diff(DistHashes, HashTree(inst.Dir)).Where(d => !d.StartsWith("появился", StringComparison.Ordinal)).ToList();
        var installed = lines.Count(l => l.Contains("Successfully installed the file", StringComparison.Ordinal));

        c.Evidence.Add($"установщик: код {code} за {ms} мс; программа вышла на {(appGone is { } g ? $"{g} мс" : "— НЕ вышла")}, прокси снят на {(proxyOff is { } q ? $"{q} мс" : "— НЕ снят")} от его старта");
        c.Evidence.AddRange(lines.Where(l => l.Contains("departament", StringComparison.OrdinalIgnoreCase) && (l.Contains("quit", StringComparison.OrdinalIgnoreCase) || l.Contains("Terminating", StringComparison.Ordinal))).Take(6).Select(l => "журнал установщика: " + l.Trim()));
        c.Evidence.Add($"«Successfully installed the file»: {installed}; ядра после: {(cores.Count == 0 ? "нет" : string.Join(", ", cores))}; прокси после: {proxy}");
        c.Evidence.Add(diff.Count == 0 ? $"все {DistHashes.Count} файлов = сборка (хвост AmazTool.exe затёрт)" : "расхождения со сборкой: " + string.Join("; ", diff.Take(6)));
        var problems = new List<string>();
        if (!connected)
        {
            problems.Add("программа перед установкой не подключилась — проверка неполная");
        }
        if (code != 0)
        {
            problems.Add($"код выхода установщика {code}");
        }
        if (!p.HasExited)
        {
            problems.Add("программа осталась запущенной");
        }
        if (lines.Any(l => l.Contains("did not quit", StringComparison.Ordinal)))
        {
            problems.Add("программа не вышла сама за 15 с, установщик закрыл её силой");
        }
        if (suspicious.Count > 0)
        {
            problems.Add("в журнале установщика: " + string.Join(" | ", suspicious.Take(4).Select(l => l.Trim())));
        }
        if (cores.Count > 0)
        {
            problems.Add("ядра остались: " + string.Join(", ", cores));
        }
        if (proxy.Enabled)
        {
            problems.Add("системный прокси остался включённым: " + proxy);
        }
        if (diff.Count > 0)
        {
            problems.Add("файлы не заменены: " + string.Join("; ", diff.Take(3)));
        }
        if (!p.HasExited)
        {
            inst.KillEverything();
        }
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"подключённая программа вышла сама через {appGone} мс после старта установщика (--quit), прокси снят на {proxyOff} мс, ядра нет; {installed} файлов заменены без «занят», код 0 за {ms} мс"
            : string.Join("; ", problems));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void InstallerQuit(CheckResult c, AppDriver inst)
    {
        using var p = inst.Launch(new Dictionary<string, string> { ["DP_CONNECT_AFTER_MS"] = "500" });
        var connected = WaitConnected(c, inst, "перед --quit");
        var sw = Stopwatch.StartNew();
        var (code, quitMs) = RunPlain(inst.ExePath, ["--quit"], TimeSpan.FromSeconds(30));
        var gone = Net.WaitUntil(() => p.HasExited, TimeSpan.FromSeconds(15), 25);
        var goneMs = gone.HasValue ? sw.ElapsedMilliseconds : (long?)null;
        var coresGone = Net.WaitUntil(() => inst.RunningCores().Count == 0, TimeSpan.FromSeconds(10), 100);
        var proxy = Win.ReadSystemProxy();
        c.Evidence.Add($"«departament.exe --quit» вернулся за {quitMs} мс с кодом {code}; программа вышла через {(goneMs is { } g ? $"{g} мс" : "— НЕ вышла за 15 с")}; ядра: {(coresGone.HasValue ? "нет" : string.Join(", ", inst.RunningCores()))}; прокси: {proxy}");

        //  Без запущенной копии --quit ничего не запускает.
        var (code2, ms2) = RunPlain(inst.ExePath, ["--quit"], TimeSpan.FromSeconds(30));
        Thread.Sleep(1500);
        var stray = Process.GetProcessesByName("departament").Where(x => ProcessPath(x) is { } path && SamePath(path, inst.ExePath)).Select(x => x.Id).ToList();
        c.Evidence.Add($"«--quit» без запущенной копии: код {code2} за {ms2} мс, запущенных копий после: {(stray.Count == 0 ? "нет" : string.Join(", ", stray))}");
        var problems = new List<string>();
        if (!connected)
        {
            problems.Add("программа не подключилась — выход проверен без ядра");
        }
        if (goneMs is null)
        {
            problems.Add("программа не вышла за 15 с");
        }
        else if (goneMs > 10_000)
        {
            problems.Add($"выход занял {goneMs} мс");
        }
        if (!coresGone.HasValue)
        {
            problems.Add("ядро осталось");
        }
        if (proxy.Enabled)
        {
            problems.Add("системный прокси остался: " + proxy);
        }
        if (stray.Count > 0)
        {
            problems.Add("«--quit» без запущенной копии запустил программу");
        }
        if (!p.HasExited)
        {
            inst.KillEverything();
        }
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"подключённая программа вышла через {goneMs} мс после «--quit», ядра нет, прокси снят; без запущенной копии «--quit» ничего не запускает"
            : string.Join("; ", problems));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void InstallerSelfUpdate(CheckResult c, CheckResult restart, CheckResult reconnect, AppDriver inst)
    {
        if (_newPackage is not { } pkg)
        {
            c.Set(Status.Skip, "нет пакета «новой версии» (фаза самообновления до него не дошла)");
            restart.Set(Status.Skip, "нет пакета");
            reconnect.Set(Status.Skip, "нет пакета");
            return;
        }
        var expected = o.ExpectedVersion ?? "1.0.0";
        var dir = inst.Dir;
        var userFile = Path.Combine(dir, "guiConfigs", "e2e-user.txt");
        File.WriteAllText(userFile, $"user data {Guid.NewGuid()}");
        var userHash = Binary.Sha256(userFile);
        var p = inst.Launch(new Dictionary<string, string> { ["DP_CONNECT_AFTER_MS"] = "500" });
        var connected = WaitConnected(c, inst, "перед обновлением");
        //  Метка передачи — как её пишет подключённая программа в «Перезапустить» (AppUpdatePendingInstall):
        //  с сервером и режимом, чтобы новая версия вернула подключение сама.
        var package = StageUpdateFiles(dir, pkg, "v" + expected, connected
            ? new { Tag = "v" + expected, PreRelease = false, From = expected, HandedOffUtc = DateTime.UtcNow, Reconnect = new { ServerId = _vless!.IndexId, Tun = false } }
            : null);

        //  Программа отдаёт пакет установщику и выходит тем же путём, что «Выход» в трее (InstallAsync →
        //  AppExitAsync). Здесь выход — «--quit»: тот же MenuExit_Click.
        long quitMs = 0;
        DateTime? oldGone = null;
        var run = RunAmazTool(dir, package, p.Id, () =>
        {
            quitMs = RunPlain(inst.ExePath, ["--quit"], TimeSpan.FromSeconds(30)).Ms;
            if (p.WaitForExit(15_000))
            {
                oldGone = DateTime.UtcNow;
            }
        }, "installer-selfupdate");
        var proxy = Win.ReadSystemProxy();
        var problems = new List<string>();
        c.Evidence.Add($"AmazTool из {dir} ждал pid {p.Id}: {(run.WaitingMs is { } w ? $"записал через {w:F0} мс" : "НЕТ строки «waiting for pid»")}; «--quit» {quitMs} мс; от выхода до конца AmazTool {run.AfterReleaseMs} мс, код {run.ExitCode?.ToString() ?? "—"}; {SwapTiming(run.Log)}");
        c.Evidence.Add($"прокси после: {proxy}; «stopping core still running»: {(run.Text.Contains("stopping core", StringComparison.Ordinal) ? "ДА — ядро пережило выход программы" : "нет, ядро остановила сама программа")}");
        if (!connected)
        {
            problems.Add("программа перед обновлением не подключилась — проверка неполная");
        }
        if (run.WaitingMs is null)
        {
            problems.Add("AmazTool не записал, что ждёт программу");
        }
        if (run.ExitCode != 0)
        {
            problems.Add($"код выхода {run.ExitCode?.ToString() ?? "— (не вышел за 3 мин)"}");
        }
        if (!run.Text.Contains("upgrade installed", StringComparison.Ordinal))
        {
            problems.Add("в журнале нет «upgrade installed»");
        }
        var wrong = pkg.Files.Where(kv => !File.Exists(Path.Combine(dir, kv.Key)) || Binary.Sha256(Path.Combine(dir, kv.Key)) != kv.Value).Select(kv => kv.Key).ToList();
        if (wrong.Count > 0)
        {
            problems.Add($"не заменены {wrong.Count} файлов: {string.Join(", ", wrong.Take(5))}");
        }
        if (Binary.Sha256(userFile) != userHash || File.Exists(Path.Combine(dir, "guiConfigs", "e2e-trap.txt")))
        {
            problems.Add("тронуты данные пользователя в guiConfigs");
        }
        if (Directory.Exists(Path.Combine(dir, ".update")))
        {
            problems.Add("рабочая папка .update осталась");
        }
        if (proxy.Enabled)
        {
            problems.Add("системный прокси остался: " + proxy);
        }
        c.Evidence.AddRange(run.Log.Where(l => !l.Contains("attempt", StringComparison.Ordinal)).Take(20).Select(l => "upgrade.log: " + l));
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"подключённая программа в Program Files вышла по «--quit», AmazTool заменил все {pkg.Files.Count} файлов за {run.AfterReleaseMs} мс после её выхода, guiConfigs не тронут, прокси снят, код 0"
            : string.Join("; ", problems));

        JudgeRestart(restart, dir, run, pkg.ExeHash, $"updated to {expected}", "installer-selfupdate-restart",
            _ => CheckReconnect(reconnect, inst, connected, oldGone));
        var dv = UninstallKey()?.GetValue("DisplayVersion") as string;
        restart.Evidence.Add($"DisplayVersion после самообновления: «{dv}»");
        if (dv != expected && restart.Status == Status.Pass)
        {
            restart.Set(Status.Fail, $"в «Приложениях» версия «{dv}», программа — {expected}");
        }
        if (!p.HasExited)
        {
            inst.KillEverything();
        }
        p.Dispose();
    }

    /// <summary>
    /// Подключение после перезапуска ради обновления (AppUpdateReconnect): новая версия читает метку и
    /// подключается сама. Считается разрыв: от выхода прежней версии до снова включённого прокси.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void CheckReconnect(CheckResult c, AppDriver inst, bool wasConnected, DateTime? oldGone)
    {
        if (!wasConnected || _access is null)
        {
            c.Set(Status.Skip, "перед обновлением подключения не было — возвращать нечего");
            return;
        }
        var expected = $"127.0.0.1:{o.LocalPort}";
        var back = Net.WaitUntil(() => Net.IsListening(o.LocalPort) && Win.ReadSystemProxy() is { Enabled: true } s && s.Server!.Contains(expected), TimeSpan.FromSeconds(40), 100);
        var gap = back.HasValue && oldGone is { } g ? (DateTime.UtcNow - g).TotalMilliseconds : (double?)null;
        var lines = AppLogText(inst.Dir).Replace("\r", "").Split('\n').Where(l => l.Contains("reconnect", StringComparison.OrdinalIgnoreCase)).TakeLast(3).ToList();
        c.Evidence.AddRange(lines.Select(l => "журнал программы: " + l.Trim()));
        c.Evidence.Add($"прокси: {Win.ReadSystemProxy()}; ядра: {string.Join(", ", inst.RunningCores())}");
        if (!back.HasValue)
        {
            c.Set(Status.Fail, $"за 40 с после перезапуска подключение не вернулось: прокси {Win.ReadSystemProxy()}, {expected} {(Net.IsListening(o.LocalPort) ? "слушается" : "не слушается")}");
            return;
        }
        var mark = _access.Mark();
        var fetch = Net.SystemProxyFetch(o.Target1);
        var hits = _access.WaitFor(mark, [HostPort(o.Target1)], TimeSpan.FromSeconds(5));
        c.Evidence.Add("Windows PowerShell через системный прокси: " + fetch.Output.Replace("\r", "").Replace("\n", "; "));
        c.Evidence.AddRange(hits.Take(2));
        c.Set(fetch.Ok && hits.Count > 0 ? Status.Pass : Status.Fail, fetch.Ok && hits.Count > 0
            ? $"без VPN {(gap is { } x ? $"{x / 1000:F1} с" : "—")} (от выхода прежней версии до прокси новой); {HostPort(o.Target1)}: HTTP {fetch.Code}, сервер записал соединение"
            : $"прокси вернулся, но запрос не дошёл до сервера: HTTP {fetch.Code} {fetch.Error}");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void InstallerUninstall(CheckResult c, CheckResult leftovers, AppDriver inst, string logs)
    {
        var dir = inst.Dir;
        //  Автозапуск включает сама программа (AutostartHelper): с флагом в настройках — задача
        //  планировщика «departament». Если на этом запуске она его не завела, заводим сами.
        inst.PatchConfig(root => AppDriver.Obj(root, "GuiItem")["AutoRun"] = true);
        using var p = inst.Launch(new Dictionary<string, string> { ["DP_CONNECT_AFTER_MS"] = "500" });
        var connected = WaitConnected(c, inst, "перед удалением");
        var taskByApp = Net.WaitUntil(() => TaskExists(AutostartName), TimeSpan.FromSeconds(8), 500);
        if (taskByApp is null)
        {
            var (tc, _, te, _) = Net.Run("schtasks.exe", ["/Create", "/TN", AutostartName, "/TR", $"\"{inst.ExePath}\"", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"], TimeSpan.FromSeconds(30));
            c.Evidence.Add($"задачу «{AutostartName}» на этом запуске программа не завела; заведена стендом: код {tc} {te.Trim()}");
        }
        else
        {
            c.Evidence.Add($"задачу планировщика «{AutostartName}» завела сама программа (флаг AutoRun)");
        }
        //  Run-значение — запасной путь программы без прав администратора; ставим сами.
        using (var run = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            run.SetValue(AutostartName, $"\"{inst.ExePath}\"");
        }
        var schemeByApp = Registry.CurrentUser.OpenSubKey(SchemeKeyPath) is not null;
        if (!schemeByApp)
        {
            using var k = Registry.CurrentUser.CreateSubKey(SchemeKeyPath);
            k.SetValue("URL Protocol", "");
        }
        c.Evidence.Add($"ссылки departamentvpn:// {(schemeByApp ? "записала сама программа" : "записаны стендом")}; перед удалением: задача {(TaskExists(AutostartName) ? "есть" : "нет")}, Run «{AutostartName}» есть, программа {(connected ? "подключена" : "НЕ подключена")}");

        var log = Path.Combine(logs, "3-uninstall.log");
        var sw = Stopwatch.StartNew();
        var (code, _) = RunPlain(Path.Combine(dir, "unins000.exe"), ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + log], TimeSpan.FromMinutes(3));
        //  unins000.exe перезапускает себя из %TEMP% и сразу выходит: удаление закончено, когда исчезли и
        //  запись в «Приложениях», и сам unins000.exe, и его копия (_iu*.tmp).
        var done = Net.WaitUntil(() => UninstallKey() is null && !File.Exists(Path.Combine(dir, "unins000.exe"))
                                       && !Process.GetProcesses().Any(x => x.ProcessName.StartsWith("_iu", StringComparison.OrdinalIgnoreCase)),
            TimeSpan.FromMinutes(3), 250);
        var total = sw.ElapsedMilliseconds;
        var (lines, suspicious) = InnoLog(log);
        //  Папку с настройками деинсталлятор и не должен удалить: «не пуста» здесь — ожидаемо.
        suspicious = suspicious.Where(l => !l.Contains("Failed to delete directory", StringComparison.Ordinal)).ToList();
        var cores = inst.RunningCores();
        var proxy = Win.ReadSystemProxy();
        var programFiles = DistHashes.Keys.Where(k => File.Exists(Path.Combine(dir, k))).ToList();
        var kept = File.Exists(inst.ConfigPath) && File.Exists(inst.DbPath);
        var task = TaskExists(AutostartName);
        var runValue = Registry.CurrentUser.OpenSubKey(RunKeyPath)?.GetValue(AutostartName) is not null;
        var scheme = Registry.CurrentUser.OpenSubKey(SchemeKeyPath) is not null;
        var key = UninstallKey() is not null;
        c.Evidence.Add($"unins000.exe: код {code}; удаление закончено через {(done.HasValue ? $"{total} мс" : "— НЕ закончилось за 3 мин")}");
        c.Evidence.AddRange(lines.Where(l => l.Contains("quit", StringComparison.OrdinalIgnoreCase) || l.Contains("Terminating", StringComparison.Ordinal)).Take(6).Select(l => "журнал удаления: " + l.Trim()));
        c.Evidence.Add($"программа {(p.HasExited ? "вышла" : "ОСТАЛАСЬ")}; ядра: {(cores.Count == 0 ? "нет" : string.Join(", ", cores))}; прокси: {proxy}");
        c.Evidence.Add($"файлов сборки осталось: {programFiles.Count}; guiNConfig.json и guiNDB.db: {(kept ? "на месте" : "НЕТ")}; задача: {(task ? "ОСТАЛАСЬ" : "снята")}; Run: {(runValue ? "ОСТАЛСЯ" : "снят")}; departamentvpn://: {(scheme ? "ОСТАЛИСЬ" : "сняты")}; запись в «Приложениях»: {(key ? "ОСТАЛАСЬ" : "снята")}; ярлыки: «Пуск» {(File.Exists(StartMenuShortcut) ? "ОСТАЛСЯ" : "снят")}, рабочий стол {(File.Exists(DesktopShortcut) ? "ОСТАЛСЯ" : "снят")}");
        var problems = new List<string>();
        if (!done.HasValue)
        {
            problems.Add("удаление не закончилось за 3 мин");
        }
        if (!p.HasExited)
        {
            problems.Add("программа осталась запущенной");
        }
        if (lines.Any(l => l.Contains("did not quit", StringComparison.Ordinal)))
        {
            problems.Add("программа не вышла сама за 15 с, закрыта силой");
        }
        if (suspicious.Count > 0)
        {
            problems.Add("в журнале удаления: " + string.Join(" | ", suspicious.Take(4).Select(l => l.Trim())));
        }
        if (cores.Count > 0)
        {
            problems.Add("ядра остались");
        }
        if (proxy.Enabled)
        {
            problems.Add("системный прокси остался: " + proxy);
        }
        if (programFiles.Count > 0)
        {
            problems.Add($"не удалены {programFiles.Count} файлов программы: {string.Join(", ", programFiles.Take(4))}");
        }
        if (!kept)
        {
            problems.Add("тихое удаление стёрло настройки");
        }
        if (task || runValue || scheme || key)
        {
            problems.Add("остались: " + string.Join(", ", new[] { task ? "задача планировщика" : null, runValue ? "значение Run" : null, scheme ? "ключ departamentvpn" : null, key ? "запись в «Приложениях»" : null }.OfType<string>()));
        }
        if (File.Exists(StartMenuShortcut) || File.Exists(DesktopShortcut))
        {
            problems.Add("остались ярлыки");
        }
        if (!p.HasExited)
        {
            inst.KillEverything();
        }
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"за {total} мс: подключённая программа вышла сама, прокси снят; файлы программы, ярлыки, задача, Run, departamentvpn:// и запись убраны; guiConfigs на месте"
            : string.Join("; ", problems));

        var left = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f))
                .Where(rel => !UserDataDirs.Contains(rel.Split('\\', '/')[0], StringComparer.OrdinalIgnoreCase)).ToList()
            : [];
        leftovers.Evidence.Add($"в {dir} после удаления: {(Directory.Exists(dir) ? string.Join(", ", Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName)) : "папки нет")}");
        leftovers.Set(left.Count == 0 ? Status.Pass : Status.Warn, left.Count == 0
            ? "кроме папок с данными пользователя ничего не осталось"
            : $"остались {left.Count} файлов, которых установщик не ставил: {string.Join(", ", left.Take(6))}. Они пришли с самообновлением (новые файлы нового выпуска); [UninstallDelete] убирает только bin, .update и AmazTool.exe.tmp");
    }

    /// <summary>
    /// Галочка «Запустить departament» на последней странице. Тихая установка её пропускает
    /// (skipifsilent), поэтому workflow собирает копии установщика без этого флага. У человека установщик
    /// запускается двойным щелчком, без прав администратора, и повышается через UAC; postinstall Inno по
    /// умолчанию выполняет от ИСХОДНОГО пользователя, и программе с requireAdministrator Windows отказывает
    /// (CreateProcess, код 740). Из уже повышенного процесса этого не воспроизвести: Inno некуда
    /// «вернуться», поэтому стенд запускает установщик через Explorer — с токеном обычного пользователя.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void InstallerPostinstall(CheckResult c, CheckResult baseline, AppDriver inst, string logs)
    {
        if (o.SetupPostinstall is null || !File.Exists(o.SetupPostinstall))
        {
            c.Set(Status.Skip, "нет копии установщика без skipifsilent (шаг workflow не собрал её)");
            baseline.Set(Status.Skip, "нет копии установщика");
            return;
        }
        inst.KillEverything();
        var (lua, consent) = UacState();
        //  Повыситься без запроса можно, только если UAC включён и администратору не задают вопросов
        //  (ConsentPromptBehaviorAdmin=0): иначе окно UAC ждало бы нажатия, которого в CI нет.
        var asUser = lua == 1 && consent == 0;
        var how = asUser
            ? "установщик запущен через Explorer, с токеном обычного пользователя, UAC повышает без запроса"
            : $"запуск без прав администратора здесь невозможен (EnableLUA={lua?.ToString() ?? "—"}, ConsentPromptBehaviorAdmin={consent?.ToString() ?? "—"}) — установщик запущен из повышенного стенда";

        var run = RunSetupLikeUser(o.SetupPostinstall, Path.Combine(logs, "4-postinstall.log"), "postinstall", asUser);
        c.Evidence.Add(how);
        c.Evidence.Add($"уровень процесса, запустившего установщик: {run.Integrity}; установщик: код {run.Code?.ToString() ?? "— (не закончил за 6 мин)"} за {run.Ms} мс");
        var started = Net.WaitUntil(() => InstalledProcess(inst) is not null, TimeSpan.FromSeconds(20), 250);
        var (lines, _) = InnoLog(run.Log);
        var errors = lines.Where(IsLaunchError).ToList();
        c.Evidence.AddRange(lines.Where(l => l.Contains("departament.exe", StringComparison.OrdinalIgnoreCase) && !l.Contains("Dest filename", StringComparison.Ordinal)
                                             && !l.Contains("Terminating", StringComparison.Ordinal)).Take(4).Select(l => "журнал: " + l.Trim()));
        c.Evidence.AddRange(errors.Take(4).Select(l => "журнал: " + l.Trim()));
        var app = InstalledProcess(inst);
        c.Evidence.Add($"departament.exe из {inst.Dir}: {(app is { } a ? $"запущен (pid {a.Id}) через {started:F0} мс после конца установки" : "НЕ запущен за 20 с")}");
        if (app is not null)
        {
            Net.WaitUntil(() => Win.MainWindowOf(Win.WindowsOf(app.Id)) is not null, TimeSpan.FromSeconds(15), 100);
            Thread.Sleep(1000);
            Win.SaveScreenshot(Path.Combine(_shots, "installer-postinstall.png"));
        }
        QuitInstalled(inst, app);
        var problems = new List<string>();
        if (run.Code != 0)
        {
            problems.Add($"код выхода установщика {run.Code?.ToString() ?? "—"}");
        }
        if (errors.Count > 0)
        {
            problems.Add("в журнале установщика: " + string.Join(" | ", errors.Take(2).Select(l => l.Trim())));
        }
        if (app is null)
        {
            problems.Add("программа в конце установки не запустилась");
        }
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"{(asUser ? "установщик с токеном обычного пользователя (" + run.Integrity + "), повышен UAC" : "установщик из повышенного стенда")}: код 0, программа из Program Files запущена через {started:F0} мс, ни «740», ни «failed; code» в журнале"
            : string.Join("; ", problems));

        if (o.SetupBaseline is null || !File.Exists(o.SetupBaseline))
        {
            baseline.Set(Status.Skip, "нет копии установщика без «runascurrentuser shellexec»");
            return;
        }
        if (!asUser)
        {
            baseline.Set(Status.NotObservable, "из повышенного процесса Inno выполняет postinstall с правами установщика, и 740 не воспроизвести: " + how);
            return;
        }
        var b = RunSetupLikeUser(o.SetupBaseline, Path.Combine(logs, "5-postinstall-baseline.log"), "postinstall-baseline", asUser);
        var bStarted = Net.WaitUntil(() => InstalledProcess(inst) is not null, TimeSpan.FromSeconds(15), 250);
        var (bLines, _) = InnoLog(b.Log);
        var bErrors = bLines.Where(IsLaunchError).ToList();
        baseline.Evidence.Add($"уровень процесса, запустившего установщик: {b.Integrity}; установщик: код {b.Code?.ToString() ?? "—"} за {b.Ms} мс; программа {(bStarted.HasValue ? "ЗАПУЩЕНА" : "не запущена")}");
        baseline.Evidence.AddRange(bErrors.Take(4).Select(l => "журнал: " + l.Trim()));
        QuitInstalled(inst, InstalledProcess(inst));
        var caught = bErrors.Any(l => l.Contains("740", StringComparison.Ordinal));
        baseline.Set(caught && !bStarted.HasValue ? Status.Pass : Status.Fail, caught && !bStarted.HasValue
            ? $"без флагов запуск в конце установки падает: «{bErrors.First(l => l.Contains("740", StringComparison.Ordinal)).Trim()}» — та самая ошибка с кодом; с флагами её нет"
            : $"без флагов ошибка 740 не воспроизвелась (программа {(bStarted.HasValue ? "запущена" : "не запущена")}, строк с ошибкой: {bErrors.Count}) — проверка выше этот случай не различает");
    }

    private static bool IsLaunchError(string l) =>
        l.Contains("740", StringComparison.Ordinal) || l.Contains("CreateProcess failed", StringComparison.OrdinalIgnoreCase)
        || l.Contains("ShellExecuteEx failed", StringComparison.OrdinalIgnoreCase) || l.Contains("failed; code", StringComparison.OrdinalIgnoreCase)
        || l.Contains("Unable to execute", StringComparison.OrdinalIgnoreCase);

    private sealed record SetupRun(int? Code, long Ms, string Integrity, string Log);

    /// <summary>
    /// Установщик так, как его запускает человек: через Explorer, то есть с токеном обычного
    /// пользователя (Explorer не повышен), а права администратора установщик просит у UAC сам.
    /// Запуск идёт через .cmd, который ждёт установщик и записывает его код.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private SetupRun RunSetupLikeUser(string setup, string log, string tag, bool viaExplorer)
    {
        if (!viaExplorer)
        {
            var (elevatedCode, elevatedMs) = RunPlain(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=" + log], TimeSpan.FromMinutes(6));
            return new SetupRun(elevatedCode, elevatedMs, "High (стенд)", log);
        }
        var dir = Path.Combine(o.Work, "setup-as-user");
        Directory.CreateDirectory(dir);
        var done = Path.Combine(dir, tag + ".done");
        var who = Path.Combine(dir, tag + ".whoami.txt");
        var cmd = Path.Combine(dir, tag + ".cmd");
        File.Delete(done);
        File.Delete(who);
        File.WriteAllText(cmd, string.Join("\r\n",
            "@echo off",
            $"whoami /groups > \"{who}\"",
            $"\"{setup}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- \"/LOG={log}\"",
            $"echo %ERRORLEVEL% > \"{done}\"",
            ""));
        var sw = Stopwatch.StartNew();
        using (Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { cmd } }))
        {
        }
        var finished = Net.WaitUntil(() => File.Exists(done) && ReadShared(done).Trim().Length > 0, TimeSpan.FromMinutes(6), 250);
        var integrity = System.Text.RegularExpressions.Regex.Match(ReadShared(who), @"Mandatory Label\\(\w+) Mandatory Level") is { Success: true } m
            ? m.Groups[1].Value : "не определён";
        int? code = finished.HasValue && int.TryParse(ReadShared(done).Trim(), out var x) ? x : null;
        return new SetupRun(code, sw.ElapsedMilliseconds, integrity, log);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static (int? EnableLua, int? ConsentPromptBehaviorAdmin) UacState()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        return (k?.GetValue("EnableLUA") as int?, k?.GetValue("ConsentPromptBehaviorAdmin") as int?);
    }

    private static Process? InstalledProcess(AppDriver inst) =>
        Process.GetProcessesByName("departament").FirstOrDefault(p => ProcessPath(p) is { } path && SamePath(path, inst.ExePath));

    /// <summary>Закрыть установленную программу штатно («--quit»), не вышла за 10 с — силой.</summary>
    private static void QuitInstalled(AppDriver inst, Process? app)
    {
        if (app is null)
        {
            return;
        }
        RunPlain(inst.ExePath, ["--quit"], TimeSpan.FromSeconds(30));
        if (!app.WaitForExit(10_000))
        {
            inst.KillEverything();
        }
        app.Dispose();
    }

    #region Помощники

    private (int Code, long Ms, string Log) RunSetup(string log)
    {
        var (code, ms) = RunPlain(o.Setup!, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=" + log], TimeSpan.FromMinutes(6));
        return (code, ms, log);
    }

    /// <summary>
    /// Запуск без перехвата вывода. Установщик и деинсталлятор порождают процессы, которые наследовали бы
    /// перехваченный stdout, и чтение вывода ждало бы их всех, а не сам запущенный exe.
    /// </summary>
    internal static (int Code, long Ms) RunPlain(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        var sw = Stopwatch.StartNew();
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start вернул null");
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(true); } catch { }
                return (-1, sw.ElapsedMilliseconds);
            }
            return (p.ExitCode, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Info($"{Path.GetFileName(exe)} не запустился: {ex.Message}");
            return (-2, sw.ElapsedMilliseconds);
        }
    }

    private static (List<string> Lines, List<string> Suspicious) InnoLog(string path)
    {
        var lines = ReadShared(path).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var suspicious = lines.Where(l =>
            l.Contains("in use", StringComparison.OrdinalIgnoreCase) || l.Contains("Retrying", StringComparison.Ordinal)
            || l.Contains("replace on restart", StringComparison.OrdinalIgnoreCase) || l.Contains("Need to restart Windows? Yes", StringComparison.Ordinal)
            || l.Contains("Failed", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal)
            || l.Contains("Terminating", StringComparison.Ordinal) || l.Contains("WMI:", StringComparison.Ordinal)).ToList();
        return (lines, suspicious);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static RegistryKey? UninstallKey(bool writable = false) =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(UninstallKeyPath, writable);

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "departament VPN.lnk");

    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "departament VPN.lnk");

    private static string? ShortcutTarget(string lnk)
    {
        var (code, so, _, _) = Net.Run("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"(New-Object -ComObject WScript.Shell).CreateShortcut('{lnk.Replace("'", "''")}').TargetPath"],
            TimeSpan.FromSeconds(60));
        return code == 0 && so.Trim().Length > 0 ? so.Trim() : null;
    }

    private static bool TaskExists(string name) =>
        Net.Run("schtasks.exe", ["/Query", "/TN", name], TimeSpan.FromSeconds(30)).ExitCode == 0;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Настройки и узлы основного запуска — в установленную программу: узел VLESS выбран, режим
    /// «Только прокси» с системным прокси (SysProxyType=1), чтобы было что снимать при выходе.
    /// </summary>
    private void PrepareConnectedConfig(AppDriver inst)
    {
        if (!CanConnect)
        {
            return;
        }
        CopyDir(_app.ConfigDir, inst.ConfigDir);
        inst.PatchConfig(root =>
        {
            root["IndexId"] = _vless!.IndexId;
            AppDriver.Obj(root, "TunModeItem")["EnableTun"] = false;
            AppDriver.Obj(root, "SystemProxyItem")["SysProxyType"] = 1;
        });
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private bool WaitConnected(CheckResult c, AppDriver inst, string when)
    {
        if (!CanConnect)
        {
            c.Evidence.Add($"{when}: без подключения — нет узла VLESS или сервер не прошёл самопроверку");
            return false;
        }
        var expected = $"127.0.0.1:{o.LocalPort}";
        var listen = Net.WaitUntil(() => Net.IsListening(o.LocalPort), TimeSpan.FromSeconds(25));
        var proxy = listen.HasValue
            ? Net.WaitUntil(() => IsWin && Win.ReadSystemProxy() is { Enabled: true } s && s.Server!.Contains(expected), TimeSpan.FromSeconds(10), 50)
            : null;
        c.Evidence.Add($"{when}: ядро слушает {expected} {(listen is { } l ? $"через {l:F0} мс" : "— НЕТ")}; прокси: {Win.ReadSystemProxy()}; ядра: {string.Join(", ", inst.RunningCores())}");
        return listen.HasValue && proxy.HasValue;
    }

    #endregion Помощники
}
