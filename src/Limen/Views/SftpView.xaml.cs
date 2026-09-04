using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Limen;

public partial class SftpView : UserControl, IDisposable
{
    private readonly SshProfile _profile;
    private readonly SshConnector _connector;
    private readonly ObservableCollection<FileEntry> _local = [];
    private readonly ObservableCollection<FileEntry> _remote = [];
    private ISftpClient? _client;
    private SshConnectionRoute? _route;
    private RemoteShell? _shell;
    private readonly bool _remoteOnly;
    private readonly SemaphoreSlim _navigationLock = new(1, 1);
    private long _navigationVersion;
    private bool _connecting;
    private bool _mutating;
    private string _remoteHome = "/";
    private string? _lastFollowPath;
    private CancellationTokenSource? _transfer;
    private string _localPath = string.Empty;
    private string _remotePath = "/";
    private string? _requestedRemotePath;
    private Point? _remoteDragStart;
    private FileEntry? _remoteDragEntry;
    private bool _remoteDragPreparing;
    private bool _disposed;

    public event Action<string>? StatusChanged;
    public event Action<bool>? ConnectedChanged;

    public SftpView(SshProfile profile, SshConnector connector, bool remoteOnly = false)
    {
        InitializeComponent();
        _profile = profile;
        _connector = connector;
        _remoteOnly = remoteOnly;
        LocalList.ItemsSource = _local;
        RemoteList.ItemsSource = _remote;
        LocalList.PreviewMouseRightButtonDown += FileList_RightButtonDown;
        RemoteList.PreviewMouseRightButtonDown += FileList_RightButtonDown;
        // GridView columns do not stretch on their own, so the name column is
        // given whatever the fixed columns leave behind.
        LocalList.SizeChanged += (_, _) => FitColumns(LocalList);
        RemoteList.SizeChanged += (_, _) => FitColumns(RemoteList);
        RemoteHeader.Text = _profile.RoutedTarget;

        var tag = SessionColors.Brush(profile.Color);
        TagStrip.Background = tag;
        TagStrip.Visibility = tag is null ? Visibility.Collapsed : Visibility.Visible;

        if (remoteOnly)
        {
            LocalPane.Visibility = Visibility.Collapsed;
            TransferPane.Visibility = Visibility.Collapsed;
            LocalColumn.Width = new GridLength(0);
            TransferColumn.Width = new GridLength(0);
            RemoteColumn.Width = new GridLength(1, GridUnitType.Star);
            RemoteHeader.Text = Strings.Get("Sftp.FollowsTerminal");
            // Embedded beside a terminal there is nothing to transfer between,
            // so the transfer strip would only eat vertical space.
            StatusStrip.Visibility = Visibility.Collapsed;
            // The workspace already paints the tag strip above this pane.
            TagStrip.Visibility = Visibility.Collapsed;
        }

        // Enumerating the local directory costs UI-thread time; in remote-only
        // mode that pane is collapsed, so the work is pure waste.
        if (!remoteOnly)
        {
            var start = profile.LocalDirectory.Length > 0 && Directory.Exists(profile.LocalDirectory)
                ? profile.LocalDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ShowLocal(start);
        }

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ConnectAsync();
    }

    // 접속 -----------------------------------------------------------------

    private async Task ConnectAsync()
    {
        if (_disposed || _connecting || _client is { IsConnected: true }) return;
        _connecting = true;
        try { await ConnectCoreAsync(); }
        finally { _connecting = false; }
    }

