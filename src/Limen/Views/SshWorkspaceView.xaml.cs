using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Limen;

public partial class SshWorkspaceView : UserControl, IDisposable
{
    private readonly SshProfile _profile;
    private readonly SshConnector _connector;
    private readonly TerminalView _terminal;
    private SftpView? _sftp;
    private GridLength _openWidth = new(2, GridUnitType.Star);
    private string? _pendingDirectory;
    private ServerMetrics? _lastMetrics;
    private bool _openingSftp;
    private bool _disposed;

    public event Action<string>? StatusChanged;
    public event Action<bool>? ConnectedChanged;

    public SshWorkspaceView(SshProfile profile, SshConnector connector)
    {
        InitializeComponent();
        _profile = profile;
        _connector = connector;
        _terminal = new TerminalView(profile, connector);
        _terminal.StatusChanged += message => StatusChanged?.Invoke(message);
        _terminal.ConnectedChanged += Terminal_ConnectedChanged;
        _terminal.RemoteDirectoryChanged += Terminal_RemoteDirectoryChanged;
        _terminal.MetricsChanged += Terminal_MetricsChanged;
        // Severity colours come from theme brushes, so a theme flip has to
        // repaint the gauges with the sample already on screen.
        ThemeManager.Changed += OnThemeChanged;
        ApplyTag(profile);
        ApplyLogState();
        TerminalHost.Children.Add(_terminal);
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        if (_terminal.IsLogging)
        {
            _terminal.StopLog();
            ApplyLogState();
            StatusChanged?.Invoke(Strings.Format("Workspace.LogStopped", _profile.Name));
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.Get("Workspace.LogDialogTitle"),
            Filter = Strings.Get("Workspace.LogFilter"),
            FileName = $"{Sanitize(_profile.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Limen")
        };
        Directory.CreateDirectory(dialog.InitialDirectory);
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var path = _terminal.StartLog(dialog.FileName);
            ApplyLogState();
            StatusChanged?.Invoke(Strings.Format("Workspace.LogStarted", _profile.Name, path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), Strings.Format("Workspace.LogFailed", ex.Message),
                Strings.Get("Workspace.LogTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyLogState()
    {
        var recording = _terminal.IsLogging;
        LogIcon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,
            recording ? "Danger" : "Muted");
        LogButton.ToolTip = recording ? Strings.Get("Workspace.StopLog") : Strings.Get("Workspace.StartLog");
    }

    /// Session names carry brackets and slashes; a file name cannot.
    private static string Sanitize(string name)
    {
        var cleaned = new string(name
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray())
            .Trim();
        return cleaned.Length == 0 ? "session" : cleaned;
    }

    private void ApplyTag(SshProfile profile)
    {
        var tag = SessionColors.Brush(profile.Color);
        TagStrip.Background = tag;
        TagStrip.Visibility = tag is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnThemeChanged(bool dark) => Dispatcher.BeginInvoke(() => Render(_lastMetrics));

    private void Terminal_MetricsChanged(ServerMetrics? metrics)
    {
        _lastMetrics = metrics;
        Render(metrics);
    }

    private void Render(ServerMetrics? metrics)
    {
        if (metrics is null)
        {
            MetricsDot.Fill = Token("Idle");
            MetricsStateText.Text = Strings.Get("Metrics.Waiting");
            MetricsTimeText.Text = string.Empty;
            ShowGpu(true);
            foreach (var (bar, value, detail) in Gauges())
            {
                bar.Value = 0;
                bar.Foreground = Token("LineStrong");
                value.Text = "—";
                value.Foreground = Token("Faint");
                detail.Text = string.Empty;
            }
            return;
        }

        MetricsDot.Fill = Token("Success");
        MetricsStateText.Text = Strings.Get("Metrics.Live");
        MetricsTimeText.Text = metrics.SampledAt.ToString("HH:mm:ss");

        if (metrics.CpuPercent is null)
        {
            CpuBar.Value = 0;
            CpuBar.Foreground = Token("LineStrong");
            CpuValue.Text = "…";
            CpuValue.Foreground = Token("Faint");
            CpuText.Text = Strings.Get("Metrics.FirstSample");
            CpuText.Foreground = Token("Faint");
        }
        else
        {
            Set(CpuBar, CpuValue, CpuText, metrics.CpuPercent.Value, CpuDetail(metrics));
        }

        Set(MemoryBar, MemoryValue, MemoryText, metrics.MemoryPercent,
            Pair(metrics.MemoryUsedKb, metrics.MemoryTotalKb));

        // A machine without a GPU should not keep a dead gauge on screen.
        if (metrics.GpuPercent is null)
        {
            ShowGpu(false);
        }
        else
        {
            ShowGpu(true);
            Set(GpuBar, GpuValue, GpuText, metrics.GpuPercent.Value,
                PairMb(metrics.GpuMemoryUsedMb, metrics.GpuMemoryTotalMb));
        }

        var free = Math.Max(0, metrics.DiskTotalKb - metrics.DiskUsedKb);
        Set(DiskBar, DiskValue, DiskText, metrics.DiskPercent,
            Strings.Format("Metrics.DiskDetail", Pair(metrics.DiskUsedKb, metrics.DiskTotalKb), Size(free)));
    }

    private static string CpuDetail(ServerMetrics metrics)
    {
        var cores = metrics.CoreCount > 0 ? Strings.Format("Metrics.Cores", metrics.CoreCount) : string.Empty;
        var load = metrics.Load1 is null ? string.Empty : $"load {metrics.Load1:0.00}";
        return string.Join(" · ", new[] { cores, load }.Where(part => part.Length > 0));
    }

    private void ShowGpu(bool visible)
    {
        GpuGauge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        GpuColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        GpuColumn.MinWidth = visible ? 120 : 0;
    }

    private (ProgressBar Bar, TextBlock Value, TextBlock Detail)[] Gauges() =>
    [
        (CpuBar, CpuValue, CpuText), (MemoryBar, MemoryValue, MemoryText),
        (GpuBar, GpuValue, GpuText), (DiskBar, DiskValue, DiskText)
    ];

    private void Set(ProgressBar bar, TextBlock value, TextBlock detail, double percent, string figures)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var alarming = clamped >= DangerAt;

        bar.Value = clamped;
        bar.Foreground = Severity(clamped);
        value.Text = $"{clamped:0}%";
        value.Foreground = Token(alarming ? "Danger" : "Ink");
        detail.Text = figures;
        detail.Foreground = Token(alarming ? "Danger" : "Faint");
    }

    private const double WarnAt = 75;
    private const double DangerAt = 90;

    /// Colour is the alarm, not a per-metric identity: a green memory bar at
    /// 95% would be worse than no bar at all.
    private Brush Severity(double percent) =>
        Token(percent >= DangerAt ? "Danger" : percent >= WarnAt ? "Warn" : "Success");

    private Brush Token(string key) => (Brush)FindResource(key);

    /// "11.6 / 16.0 GiB" — one unit for the pair, dropping decimals once the
    /// total is large enough that they are noise.
    private static string Pair(long usedKb, long totalKb)
    {
        var used = usedKb / 1024d / 1024d;
        var total = totalKb / 1024d / 1024d;
        var format = total >= 100 ? "0" : "0.0";
        return $"{used.ToString(format)} / {total.ToString(format)} GiB";
    }

    private static string PairMb(long usedMb, long totalMb) =>
        Pair(usedMb * 1024, totalMb * 1024);

    private static string Size(long kilobytes)
    {
        var gib = kilobytes / 1024d / 1024d;
        return gib >= 10 ? $"{gib:0} GiB" : $"{gib:0.0} GiB";
    }

    public void FocusTerminal() => _terminal.FocusTerminal();

    private void Terminal_ConnectedChanged(bool connected)
    {
        ConnectedChanged?.Invoke(connected);
        if (connected && _sftp is null && !_openingSftp) _ = OpenSftpPanelAsync();
    }

    /// The panel needs a second SSH connection of its own — through the bastion
    /// that is a second full handshake. Opening it the instant the shell comes
    /// up makes the first screen stutter, so it waits for the login banner to
    /// land first.
    private async Task OpenSftpPanelAsync()
    {
        _openingSftp = true;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700));
            if (_disposed || _sftp is not null) return;

