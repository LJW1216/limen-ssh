using Microsoft.Web.WebView2.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Limen;

public partial class TerminalView : UserControl, IDisposable
{
    private const string Dim = "\u001b[90m";
    private const string Red = "\u001b[31m";
    private const string Reset = "\u001b[0m";

    private readonly SshProfile _profile;
    private readonly SshConnector _connector;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SshClient? _client;
    private ShellStream? _shell;
    private SshConnectionRoute? _route;
    private CancellationTokenSource? _metricsCancellation;
    private double? _lastGpuPercent;
    private long _lastGpuUsedMb;
    private long _lastGpuTotalMb;
    private uint _columns = 80;
    private uint _rows = 24;
    private bool _awaitingRetry;
    private SessionLog? _log;
    private bool _disposed;

    public event Action<string>? StatusChanged;
    public event Action<bool>? ConnectedChanged;
    public event Action<string>? RemoteDirectoryChanged;
    public event Action<ServerMetrics?>? MetricsChanged;

    public TerminalView(SshProfile profile, SshConnector connector)
    {
        InitializeComponent();
        _profile = profile;
        _connector = connector;
        ThemeManager.Changed += OnThemeChanged;
        Loaded += OnLoaded;
    }

    public bool IsLogging => _log is not null;

    /// Starts mirroring terminal output to a file. Returns the path so the
    /// caller can say where it went.
    public string StartLog(string path)
    {
        StopLog();
        _log = new SessionLog(path,
            Strings.Format("Log.Started", _profile.DisplayPath, _profile.RoutedTarget, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        return _log.Path;
    }

    public void StopLog()
    {
        _log?.Dispose();
        _log = null;
    }

    public void FocusTerminal() => _ = Browser.ExecuteScriptAsync("window.terminalFocus && window.terminalFocus()");

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Limen", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.WebMessageReceived += OnWebMessage;
            // Half a megabyte of inlined xterm.js; building it blocks the UI
            // thread on the very first terminal of the session.
            var html = await Task.Run(TerminalHtml.Build);
            if (_disposed) return;
            Browser.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Browser.Visibility = Visibility.Collapsed;
            FallbackText.Visibility = Visibility.Visible;
            FallbackText.Text = Strings.Get("Terminal.InitFailed") + "\n\n" + ex.Message
                + "\n\n" + Strings.Get("Terminal.InitHint");
            StatusChanged?.Invoke(Strings.Format("Terminal.InitFailedStatus", _profile.Name));
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        switch (root.GetProperty("type").GetString())
        {
            case "ready":
                _columns = (uint)root.GetProperty("cols").GetInt32();
                _rows = (uint)root.GetProperty("rows").GetInt32();
                PushTheme();
                PushFontSize();
                _ = ConnectAsync();
                break;
            case "fontsize":
                TerminalPrefs.FontSize = root.GetProperty("size").GetInt32();
                break;
            case "input":
                OnInput(Convert.FromBase64String(root.GetProperty("data").GetString() ?? string.Empty));
                break;
            case "resize":
                _columns = (uint)root.GetProperty("cols").GetInt32();
                _rows = (uint)root.GetProperty("rows").GetInt32();
                Resize();
                break;
            case "cwd":
                var path = root.GetProperty("path").GetString();
                if (!string.IsNullOrWhiteSpace(path) && path.StartsWith('/'))
                    RemoteDirectoryChanged?.Invoke(path);
                break;
            case "copy":
                var selection = root.GetProperty("text").GetString();
                if (!string.IsNullOrEmpty(selection))
                {
                    Clipboard.SetText(selection);
                    StatusChanged?.Invoke(Strings.Format("Terminal.Copied", _profile.Name));
                }
                break;
            case "paste" when _shell is not null:
                Paste();
                break;
        }
    }

    /// A shell executes each pasted line on arrival, so anything carrying a
    /// newline gets confirmed first.
    private void Paste()
    {
        if (!Clipboard.ContainsText()) return;

        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(Strings.Format("Terminal.ClipboardFailed", _profile.Name, ex.Message));
            return;
        }

        if (text.Length == 0) return;
        if (ConfirmPasteWindow.NeedsConfirmation(text))
        {
            var confirm = new ConfirmPasteWindow(text) { Owner = Window.GetWindow(this) };
            if (confirm.ShowDialog() != true)
            {
                FocusTerminal();
                return;
            }
        }

        _ = WriteAsync(Encoding.UTF8.GetBytes(text));
        FocusTerminal();
    }