    private async Task ConnectCoreAsync()
    {
        _client?.Dispose();
        _shell?.Dispose();
        _route?.Dispose();
        _client = null;
        _shell = null;
        _route = null;
        _lastFollowPath = null;

        SshCredentials credentials;
        SshCredentials? jumpCredentials;
        try
        {
            jumpCredentials = _profile.JumpHost is null ? null : _connector.ResolveJump(_profile);
            if (_profile.JumpHost is not null && jumpCredentials is null)
            {
                FailRemote(Strings.Get("Sftp.BastionCancelled"));
                return;
            }
            var resolved = _connector.Resolve(_profile);
            if (resolved is null)
            {
                FailRemote(Strings.Get("Sftp.Cancelled"));
                return;
            }
            credentials = resolved;
        }
        catch (Exception ex)
        {
            FailRemote(ex.Message);
            return;
        }

        RemoteOverlayText.Text = Strings.Format("Sftp.ConnectingTo", _profile.RoutedTarget);
        StatusChanged?.Invoke(Strings.Format("Sftp.ConnectingStatus", _profile.Name));

        try
        {
            var (client, route) = await Task.Run(() =>
            {
                var openedRoute = _connector.OpenRoute(_profile, credentials, jumpCredentials);
                var sftp = new SftpClient(openedRoute.ConnectionInfo)
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(30)
                };
                try
                {
                    _connector.PinHostKey(sftp, _profile);
                    sftp.Connect();
                    return (sftp, openedRoute);
                }
                catch (Exception ex)
                {
                    sftp.Dispose();
                    openedRoute.Dispose();
                    var message = _profile.JumpHost is null
                        ? Strings.Format("Sftp.StageDirect", _profile.Target)
                        : Strings.Format("Sftp.StageBastionTarget", _profile.Target);
                    throw new InvalidOperationException($"{message}\n{ex.Message}", ex);
                }
            });

            if (_disposed)
            {
                client.Dispose();
                route.Dispose();
                return;
            }

            _client = client;
            _route = route;
            _remoteHome = _remotePath = client.WorkingDirectory;
            _connector.Remember(_profile, credentials);
            if (jumpCredentials is not null) _connector.RememberJump(_profile, jumpCredentials);
            RemoteOverlay.Visibility = Visibility.Collapsed;
            ConnectedChanged?.Invoke(true);
            StatusChanged?.Invoke(Strings.Format("Sftp.Connected", _profile.Name));

            var start = _requestedRemotePath
                ?? (_profile.RemoteDirectory.Length > 0 ? _profile.RemoteDirectory : client.WorkingDirectory);
            await ShowRemoteAsync(start, follow: _requestedRemotePath is not null);
        }
        catch (Exception ex)
        {
            FailRemote(Strings.Format("Sftp.ConnectFailed", _profile.RoutedTarget) + "\n\n" + ex.Message);
        }
    }

    private static void FitColumns(ListView list)
    {
        if (list.View is not GridView grid || grid.Columns.Count < 3) return;
        var reserved = grid.Columns[1].ActualWidth + grid.Columns[2].ActualWidth + 28;
        grid.Columns[0].Width = Math.Max(140, list.ActualWidth - reserved);
    }

    private void FailRemote(string message, string? detail = null)
    {
        if (_disposed) return;
        RemoteOverlay.Visibility = Visibility.Visible;
        ConnectSpinner.Visibility = Visibility.Collapsed;
        RemoteOverlayText.Text = message;
        RemoteOverlayDetail.Text = detail ?? string.Empty;
        RemoteOverlayDetail.Visibility = string.IsNullOrWhiteSpace(detail)
            ? Visibility.Collapsed
            : Visibility.Visible;
        RemoteRetryButton.Visibility = Visibility.Visible;
        ConnectedChanged?.Invoke(false);
        StatusChanged?.Invoke(Strings.Format("Sftp.ConnectFailedStatus", _profile.Name));
    }

    private async void RemoteRename_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _mutating || _transfer is not null) return;
        var entry = Selected(RemoteList).FirstOrDefault();
        if (entry is null || entry.IsParent || _client is null) return;

        var name = AskName(entry.Name, remote: true);
        if (name is null) return;

        try
        {
            _mutating = true;
            var parent = ParentRemote(entry.FullPath);
            var target = parent.EndsWith('/') ? parent + name : $"{parent}/{name}";
            await Task.Run(() => _client.RenameFile(entry.FullPath, target));
            await ShowRemoteAsync(_remotePath);
            StatusChanged?.Invoke(Strings.Format("Sftp.Renamed", entry.Name, name));
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally { _mutating = false; }
    }

    private async void RemoteChmod_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _mutating || _transfer is not null) return;
        var entry = Selected(RemoteList).FirstOrDefault();
        if (entry is null || entry.IsParent || _client is null) return;

        string current;
        try
        {
            current = Octal(await Task.Run(() => _client.GetAttributes(entry.FullPath)));
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
            return;
        }

        var prompt = new PromptWindow(Strings.Get("Sftp.Permissions"),
            Strings.Format("Sftp.ChmodPrompt", entry.Name),
            canRemember: false, masked: false, initial: current) { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;

        var text = prompt.Value.Trim();
        if (_disposed || _mutating || _transfer is not null) return;
        if (text.Length is < 3 or > 4 || text.Any(c => c is < '0' or > '7'))
        {
            Warn(Strings.Get("Sftp.ChmodBad"));
            return;
        }

        try
        {
            _mutating = true;
            var mode = Convert.ToInt16(text, 8);
            await Task.Run(() => _client.ChangePermissions(entry.FullPath, mode));
            await ShowRemoteAsync(_remotePath);
            StatusChanged?.Invoke(Strings.Format("Sftp.Chmodded", entry.Name, text));
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally { _mutating = false; }
    }

    /// SSH.NET exposes permissions as booleans; fold them back into the octal
    /// form people actually type.
    private static string Octal(Renci.SshNet.Sftp.SftpFileAttributes attributes)
    {
        var owner = (attributes.OwnerCanRead ? 4 : 0) + (attributes.OwnerCanWrite ? 2 : 0) + (attributes.OwnerCanExecute ? 1 : 0);
        var group = (attributes.GroupCanRead ? 4 : 0) + (attributes.GroupCanWrite ? 2 : 0) + (attributes.GroupCanExecute ? 1 : 0);
        var others = (attributes.OthersCanRead ? 4 : 0) + (attributes.OthersCanWrite ? 2 : 0) + (attributes.OthersCanExecute ? 1 : 0);
        var special = (attributes.IsUIDBitSet ? 4 : 0) + (attributes.IsGroupIDBitSet ? 2 : 0) + (attributes.IsStickyBitSet ? 1 : 0);
        return (special == 0 ? "" : special.ToString()) + $"{owner}{group}{others}";
    }

    private async void RemoteRetry_Click(object sender, RoutedEventArgs e)
    {
        ConnectSpinner.Visibility = Visibility.Visible;
        RemoteRetryButton.Visibility = Visibility.Collapsed;
        RemoteOverlayText.Text = Strings.Get("Sftp.Connecting");
        RemoteOverlayDetail.Visibility = Visibility.Collapsed;
        await ConnectAsync();
    }

    // 로컬 목록 -------------------------------------------------------------

    private void ShowLocal(string path)
    {
        if (_disposed || _remoteOnly || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            if (!directory.Exists) throw new DirectoryNotFoundException(Strings.Format("Sftp.FolderMissing", path));

            var entries = new List<FileEntry>();
            if (directory.Parent is not null)
                entries.Add(new FileEntry { Name = "..", FullPath = directory.Parent.FullName, IsDirectory = true, IsParent = true });

            foreach (var child in directory.EnumerateDirectories())
                entries.Add(new FileEntry { Name = child.Name, FullPath = child.FullName, IsDirectory = true, Modified = child.LastWriteTime });
            foreach (var file in directory.EnumerateFiles())
                entries.Add(new FileEntry { Name = file.Name, FullPath = file.FullName, Size = file.Length, Modified = file.LastWriteTime });

            entries.Sort(FileEntry.Compare);
            _local.Clear();
            foreach (var entry in entries) _local.Add(entry);

            _localPath = directory.FullName;
            LocalPathBox.Text = _localPath;
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
            LocalPathBox.Text = _localPath;
        }
    }

    private void LocalRefresh_Click(object sender, RoutedEventArgs e) => ShowLocal(_localPath);

    private void LocalUp_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_localPath)) return;
        var parent = Directory.GetParent(_localPath);
        if (parent is not null) ShowLocal(parent.FullName);
    }

    private void LocalPath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ShowLocal(LocalPathBox.Text.Trim());
    }

    private void LocalList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        LocalRename_Click(sender, e);
        e.Handled = true;
    }

    private void RemoteList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2) return;
        RemoteRename_Click(sender, e);
        e.Handled = true;
    }

    private void LocalList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DoubleClickedEntry(LocalList, e) is not FileEntry entry) return;
        e.Handled = true;
        if (entry.IsDirectory) ShowLocal(entry.FullPath);
        else OpenLocalFile(entry.FullPath);
    }

    private void LocalNewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = Ask(Strings.Get("Common.NewFolder"), Strings.Get("Sftp.NewFolderLocal"));
        if (name is null || !ValidateName(name, remote: false)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(_localPath, name));
            ShowLocal(_localPath);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
    }

    private void LocalDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_transfer is not null) return;
        var targets = Selected(LocalList);
        if (targets.Count == 0 || !ConfirmDelete(targets)) return;
        try
        {
            foreach (var entry in targets)
            {
                if (entry.IsDirectory) Directory.Delete(entry.FullPath, recursive: true);
                else File.Delete(entry.FullPath);
            }
            ShowLocal(_localPath);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
    }

    private void LocalRename_Click(object sender, RoutedEventArgs e)
    {
        if (_transfer is not null) return;
        var entry = Selected(LocalList).FirstOrDefault();
        if (entry is null || entry.IsParent) return;

        var name = AskName(entry.Name);
        if (name is null) return;

        try
        {
            var target = Path.Combine(Path.GetDirectoryName(entry.FullPath)!, name);
            if (entry.IsDirectory) Directory.Move(entry.FullPath, target);
            else File.Move(entry.FullPath, target);
            ShowLocal(_localPath);
            StatusChanged?.Invoke(Strings.Format("Sftp.Renamed", entry.Name, name));
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
    }

    /// Shared rename prompt. Rejects path separators so a rename can never turn
    /// into a move to somewhere unexpected.
    private string? AskName(string current, bool remote = false)
    {
        while (true)
        {
            var prompt = new PromptWindow(Strings.Get("Common.Rename"), Strings.Format("Sftp.RenamePrompt", current),
                canRemember: false, masked: false, initial: current) { Owner = Window.GetWindow(this) };
            if (prompt.ShowDialog() != true) return null;

            var name = prompt.Value.Trim();
            if (name.Length == 0 || name == current) return null;
            if (remote ? TransferPaths.IsRemoteName(name) : TransferPaths.IsLocalName(name)) return name;

            Warn(Strings.Get("Sftp.RenameBadName"));
        }
    }

    private void Local_Drop(object sender, DragEventArgs e)
    {
        if (_remoteDragPreparing) { e.Handled = true; return; }
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        var destination = _remotePath;
        _ = RunTransferAsync(Strings.Get("Sftp.Upload"), async (token, report) =>
        {
            foreach (var path in paths) await UploadAsync(path, destination, token, report);
        });
    }

    // 원격 목록 -------------------------------------------------------------

    private async Task ShowRemoteAsync(string path, bool follow = false)
    {
        var client = _client;
        if (_disposed || client is null) return;
        var version = ++_navigationVersion;
        var requested = TransferPaths.ResolveRemote(path, _remotePath, _remoteHome);
        await _navigationLock.WaitAsync();
        try
        {
            if (_disposed || version != _navigationVersion) return;
            var (normalized, files, directoryLinks) = await Task.Run(() =>
            {
                client.ChangeDirectory(requested);
                var resolved = client.WorkingDirectory;
                var listing = client.ListDirectory(resolved).ToList();
                var links = listing.Where(file => file.IsSymbolicLink && IsDirectoryLink(client, file.FullName))
                    .Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
                return (resolved, listing, links);
            });
            if (_disposed || version != _navigationVersion) return;

            var entries = new List<FileEntry>();
            if (normalized != "/")
                entries.Add(new FileEntry { Name = "..", FullPath = ParentRemote(normalized), IsDirectory = true, IsParent = true });

            foreach (var file in files)
            {
                if (file.Name is "." or "..") continue;
                entries.Add(new FileEntry
                {
                    Name = file.Name,
                    FullPath = CombineRemote(normalized, file.Name),
                    IsDirectory = file.IsDirectory || directoryLinks.Contains(file.Name),
                    Size = file.Length,
                    Modified = file.LastWriteTime
                });
            }

            entries.Sort(FileEntry.Compare);
            _remote.Clear();
            foreach (var entry in entries) _remote.Add(entry);

            _remotePath = normalized;
            RemotePathBox.Text = normalized;
        }
        catch (Exception ex)
        {
            if (_disposed || version != _navigationVersion) return;
            if (!client.IsConnected) FailRemote(ex.Message);
            else if (follow) StatusChanged?.Invoke($"SFTP: {path}: {ex.Message}");
            else Warn(ex.Message);
            RemotePathBox.Text = _remotePath;
        }
        finally { _navigationLock.Release(); }
    }

    private static bool IsDirectoryLink(ISftpClient client, string path)
    {
        try { return client.GetAttributes(path).IsDirectory; }
        catch { return false; } // A dangling link must not hide the directory listing.
    }

    public async Task NavigateRemoteAsync(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;
        _requestedRemotePath = path;
        if (_client is null) return;
        if (_lastFollowPath == path) return;
        _lastFollowPath = path;
        await ShowRemoteAsync(path, follow: true);
        if (_requestedRemotePath == path) _requestedRemotePath = null;
    }

    private async void RemoteRefresh_Click(object sender, RoutedEventArgs e) => await ShowRemoteAsync(_remotePath);

    private async void RemoteUp_Click(object sender, RoutedEventArgs e)
    {
        if (_remotePath != "/") await ShowRemoteAsync(ParentRemote(_remotePath));
    }

    private async void RemotePath_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await ShowRemoteAsync(RemotePathBox.Text.Trim());
    }

    private async void RemoteList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DoubleClickedEntry(RemoteList, e) is not FileEntry entry) return;
        e.Handled = true;
        if (entry.IsDirectory) await ShowRemoteAsync(entry.FullPath);
        else await OpenRemoteFileAsync(entry);
    }

    private void RemoteList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _remoteDragEntry = EntryAt(RemoteList, e.OriginalSource as DependencyObject);
        _remoteDragStart = _remoteDragEntry is { IsParent: false }
            ? e.GetPosition(RemoteList)
            : null;
    }

    private void RemoteList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_remoteDragPreparing)
        {
            _remoteDragStart = null;
            _remoteDragEntry = null;
        }
    }

    private async void RemoteList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_remoteDragPreparing || _transfer is not null || _remoteDragStart is not Point start ||
            _remoteDragEntry is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(RemoteList);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var targets = Selected(RemoteList);
        if (!targets.Contains(_remoteDragEntry)) targets = [_remoteDragEntry];
        if (targets.Count == 0) return;

        e.Handled = true;
        _remoteDragPreparing = true;
        _remoteDragStart = null;
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Limen", "DragDrop", Guid.NewGuid().ToString("N"));

        try
        {
            var downloaded = await RunTransferAsync(Strings.Get("Sftp.DragDownload"), async (token, report) =>
            {
                Directory.CreateDirectory(stagingDirectory);
                foreach (var entry in targets)
                    await DownloadAsync(entry.FullPath, entry.IsDirectory, stagingDirectory, token, report);
            });
            if (!downloaded) return;

            var localPaths = targets
                .Select(entry => Path.Combine(stagingDirectory, entry.Name))
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToArray();
            if (localPaths.Length == 0) return;

            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                TransferText.Text = Strings.Get("Sftp.DragHold");
                return;
            }

            var data = new DataObject(DataFormats.FileDrop, localPaths);
            var effect = DragDrop.DoDragDrop(RemoteList, data, DragDropEffects.Copy);
            TransferText.Text = effect == DragDropEffects.None ? Strings.Get("Sftp.DragCancelled") : Strings.Get("Sftp.DragDone");
        }
        finally
        {
            _remoteDragPreparing = false;
            _remoteDragEntry = null;
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async void RemoteNewFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _mutating || _transfer is not null) return;
        if (_client is null) return;
        var name = Ask(Strings.Get("Common.NewFolder"), Strings.Get("Sftp.NewFolderRemote"));
        if (name is null || !ValidateName(name, remote: true)) return;
        try
        {
            _mutating = true;
            var client = _client;
            var target = CombineRemote(_remotePath, name);
            await Task.Run(() => client.CreateDirectory(target));
            await ShowRemoteAsync(_remotePath);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally { _mutating = false; }
    }

    private async void RemoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _mutating || _transfer is not null) return;
        if (_client is null) return;
        var targets = Selected(RemoteList).Where(entry => !entry.IsParent).ToList();
        if (targets.Count == 0 || !ConfirmDelete(targets)) return;

        StatusChanged?.Invoke(Strings.Format("Sftp.Deleting", targets.Count));
        try
        {
            _mutating = true;
            var fast = await Task.Run(() => DeleteRemoteBatch(targets));
            await ShowRemoteAsync(_remotePath);
            StatusChanged?.Invoke(Strings.Format(fast ? "Sftp.DeletedFast" : "Sftp.Deleted", targets.Count));
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally { _mutating = false; }
    }

    // SFTP has no recursive delete — the protocol offers "remove this file" and
    // "remove this empty directory" and nothing else — so the walk below pays a
    // round trip per entry. A node_modules over a 20 ms link takes minutes that
    // way; one `rm -rf` takes a second. Returns true when the server did the
    // work, which only happens for directories that passed the proof below.
    private bool DeleteRemoteBatch(List<FileEntry> targets)
    {
        var fast = false;
        foreach (var entry in targets)
        {
            if (entry.IsDirectory && TryDeleteOnServer(entry.FullPath))
            {
                fast = true;
                continue;
            }
            DeleteRemote(entry.FullPath, entry.IsDirectory);
        }
        return fast;
    }

    // A chrooted SFTP subsystem and an SSH shell need not share a filesystem
    // root: the /data/foo the user is browsing can be /home/u/data/foo to the
    // shell, and handing that path to rm would delete something else entirely.
    // So prove the two agree before trusting rm — write a marker only this call
    // could have produced, through SFTP, and ask the shell to find it at the
    // same absolute path. Any failure at all falls back to the protocol walk.
    private bool TryDeleteOnServer(string path) =>
        _client is not null && _route is not null && TryDeleteOnServer(path, _client, Shell());

    internal static bool TryDeleteOnServer(string path, ISftpClient client, IRemoteShell shell)
    {
        if (RemoteShell.IsDangerous(path)) return false;
        // rm -rf on a symlink removes the link; the walk below does the same,
        // just as fast. Keep the one code path that already handles it.
        try { if (client.Get(path).IsSymbolicLink) return false; }
        catch (Exception) { return false; }

        // Writing the marker needs write permission on the directory — which
        // deleting its contents needs too, so this rules nothing out.
        var marker = path.TrimEnd('/') + "/.limen-rm-" + Guid.NewGuid().ToString("n");
        try { client.WriteAllBytes(marker, []); }
        catch (Exception) { return false; }

        if (!shell.TryRun($"test -f {RemoteShell.Quote(marker)}", out _))
        {
            try { client.DeleteFile(marker); } catch (Exception) { }
            return false;
        }

        // A partial rm is still safe to fall back from: the walk finishes the
        // job, marker included.
        return shell.TryRun("rm -rf -- " + RemoteShell.Quote(path), out _);
    }

    private RemoteShell Shell() => _shell ??= new RemoteShell(_route!.ConnectionInfo);

    private void DeleteRemote(string path, bool isDirectory)
    {
        var file = _client!.Get(path);
        if (file.IsSymbolicLink || !file.IsDirectory)
        {
            _client!.DeleteFile(path);
            return;
        }
        foreach (var child in _client!.ListDirectory(path))
        {
            if (child.Name is "." or "..") continue;
            DeleteRemote(child.FullName, child.IsDirectory);
        }
        _client.DeleteDirectory(path);
    }

    // 전송 -----------------------------------------------------------------

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected(LocalList);
        if (targets.Count == 0) return;
        var destination = _remotePath;
        _ = RunTransferAsync(Strings.Get("Sftp.Upload"), async (token, report) =>
        {
            foreach (var entry in targets) await UploadAsync(entry.FullPath, destination, token, report);
        });
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var targets = Selected(RemoteList);
        if (targets.Count == 0) return;
        var destination = _localPath;
        if (_remoteOnly)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog();
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            destination = dialog.FolderName;
        }
        _ = RunTransferAsync(Strings.Get("Sftp.Download"), async (token, report) =>
        {
            foreach (var entry in targets) await DownloadAsync(entry.FullPath, entry.IsDirectory, destination, token, report);
        });
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => _transfer?.Cancel();

    private async Task<bool> RunTransferAsync(string label, Func<CancellationToken, Action<string, double>, Task> work)
    {
        if (_disposed || _mutating || _client is not { IsConnected: true } || _transfer is not null) return false;

        var transfer = _transfer = new CancellationTokenSource();
        SetTransferUi(running: true);
        try
        {
            await work(transfer.Token, ReportProgress);
            transfer.Token.ThrowIfCancellationRequested();
            if (_disposed) return false;
            TransferText.Text = Strings.Format("Sftp.TransferDone", label);
            StatusChanged?.Invoke(Strings.Format("Sftp.TransferDoneStatus", _profile.Name, label));
            await ShowRemoteAsync(_remotePath);
            ShowLocal(_localPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            TransferText.Text = Strings.Format("Sftp.TransferStopped", label);
            return false;
        }
        catch (Exception ex)
        {
            TransferText.Text = Strings.Format("Sftp.TransferFailed", label);
            Warn(ex.Message);
            return false;
        }
        finally
        {
            transfer.Dispose();
            _transfer = null;
            if (!_disposed) SetTransferUi(running: false);
        }
    }

    private void SetTransferUi(bool running)
    {
        if (_remoteOnly) StatusStrip.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        InlineCancelButton.Visibility = _remoteOnly && running ? Visibility.Visible : Visibility.Collapsed;
        if (_remoteOnly) TransferBar.Width = 100;
        TransferBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        TransferBar.Value = 0;
        CancelButton.IsEnabled = running;
        UploadButton.IsEnabled = DownloadButton.IsEnabled = !running;
        if (!running) return;
        TransferText.Text = Strings.Get("Sftp.Preparing");
    }

    private void ReportProgress(string name, double percent) => Dispatcher.Invoke(() =>
    {
        if (_disposed || _transfer is null) return;
        TransferBar.Value = Math.Clamp(percent, 0, 100);
        TransferText.Text = Strings.Format("Sftp.TransferProgress", name, percent.ToString("F0"));
    });

    private async Task UploadAsync(string localPath, string remoteDirectory, CancellationToken token, Action<string, double> report)
    {
        token.ThrowIfCancellationRequested();

        if ((File.GetAttributes(localPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(Strings.Format("Sftp.LinkTransfer", localPath));

        if (Directory.Exists(localPath))
        {
            var directoryName = new DirectoryInfo(localPath).Name;
            if (!TransferPaths.IsRemoteName(directoryName) || directoryName.Contains(':'))
                throw new IOException(Strings.Format("Sftp.InvalidLocalName", directoryName));
            var target = CombineRemote(remoteDirectory, directoryName);
            await Task.Run(() =>
            {
                if (!_client!.Exists(target)) _client.CreateDirectory(target);
                else if (_client.Get(target).IsSymbolicLink)
                    throw new IOException(Strings.Format("Sftp.LinkTransfer", target));
            }, token);
            foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
                await UploadAsync(child, target, token, report);
            return;
        }

        var info = new FileInfo(localPath);
        var remotePath = CombineRemote(remoteDirectory, info.Name);
        report(info.Name, 0);

        await using var stream = File.OpenRead(localPath);
        var progress = new Progress<UploadFileProgressReport>(p =>
            report(info.Name, info.Length == 0 ? 100 : p.TotalBytesUploaded * 100.0 / (ulong)info.Length));
        var client = _client!;
        var temporary = CombineRemote(remoteDirectory, $".limen-{Guid.NewGuid():N}.part");
        try
        {
            await client.UploadFileAsync(stream, temporary, false, progress, token);
            token.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                // The POSIX extension replaces atomically. If unsupported, fail
                // without deleting the original destination.
                var exists = client.Exists(remotePath);
                if (exists)
                    client.ChangePermissions(temporary, Convert.ToInt16(Octal(client.GetAttributes(remotePath)), 8));
                client.RenameFile(temporary, remotePath, exists);
            }, token);
        }
        finally
        {
            try { await Task.Run(() => { if (client.IsConnected && client.Exists(temporary)) client.DeleteFile(temporary); }); }
            catch { /* A disconnected server may retain the uniquely named partial file. */ }
        }
        report(info.Name, 100);
    }

    private async Task DownloadAsync(string remotePath, bool isDirectory, string localDirectory, CancellationToken token, Action<string, double> report)
    {
        token.ThrowIfCancellationRequested();
        var name = remotePath.TrimEnd('/').Split('/').Last();
        var localTarget = TransferPaths.LocalChild(localDirectory, name);

        if (isDirectory)
        {
            var target = localTarget;
            Directory.CreateDirectory(target);
            var children = await Task.Run(() => _client!.ListDirectory(remotePath).ToList(), token);
            foreach (var child in children)
            {
                if (child.Name is "." or "..") continue;
                if (child.IsSymbolicLink) throw new IOException(Strings.Format("Sftp.LinkTransfer", child.FullName));
                await DownloadAsync(child.FullName, child.IsDirectory, target, token, report);
            }
            return;
        }

        var length = await Task.Run(() => _client!.GetAttributes(remotePath).Size, token);
        report(name, 0);

        await AtomicDownload.WriteAsync(localDirectory, name, async (stream, cancellation) =>
        {
            var progress = new Progress<DownloadFileProgressReport>(p =>
                report(name, length == 0 ? 100 : p.TotalBytesDownloaded * 100.0 / (ulong)length));
            await _client!.DownloadFileAsync(remotePath, stream, progress, cancellation);
        }, token);
        report(name, 100);
    }

    // 보조 -----------------------------------------------------------------

    private async Task OpenRemoteFileAsync(FileEntry entry)
    {
        var safeName = Path.GetFileName(entry.Name);
        if (safeName.Length == 0) return;

        var directory = Path.Combine(Path.GetTempPath(), "Limen", "Opened", Guid.NewGuid().ToString("N"));
        var localPath = Path.Combine(directory, safeName);
        var downloaded = await RunTransferAsync(Strings.Get("Sftp.DownloadForOpen"), async (token, report) =>
        {
            Directory.CreateDirectory(directory);
            await DownloadAsync(entry.FullPath, isDirectory: false, directory, token, report);
        });
        if (downloaded && File.Exists(localPath)) OpenLocalFile(localPath);
    }

    private void OpenLocalFile(string path)
    {
        try
        {
            if (RequiresLaunchConfirmation(path))
            {
                var answer = MessageBox.Show(Window.GetWindow(this),
                    Strings.Get("Sftp.OpenExecutable") + "\n\n" + Path.GetFileName(path),
                    Strings.Get("Sftp.OpenExecutableTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusChanged?.Invoke(Strings.Format("Sftp.OpenTitle", Path.GetFileName(path)));
        }
        catch (Exception ex)
        {
            Warn(Strings.Get("Sftp.OpenFailed") + "\n\n" + ex.Message);
        }
    }

    private static bool RequiresLaunchConfirmation(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".exe" or ".com" or ".bat" or ".cmd" or ".ps1" or ".msi" or ".msix" or ".scr" or ".lnk";

    private static FileEntry? DoubleClickedEntry(ListView list, MouseButtonEventArgs e)
    {
        return EntryAt(list, e.OriginalSource as DependencyObject);
    }

    private static FileEntry? EntryAt(ListView list, DependencyObject? source) =>
        source is null ? null : (ItemsControl.ContainerFromElement(list, source) as ListViewItem)?.DataContext as FileEntry;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 탐색기가 파일 핸들을 잠시 유지하는 경우 임시 파일은 다음 OS 정리 주기에 맡긴다.
        }
    }

    private static List<FileEntry> Selected(ListView list) =>
        list.SelectedItems.Cast<FileEntry>().Where(entry => !entry.IsParent).ToList();

    private static string CombineRemote(string directory, string name) =>
        TransferPaths.CombineRemote(directory, name);

    private static string ParentRemote(string path)
    {
        var index = path.TrimEnd('/').LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private string? Ask(string title, string message)
    {
        var prompt = new PromptWindow(title, message, canRemember: false, masked: false)
        {
            Owner = Window.GetWindow(this)
        };
        if (prompt.ShowDialog() != true) return null;
        var value = prompt.Value.Trim();
        return value.Length == 0 ? null : value;
    }

    private bool ConfirmDelete(List<FileEntry> targets)
    {
        var preview = string.Join('\n', targets.Take(8).Select(entry => entry.Display));
        var extra = targets.Count > 8 ? "\n" + Strings.Format("Sftp.DeleteMore", targets.Count - 8) : string.Empty;
        // Say how a folder is removed, because it is not one file at a time.
        var recursive = targets.Any(entry => entry.IsDirectory)
            ? "\n\n" + Strings.Get("Sftp.DeleteRecursiveWarning")
            : string.Empty;
        return MessageBox.Show(Window.GetWindow(this),
            Strings.Format("Sftp.DeleteBody", targets.Count) + "\n\n" + preview + extra + recursive,
            Strings.Get("Sftp.DeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private bool ValidateName(string name, bool remote)
    {
        if (remote ? TransferPaths.IsRemoteName(name) : TransferPaths.IsLocalName(name)) return true;
        Warn(Strings.Get("Sftp.RenameBadName"));
        return false;
    }

    private static void FileList_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var list = (ListView)sender;
        var entry = EntryAt(list, e.OriginalSource as DependencyObject);
        if (entry is null) { list.SelectedItems.Clear(); return; }
        if (!list.SelectedItems.Contains(entry)) list.SelectedItem = entry;
    }

    private void Warn(string message)
    {
        if (!_disposed) MessageBox.Show(Window.GetWindow(this), message, "SFTP", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transfer?.Cancel();
        _client?.Dispose();
        _client = null;
        _shell?.Dispose();
        _shell = null;
        _route?.Dispose();
        _route = null;
    }
}
