using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DpE2E;

internal sealed record FetchResult(int Code, string Output, string Error, long ElapsedMs)
{
    public bool Ok => Code is >= 200 and < 400;
    public override string ToString() => $"HTTP {Code} за {ElapsedMs} мс{(Error.Length > 0 ? $"; {Error.Trim()}" : "")}";
}

internal static class Net
{
    /// <summary>
    /// Запрос curl'ом. Без --proxy и с --noproxy '*' curl ходит напрямую и НЕ читает системный прокси
    /// Windows: если такой запрос дошёл до сервера, его туда увёл туннель. -4 — туннель у приложения
    /// только IPv4 (EnableIPv6Address выключен).
    /// </summary>
    public static FetchResult Curl(string url, string? proxy = null, int timeoutSec = 20)
    {
        var args = new List<string> { "-sS", "-4", "-o", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", "-w", "%{http_code} %{remote_ip}", "--max-time", timeoutSec.ToString() };
        if (proxy is null)
        {
            args.AddRange(["--noproxy", "*"]);
        }
        else
        {
            //  Пустой список исключений перекрывает NO_PROXY окружения: иначе curl молча идёт мимо прокси
            //  к 127.0.0.1, и запрос «через прокси» на деле прямой.
            args.AddRange(["--noproxy", ""]);
            args.AddRange(proxy.StartsWith("socks5h://", StringComparison.Ordinal)
                ? ["--socks5-hostname", proxy["socks5h://".Length..]]
                : ["--proxy", proxy]);
        }
        args.Add(url);
        var (code, stdout, stderr, ms) = Run(OperatingSystem.IsWindows() ? "curl.exe" : "curl", args, TimeSpan.FromSeconds(timeoutSec + 10));
        var parts = stdout.Trim().Split(' ');
        var http = parts.Length > 0 && int.TryParse(parts[0], out var c) ? c : 0;
        return new FetchResult(http, stdout.Trim(), code == 0 ? "" : stderr, ms);
    }

    /// <summary>
    /// Запрос так, как его делает обычная программа Windows: Windows PowerShell 5.1 (.NET Framework,
    /// WinINet-настройки текущего пользователя). Новый процесс — чтобы прокси прочитался заново.
    /// </summary>
    public static FetchResult SystemProxyFetch(string url, int timeoutSec = 20)
    {
        var script =
            "$ProgressPreference='SilentlyContinue';" +
            $"$u='{url}';" +
            "$p=[System.Net.WebRequest]::GetSystemWebProxy();" +
            "Write-Output ('proxy=' + $p.GetProxy([uri]$u));" +
            "try {" +
            $"  $r=Invoke-WebRequest -UseBasicParsing -Uri $u -TimeoutSec {timeoutSec};" +
            "  Write-Output ('status=' + [int]$r.StatusCode)" +
            "} catch { Write-Output ('error=' + $_.Exception.Message); if ($_.Exception.Response) { Write-Output ('status=' + [int]$_.Exception.Response.StatusCode) } }";
        var (_, stdout, stderr, ms) = Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], TimeSpan.FromSeconds(timeoutSec + 30));
        var status = stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("status=", StringComparison.Ordinal));
        var code = status != null && int.TryParse(status[7..], out var c) ? c : 0;
        return new FetchResult(code, stdout.Trim(), stderr.Trim(), ms);
    }

    public static (int ExitCode, string Stdout, string Stderr, long Ms) Run(string exe, IEnumerable<string> args, TimeSpan timeout, string? workDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workDir != null)
        {
            psi.WorkingDirectory = workDir;
        }
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        var sw = Stopwatch.StartNew();
        try
        {
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(true); } catch { }
                return (-1, so.IsCompleted ? so.Result : "", "таймаут " + timeout.TotalSeconds + " с", sw.ElapsedMilliseconds);
            }
            p.WaitForExit();
            return (p.ExitCode, so.Result, se.Result, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == port);

    public static double? WaitUntil(Func<bool> condition, TimeSpan timeout, int pollMs = 100)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (condition())
                {
                    return sw.Elapsed.TotalMilliseconds;
                }
            }
            catch
            {
            }
            Thread.Sleep(pollMs);
        }
        return null;
    }

    public static NetworkInterface? Adapter(string name) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string DescribeAdapter(NetworkInterface n)
    {
        var ips = n.GetIPProperties().UnicastAddresses.Select(a => a.Address.ToString());
        var dns = n.GetIPProperties().DnsAddresses.Select(a => a.ToString());
        return $"{n.Name} [{n.Description}] {n.OperationalStatus}, адреса {string.Join(" ", ips)}, DNS {string.Join(" ", dns)}";
    }

    public static List<string> ResolveSystem(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Запрос A-записи к конкретному серверу по UDP. Нужен до тестов: прямой DNS приложения по умолчанию —
    /// 119.29.29.29, и если он не отвечает с этой машины, TUN упадёт не по вине приложения.
    /// </summary>
    public static (List<string> Ips, string? Error) QueryA(string server, string name, int timeoutMs = 3000)
    {
        try
        {
            var id = (ushort)Random.Shared.Next(1, 65535);
            var q = new List<byte>();
            var header = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0), id);
            header[2] = 0x01; // RD
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);
            q.AddRange(header);
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                q.Add((byte)label.Length);
                q.AddRange(Encoding.ASCII.GetBytes(label));
            }
            q.Add(0);
            q.AddRange([0, 1, 0, 1]); // A, IN

            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Connect(IPAddress.Parse(server), 53);
            udp.Send(q.ToArray());
            IPEndPoint? remote = null;
            var resp = udp.Receive(ref remote);
            var an = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(6));
            var pos = q.Count; // вопрос повторяется в ответе как был
            var ips = new List<string>();
            for (var i = 0; i < an && pos < resp.Length; i++)
            {
                pos = SkipName(resp, pos);
                var type = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(pos));
                var rdlen = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(pos + 8));
                pos += 10;
                if (type == 1 && rdlen == 4)
                {
                    ips.Add(new IPAddress(resp.AsSpan(pos, 4)).ToString());
                }
                pos += rdlen;
            }
            var rcode = resp[3] & 0x0F;
            return (ips, rcode == 0 ? null : $"rcode {rcode}");
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    private static int SkipName(byte[] b, int pos)
    {
        while (pos < b.Length)
        {
            var len = b[pos];
            if (len == 0)
            {
                return pos + 1;
            }
            if ((len & 0xC0) == 0xC0)
            {
                return pos + 2;
            }
            pos += len + 1;
        }
        return pos;
    }
}
