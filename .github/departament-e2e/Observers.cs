using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace DpE2E;

/// <summary>Один запуск приложения глазами стенда. Все времена — мс от создания процесса.</summary>
internal sealed class StartupRun
{
    public string Name { get; set; } = "";
    public double? LaunchCallToProcessMs { get; set; }
    public double? FirstWindowMs { get; set; }
    public double? WindowVisibleMs { get; set; }

    /// <summary>Окно закрыло собой рабочий стол — первый кадр на экране (до него окно прозрачно).</summary>
    public double? WindowDrawnMs { get; set; }

    /// <summary>В кадре есть содержимое: текст и значки, а не ровный фон.</summary>
    public double? WindowPaintedMs { get; set; }

    /// <summary>С чем сравнивать значок: первый кадр на экране, а если его не поймали — видимость окна.</summary>
    public double? WindowShownMs => WindowDrawnMs ?? WindowVisibleMs;
    public double? TrayShellMs { get; set; }
    public double? TrayToolbarMs { get; set; }
    public string? TrayShellRect { get; set; }
    public string? TrayToolbar { get; set; }
    public string? TrayUia { get; set; }
    public string? MainWindow { get; set; }
    public int? LastTrayHr { get; set; }
    public Dictionary<string, double> Timeline { get; } = [];
    public List<string> Notes { get; } = [];
    public bool Exited { get; set; }

    /// <summary>Самый ранний момент, когда значок увидели снаружи — любым способом.</summary>
    public double? TrayObservedMs => new[] { TrayShellMs, TrayToolbarMs }.Where(v => v.HasValue).Min();

    public void ReadTimeline(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        foreach (var line in File.ReadAllLines(path))
        {
            var parts = line.Split(' ', 2);
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
            {
                //  Первое вхождение: веха «core.up» может повториться при переподключении.
                Timeline.TryAdd(parts[1].Trim(), ms);
            }
        }
    }
}

internal sealed record TrayCycleSample(double Ms, bool Visible, Rect Rect, Rect? Frame, bool Iconic, bool Zoomed);

internal sealed class TrayCycleResult
{
    public TrayCycleSample? Before { get; set; }
    public TrayCycleSample? After { get; set; }
    public double? HiddenAtMs { get; set; }
    public double? ShownAtMs { get; set; }
    public bool TrayPresentWhileHidden { get; set; }
    public string? TrayWhileHiddenNote { get; set; }
    public List<string> Notes { get; } = [];
}

