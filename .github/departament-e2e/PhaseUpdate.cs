using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DpE2E;

/// <summary>Пакет «новой версии» для проверок установки: путь, его сумма и хеши exe внутри.</summary>
internal sealed record NewPackage(string Zip, string ZipHash, string ExeHash, string ToolHash, Dictionary<string, string> Files);

/// <summary>Один прогон AmazTool: что он написал в журнал и как себя вёл.</summary>
internal sealed record AmazRun(int? ExitCode, List<string> Log, double? WaitingMs, bool UntouchedWhileWaiting, long AfterReleaseMs, int? StartedPid, string Timeline)
{
    public string Text => string.Join('\n', Log);
}

/// <summary>
/// Самообновление (AmazTool) на настоящей Windows: блокировки файлов, права, Defender. В выпускной
/// сборке подставной ленты нет (DP_DEV_UPDATE_* — только Debug), поэтому приложение здесь не качает
/// обновление само: стенд делает то же, что AppUpdateManager.InstallAsync, — кладёт пакет, сумму и
/// pending.json в guiTemps/update и запускает AmazTool с PID «приложения».
/// </summary>
internal sealed partial class E2E
{
    private const string PackageTop = "departament-windows-x64";

    //  Те же папки, что UpgradeApp.UserDataDirs: их установщик не трогает никогда.
    private static readonly string[] UserDataDirs = ["guiConfigs", "guiLogs", "binConfigs", "guiTemps", "guiBackups", "guiFonts", ".update"];

    //  Хвост, которым «новые» departament.exe и AmazTool.exe отличаются от старых. Загрузчик PE и хост
    //  одиночного файла .NET данные после образа не читают, а хеш у файла другой: видно, какой exe на месте.
    private static readonly byte[] NewBuildTail = Encoding.ASCII.GetBytes("\0\0departament-e2e: this file stands for the NEW build\0\0");

    private NewPackage? _newPackage;
    private Dictionary<string, string>? _distHashes;

    private string UpdRoot => Path.Combine(o.Work, "upd");
    private string UpdOut => Path.Combine(o.Out, "update");

    private static string Exe(string baseName) => OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;

    private Dictionary<string, string> DistHashes => _distHashes ??= HashTree(o.Dist!);

    private void PhaseUpdate()
    {
        var package = report.Add("update.package", "Самообновление: пакет по договору — одна папка departament-windows-x64/, нужные файлы, .sha256 = Get-FileHash");
        var tool = report.Add("update.tool", "Самообновление: AmazTool — без консоли, один файл NativeAOT, меньше 5 МБ");
        var version = report.Add("update.version", "Самообновление: версия в departament.exe и та, что программа прочтёт о себе, — ожидаемая");
        var devhook = report.Add("update.devhook", "Самообновление: в выпуске нет подставной ленты (DP_DEV_UPDATE_*) и отладочного кода");
        var install = report.Add("update.install", "AmazTool ставит новую версию: ждёт выхода по PID, меняет файлы, данные пользователя не трогает");
        var restart = report.Add("update.restart", "После обновления запускается новая версия и доводит уборку");
        var rollback = report.Add("update.rollback", "AmazTool при занятом файле откатывает замену: прежние файлы на месте");
        var rollbackRestart = report.Add("update.rollback-restart", "После отката запускается прежняя версия и сообщает, что обновление не встало");
        var all = new[] { package, tool, version, devhook, install, restart, rollback, rollbackRestart };

        if (o.Dist is not { } dist || !File.Exists(Path.Combine(dist, Exe("departament"))))
        {
            foreach (var c in all)
            {
                c.Set(Status.Skip, "нет чистой копии сборки (--dist)");
            }
            return;
        }
        _app.KillEverything();
        Directory.CreateDirectory(UpdRoot);
        Directory.CreateDirectory(UpdOut);
        DefenderRealtime();

        var zip = CheckPackage(package, dist);
        CheckTool(tool, dist);
        {
            var exeBytes = File.ReadAllBytes(Path.Combine(dist, Exe("departament")));
            var bundle = Binary.ReadBundle(exeBytes, out var bundleNote);
            CheckVersion(version, dist, exeBytes, bundle, bundleNote);
            CheckDevHooks(devhook, dist, exeBytes, bundle, bundleNote);
        }
        GC.Collect();

        if (zip is null)
        {
            foreach (var c in new[] { install, restart, rollback, rollbackRestart })
            {
                c.Set(Status.Skip, "пакета нет или в нём нет exe приложения и AmazTool");
            }
            return;
        }
        _newPackage = MakeNewPackage(zip);
        Log.Info($"пакет «новой версии»: {_newPackage.Zip}, {_newPackage.Files.Count} файлов");
        UpdateSmoke(install, restart, dist, _newPackage);
        UpdateRollback(rollback, rollbackRestart, dist, _newPackage);
    }

    #region Пакет, AmazTool, версия, отладочные хуки

