using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpE2E;

internal enum Status
{
    Pass,
    Fail,
    NotObservable,
    Skip,
}

internal sealed class CheckResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Status Status { get; set; } = Status.Skip;

    public string Details { get; set; } = "";
    public List<string> Evidence { get; } = [];

    public CheckResult Set(Status status, string details)
    {
        Status = status;
        Details = details;
        Log.Info($"[{StatusText(status)}] {Title}: {details}");
        return this;
    }

    public static string StatusText(Status s) => s switch
    {
        Status.Pass => "PASS",
        Status.Fail => "FAIL",
        Status.NotObservable => "NOT OBSERVABLE",
        _ => "SKIP",
    };
}

/// <summary>Итог прогона: results.json, summary.md (он же уходит в $GITHUB_STEP_SUMMARY) и аннотации ::error.</summary>
internal sealed class Report
{
    public List<CheckResult> Checks { get; } = [];
    public List<string> Environment { get; } = [];
    public List<StartupRun> StartupRuns { get; } = [];
    public List<string> Notes { get; } = [];

    public CheckResult Add(string id, string title)
    {
        var c = new CheckResult { Id = id, Title = title };
        Checks.Add(c);
        return c;
    }

    public void Write(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var json = JsonSerializer.Serialize(new { Environment, Checks, StartupRuns, Notes }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(Path.Combine(outDir, "results.json"), json);

        var md = Markdown();
        File.WriteAllText(Path.Combine(outDir, "summary.md"), md);
        var stepSummary = System.Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(stepSummary))
        {
            File.AppendAllText(stepSummary, md);
        }

        //  Аннотация на каждую упавшую проверку: видна прямо на странице прогона, без чтения журнала.
        if (System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            foreach (var c in Checks.Where(c => c.Status == Status.Fail))
            {
                Console.WriteLine($"::error title={Escape(c.Title)}::{Escape(c.Details)}");
            }
            foreach (var c in Checks.Where(c => c.Status == Status.NotObservable))
            {
                Console.WriteLine($"::warning title={Escape(c.Title)}::{Escape(c.Details)}");
            }
        }
    }

    private static string Escape(string s) =>
        s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A").Replace(":", "%3A").Replace(",", "%2C");

    private string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## departament: проверка на Windows");
        sb.AppendLine();
        var fails = Checks.Count(c => c.Status == Status.Fail);
        var passes = Checks.Count(c => c.Status == Status.Pass);
        sb.AppendLine(fails == 0
            ? $"Все проверки без отказов: {passes} прошли, остальные не наблюдаемы или пропущены."
            : $"**Отказов: {fails}.** Прошли: {passes}.");
        sb.AppendLine();
        sb.AppendLine("| Проверка | Итог | Подробности |");
        sb.AppendLine("|---|---|---|");
        foreach (var c in Checks)
        {
            sb.AppendLine($"| {Cell(c.Title)} | {Badge(c.Status)} | {Cell(c.Details)} |");
        }
        sb.AppendLine();

        if (StartupRuns.Count > 0)
        {
            sb.AppendLine("### Запуск: мс от старта процесса");
            sb.AppendLine();
            sb.AppendLine("| Запуск | Окно создано | Окно видно | Первый кадр с содержимым | Значок (Shell_NotifyIconGetRect) | Значок (панель трея) | Вехи изнутри |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in StartupRuns)
            {
                sb.AppendLine($"| {r.Name} | {Ms(r.FirstWindowMs)} | {Ms(r.WindowVisibleMs)} | {Ms(r.WindowPaintedMs)} | {Ms(r.TrayShellMs)} | {Ms(r.TrayToolbarMs)} | {Cell(string.Join(", ", r.Timeline.Select(kv => $"{kv.Key} {kv.Value:F0}")))} |");
            }
            sb.AppendLine();
        }

        var withEvidence = Checks.Where(c => c.Evidence.Count > 0).ToList();
        if (withEvidence.Count > 0)
        {
            sb.AppendLine("<details><summary>Доказательства по проверкам</summary>");
            sb.AppendLine();
            foreach (var c in withEvidence)
            {
                sb.AppendLine($"**{c.Title}**");
                sb.AppendLine();
                sb.AppendLine("```");
                foreach (var e in c.Evidence.Take(40))
                {
                    sb.AppendLine(e.Length > 400 ? e[..400] + "…" : e);
                }
                sb.AppendLine("```");
                sb.AppendLine();
            }
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (Environment.Count > 0)
        {
            sb.AppendLine("<details><summary>Машина</summary>");
            sb.AppendLine();
            foreach (var e in Environment)
            {
                sb.AppendLine($"- {e}");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
        foreach (var n in Notes)
        {
            sb.AppendLine($"> {n}");
        }
        return sb.ToString();
    }

    private static string Ms(double? v) => v is { } x ? x.ToString("F0", CultureInfo.InvariantCulture) : "—";

    private static string Badge(Status s) => s switch
    {
        Status.Pass => "**PASS**",
        Status.Fail => "**FAIL**",
        Status.NotObservable => "NOT OBSERVABLE",
        _ => "SKIP",
    };

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");
}

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static string? _file;

    public static void To(string path) => _file = path;

    public static void Info(string message)
    {
        var line = $"[{Clock.Elapsed.TotalSeconds,7:F1}s] {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            if (_file != null)
            {
                try { File.AppendAllText(_file, line + System.Environment.NewLine); } catch { }
            }
        }
    }
}