[SupportedOSPlatform("windows")]
internal static class Observers
{
    /// <summary>
    /// Запуск под наблюдением: окно создано → видно → первый кадр с содержимым → значок в трее.
    /// Опрос раз в 1–2 мс; кадр окна снимается с экрана (что видит человек), значок проверяется у самой
    /// оболочки (Shell_NotifyIconGetRect) и в панелях области уведомлений.
    /// </summary>
    public static StartupRun Startup(AppDriver app, string name, Dictionary<string, string> env, string shotsDir,
        string timelinePath, TimeSpan exitWait, Func<StartupRun, string?>? whileAlive = null)
    {
        var run = new StartupRun { Name = name };
        File.Delete(timelinePath);
        env["DP_TIMELINE"] = timelinePath;

        var sw = Stopwatch.StartNew();
        var p = app.Launch(env);
        var launchedAt = sw.Elapsed.TotalMilliseconds;
        double created;
        try
        {
            created = (p.StartTime - (DateTime.Now - sw.Elapsed)).TotalMilliseconds;
        }
        catch
        {
            created = launchedAt;
        }
        run.LaunchCallToProcessMs = created;
        double Rel(double ms) => ms - created;

        nint msg = 0, main = 0;
        double nextShell = 0, nextToolbar = 0, nextPaint = 0, trayFoundAt = double.MaxValue;
        //  Предел наблюдения меньше DP_EXIT_AFTER_MS запуска: после него ещё нужны снимок и UIA при живом окне.
        var hardStop = TimeSpan.FromSeconds(9);
        while (sw.Elapsed < hardStop && !p.HasExited)
        {
            var ms = sw.Elapsed.TotalMilliseconds;
            if (main == 0 || msg == 0)
            {
                var wins = Win.WindowsOf(p.Id);
                if (msg == 0)
                {
                    msg = Win.MessageWindowOf(wins);
                }
                if (run.FirstWindowMs is null && wins.Any(w => w.Class.StartsWith("Avalonia-", StringComparison.Ordinal)))
                {
                    run.FirstWindowMs = Rel(ms);
                }
                if (main == 0 && Win.MainWindowOf(wins) is { } w)
                {
                    main = w.Hwnd;
                    run.WindowVisibleMs = Rel(ms);
                    run.MainWindow = $"«{w.Title}» класс {w.Class}, {w.Rect}, DPI {Win.DpiOf(w.Hwnd)}";
                    //  Что человек видит в миг появления окна (может быть ещё пустым).
                    if (Win.ClientRectOnScreen(main) is { } cr)
                    {
                        Win.SaveScreenshot(Path.Combine(shotsDir, $"startup-{name}-1-visible.png"), cr);
                    }
                }
            }

            if (main != 0 && run.WindowPaintedMs is null && ms >= nextPaint)
            {
                nextPaint = ms + 8;
                if (Win.ClientRectOnScreen(main) is { } cr && Win.Capture(cr) is { } shot)
                {
                    var (drawn, content) = Analyze(shot.Bgra, shot.Width, shot.Height);
                    var at = Rel(sw.Elapsed.TotalMilliseconds);
                    if (drawn && run.WindowDrawnMs is null)
                    {
                        run.WindowDrawnMs = at;
                        Png.Save(Path.Combine(shotsDir, $"startup-{name}-2-first-frame.png"), shot.Bgra, shot.Width, shot.Height);
                    }
                    if (content)
                    {
                        run.WindowPaintedMs = at;
                        Png.Save(Path.Combine(shotsDir, $"startup-{name}-2-first-content.png"), shot.Bgra, shot.Width, shot.Height);
                    }
                }
            }

            if (msg != 0 && run.TrayShellMs is null && ms >= nextShell)
            {
                nextShell = ms + 3;
                var (hr, rect) = Win.TrayIconRect(msg, 1);
                run.LastTrayHr = hr;
                if (hr == 0)
                {
                    run.TrayShellMs = Rel(sw.Elapsed.TotalMilliseconds);
                    run.TrayShellRect = rect.ToString();
                    trayFoundAt = Math.Min(trayFoundAt, ms);
                }
            }

            if (msg != 0 && run.TrayToolbarMs is null && ms >= nextToolbar)
            {
                nextToolbar = ms + 40;
                var probes = Win.TrayToolbars(msg, 1);
                if (probes.FirstOrDefault(t => t.Found) is { } hit)
                {
                    run.TrayToolbarMs = Rel(sw.Elapsed.TotalMilliseconds);
                    run.TrayToolbar = $"{hit.Where}: {hit.Note}";
                    trayFoundAt = Math.Min(trayFoundAt, ms);
                }
                else
                {
                    run.TrayToolbar = string.Join("; ", probes.Select(t => t.Exists ? $"{t.Where}: кнопок {t.Buttons}{(t.Note is null ? "" : ", " + t.Note)}" : $"{t.Where}: панели нет"));
                }
            }

            //  Всё увидели — ещё полторы секунды на случай, если окно перерисуется или значок уйдёт. Содержимое
            //  ждём не дольше трёх секунд после первого кадра: ровный экран без текста — тоже ответ.
            var drawnAt = run.WindowDrawnMs is { } d ? d + created : (double?)null;
            if (drawnAt.HasValue && (run.WindowPaintedMs.HasValue || ms > drawnAt + 3000) && ms > trayFoundAt + 1500)
            {
                break;
            }
            Thread.Sleep(1);
        }

        if (p.HasExited)
        {
            run.Notes.Add($"приложение завершилось само на {Rel(sw.Elapsed.TotalMilliseconds):F0} мс, код {p.ExitCode}");
        }
        else
        {
            Win.SaveScreenshot(Path.Combine(shotsDir, $"startup-{name}-3-screen.png"));
            if (Win.TaskbarRect() is { } tb)
            {
                Win.SaveScreenshot(Path.Combine(shotsDir, $"startup-{name}-4-taskbar.png"), tb);
            }
            if (msg != 0 && run.TrayShellMs is null)
            {
                run.Notes.Add($"Shell_NotifyIconGetRect так и не ответил S_OK: последний HRESULT 0x{run.LastTrayHr:X8}");
            }
            if (msg == 0)
            {
                run.Notes.Add("окно сообщений Avalonia (AvaloniaMessageWindow) не найдено — значок проверить не по чему");
            }
            try
            {
                var extra = whileAlive?.Invoke(run);
                if (extra != null)
                {
                    run.Notes.Add(extra);
                }
            }
            catch (Exception ex)
            {
                run.Notes.Add("проверка при живом окне упала: " + ex.Message);
            }
        }

        run.Exited = AppDriver.WaitExit(p, exitWait);
        if (!run.Exited)
        {
            run.Notes.Add($"не вышло само за {exitWait.TotalSeconds:F0} с после наблюдения — добито");
            app.KillEverything();
        }
        run.ReadTimeline(timelinePath);
        return run;
    }

