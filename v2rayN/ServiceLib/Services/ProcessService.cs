namespace ServiceLib.Services;

public class ProcessService : IDisposable
{
    private readonly Process _process;
    private readonly Func<bool, string, Task>? _updateFunc;
    private bool _isDisposed;
    // Set BEFORE any intentional Kill (StopAsync / Dispose) so the always-on Exited handler can tell an
    // intentional teardown apart from a genuine crash and suppress the public Exited event for the former.
    private volatile bool _stopping;
    // 0 → not yet raised, 1 → raised. Guarantees the public Exited event fires at most once.
    private int _exitedRaised;
    // The log-pipe handler, retained so the exit handler can detach it (displayLog case only).
    private DataReceivedEventHandler? _dataHandler;

    /// <summary>
    /// Raised exactly once when the underlying process exits UNEXPECTEDLY on its own (crash / OOM /
    /// killed / stale after sleep-resume/network-change). Wired for EVERY process regardless of
    /// <c>displayLog</c>. It is deliberately NOT raised for an intentional teardown via
    /// <see cref="StopAsync"/> or <see cref="Dispose"/> (the <see cref="_stopping"/> guard suppresses
    /// it), so a normal stop never looks like a crash. Fires on the thread <see cref="Process.Exited"/>
    /// fires on (a ThreadPool thread); the subscriber (CoreManager) marshals/serializes as needed.
    /// </summary>
    public event Action<ProcessService>? Exited;

    public int Id => _process.Id;
    public IntPtr Handle => _process.Handle;
    public bool HasExited => _process.HasExited;

    public ProcessService(
        string fileName,
        string arguments,
        string workingDirectory,
        bool displayLog,
        bool redirectInput,
        Dictionary<string, string>? environmentVars,
        Func<bool, string, Task>? updateFunc)
    {
        _updateFunc = updateFunc;

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = displayLog,
                RedirectStandardError = displayLog,
                CreateNoWindow = true,
                StandardOutputEncoding = displayLog ? Encoding.UTF8 : null,
                StandardErrorEncoding = displayLog ? Encoding.UTF8 : null,
            },
            EnableRaisingEvents = true
        };

        if (environmentVars != null)
        {
            foreach (var kv in environmentVars)
            {
                _process.StartInfo.Environment[kv.Key] = kv.Value;
            }
        }

        if (displayLog)
        {
            RegisterLogHandlers();
        }

        // ALWAYS observe process exit (not only for the displayLog case) so an unexpected death of the
        // core / pre-service can be surfaced to CoreManager for crash detection + auto-restart. The
        // handler detaches the log pipe (displayLog only) AND raises the public Exited event — unless an
        // intentional stop is in progress (see _stopping), in which case the exit is expected and quiet.
        _process.Exited += OnProcessExited;
    }

    public async Task StartAsync(string pwd = null)
    {
        _process.Start();

        if (_process.StartInfo.RedirectStandardOutput)
        {
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        if (_process.StartInfo.RedirectStandardInput)
        {
            await Task.Delay(10);
            await _process.StandardInput.WriteLineAsync(pwd);
        }
    }

    public async Task StopAsync()
    {
        // Mark this as an intentional teardown BEFORE the Kill so the always-on Exited handler treats
        // the resulting process exit as expected (no public Exited → no false crash-restart upstream).
        _stopping = true;

        if (_process.HasExited)
        {
            return;
        }

        try
        {
            if (_process.StartInfo.RedirectStandardOutput)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }
            }

            try
            {
                if (Utils.IsNonWindows())
                {
                    _process.Kill(true);
                }
            }
            catch { }

            try
            {
                _process.Kill();
            }
            catch { }

            await Task.Delay(100);
        }
        catch (Exception ex)
        {
            await _updateFunc?.Invoke(true, ex.Message);
        }
    }

    private void RegisterLogHandlers()
    {
        _dataHandler = (sender, e) =>
        {
            if (e.Data.IsNotEmpty())
            {
                _ = _updateFunc?.Invoke(false, e.Data + Environment.NewLine);
            }
        };

        _process.OutputDataReceived += _dataHandler;
        _process.ErrorDataReceived += _dataHandler;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // Detach the log pipe handlers (present only in the displayLog case) — preserves the exact
        // behavior of the original Exited handler.
        if (_dataHandler != null)
        {
            try
            {
                _process.OutputDataReceived -= _dataHandler;
                _process.ErrorDataReceived -= _dataHandler;
            }
            catch
            {
            }
        }

        // Intentional teardown (StopAsync / Dispose set _stopping first) → the exit is expected, stay
        // quiet so it is never mistaken for a crash.
        if (_stopping || _isDisposed)
        {
            return;
        }

        // Unexpected exit — raise the public event exactly once.
        if (Interlocked.Exchange(ref _exitedRaised, 1) == 0)
        {
            try
            {
                Exited?.Invoke(this);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        // Mark the intentional teardown BEFORE the Kill so a Dispose-driven exit is never surfaced as a
        // crash (belt-and-suspenders to StopAsync, which is normally called first).
        _stopping = true;

        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.CancelOutputRead();
                }
                catch { }
                try
                {
                    _process.CancelErrorRead();
                }
                catch { }

                _process.Kill();
            }

            _process.Dispose();
        }
        catch (Exception ex)
        {
            _updateFunc?.Invoke(true, ex.Message);
        }

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