    private string? CheckPackage(CheckResult c, string dist)
    {
        string zip, sha, source;
        if (o.ReleaseZip is { } releaseZip)
        {
            zip = releaseZip;
            sha = o.ReleaseSha256 ?? releaseZip + ".sha256";
            source = "архив выпуска (release-assets)";
        }
        else
        {
            //  На ветке архив в артефакт не выкладывается. Собираем его ровно как шаг «Package release zip»
            //  в departament-build.yml: копия dist под departament-windows-x64 и
            //  ZipFile.CreateFromDirectory(Optimal, includeBaseDirectory: true), сумма в формате sha256sum.
            var stage = Path.Combine(UpdRoot, "pkg");
            DeleteDir(stage);
            CopyTree(dist, Path.Combine(stage, PackageTop));
            zip = Path.Combine(UpdRoot, PackageTop + ".zip");
            File.Delete(zip);
            var sw = Stopwatch.StartNew();
            ZipFile.CreateFromDirectory(Path.Combine(stage, PackageTop), zip, CompressionLevel.Optimal, includeBaseDirectory: true);
            c.Evidence.Add($"архив собран за {sw.ElapsedMilliseconds} мс");
            DeleteDir(stage);
            sha = zip + ".sha256";
            File.WriteAllText(sha, $"{Binary.Sha256(zip)}  {PackageTop}.zip\n");
            source = "собран из артефакта сборки так же, как в departament-build.yml";
        }
        if (!File.Exists(zip))
        {
            c.Set(Status.Fail, $"нет архива {zip}");
            return null;
        }

        var problems = new List<string>();
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        long unpacked = 0;
        int entries;
        using (var archive = ZipFile.OpenRead(zip))
        {
            entries = archive.Entries.Count;
            if (entries > 20_000)
            {
                problems.Add($"записей {entries} — больше 20 000, программа такой пакет отвергнет");
            }
            foreach (var e in archive.Entries)
            {
                var name = e.FullName;
                //  Договор сборки строже AmazTool: он «\» терпит, а сборка такой архив не выпускает.
                if (name.Contains('\\'))
                {
                    problems.Add($"«\\» в имени записи: {name}");
                    continue;
                }
                if (!name.StartsWith(PackageTop + "/", StringComparison.Ordinal))
                {
                    problems.Add($"запись вне {PackageTop}/: {name}");
                    continue;
                }
                var rel = name[(PackageTop.Length + 1)..];
                if (rel.Length == 0)
                {
                    continue;
                }
                var segments = rel.TrimEnd('/').Split('/');
                if (segments.Any(s => s is "" or "." or ".." || s.Contains(':')))
                {
                    problems.Add($"небезопасный путь: {name}");
                    continue;
                }
                if (UserDataDirs.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"в пакете папка данных пользователя: {name}");
                }
                unpacked += e.Length;
                if (!rel.EndsWith('/'))
                {
                    files[rel] = e.Length;
                }
            }
        }
        if (unpacked > 1L << 30)
        {
            problems.Add($"в распакованном виде {unpacked / 1048576} МБ — больше 1 ГиБ, программа такой пакет отвергнет");
        }
        string[] required = IsWin
            ? ["departament.exe", "AmazTool.exe", "bin/Xray/xray.exe", "bin/Xray/wintun.dll", "bin/sing_box/sing-box.exe", "bin/geoip.dat", "bin/geosite.dat"]
            : [Exe("departament"), Exe("AmazTool")];
        foreach (var must in required)
        {
            if (!files.TryGetValue(must, out var len) || len == 0)
            {
                problems.Add($"нет {PackageTop}/{must}");
            }
        }

        //  Содержимое — ровно то, что проверялось в остальных фазах (для архива выпуска это и есть довод,
        //  что выпущено проверенное).
        var distFiles = Directory.EnumerateFiles(dist, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dist, f).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var missing = distFiles.Where(f => !files.ContainsKey(f)).ToList();
        var extra = files.Keys.Where(f => !distFiles.Contains(f)).ToList();
        if (missing.Count > 0)
        {
            problems.Add($"в архиве нет {missing.Count} файлов сборки: {string.Join(", ", missing.Take(5))}");
        }
        if (extra.Count > 0)
        {
            problems.Add($"в архиве {extra.Count} файлов, которых нет в сборке: {string.Join(", ", extra.Take(5))}");
        }
        if (o.ReleaseZip is not null)
        {
            var inZip = ZipHashes(zip);
            var differ = inZip.Where(kv => DistHashes.TryGetValue(kv.Key, out var h) && h != kv.Value).Select(kv => kv.Key).ToList();
            c.Evidence.Add($"содержимое архива выпуска сверено с проверенной сборкой по SHA-256: расходится {differ.Count} файлов");
            if (differ.Count > 0)
            {
                problems.Add($"в архиве выпуска другие файлы, чем в проверенной сборке: {string.Join(", ", differ.Take(5))}");
            }
        }