    /// <summary>
    /// Что видно на месте окна (выборка — каждый 3-й пиксель). «Нарисовано» — окно закрыло собой
    /// рабочий стол: цвета-ключа меньше половины точек (до первого кадра окно прозрачно и место
    /// пурпурное). «Содержимое» — к тому же не меньше 24 разных цветов и хотя бы 1% точек не цвета фона:
    /// текст, значки и сглаживание; ровный фон этого не даёт.
    /// </summary>
    public static (bool Drawn, bool Content) Analyze(byte[] bgra, int w, int h)
    {
        var counts = new Dictionary<int, int>();
        var total = 0;
        var key = 0;
        for (var y = 0; y < h; y += 3)
        {
            for (var x = 0; x < w; x += 3)
            {
                var i = (y * w + x) * 4;
                var c = bgra[i] | (bgra[i + 1] << 8) | (bgra[i + 2] << 16);
                counts[c] = counts.GetValueOrDefault(c) + 1;
                total++;
                //  Ключ с допуском: DWM может чуть смешать края.
                if (bgra[i] > 235 && bgra[i + 1] < 20 && bgra[i + 2] > 235)
                {
                    key++;
                }
            }
        }
        if (total == 0)
        {
            return (false, false);
        }
        var drawn = key < total / 2;
        var mode = counts.Values.Max();
        return (drawn, drawn && counts.Count >= 24 && 1.0 - (double)mode / total >= 0.01);
    }

