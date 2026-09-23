using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DpE2E;

/// <summary>
/// Раздача подписок на 127.0.0.1: /sub — base64 со ссылкой VLESS, /subjson — XRAY_JSON, /probe/… —
/// «сайт за туннелем» для локального прогона на Linux. Свой крошечный HTTP поверх TcpListener:
/// HttpListener на Windows идёт через http.sys и требует URL ACL, а здесь нужен один ответ на запрос.
/// </summary>
internal sealed class SubServer : IDisposable
{
    public sealed record Response(int Code, string ContentType, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null);

    private readonly TcpListener _listener;
    private readonly Func<string, Response> _handler;
    private readonly string _logPath;
    private readonly object _gate = new();
    private volatile bool _stopped;

    public List<string> Requests { get; } = [];

    public SubServer(int port, Func<string, Response> handler, string logPath)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _handler = handler;
        _logPath = logPath;
    }

    public void Start()
    {
        _listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "SubServer" }.Start();
    }

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
            catch when (_stopped)
            {
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buf = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n") && head.Length < 32768)
                {
                    var n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        break;
                    }
                    head.Append(Encoding.ASCII.GetString(buf, 0, n));
                }
                var lines = head.ToString().Split("\r\n");
                var parts = lines[0].Split(' ');
                var path = parts.Length > 1 ? parts[1] : "/";
                var ua = lines.FirstOrDefault(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))?[11..].Trim() ?? "";
                var response = _handler(path);
                var sb = new StringBuilder();
                sb.Append($"HTTP/1.1 {response.Code} {(response.Code == 200 ? "OK" : "Not Found")}\r\n");
                sb.Append($"Content-Type: {response.ContentType}\r\n");
                sb.Append($"Content-Length: {response.Body.Length}\r\n");
                sb.Append("Connection: close\r\n");
                foreach (var (k, v) in response.Headers ?? new Dictionary<string, string>())
                {
                    sb.Append($"{k}: {v}\r\n");
                }
                sb.Append("\r\n");
                stream.Write(Encoding.ASCII.GetBytes(sb.ToString()));
                stream.Write(response.Body);
                var line = $"{DateTime.Now:HH:mm:ss.fff} {client.Client.RemoteEndPoint} {parts[0]} {path} → {response.Code} ({response.Body.Length} B) UA=\"{ua}\"";
                lock (_gate)
                {
                    Requests.Add(line);
                    File.AppendAllText(_logPath, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        try { _listener.Stop(); } catch { }
    }
}

/// <summary>Процесс ядра стенда (сервер или ручной клиент): вывод — в файл, остановка — вместе с деревом.</summary>
internal sealed class CoreProcess : IDisposable
{
    public Process Process { get; }
    private readonly StreamWriter _log;

    private CoreProcess(Process p, StreamWriter log)
    {
        Process = p;
        _log = log;
    }

    public static CoreProcess Start(string exe, string args, string logPath)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        var log = new StreamWriter(File.Open(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (log) { log.WriteLine(e.Data); } } };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (log) { log.WriteLine(e.Data); } } };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return new CoreProcess(p, log);
    }

    public bool Alive => !Process.HasExited;

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(5000);
            }
        }
        catch
        {
        }
        try { lock (_log) { _log.Dispose(); } } catch { }
    }
}

/// <summary>Хвост журнала, который пишет чужой процесс: что добавилось после отметки.</summary>
internal sealed class TailFile(string path)
{
    public string Path { get; } = path;

    public long Mark() => File.Exists(Path) ? new FileInfo(Path).Length : 0;

    public List<string> LinesSince(long mark)
    {
        if (!File.Exists(Path))
        {
            return [];
        }
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (mark > fs.Length)
        {
            mark = 0;
        }
        fs.Seek(mark, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }

    /// <summary>Ждать строку, в которой есть хоть одна из подстрок; журнал ядра пишется с задержкой.</summary>
    public List<string> WaitFor(long mark, IReadOnlyCollection<string> anyOf, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var hits = LinesSince(mark).Where(l => anyOf.Any(s => l.Contains(s, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count > 0 || sw.Elapsed > timeout)
            {
                return hits;
            }
            Thread.Sleep(200);
        }
    }
}