    private void OnInput(byte[] bytes)
    {
        if (_shell is not null)
        {
            _ = WriteAsync(bytes);
            return;
        }
        // 연결이 없는 동안 Enter 는 재접속 트리거로 쓴다.
        if (_awaitingRetry && bytes.Contains((byte)'\r'))
        {
            _awaitingRetry = false;
            _ = ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        if (_shell is not null || _disposed) return;

        SshCredentials credentials;
        SshCredentials? jumpCredentials;
        try
        {
            jumpCredentials = _profile.JumpHost is null ? null : _connector.ResolveJump(_profile);
            if (_profile.JumpHost is not null && jumpCredentials is null)
            {
                Fail(Strings.Get("Sftp.BastionCancelled"));
                return;
            }
            var resolved = _connector.Resolve(_profile);
            if (resolved is null)
            {
                Fail(Strings.Get("Sftp.Cancelled"));
                return;
            }
            credentials = resolved;
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            return;
        }

        Notice($"{Dim}" + Strings.Format("Terminal.ConnectingNotice", _profile.RoutedTarget) + $"{Reset}\r\n");
        StatusChanged?.Invoke(Strings.Format("Terminal.Connecting", _profile.Name));

        try
        {
            var columns = _columns;
            var rows = _rows;
            var (client, shell, route) = await Task.Run(() =>
            {
                var openedRoute = _connector.OpenRoute(_profile, credentials, jumpCredentials);
                var sshClient = new SshClient(openedRoute.ConnectionInfo)
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(30)
                };
                try
                {
                    _connector.PinHostKey(sshClient, _profile);
                    sshClient.Connect();
                    return (sshClient, sshClient.CreateShellStream("xterm-256color", columns, rows, 0, 0, 16 * 1024), openedRoute);
                }
                catch (Exception ex)
                {
                    sshClient.Dispose();
                    openedRoute.Dispose();
                    var message = _profile.JumpHost is null
                        ? Strings.Format("Terminal.StageDirect", _profile.Target)
                        : Strings.Format("Terminal.StageBastionTarget", _profile.Target);
                    throw new InvalidOperationException($"{message}\n{ex.Message}", ex);
                }
            });

            if (_disposed)
            {
                shell.Dispose();
                client.Dispose();
                route.Dispose();
                return;
            }

            _client = client;
            _shell = shell;
            _route = route;
            _connector.Remember(_profile, credentials);
            if (jumpCredentials is not null) _connector.RememberJump(_profile, jumpCredentials);

            ConnectedChanged?.Invoke(true);
            StartMetricsPolling();
            StatusChanged?.Invoke(Strings.Format("Terminal.Connected", _profile.Name, _profile.RoutedTarget));
            FocusTerminal();

            _ = Task.Run(ReadLoop);
            await InstallDirectoryReportingAsync();
            if (_profile.LoginCommand.Length > 0)
                await WriteAsync(Encoding.UTF8.GetBytes(_profile.LoginCommand + "\n"));
        }
        catch (Exception ex)
        {
            Fail(Strings.Format("Terminal.ConnectFailed", _profile.RoutedTarget) + "\r\n" + ex.Message);
        }
    }

    private async Task InstallDirectoryReportingAsync()
    {
        // Bash/Zsh 프롬프트마다 OSC 7을 내보낸다. 터미널 화면에는 표시되지 않고
        // WebView2 파서가 경로만 WPF 쪽 SFTP 패널에 전달한다.
        const string command =
            " if [ -n \"$BASH_VERSION\" ]; then " +
            "__ssb_cwd(){ printf '\\033]7;file://%s%s\\033\\\\' \"${HOSTNAME:-localhost}\" \"$PWD\"; }; " +
            "case \";$PROMPT_COMMAND;\" in *\";__ssb_cwd;\"*) ;; *) PROMPT_COMMAND=\"__ssb_cwd${PROMPT_COMMAND:+;$PROMPT_COMMAND}\";; esac; __ssb_cwd; " +
            "elif [ -n \"$ZSH_VERSION\" ]; then autoload -Uz add-zsh-hook; " +
            "__ssb_cwd(){ printf '\\033]7;file://%s%s\\033\\\\' \"${HOST:-localhost}\" \"$PWD\"; }; " +
            "add-zsh-hook precmd __ssb_cwd; __ssb_cwd; fi\n";
        await Task.Delay(150);
        await WriteAsync(Encoding.UTF8.GetBytes(command));
    }