    /// <summary>
    /// Уход в трей и возврат (DP_TRAY_CYCLE): прямоугольник окна до ухода и после возвращения, плюс
    /// значок, пока окна нет, — это единственная дорога назад.
    /// </summary>
    public static TrayCycleResult TrayCycle(AppDriver app, Dictionary<string, string> env, string shotsDir, TimeSpan total)
    {
        var result = new TrayCycleResult();
        var sw = Stopwatch.StartNew();
        var p = app.Launch(env);
        nint main = 0, msg = 0;
        TrayCycleSample? lastVisible = null;
        var shotBefore = false;
        var shotHidden = false;
        double? visibleSince = null;
        while (sw.Elapsed < total && !p.HasExited)
        {
            var ms = sw.Elapsed.TotalMilliseconds;
            if (main == 0)
            {
                var wins = Win.WindowsOf(p.Id);
                msg = Win.MessageWindowOf(wins);
                if (Win.MainWindowOf(wins) is { } w)
                {
                    main = w.Hwnd;
                    visibleSince = ms;
                }
                Thread.Sleep(10);
                continue;
            }
            if (Win.Describe(main) is not { } info)
            {
                result.Notes.Add("окно исчезло (уничтожено), а не спряталось");
                break;
            }
            var sample = new TrayCycleSample(ms, info.Visible && !info.Cloaked, info.Rect, Win.ExtendedFrame(main), info.Iconic, info.Zoomed);
            if (result.HiddenAtMs is null)
            {
                if (sample.Visible)
                {
                    lastVisible = sample;
                    if (!shotBefore && visibleSince is { } since && ms - since > 2500)
                    {
                        shotBefore = Win.SaveScreenshot(Path.Combine(shotsDir, "tray-cycle-1-before.png"));
                    }
                }
                else
                {
                    result.HiddenAtMs = ms;
                    result.Before = lastVisible;
                }
            }
            else if (result.ShownAtMs is null)
            {
                if (!sample.Visible)
                {
                    if (!shotHidden && ms - result.HiddenAtMs > 600)
                    {
                        shotHidden = Win.SaveScreenshot(Path.Combine(shotsDir, "tray-cycle-2-hidden.png"));
                        if (msg != 0)
                        {
                            var (hr, rect) = Win.TrayIconRect(msg, 1);
                            var toolbar = Win.TrayToolbars(msg, 1).FirstOrDefault(t => t.Found);
                            result.TrayPresentWhileHidden = hr == 0 || toolbar != null;
                            result.TrayWhileHiddenNote = hr == 0 ? $"Shell_NotifyIconGetRect S_OK {rect}" :
                                toolbar != null ? $"{toolbar.Where}: {toolbar.Note}" : $"Shell_NotifyIconGetRect 0x{hr:X8}, в панелях не найден";
                        }
                    }
                }
                else
                {
                    result.ShownAtMs = ms;
                }
            }
            else if (ms - result.ShownAtMs > 1200)
            {
                //  Дали окну осесть после показа: здесь уже финальная геометрия.
                result.After = sample;
                Win.SaveScreenshot(Path.Combine(shotsDir, "tray-cycle-3-after.png"));
                break;
            }
            Thread.Sleep(20);
        }
        if (!AppDriver.WaitExit(p, TimeSpan.FromSeconds(30)))
        {
            result.Notes.Add("не вышло само — добито");
            app.KillEverything();
        }
        return result;
    }

    /// <summary>
    /// Способ 3 — UI Automation над областью уведомлений (Windows PowerShell 5.1, tray-uia.ps1): кнопка
    /// значка с именем-подсказкой «departament» на панели задач или в окне переполнения.
    /// </summary>
    public static string TrayUia(string scriptPath, string outJson, string shotPath, bool openOverflow)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-Name", "departament", "-Out", outJson, "-Shot", shotPath };
        if (openOverflow)
        {
            args.Add("-OpenOverflow");
        }
        var (code, stdout, stderr, ms) = Net.Run("powershell.exe", args, TimeSpan.FromSeconds(60));
        try
        {
            if (File.Exists(outJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(outJson));
                var r = doc.RootElement;
                var found = r.TryGetProperty("found", out var f) && f.GetBoolean();
                var where = r.TryGetProperty("where", out var wh) ? wh.GetString() : null;
                var scanned = r.TryGetProperty("scanned", out var sc) ? sc.GetInt32() : 0;
                var err = r.TryGetProperty("error", out var e) ? e.GetString() : null;
                var chevron = r.TryGetProperty("chevron", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString() : null;
                var extra = chevron is null ? "" : $"; переполнение: {chevron}";
                return found ? $"найден: {where} (просмотрено элементов: {scanned}, {ms} мс{extra})"
                    : $"не найден (просмотрено элементов: {scanned}{extra}{(string.IsNullOrEmpty(err) ? "" : ", ошибка: " + err)})";
            }
        }
        catch (Exception ex)
        {
            return $"ответ UIA не разобран: {ex.Message}";
        }
        return $"UIA не ответил (код {code}): {stderr.Trim()} {stdout.Trim()}".Trim();
    }
}
