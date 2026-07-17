using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.Manager;

namespace v2rayN.Desktop;

internal class Program
{
    public static EventWaitHandle ProgramStarted;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (OnStartup(args) == false)
        {
            Environment.Exit(0);
            return;
        }

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static bool OnStartup(string[]? Args)
    {
        var args = Args ?? [];
        // Browser→app SSO return (departamentvpn://auth?code=…): the OS launches us with the URL as an arg.
        var authUrl = ExtractAuthUrl(args);

        if (Utils.IsWindows())
        {
            var exePathKey = Utils.GetMd5(Utils.GetExePath());
            var rebootas = args.Any(t => t == Global.RebootAs);
            ProgramStarted = new EventWaitHandle(false, EventResetMode.AutoReset, exePathKey, out var bCreatedNew);
            if (!rebootas && !bCreatedNew)
            {
                // A live instance already holds the single-instance gate. Previously the second instance
                // simply exited, dropping its args — so a scheme callback could never reach the running
                // app. Now, if we were launched to deliver an auth URL, forward it over the named pipe
                // keyed off this exe, THEN exit; the running instance receives + completes the login.
                if (authUrl != null)
                {
                    AppHandoffChannel.ForwardToRunningInstance(authUrl);
                }
                ProgramStarted.Set();
                return false;
            }
        }
        else
        {
            _ = new Mutex(true, "v2rayN", out var bOnlyOneInstance);
            if (!bOnlyOneInstance)
            {
                if (authUrl != null)
                {
                    AppHandoffChannel.ForwardToRunningInstance(authUrl);
                }
                return false;
            }
        }

        // This is the primary (surviving) instance. Start the pipe receiver so any later scheme callback
        // forwarded by a second instance routes into the running app. If WE were the one launched by the
        // scheme (cold start), buffer the URL until App wires its handler.
        AppHandoffChannel.StartServer();
        if (authUrl != null)
        {
            AppHandoffChannel.Receive(authUrl);
        }

        if (!AppManager.Instance.InitApp())
        {
            return false;
        }

        AppManager.Instance.WindowDialog = new WindowDialog();
        return true;
    }

    private static string? ExtractAuthUrl(string[] args)
        => args.FirstOrDefault(a => a != null && a.StartsWith(AppHandoffChannel.SchemePrefix, StringComparison.OrdinalIgnoreCase));

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
           .UsePlatformDetect()
           //.WithInterFont()
           .WithFontByDefault()
#if DEBUG
           .WithDeveloperTools()
#endif
           .LogToTrace()
           .UseReactiveUI(_ => { });

        if (OperatingSystem.IsMacOS())
        {
            var showInDock = Design.IsDesignMode || AppManager.Instance.Config.UiItem.MacOSShowInDock;
            builder = builder.With(new MacOSPlatformOptions { ShowInDock = showInDock });
        }

        return builder;
    }
}

/// <summary>
/// Browser→app SSO handoff channel. The site's <c>/app-login</c> page returns to the app via the custom
/// scheme <c>departamentvpn://auth?code=…</c>. Because the app is single-instance, a scheme launch while
/// the app is already running spawns a throwaway second process; this channel forwards that process's URL
/// to the live instance over a per-exe named pipe (cross-platform: Windows named pipes / Unix domain
/// sockets under the hood). The live instance's <see cref="StartServer"/> loop receives it and hands it to
/// the App-level handler (<see cref="SetHandler"/>), which routes the code into the login flow. A URL that
/// arrives before the handler is wired (cold-start launch) is buffered and drained on <see cref="SetHandler"/>.
/// </summary>
internal static class AppHandoffChannel
{
    public const string SchemePrefix = AccountVmScheme + "://";

    // Kept in sync with AccountViewModel.AppScheme (matches the site allowlist ^departament[a-z0-9]*$).
    private const string AccountVmScheme = "departamentvpn";

    private static readonly object _gate = new();
    private static string? _pending;
    private static Action<string>? _handler;

    private static string PipeName()
        => "departamentvpn-" + (Utils.IsWindows() ? Utils.GetMd5(Utils.GetExePath()) : "v2rayN");

    /// <summary>Wires the App-level receiver and drains any URL buffered before it was ready.</summary>
    public static void SetHandler(Action<string> handler)
    {
        string? pending;
        lock (_gate)
        {
            _handler = handler;
            pending = _pending;
            _pending = null;
        }
        if (pending != null)
        {
            try { handler(pending); } catch { }
        }
    }

    /// <summary>Delivers a scheme URL to the App handler, or buffers it until the handler is set.</summary>
    public static void Receive(string url)
    {
        Action<string>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler == null)
            {
                _pending = url;
                return;
            }
        }
        try { handler(url); } catch { }
    }

    /// <summary>Runs a background loop that accepts one forwarded URL per connection for the app lifetime.</summary>
    public static void StartServer()
    {
        var t = new Thread(ServerLoop) { IsBackground = true, Name = "AppHandoffPipe" };
        t.Start();
    }

    private static void ServerLoop()
    {
        var name = PipeName();
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Encoding.UTF8);
                var url = reader.ReadToEnd()?.Trim();
                if (!string.IsNullOrEmpty(url))
                {
                    Receive(url);
                }
            }
            catch
            {
                // Pipe fault — pause briefly and recreate rather than tearing the app down.
                try { Thread.Sleep(200); } catch { }
            }
        }
    }

    /// <summary>Second-instance side: hands the scheme URL to the running app, then the caller exits.</summary>
    public static void ForwardToRunningInstance(string url)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(), PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.Write(url);
        }
        catch
        {
            // The live instance didn't accept the URL — exit quietly; the user can still «войти по коду».
        }
    }
}