            var panel = new SftpView(_profile, _connector, remoteOnly: true);
            panel.StatusChanged += message => StatusChanged?.Invoke(message);
            SftpHost.Children.Clear();
            SftpHost.Children.Add(panel);
            _sftp = panel;

            // A directory report that arrived while the panel was still opening
            // would otherwise be dropped.
            if (_pendingDirectory is { } pending)
            {
                await panel.NavigateRemoteAsync(pending);
                if (_pendingDirectory == pending) _pendingDirectory = null;
            }
        }
        finally
        {
            _openingSftp = false;
        }
    }

    private async void Terminal_RemoteDirectoryChanged(string path)
    {
        _pendingDirectory = path;
        if (_sftp is null) return;
        var requested = _pendingDirectory;
        await _sftp.NavigateRemoteAsync(requested);
        if (_pendingDirectory == requested) _pendingDirectory = null;
    }

    private void ToggleSftp_Click(object sender, RoutedEventArgs e)
    {
        var closing = SftpColumn.Width.Value > 0;
        if (closing)
        {
            _openWidth = SftpColumn.Width;
            SftpColumn.MinWidth = 0;
            SftpColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
        }
        else
        {
            SftpColumn.MinWidth = 280;
            SftpColumn.Width = _openWidth.Value > 0 ? _openWidth : new GridLength(2, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(5);
        }

        ToggleSftpButton.ToolTip = closing ? Strings.Get("Workspace.ExpandSftp") : Strings.Get("Workspace.CollapseSftp");
        ToggleIcon.Opacity = closing ? 0.45 : 1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ThemeManager.Changed -= OnThemeChanged;
        _terminal.Dispose();
        _sftp?.Dispose();
    }
}