        //  Сумма: как её разберёт программа (AppUpdateChecksum.TryParse) и что скажет Get-FileHash.
        var own = Binary.Sha256(zip);
        var byPowerShell = IsWin ? GetFileHash(zip) : null;
        var shaText = File.Exists(sha) ? File.ReadAllText(sha) : null;
        var lines = (shaText ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        var m = lines.Count == 1 ? Regex.Match(lines[0].Trim(), @"^([0-9A-Fa-f]{64})(?:[ \t]+\*?(\S.*))?$") : Match.Empty;
        if (shaText is null)
        {
            problems.Add("нет файла .sha256");
        }
        else if (!m.Success)
        {
            problems.Add($".sha256 не в формате sha256sum: «{Shorten(shaText.Trim())}»");
        }
        else
        {
            var hex = m.Groups[1].Value;
            var name = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
            if (name is not null && name != PackageTop + ".zip")
            {
                problems.Add($"в .sha256 имя «{name}», программа ждёт {PackageTop}.zip");
            }
            if (hex != hex.ToLowerInvariant())
            {
                problems.Add("сумма в .sha256 не в нижнем регистре");
            }
            if (!string.Equals(hex, own, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"сумма в .sha256 {hex} ≠ SHA-256 архива {own}");
            }
            if (byPowerShell is not null && byPowerShell != hex)
            {
                problems.Add($"Get-FileHash даёт {byPowerShell}, в .sha256 — {hex}");
            }
            if (shaText != $"{own}  {PackageTop}.zip\n")
            {
                c.Evidence.Add($"строка суммы не ровно «<hex>  {PackageTop}.zip\\n», но программа её примет: «{Shorten(shaText)}»");
            }
        }
        if (byPowerShell is null && IsWin)
        {
            problems.Add("Get-FileHash не ответил");
        }

        var size = new FileInfo(zip).Length;
        c.Evidence.Add($"источник: {source}");
        c.Evidence.Add($"{size / 1048576.0:F1} МБ, записей {entries}, файлов {files.Count}, в распакованном виде {unpacked / 1048576.0:F0} МБ");
        c.Evidence.Add($"SHA-256 архива {own}; Get-FileHash {(byPowerShell ?? "—")}; .sha256: «{Shorten((shaText ?? "").Trim())}»");
        if (files.Keys.Where(f => f.StartsWith("bin/srss/", StringComparison.Ordinal)).ToList() is { Count: > 0 } srs)
        {
            c.Evidence.Add($"наборы правил в bin/srss: {srs.Count} ({string.Join(", ", srs.Select(Path.GetFileName).Take(6))}{(srs.Count > 6 ? ", …" : "")})");
        }
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"{source}: {size / 1048576.0:F1} МБ, {files.Count} файлов, все под {PackageTop}/ с «/», exe и AmazTool на месте; .sha256 = SHA-256 = Get-FileHash ({own[..12]}…)"
            : string.Join("; ", problems.Take(6)));
        return files.ContainsKey(Exe("departament")) && files.ContainsKey(Exe("AmazTool")) ? zip : null;
    }

    private void CheckTool(CheckResult c, string dist)
    {
        var path = Path.Combine(dist, Exe("AmazTool"));
        if (!File.Exists(path))
        {
            c.Set(Status.Fail, "AmazTool в сборке нет: обновляться будет нечем");
            return;
        }
        var problems = new List<string>();
        var size = new FileInfo(path).Length;
        if (size >= 5L * 1048576)
        {
            problems.Add($"{size / 1048576.0:F1} МБ — не меньше 5 МБ");
        }
        var pe = Binary.ReadPe(path);
        string peNote;
        if (pe is null)
        {
            peNote = IsWin ? "заголовок PE не читается" : "не PE (подсистема проверяется на Windows)";
            if (IsWin)
            {
                problems.Add(peNote);
            }
        }
        else
        {
            peNote = $"PE {pe.Machine}, подсистема {pe.Subsystem} ({(int)pe.Subsystem}), {(pe.Managed ? "есть заголовок CLR (managed)" : "машинный код без CLR (NativeAOT)")}";
            if (pe.Subsystem != Subsystem.WindowsGui)
            {
                problems.Add($"подсистема {pe.Subsystem}: при обновлении у человека откроется окно консоли");
            }
            if (pe.Managed)
            {
                problems.Add("в exe заголовок CLR: это не NativeAOT, ему нужна среда .NET рядом");
            }
            if (pe.Machine != Machine.Amd64)
            {
                problems.Add($"машина {pe.Machine}, ждали Amd64");
            }
        }
        c.Evidence.Add($"{size} байт ({size / 1048576.0:F2} МБ); {peNote}");

        var siblings = new[] { "AmazTool.dll", "AmazTool.pdb", "AmazTool.deps.json", "AmazTool.runtimeconfig.json", "AmazTool.dbg" }
            .Where(f => File.Exists(Path.Combine(dist, f))).ToList();
        if (siblings.Count > 0)
        {
            problems.Add("рядом лежат " + string.Join(", ", siblings));
        }
        var pdbs = Directory.EnumerateFiles(dist, "*.pdb", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dist, f)).ToList();
        if (pdbs.Count > 0)
        {
            problems.Add("в сборке pdb: " + string.Join(", ", pdbs.Take(5)));
        }
        var rootDlls = Directory.EnumerateFiles(dist, "*.dll").Select(Path.GetFileName).ToList();
        c.Evidence.Add($"своих файлов рядом у AmazTool нет; dll в корне сборки (это библиотеки приложения, которых нет в одиночном exe): {(rootDlls.Count == 0 ? "нет" : string.Join(", ", rootDlls))}");

        //  Один файл без среды: запуск в пустой папке, где рядом нет ни dll, ни .NET.
        var alone = Path.Combine(UpdRoot, "alone");
        DeleteDir(alone);
        Directory.CreateDirectory(alone);
        var copy = Path.Combine(alone, Exe("AmazTool"));
        File.Copy(path, copy);
        var (code, _, se, ms) = Net.Run(copy, [], TimeSpan.FromSeconds(30), alone);
        var logText = ReadShared(Path.Combine(alone, "guiLogs", "upgrade.log"));
        c.Evidence.Add($"в пустой папке без аргументов: код {code} за {ms} мс, upgrade.log: {Shorten(logText.Replace("\r", "").Replace('\n', '|'))}");
        if (!logText.Contains("no command, nothing to do", StringComparison.Ordinal))
        {
            problems.Add($"в пустой папке AmazTool не отработал (код {code}{(se.Length > 0 ? ", " + Shorten(se.Trim()) : "")})");
        }
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"{size / 1048576.0:F2} МБ, {peNote}; рядом ни dll, ни pdb; в пустой папке работает сам (код {code}, {ms} мс)"
            : string.Join("; ", problems));
    }

    private static AssemblyFacts? FactsOf(byte[] exe, List<BundleEntry>? bundle, string name) =>
        bundle?.FirstOrDefault(e => e.Path == name) is { } entry ? Binary.ReadAssembly(Binary.Extract(exe, entry)) : null;

    private void CheckVersion(CheckResult c, string dist, byte[] exeBytes, List<BundleEntry>? bundle, string bundleNote)
    {
        var exe = Path.Combine(dist, Exe("departament"));
        var fv = FileVersionInfo.GetVersionInfo(exe);
        c.Evidence.Add($"departament.exe: ProductVersion «{fv.ProductVersion}», FileVersion «{fv.FileVersion}»");
        var toolPath = Path.Combine(dist, Exe("AmazTool"));
        if (File.Exists(toolPath))
        {
            var tv = FileVersionInfo.GetVersionInfo(toolPath);
            c.Evidence.Add($"AmazTool: ProductVersion «{tv.ProductVersion}», FileVersion «{tv.FileVersion}»");
        }
        c.Evidence.Add(bundleNote);
        var serviceLib = FactsOf(exeBytes, bundle, "ServiceLib.dll");
        var desktop = FactsOf(exeBytes, bundle, "departament.dll");
        c.Evidence.Add($"ServiceLib.dll в exe: InformationalVersion «{serviceLib?.InformationalVersion ?? "—"}» — её читает Utils.GetVersionInfo, с ней программа сравнивает выпуски");
        c.Evidence.Add($"departament.dll в exe: InformationalVersion «{desktop?.InformationalVersion ?? "—"}»");
        if (o.ExpectedVersion is not { } expected)
        {
            c.Set(Status.NotObservable, "ожидаемая версия не передана (--expected-version)");
            return;
        }
        static bool Same(string? v, string exp) => v is not null && (v == exp || v.StartsWith(exp + "+", StringComparison.Ordinal));
        var problems = new List<string>();
        if (IsWin && !Same(fv.ProductVersion, expected))
        {
            problems.Add($"ProductVersion departament.exe «{fv.ProductVersion}», а ждали {expected}");
        }
        if (serviceLib?.InformationalVersion is { } running && !Same(running, expected))
        {
            problems.Add($"программа назовёт себя «{running.Split('+')[0]}», а не {expected}: выпуски она будет сравнивать не с той версией");
        }
        if (problems.Count > 0)
        {
            c.Set(Status.Fail, string.Join("; ", problems));
        }
        else if (serviceLib?.InformationalVersion is null)
        {
            c.Set(IsWin ? Status.Pass : Status.NotObservable, $"ProductVersion «{fv.ProductVersion}» = {expected}; версию внутри exe прочесть не удалось ({bundleNote})");
        }
        else
        {
            c.Set(Status.Pass, $"ожидали {expected}: departament.exe ProductVersion «{fv.ProductVersion}», программа прочтёт о себе «{serviceLib.InformationalVersion.Split('+')[0]}»");
        }
    }

    private void CheckDevHooks(CheckResult c, string dist, byte[] exeBytes, List<BundleEntry>? bundle, string bundleNote)
    {
        var problems = new List<string>();
        var blind = new List<string>();
        var u16 = Encoding.Unicode;
        var u8 = Encoding.UTF8;

        //  Поиск по байтам на любом смещении. Select-String -Encoding Unicode видит только чётные смещения,
        //  а строки кучи #US в метаданных .NET лежат и на нечётных.
        void Probe(string where, ReadOnlySpan<byte> bytes, string forbidden, Encoding enc, string control, bool required = true)
        {
            var bad = Binary.Count(bytes, forbidden, enc);
            var ctl = Binary.Count(bytes, control, enc);
            var encName = enc == u16 ? "UTF-16LE" : "UTF-8";
            c.Evidence.Add($"{where}: «{forbidden}» ({encName}) — {bad}; контроль «{control}» — {ctl}");
            if (bad > 0)
            {
                problems.Add($"{where}: «{forbidden}» найдено {bad} раз");
            }
            else if (ctl == 0 && required)
            {
                blind.Add($"{where}: контрольной строки «{control}» нет, поиск здесь ничего не доказывает");
            }
        }

        Probe("departament.exe целиком", exeBytes, "DP_DEV_UPDATE", u16, "DP_TRAY_CYCLE");
        if (bundle is null)
        {
            blind.Add("оглавление exe не прочитано: " + bundleNote);
        }
        else
        {
            //  Подставная лента — в ServiceLib (AppUpdateChannel), экран состояний — в departament.dll
            //  (UpdateNotice). Отладочные методы ищутся по именам в метаданных (UTF-8).
            foreach (var (asm, control16, forbidden8, control8) in new[]
                     {
                         ("ServiceLib.dll", "V2RAYN_LOCAL_APPLICATION_DATA_V2", "DevShowState", "EnsureReconciled"),
                         ("departament.dll", "DP_TRAY_CYCLE", "WithDeveloperTools", "StartAutoCheck"),
                     })
            {
                var entry = bundle.FirstOrDefault(e => e.Path == asm);
                if (entry is null)
                {
                    blind.Add($"{asm} в exe не найден");
                    continue;
                }
                var bytes = Binary.Extract(exeBytes, entry);
                c.Evidence.Add($"{asm}: {entry.Size} байт, {(entry.CompressedSize > 0 ? $"сжат до {entry.CompressedSize}" : "не сжат")}");
                Probe(asm, bytes, "DP_DEV_UPDATE", u16, control16);
                Probe(asm, bytes, forbidden8, u8, control8);
                if (Binary.ReadAssembly(bytes)?.DebuggableModes is { } modes)
                {
                    var debug = (modes & Binary.DebuggingModesDisableOptimizations) != 0;
                    c.Evidence.Add($"{asm}: DebuggableAttribute {modes} — {(debug ? "оптимизация ВЫКЛЮЧЕНА, это Debug" : "Release")}");
                    if (debug)
                    {
                        problems.Add($"{asm} собран как Debug");
                    }
                }
            }
        }
        //  В AmazTool (NativeAOT) строки лежат в сжатых «dehydrated» данных и в байтах файла не видны даже
        //  контрольные: поиск там слеп, поэтому он только для сведения. Хуков DP_DEV_UPDATE в AmazTool нет и
        //  в исходниках.
        var toolPath = Path.Combine(dist, Exe("AmazTool"));
        if (File.Exists(toolPath))
        {
            Probe("AmazTool (для сведения)", File.ReadAllBytes(toolPath), "DP_DEV_UPDATE", u16, "upgrade.log", required: false);
        }
        c.Set(problems.Count > 0 ? Status.Fail : blind.Count > 0 ? Status.NotObservable : Status.Pass,
            problems.Count > 0 ? string.Join("; ", problems)
            : blind.Count > 0 ? string.Join("; ", blind)
            : "«DP_DEV_UPDATE» нет ни в exe целиком, ни в ServiceLib.dll и departament.dll внутри него (контрольные строки там находятся), отладочных DevShowState и WithDeveloperTools нет, обе сборки — Release");
    }

    private static string? GetFileHash(string path)
    {
        var (code, so, _, _) = Net.Run("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", $"(Get-FileHash -Algorithm SHA256 -LiteralPath '{path.Replace("'", "''")}').Hash.ToLowerInvariant()"],
            TimeSpan.FromMinutes(2));
        var hex = so.Trim();
        return code == 0 && hex.Length == 64 ? hex : null;
    }

    #endregion Пакет, AmazTool, версия, отладочные хуки

    #region Установка поверх старой версии и откат

    private NewPackage MakeNewPackage(string pristineZip)
    {
        var dir = Path.Combine(UpdRoot, "new");
        DeleteDir(dir);
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, PackageTop + ".zip");
        File.Copy(pristineZip, zip);
        string exeHash, toolHash;
        using (var za = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            exeHash = AppendTail(za, $"{PackageTop}/{Exe("departament")}");
            toolHash = AppendTail(za, $"{PackageTop}/{Exe("AmazTool")}");
            Put(za, $"{PackageTop}/e2e-marker.txt", "new");
            Put(za, $"{PackageTop}/bin/e2e-marker.txt", "new");
            Put(za, $"{PackageTop}/e2e-added/added.txt", "added by the new package");
            //  Ловушки: пакет, в котором вдруг оказались данные пользователя, не должен их затереть.
            Put(za, $"{PackageTop}/guiConfigs/e2e-user.txt", "FROM THE PACKAGE: must never replace user data");
            Put(za, $"{PackageTop}/guiConfigs/e2e-trap.txt", "FROM THE PACKAGE: must never land in guiConfigs");
        }
        return new NewPackage(zip, Binary.Sha256(zip), exeHash, toolHash, ZipHashes(zip));
    }

    private static string AppendTail(ZipArchive za, string name)
    {
        var old = za.GetEntry(name) ?? throw new InvalidDataException($"в пакете нет {name}");
        byte[] data;
        using (var s = old.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            ms.Write(NewBuildTail);
            data = ms.ToArray();
        }
        var time = old.LastWriteTime;
        old.Delete();
        var entry = za.CreateEntry(name, CompressionLevel.Fastest);
        entry.LastWriteTime = time;
        using (var s = entry.Open())
        {
            s.Write(data);
        }
        return Binary.Sha256(data);
    }

    private static void Put(ZipArchive za, string name, string text)
    {
        za.GetEntry(name)?.Delete();
        using var w = new StreamWriter(za.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    /// <summary>SHA-256 файлов пакета без папок данных пользователя: что должно оказаться в каталоге после замены.</summary>
    private static Dictionary<string, string> ZipHashes(string zip)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var e in archive.Entries)
        {
            var name = e.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');
            var rel = slash < 0 ? name : name[(slash + 1)..];
            if (rel.Length == 0 || rel.EndsWith('/') || UserDataDirs.Contains(rel.Split('/')[0], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            using var s = e.Open();
            d[rel] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s));
        }
        return d;
    }

    /// <summary>
    /// Каталог «старой версии»: чистая копия сборки, метки «old», файл пользователя и то, что программа
    /// оставляет перед передачей установщику (AppUpdateManager.InstallAsync): пакет, его сумма и
    /// pending.json с тегом выпуска в guiTemps/update.
    /// </summary>
    private static string PrepareOldInstall(string dir, string dist, NewPackage pkg, string pendingTag)
    {
        DeleteDir(dir);
        CopyTree(dist, dir);
        File.WriteAllText(Path.Combine(dir, "e2e-marker.txt"), "old");
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        File.WriteAllText(Path.Combine(dir, "bin", "e2e-marker.txt"), "old");
        Directory.CreateDirectory(Path.Combine(dir, "guiConfigs"));
        File.WriteAllText(Path.Combine(dir, "guiConfigs", "e2e-user.txt"), $"user data {Guid.NewGuid()}");
        return StageUpdateFiles(dir, pkg, pendingTag);
    }

    /// <param name="pending">Метка передачи целиком (AppUpdatePendingInstall); null — метка старого вида, только тег.</param>
    private static string StageUpdateFiles(string dir, NewPackage pkg, string pendingTag, object? pending = null)
    {
        var upd = Path.Combine(dir, "guiTemps", "update");
        Directory.CreateDirectory(upd);
        var zip = Path.Combine(upd, PackageTop + ".zip");
        File.Copy(pkg.Zip, zip, overwrite: true);
        File.WriteAllText(zip + ".sha256", $"{pkg.ZipHash}  {PackageTop}.zip\n");
        File.WriteAllText(Path.Combine(upd, "pending.json"), JsonSerializer.Serialize(pending ?? new { Tag = pendingTag, PreRelease = false }));
        return zip;
    }

    /// <summary>Процесс, который изображает запущенную программу: AmazTool ждёт его выхода по PID.</summary>
    private static Process StartSleeper()
    {
        var psi = IsWin
            ? new ProcessStartInfo("ping.exe") { ArgumentList = { "-n", "900", "127.0.0.1" } }
            : new ProcessStartInfo("sleep") { ArgumentList = { "900" } };
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        return Process.Start(psi) ?? throw new InvalidOperationException("не запустился процесс-заглушка");
    }

    /// <summary>
    /// AmazTool так, как его запускает программа (InstallAsync): без оболочки, рабочая папка — каталог
    /// установки, аргументы списком. <paramref name="release"/> отпускает «приложение», пока AmazTool ждёт.
    /// </summary>
    private AmazRun RunAmazTool(string dir, string package, int appPid, Action release, string name, int restartedExitMs = 60000)
    {
        var log = Path.Combine(dir, "guiLogs", "upgrade.log");
        var psi = new ProcessStartInfo(Path.Combine(dir, Exe("AmazTool"))) { UseShellExecute = false, WorkingDirectory = dir };
        foreach (var a in new[] { "upgrade", "--package", package, "--pid", appPid.ToString() })
        {
            psi.ArgumentList.Add(a);
        }
        //  Окружение наследует и перезапущенная программа: вехи её первого кадра и страховочный выход.
        var timeline = Path.Combine(_timelines, $"timeline-{name}.txt");
        File.Delete(timeline);
        psi.Environment["DP_TIMELINE"] = timeline;
        psi.Environment["DP_EXIT_AFTER_MS"] = restartedExitMs.ToString();
        var marker = Path.Combine(dir, "e2e-marker.txt");
        var markerBefore = File.Exists(marker) ? File.ReadAllText(marker) : null;

        var sw = Stopwatch.StartNew();
        using var amaz = Process.Start(psi) ?? throw new InvalidOperationException("AmazTool не запустился");
        var waiting = Net.WaitUntil(() => ReadShared(log).Contains($"waiting for pid {appPid} to exit", StringComparison.Ordinal), TimeSpan.FromSeconds(20), 50);
        //  Пока «программа» работает, установщик не трогает ни одного файла и не создаёт рабочий каталог.
        Thread.Sleep(1500);
        var untouched = !amaz.HasExited && !Directory.Exists(Path.Combine(dir, ".update"))
                        && (File.Exists(marker) ? File.ReadAllText(marker) : null) == markerBefore;
        var releasedAt = sw.ElapsedMilliseconds;
        release();
        var exited = amaz.WaitForExit(TimeSpan.FromMinutes(3));
        var afterRelease = sw.ElapsedMilliseconds - releasedAt;
        int? code = null;
        if (exited)
        {
            code = amaz.ExitCode;
        }
        else
        {
            try { amaz.Kill(true); } catch { }
        }
        var lines = ReadShared(log).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        int? started = null;
        foreach (var l in lines)
        {
            if (Regex.Match(l, @"started .* \(pid (\d+)\)") is { Success: true } mm)
            {
                started = int.Parse(mm.Groups[1].Value);
            }
        }
        if (File.Exists(log))
        {
            Directory.CreateDirectory(UpdOut);
            File.Copy(log, Path.Combine(UpdOut, $"upgrade-{name}.log"), overwrite: true);
        }
        return new AmazRun(code, lines, waiting, untouched, afterRelease, started, timeline);
    }

    private void UpdateSmoke(CheckResult c, CheckResult restart, string dist, NewPackage pkg)
    {
        var expected = o.ExpectedVersion ?? "1.0.0";
        //  Пробел в пути — как у установленной программы в «Program Files».
        var dir = Path.Combine(UpdRoot, "install 1");
        var package = PrepareOldInstall(dir, dist, pkg, "v" + expected);
        var userFile = Path.Combine(dir, "guiConfigs", "e2e-user.txt");
        var userHash = Binary.Sha256(userFile);
        using var sleeper = StartSleeper();
        var run = RunAmazTool(dir, package, sleeper.Id, () => { try { sleeper.Kill(true); } catch { } }, "update-install");

        var problems = new List<string>();
        c.Evidence.Add($"«приложение» — процесс-заглушка pid {sleeper.Id}; AmazTool записал, что ждёт его, через {(run.WaitingMs is { } w ? $"{w:F0} мс" : "— (НЕТ такой строки)")}; пока ждал, файлов не трогал: {(run.UntouchedWhileWaiting ? "да" : "НЕТ")}");
        c.Evidence.Add($"от выхода «приложения» до конца AmazTool: {run.AfterReleaseMs} мс, код {run.ExitCode?.ToString() ?? "— (не вышел за 3 мин)"}; {SwapTiming(run.Log)}");
        if (run.WaitingMs is null)
        {
            problems.Add("в журнале нет «waiting for pid …»: установщик не ждал программу");
        }
        if (!run.UntouchedWhileWaiting)
        {
            problems.Add("пока программа работала, установщик уже что-то менял или вышел");
        }
        if (run.ExitCode != 0)
        {
            problems.Add($"код выхода {run.ExitCode?.ToString() ?? "—"}");
        }
        foreach (var must in new[] { "upgrade installed", $"skipped (user data is never overwritten): {PackageTop}/guiConfigs/e2e-user.txt" })
        {
            if (!run.Text.Contains(must, StringComparison.Ordinal))
            {
                problems.Add($"в журнале нет «{must}»");
            }
        }
        var wrong = pkg.Files.Where(kv => !File.Exists(Path.Combine(dir, kv.Key)) || Binary.Sha256(Path.Combine(dir, kv.Key)) != kv.Value).Select(kv => kv.Key).ToList();
        c.Evidence.Add($"файлов пакета на месте с его хешем: {pkg.Files.Count - wrong.Count} из {pkg.Files.Count}; departament.exe — {(Hash(dir, Exe("departament")) == pkg.ExeHash ? "новый" : "НЕ новый")}, AmazTool — {(Hash(dir, Exe("AmazTool")) == pkg.ToolHash ? "новый" : "НЕ новый")}");
        if (wrong.Count > 0)
        {
            problems.Add($"не заменены {wrong.Count} файлов: {string.Join(", ", wrong.Take(5))}");
        }
        if (Binary.Sha256(userFile) != userHash)
        {
            problems.Add("guiConfigs/e2e-user.txt изменён — пакет затёр данные пользователя");
        }
        if (File.Exists(Path.Combine(dir, "guiConfigs", "e2e-trap.txt")))
        {
            problems.Add("в guiConfigs появился файл из пакета");
        }
        if (Directory.Exists(Path.Combine(dir, ".update")))
        {
            problems.Add("рабочая папка .update осталась");
        }
        c.Evidence.Add($"AmazTool.exe.tmp (старый установщик, отодвинутый на время замены) сразу после выхода: {(File.Exists(Path.Combine(dir, Exe("AmazTool") + ".tmp")) ? "есть" : "нет")}");
        c.Evidence.AddRange(run.Log.Where(l => !l.Contains("attempt", StringComparison.Ordinal)).Take(25).Select(l => "upgrade.log: " + l));
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"ждал PID {sleeper.Id} и ничего не трогал; после его выхода за {run.AfterReleaseMs} мс заменил все {pkg.Files.Count} файлов (departament.exe и сам AmazTool — новые), guiConfigs не тронут, .update убран, код 0"
            : string.Join("; ", problems));

        JudgeRestart(restart, dir, run, pkg.ExeHash, $"updated to {expected}", "update-restart");
    }

    private void UpdateRollback(CheckResult c, CheckResult restart, string dist, NewPackage pkg)
    {
        var expected = o.ExpectedVersion ?? "1.0.0";
        var next = BumpPatch(expected);
        var dir = Path.Combine(UpdRoot, "install 2");
        var package = PrepareOldInstall(dir, dist, pkg, "v" + next);
        var before = HashTree(dir);
        AmazRun run;
        string blocker;
        using (var sleeper = StartSleeper())
        {
            Action release = () => { try { sleeper.Kill(true); } catch { } };
            if (IsWin)
            {
                //  Файл занят «намертво» (без FILE_SHARE_DELETE): переименовать его нельзя, как бывает, когда
                //  ядро не вышло или файл держит антивирус.
                blocker = @"bin\Xray\xray.exe держал стенд (FileShare.None)";
                using var hold = new FileStream(Path.Combine(dir, "bin", "Xray", "xray.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
                run = RunAmazTool(dir, package, sleeper.Id, release, "update-rollback");
            }
            else
            {
                //  На Linux открытый файл переименованию не мешает; сбой замены даёт папка на месте нового
                //  файла. Это проверка самого стенда, настоящая — на Windows.
                blocker = "на месте e2e-added/added.txt — папка (Linux: занятый файл не мешает переименованию)";
                Directory.CreateDirectory(Path.Combine(dir, "e2e-added", "added.txt"));
                run = RunAmazTool(dir, package, sleeper.Id, release, "update-rollback");
            }
        }
        var problems = new List<string>();
        var attempts = run.Log.Count(l => l.Contains("xray.exe", StringComparison.OrdinalIgnoreCase) && l.Contains("attempt", StringComparison.Ordinal));
        c.Evidence.Add($"{blocker}; попыток отодвинуть xray.exe: {attempts}; от выхода «приложения» до конца AmazTool {run.AfterReleaseMs} мс, код {run.ExitCode?.ToString() ?? "—"}");
        if (run.ExitCode != 1)
        {
            problems.Add($"код выхода {run.ExitCode?.ToString() ?? "— (не вышел за 3 мин)"}, ждали 1");
        }
        foreach (var must in new[] { "replacing files failed", "rollback complete, the previous version is back in place", "upgrade not installed, the previous version stays" })
        {
            if (!run.Text.Contains(must, StringComparison.Ordinal))
            {
                problems.Add($"в журнале нет «{must}»");
            }
        }
        var after = HashTree(dir);
        var diff = Diff(before, after);
        c.Evidence.Add(diff.Count == 0 ? $"все {before.Count} файлов программы — те же, что до обновления (SHA-256)" : "расхождения: " + string.Join("; ", diff.Take(8)));
        if (diff.Count > 0)
        {
            problems.Add($"после отката не то, что было: {string.Join("; ", diff.Take(4))}");
        }
        if (Directory.Exists(Path.Combine(dir, ".update")))
        {
            problems.Add("рабочая папка .update осталась");
        }
        c.Evidence.AddRange(run.Log.Where(l => !l.Contains("attempt", StringComparison.Ordinal) || l.Contains("attempt 9/10", StringComparison.Ordinal)).Take(25).Select(l => "upgrade.log: " + l));
        var rolled = run.Log.FirstOrDefault(l => l.Contains("rolling back", StringComparison.Ordinal));
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"xray.exe не отодвинулся за {attempts} попыток → «{rolled?.Split(' ', 3).Last()}» → все файлы прежние, метка «old», добавленных нет, .update убран, код 1"
            : string.Join("; ", problems));

        JudgeRestart(restart, dir, run, DistHashes[Exe("departament")], $"installing {next} did not take, still {expected}", "update-rollback-restart");
    }

    /// <summary>Установщик поднял программу из того же каталога, это нужный exe, и она сверила итог.</summary>
    private void JudgeRestart(CheckResult c, string dir, AmazRun run, string exeHash, string reconcileLine, string name, Action<Process>? whileRunning = null)
    {
        var exePath = Path.Combine(dir, Exe("departament"));
        var problems = new List<string>();
        Process? app = null;
        try
        {
            app = run.StartedPid is { } pid ? Process.GetProcessById(pid) : null;
        }
        catch
        {
        }
        var path = app is null ? null : ProcessPath(app);
        c.Evidence.Add($"в upgrade.log: {run.Log.LastOrDefault(l => l.Contains("started ", StringComparison.Ordinal)) ?? "строки «started …» нет"}");
        if (app is null || path is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(app is null ? "перезапущенной программы нет" : $"запущен не тот exe: {path}");
        }
        var exeNow = Hash(dir, Exe("departament"));
        c.Evidence.Add($"departament.exe в каталоге: {(exeNow == exeHash ? "ожидаемый" : "НЕ ожидаемый")} (sha256 {exeNow[..12]}…)");
        if (exeNow != exeHash)
        {
            problems.Add("на месте не тот departament.exe");
        }
        if (app is not null && IsWin)
        {
            var shown = Net.WaitUntil(() => IsWin && Win.MainWindowOf(Win.WindowsOf(app.Id)) is not null, TimeSpan.FromSeconds(25), 100);
            c.Evidence.Add($"окно: {(shown is { } s ? $"видно через {s:F0} мс после выхода AmazTool" : "НЕ появилось за 25 с")}");
            if (shown is null)
            {
                problems.Add("окно перезапущенной программы не появилось");
            }
        }
        //  Итог прошлой установки программа сверяет сама (AppUpdateManager.EnsureReconciled) после первого кадра.
        var reconciled = Net.WaitUntil(() => AppLogText(dir).Contains(reconcileLine, StringComparison.Ordinal), TimeSpan.FromSeconds(30), 250);
        c.Evidence.Add($"журнал программы: «{reconcileLine}» — {(reconciled.HasValue ? "есть" : "НЕТ")}");
        if (!reconciled.HasValue)
        {
            problems.Add($"программа не записала «{reconcileLine}»");
        }
        var upd = Path.Combine(dir, "guiTemps", "update");
        var cleaned = Net.WaitUntil(() => !Directory.Exists(upd) || Directory.GetFiles(upd).Length == 0, TimeSpan.FromSeconds(15), 250);
        var tmp = Path.Combine(dir, Exe("AmazTool") + ".tmp");
        var tmpGone = Net.WaitUntil(() => !File.Exists(tmp), TimeSpan.FromSeconds(15), 250);
        c.Evidence.Add($"guiTemps/update после запуска: {(cleaned.HasValue ? "пуст" : string.Join(", ", Directory.GetFiles(upd).Select(Path.GetFileName)))}; AmazTool.exe.tmp: {(tmpGone.HasValue ? "нет" : "ОСТАЛСЯ")}");
        if (!cleaned.HasValue)
        {
            problems.Add("скачанный пакет и pending.json не убраны");
        }
        if (!tmpGone.HasValue)
        {
            problems.Add("AmazTool.exe.tmp не убран");
        }
        if (app is not null && whileRunning is not null)
        {
            whileRunning(app);
        }
        if (IsWin)
        {
            Thread.Sleep(1500);
            Win.SaveScreenshot(Path.Combine(_shots, $"{name}.png"));
        }
        var tl = new StartupRun();
        tl.ReadTimeline(run.Timeline);
        if (tl.Timeline.TryGetValue("window.frame", out var frame))
        {
            c.Evidence.Add($"первый кадр перезапущенной программы — {frame:F0} мс от её старта");
        }
        if (app is not null)
        {
            //  Закрываем штатно, как установщик («--quit»): убитая программа могла бы оставить прокси. Только на
            //  Windows: там канал «--quit» свой у каждого exe, а на Linux он общий на машину и дошёл бы до чужой копии.
            if (IsWin)
            {
                RunPlain(exePath, ["--quit"], TimeSpan.FromSeconds(30));
            }
            if (!app.WaitForExit(IsWin ? 10_000 : 0))
            {
                try { app.Kill(true); } catch { }
            }
            app.Dispose();
        }
        new AppDriver(dir).KillEverything();
        CopyDir(Path.Combine(dir, "guiLogs"), Path.Combine(UpdOut, name, "guiLogs"));
        c.Set(problems.Count == 0 ? Status.Pass : Status.Fail, problems.Count == 0
            ? $"запущен {Path.GetFileName(exePath)} из того же каталога (pid {run.StartedPid}), окно видно, в журнале «{reconcileLine}», пакет и pending.json убраны"
            : string.Join("; ", problems));
    }

    private static string SwapTiming(List<string> log)
    {
        static DateTime? At(string? l) => l is not null && l.Length > 23 && DateTime.TryParse(l[..23], out var t) ? t : null;
        var exited = At(log.FirstOrDefault(l => l.Contains(" exited", StringComparison.Ordinal) || l.Contains("has already exited", StringComparison.Ordinal)));
        var staged = At(log.FirstOrDefault(l => l.Contains("staged ", StringComparison.Ordinal)));
        var done = At(log.FirstOrDefault(l => l.Contains("upgrade installed", StringComparison.Ordinal) || l.Contains("upgrade not installed", StringComparison.Ordinal)));
        return exited is { } e && staged is { } s && done is { } d
            ? $"распаковка {(s - e).TotalMilliseconds:F0} мс, замена {(d - s).TotalMilliseconds:F0} мс"
            : "время шагов по журналу не определить";
    }

    #endregion Установка поверх старой версии и откат

    #region Defender

    /// <summary>
    /// На образе раннера Defender обычно выключен и весь C:\ в исключениях. У человека он работает, и
    /// именно он держит только что распакованные exe на время проверки. Для фаз обновления и
    /// установщика включаем проверку в реальном времени и снимаем исключения, если это позволено.
    /// </summary>
    private void DefenderRealtime()
    {
        if (!IsWin || !o.DefenderRealtime || _defenderTouched)
        {
            return;
        }
        _defenderTouched = true;
        const string script = """
            try {
              $s = Get-MpComputerStatus -ErrorAction Stop; $p = Get-MpPreference
              "до: служба $($s.AMServiceEnabled), антивирус $($s.AntivirusEnabled), в реальном времени $($s.RealTimeProtectionEnabled), исключения [$($p.ExclusionPath -join '; ')]"
              Set-MpPreference -DisableRealtimeMonitoring $false -DisableIOAVProtection $false -DisableBehaviorMonitoring $false -DisableScriptScanning $false -ErrorAction Stop
              if ($p.ExclusionPath) { Remove-MpPreference -ExclusionPath $p.ExclusionPath -ErrorAction Stop }
              Start-Sleep -Seconds 3
              $s = Get-MpComputerStatus; $p = Get-MpPreference
              "после: в реальном времени $($s.RealTimeProtectionEnabled), проверка загрузок $($s.IoavProtectionEnabled), поведение $($s.BehaviorMonitorEnabled), исключения [$($p.ExclusionPath -join '; ')], движок $($s.AMEngineVersion), базы $($s.AntivirusSignatureVersion)"
            } catch { "Defender недоступен: $($_.Exception.Message)" }
            """;
        var (_, so, se, _) = Net.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], TimeSpan.FromMinutes(2));
        var text = (so + se).Replace("\r", "").Trim().Replace("\n", " → ");
        report.Environment.Add("Defender на фазы обновления и установщика: " + text);
        Log.Info("Defender: " + text);
    }

    private bool _defenderTouched;

    private void DefenderDetections()
    {
        if (!IsWin || !_defenderTouched)
        {
            return;
        }
        const string script = """
            try { $d = @(Get-MpThreatDetection -ErrorAction Stop); if ($d.Count -eq 0) { 'обнаружений нет' } else { $d | ForEach-Object { "$($_.InitialDetectionTime) угроза $($_.ThreatID): $($_.Resources -join ', ')" } } } catch { "не прочитать: $($_.Exception.Message)" }
            """;
        var (_, so, se, _) = Net.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromMinutes(1));
        report.Notes.Add("Defender за фазы обновления и установщика: " + (so + se).Replace("\r", "").Trim().Replace("\n", "; "));
    }

    #endregion Defender

    #region Файлы

    private static string BumpPatch(string version)
    {
        var core = version.Split('-', '+')[0].Split('.');
        return core.Length == 3 && int.TryParse(core[2], out var patch) ? $"{core[0]}.{core[1]}.{patch + 1}" : "999.0.0";
    }

    private static string Hash(string dir, string rel) =>
        File.Exists(Path.Combine(dir, rel)) ? Binary.Sha256(Path.Combine(dir, rel)) : "(нет файла)";

    /// <summary>SHA-256 файлов каталога без папок данных пользователя.</summary>
    private static Dictionary<string, string> HashTree(string dir)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir))
        {
            return d;
        }
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (UserDataDirs.Contains(rel.Split('/')[0], StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            d[rel] = Binary.Sha256(f);
        }
        return d;
    }

    private static List<string> Diff(Dictionary<string, string> before, Dictionary<string, string> after)
    {
        var list = new List<string>();
        foreach (var (k, v) in before)
        {
            if (!after.TryGetValue(k, out var a))
            {
                list.Add("пропал " + k);
            }
            else if (a != v)
            {
                list.Add("изменён " + k);
            }
        }
        list.AddRange(after.Keys.Where(k => !before.ContainsKey(k)).Select(k => "появился " + k));
        return list;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        }
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
        }
    }

    private static void DeleteDir(string dir)
    {
        for (var i = 0; i < 10 && Directory.Exists(dir); i++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Чтение файла, в который прямо сейчас пишут (журнал AmazTool, NLog): без запрета записи.</summary>
    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Журналы программы (guiLogs/*.txt, NLog), без журнала установщика.</summary>
    private static string AppLogText(string dir)
    {
        var logs = Path.Combine(dir, "guiLogs");
        if (!Directory.Exists(logs))
        {
            return "";
        }
        return string.Join('\n', Directory.EnumerateFiles(logs, "*.txt").Select(ReadShared));
    }

    private static string? ProcessPath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    #endregion Файлы
}
