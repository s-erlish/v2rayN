using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace DpE2E;

internal sealed record ProfileRow(string IndexId, int ConfigType, int? CoreType, string? Subid, string? Remarks, string? Address, int Port);

/// <summary>
/// Приложение как чёрный ящик: запуск с переменными DEV-обвязки, ожидание штатного выхода, правка
/// guiNConfig.json и guiNDB.db между запусками (только когда приложение не работает).
/// </summary>
internal sealed class AppDriver(string dir)
{
    public string Dir { get; } = Path.GetFullPath(dir);

    public string ExePath => Path.Combine(Dir, OperatingSystem.IsWindows() ? "departament.exe" : "departament");
    public string ConfigDir => Path.Combine(Dir, "guiConfigs");
    public string ConfigPath => Path.Combine(ConfigDir, "guiNConfig.json");
    public string DbPath => Path.Combine(ConfigDir, "guiNDB.db");
    public string LogsDir => Path.Combine(Dir, "guiLogs");
    public string BinConfigsDir => Path.Combine(Dir, "binConfigs");

    /// <summary>Ядро из поставки (bin/Xray/xray.exe на Windows, bin/xray/xray на Linux).</summary>
    public string? CoreExe(string name)
    {
        var bin = Path.Combine(Dir, "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        return Directory.EnumerateFiles(bin, file, new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
            .FirstOrDefault();
    }

    public Process Launch(IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Dir };
        foreach (var (k, v) in env)
        {
            psi.Environment[k] = v;
        }
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start вернул null");
    }

    public static bool WaitExit(Process p, TimeSpan timeout)
    {
        try
        {
            return p.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Добить всё, что запущено из папки приложения: само приложение и его ядра.</summary>
    public List<string> KillEverything()
    {
        var killed = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && path.StartsWith(Dir, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    killed.Add($"{p.ProcessName} ({p.Id})");
                }
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }
        return killed;
    }

    public List<string> RunningCores()
    {
        var list = new List<string>();
        foreach (var name in new[] { "xray", "sing-box" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path != null && path.StartsWith(Dir, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add($"{name} (pid {p.Id})");
                    }
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return list;
    }

    #region guiNConfig.json

    public JsonObject ReadConfig() =>
        JsonNode.Parse(File.ReadAllText(ConfigPath), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
        ?? throw new InvalidDataException("guiNConfig.json — не объект");

    public void PatchConfig(Action<JsonObject> patch)
    {
        var root = ReadConfig();
        patch(root);
        File.WriteAllText(ConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static JsonObject Obj(JsonObject root, string name)
    {
        if (root[name] is JsonObject o)
        {
            return o;
        }
        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    #endregion guiNConfig.json

    #region guiNDB.db

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        c.Open();
        return c;
    }

    /// <summary>
    /// Подписка в том виде, в каком её заводит приложение (ConfigHandler.AddSubItem): тот же UA, и
    /// UserInfoUpdated = 0 — сведения «не приходили ни разу», поэтому приложение само скачает её при
    /// запуске (MainWindowViewModel.RefreshStaleSubscriptionsOnStartupAsync). Импорт идёт штатным путём.
    /// </summary>
    public void AddSubscription(string id, string remarks, string url, int sort)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT OR REPLACE INTO SubItem (Id, Remarks, Url, Enabled, UserAgent, Sort, AutoUpdateInterval, UpdateTime, " +
            "UploadUsed, DownloadUsed, TotalTraffic, Expire, UserInfoUpdated, Pinned) " +
            "VALUES ($id, $remarks, $url, 1, 'v2rayNG/1.10.6', $sort, 0, 0, 0, 0, 0, 0, 0, 0)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$remarks", remarks);
        cmd.Parameters.AddWithValue("$url", url);
        cmd.Parameters.AddWithValue("$sort", sort);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сведения подписок «свежие на сутки вперёд»: следующие запуски не перекачивают их посреди проверки.</summary>
    public void FreezeSubscriptions()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE SubItem SET UserInfoUpdated = $t";
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.Now.AddDays(1).ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public List<ProfileRow> Profiles()
    {
        var list = new List<ProfileRow>();
        if (!File.Exists(DbPath))
        {
            return list;
        }
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT IndexId, ConfigType, CoreType, Subid, Remarks, Address, Port FROM ProfileItem";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ProfileRow(
                r.GetString(0),
                r.IsDBNull(1) ? 0 : r.GetInt32(1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? 0 : r.GetInt32(6)));
        }
        return list;
    }

    public List<string> SubscriptionRows()
    {
        var list = new List<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Remarks, Url, UserInfoUpdated, ProfileTitle FROM SubItem";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add($"{r.GetValue(0)} «{r.GetValue(1)}» {r.GetValue(2)} UserInfoUpdated={r.GetValue(3)} ProfileTitle={r.GetValue(4)}");
        }
        return list;
    }

    #endregion guiNDB.db
}