    private void ReadLoop()
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!_disposed && _shell is not null)
            {
                var count = _shell.Read(buffer, 0, buffer.Length);
                if (count <= 0) break;
                _log?.Append(buffer, count);
                PostOutput(buffer[..count]);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SshException)
        {
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            StopMetricsPolling();
            _shell?.Dispose();
            _shell = null;
            _client?.Dispose();
            _client = null;
            _route?.Dispose();
            _route = null;
            _awaitingRetry = true;
            ConnectedChanged?.Invoke(false);
            StatusChanged?.Invoke(Strings.Format("Terminal.Disconnected", _profile.Name));
            Notice($"\r\n{Dim}" + Strings.Get("Terminal.SessionEndedBox") + $"{Reset}\r\n");
        });
    }

    private void StartMetricsPolling()
    {
        StopMetricsPolling();
        _lastGpuPercent = null;
        _lastGpuUsedMb = 0;
        _lastGpuTotalMb = 0;
        _metricsCancellation = new CancellationTokenSource();
        _ = PollMetricsAsync(_metricsCancellation.Token);
    }

    private void StopMetricsPolling()
    {
        _metricsCancellation?.Cancel();
        _metricsCancellation?.Dispose();
        _metricsCancellation = null;
        MetricsChanged?.Invoke(null);
    }

    private async Task PollMetricsAsync(CancellationToken cancellationToken)
    {
        // Connecting already floods the link: the login banner, the SFTP panel's
        // own handshake and this exec channel would otherwise land together and
        // the first screen would stutter. Metrics can wait a moment.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Starting at 1 keeps nvidia-smi — the slowest probe — out of the first
        // sample, so the strip fills in quickly and learns about the GPU next.
        var sampleIndex = 1;
        while (!cancellationToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                var client = _client;
                if (client is null || !client.IsConnected) break;
                var includeGpu = sampleIndex++ % 4 == 0;
                var metricsCommand = BuildMetricsCommand(includeGpu);
                var output = await Task.Run(() =>
                {
                    using var command = client.CreateCommand(metricsCommand);
                    command.CommandTimeout = TimeSpan.FromSeconds(6);
                    return command.Execute();
                }, cancellationToken);

                var metrics = ParseMetrics(output);
                if (metrics is not null && !cancellationToken.IsCancellationRequested)
                    await Dispatcher.InvokeAsync(() => MetricsChanged?.Invoke(metrics));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested) break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static string BuildMetricsCommand(bool includeGpu)
    {
        const string core =
            "LC_ALL=C; " +
            "set -- $(awk '/^cpu /{t=0;for(i=2;i<=9&&i<=NF;i++)t+=$i;print t,$5+$6;exit}' /proc/stat 2>/dev/null); " +
            "__ssb_t1=$1; __ssb_i1=$2; sleep 0.5; " +
            "set -- $(awk '/^cpu /{t=0;for(i=2;i<=9&&i<=NF;i++)t+=$i;print t,$5+$6;exit}' /proc/stat 2>/dev/null); " +
            "awk -v t1=\"$__ssb_t1\" -v i1=\"$__ssb_i1\" -v t2=\"$1\" -v i2=\"$2\" " +
            "'BEGIN{d=t2-t1;if(d>0)printf \"CPU %.1f\\n\",100*(d-(i2-i1))/d;else print \"CPU NA\"}'; " +
            "printf 'MEM '; awk '/^MemTotal:/{t=$2}/^MemAvailable:/{a=$2}END{printf \"%s %s\\n\",t,a}' /proc/meminfo 2>/dev/null; " +
            "printf 'DISK '; df -Pk / 2>/dev/null | awk 'NR==2{gsub(/%/,\"\",$5);printf \"%s %s %s\\n\",$2,$3,$5}'; " +
            "printf 'SYS '; awk '{printf \"%s \",$1}' /proc/loadavg 2>/dev/null; " +
            "(nproc 2>/dev/null || awk '/^processor/{n++}END{print n+0}' /proc/cpuinfo 2>/dev/null || echo 0); ";
        if (!includeGpu) return core;
        return core +
            "if command -v nvidia-smi >/dev/null 2>&1; then " +
            "nvidia-smi --query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits 2>/dev/null | " +
            "awk -F',' '{u+=$1;mu+=$2;mt+=$3;n++}END{if(n)printf \"GPU %.1f %.0f %.0f\\n\",u/n,mu,mt;else print \"GPU NA\"}'; " +
            "else echo 'GPU NA'; fi";
    }

    private ServerMetrics? ParseMetrics(string output)
    {
        double? cpu = null;
        double memory = 0;
        double disk = 0;
        double? gpu = _lastGpuPercent;
        long memoryUsedKb = 0, memoryTotalKb = 0, diskUsedKb = 0, diskTotalKb = 0;
        long gpuUsedMb = _lastGpuUsedMb, gpuTotalMb = _lastGpuTotalMb;
        double? load = null;
        var cores = 0;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (parts[0] == "CPU" && parts.Length >= 2 &&
                double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var cpuValue))
            {
                cpu = Math.Clamp(cpuValue, 0, 100);
            }
            else if (parts[0] == "MEM" && parts.Length >= 3 &&
                     long.TryParse(parts[1], out memoryTotalKb) && long.TryParse(parts[2], out var availableKb) && memoryTotalKb > 0)
            {
                memoryUsedKb = Math.Max(0, memoryTotalKb - availableKb);
                memory = Math.Clamp(100d * memoryUsedKb / memoryTotalKb, 0, 100);
            }
            else if (parts[0] == "DISK" && parts.Length >= 4 &&
                     long.TryParse(parts[1], out diskTotalKb) && long.TryParse(parts[2], out diskUsedKb) &&
                     double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out disk))
            {
                disk = Math.Clamp(disk, 0, 100);
            }
            else if (parts[0] == "GPU" && parts.Length >= 4 &&
                     double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var gpuValue) &&
                     long.TryParse(parts[2].Split('.')[0], out gpuUsedMb) && long.TryParse(parts[3].Split('.')[0], out gpuTotalMb))
            {
                gpu = Math.Clamp(gpuValue, 0, 100);
                _lastGpuPercent = gpu;
                _lastGpuUsedMb = gpuUsedMb;
                _lastGpuTotalMb = gpuTotalMb;
            }
            else if (parts[0] == "SYS" && parts.Length >= 3 &&
                     double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var loadValue) &&
                     int.TryParse(parts[2], out cores))
            {
                load = loadValue;
            }
            else if (parts[0] == "GPU" && parts[1] == "NA")
            {
                gpu = _lastGpuPercent = null;
                gpuUsedMb = _lastGpuUsedMb = 0;
                gpuTotalMb = _lastGpuTotalMb = 0;
            }
        }

        if (memoryTotalKb == 0 && diskTotalKb == 0 && cpu is null) return null;
        return new ServerMetrics(cpu, memory, memoryUsedKb, memoryTotalKb, gpu, gpuUsedMb, gpuTotalMb,
            disk, diskUsedKb, diskTotalKb, load, cores, DateTime.Now);
    }

    private async Task WriteAsync(byte[] bytes)
    {
        if (_disposed || _shell is null || bytes.Length == 0) return;
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _shell.Write(bytes, 0, bytes.Length);
            _shell.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SshException)
        {
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Resize()
    {
        try
        {
            _shell?.ChangeWindowSize(_columns, _rows, 0, 0);
        }
        catch (Exception e) when (e is ObjectDisposedException or SshException)
        {
        }
    }

    private void PostOutput(byte[] bytes)
    {
        var message = JsonSerializer.Serialize(new { type = "output", data = Convert.ToBase64String(bytes) });
        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) Browser.CoreWebView2?.PostWebMessageAsJson(message);
        });
    }

    /// Repaints the xterm palette when the app theme flips.
    private void OnThemeChanged(bool dark) => Dispatcher.BeginInvoke(PushTheme);

    private void PushTheme()
    {
        if (_disposed) return;
        Browser.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(ThemeManager.Token("TerminalBack"));
        Browser.CoreWebView2?.PostWebMessageAsJson(
            $$"""{"type":"theme","theme":{{TerminalHtml.PaletteJson(ThemeManager.IsDark)}},"ui":{{TerminalHtml.UiJson(ThemeManager.IsDark)}}}""");
    }

    private void PushFontSize()
    {
        if (_disposed) return;
        Browser.CoreWebView2?.PostWebMessageAsJson(
            JsonSerializer.Serialize(new { type = "font", size = TerminalPrefs.FontSize }));
    }

    private void Notice(string text)
    {
        if (_disposed) return;
        Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "notice", text }));
    }

    private void Fail(string message)
    {
        _awaitingRetry = true;
        ConnectedChanged?.Invoke(false);
        StatusChanged?.Invoke(Strings.Format("Terminal.ConnectFailedStatus", _profile.Name));
        Notice($"\r\n{Red}{message}{Reset}\r\n{Dim}" + Strings.Get("Terminal.RetryHint") + $"{Reset}\r\n");
        FocusTerminal();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMetricsPolling();
        StopLog();
        ThemeManager.Changed -= OnThemeChanged;
        if (Browser.CoreWebView2 is not null) Browser.CoreWebView2.WebMessageReceived -= OnWebMessage;
        _shell?.Dispose();
        _shell = null;
        _client?.Dispose();
        _client = null;
        _route?.Dispose();
        _route = null;
        _writeLock.Dispose();
        Browser.Dispose();
    }
}

public sealed record ServerMetrics(
    double? CpuPercent,
    double MemoryPercent,
    long MemoryUsedKb,
    long MemoryTotalKb,
    double? GpuPercent,
    long GpuMemoryUsedMb,
    long GpuMemoryTotalMb,
    double DiskPercent,
    long DiskUsedKb,
    long DiskTotalKb,
    double? Load1,
    int CoreCount,
    DateTime SampledAt);
