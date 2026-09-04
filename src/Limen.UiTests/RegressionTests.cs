using Limen;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;

internal static class RegressionTests
{
    private static int _checks;
    public static void Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Limen;component/Theme.xaml", UriKind.Relative)
        });
        Paths();
        Wait(Downloads());
        Profiles();
        ServerDelete();
        Navigation();
        Console.WriteLine($"PASS: {_checks} regression assertions (no real server or user settings modified)");
        app.Shutdown();
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        _checks++;
    }

    private static void Paths()
    {
        foreach (var (input, expected) in new[]
        {
            ("", "/work"), ("docs", "/work/docs"), ("~", "/home/user"),
            ("~/docs", "/home/user/docs"), ("../docs", "/work/../docs"),
            ("/a/link/../b", "/a/link/../b"), ("/a\\b", "/a\\b"),
            ("/ space /", "/ space /"), ("/한글/%20", "/한글/%20"), ("/", "/")
        }) Check(TransferPaths.ResolveRemote(input, "/work", "/home/user") == expected, $"resolve {input}");
        foreach (var name in new[] { "..", ".", "a/b", "a\\b", "C:escape", "CON", "nul.txt", "COM1.log", "LPT¹", "bad.", "bad ", "a\0b" })
            Check(!TransferPaths.IsLocalName(name), $"reject {name}");
        foreach (var name in new[] { "한글.txt", "a b", "100%20.txt", "COM10", "normal.txt" })
            Check(TransferPaths.IsLocalName(name), $"allow {name}");
        Check(TransferPaths.IsRemoteName("a\\b"), "POSIX backslash is literal");
        Check(TransferPaths.IsRemoteName(" name "), "POSIX whitespace is literal");
        Check(Path.IsPathFullyQualified(new ProfileStore("relative-profiles.json").FilePath), "relative store path");
    }

    private static async Task Downloads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "limen-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = Path.Combine(directory, "keep.txt");
            await File.WriteAllTextAsync(target, "original");
            using var cancellation = new CancellationTokenSource();
            try
            {
                await AtomicDownload.WriteAsync(directory, "keep.txt", async (stream, token) =>
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("partial"), token);
                    cancellation.Cancel();
                }, cancellation.Token);
                throw new Exception("cancellation ignored");
            }
            catch (OperationCanceledException) { }
            Check(await File.ReadAllTextAsync(target) == "original", "cancellation preserves original");
            Check(Directory.GetFiles(directory).Length == 1, "cancel cleans partial");
            try
            {
                await AtomicDownload.WriteAsync(directory, "keep.txt", (_, _) => throw new IOException("lost connection"), default);
            }
            catch (IOException) { }
            Check(await File.ReadAllTextAsync(target) == "original", "failure preserves original");
            Check(Directory.GetFiles(directory).Length == 1, "failure cleans partial");
            await AtomicDownload.WriteAsync(directory, "keep.txt", (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes("complete"), token).AsTask(), default);
            Check(await File.ReadAllTextAsync(target) == "complete", "success replaces original");
            await AtomicDownload.WriteAsync(directory, "empty.txt", (_, _) => Task.CompletedTask, default);
            Check(new FileInfo(Path.Combine(directory, "empty.txt")).Length == 0, "empty download");
            using (var log = new SessionLog(Path.Combine(directory, "terminal.log"), "header"))
            {
                var bytes = Encoding.UTF8.GetBytes("한글\u001b[31mRED\u001b[0m\n");
                foreach (var b in bytes) log.Append([b], 1);
            }
            Check(File.ReadAllText(Path.Combine(directory, "terminal.log")).Contains("한글RED"), "log UTF8 and escapes split across reads");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Navigation()
    {
        ThemeManager.Apply(true);
        var owner = new Window();
        var client = DispatchProxy.Create<ISftpClient, FakeSftp>();
        var fake = (FakeSftp)(object)client;
        var view = new SftpView(new SshProfile(), new SshConnector(new ProfileStore("unused.json"), owner), remoteOnly: true);
        Set(view, "_client", client);
        ((FrameworkElement)Get(view, "RemoteOverlay")!).Visibility = Visibility.Collapsed;
        var slow = Invoke<Task>(view, "ShowRemoteAsync", "/slow", false);
        Check(fake.Started.Wait(TimeSpan.FromSeconds(5)), "slow request started");
        var latest = Invoke<Task>(view, "ShowRemoteAsync", "/latest", false);
        fake.Release.Set();
        Wait(Task.WhenAll(slow, latest));
        Check((string)Get(view, "_remotePath")! == "/latest", "latest navigation wins");
        Wait(Invoke<Task>(view, "ShowRemoteAsync", "relative", false));
        Check((string)Get(view, "_remotePath")! == "/latest/relative", "relative navigation uses displayed directory");
        Wait(view.NavigateRemoteAsync("/missing"));
        Wait(view.NavigateRemoteAsync("/missing"));
        Check(fake.MissingRequests == 1, "duplicate shell reports suppressed without modal");
        Check((string)Get(view, "_remotePath")! == "/latest/relative", "failed navigation retains path");
        Wait(Invoke<Task<bool>>(view, "RunTransferAsync", "test",
            new Func<CancellationToken, Action<string, double>, Task>((_, _) => Task.CompletedTask)));
        Check((string)Get(view, "_localPath")! == "", "remote-only transfer skips empty local refresh");
        var uploadFile = Path.Combine(Path.GetTempPath(), "limen-upload-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(uploadFile, "replacement");
            var remoteTarget = "/upload/" + Path.GetFileName(uploadFile);
            fake.Files[remoteTarget] = Encoding.UTF8.GetBytes("original");
            fake.CancelUpload = true;
            try { Wait(Invoke<Task>(view, "UploadAsync", uploadFile, "/upload", CancellationToken.None, new Action<string, double>((_, _) => { }))); }
            catch (OperationCanceledException) { }
            Check(Encoding.UTF8.GetString(fake.Files[remoteTarget]) == "original", "upload cancellation preserves original");
            Check(fake.Files.Count == 1, "upload cancellation removes partial");
            fake.CancelUpload = false;
            fake.RefuseRename = true;
            try { Wait(Invoke<Task>(view, "UploadAsync", uploadFile, "/upload", CancellationToken.None, new Action<string, double>((_, _) => { }))); }
            catch (NotSupportedException) { }
            Check(Encoding.UTF8.GetString(fake.Files[remoteTarget]) == "original", "unsupported atomic rename preserves original");
            Check(fake.Files.Count == 1, "failed rename removes partial");
            fake.RefuseRename = false;
            Wait(Invoke<Task>(view, "UploadAsync", uploadFile, "/upload", CancellationToken.None, new Action<string, double>((_, _) => { })));
            Check(Encoding.UTF8.GetString(fake.Files[remoteTarget]) == "replacement", "successful upload replaces original");
        }
        finally { File.Delete(uploadFile); }

        var started = new TaskCompletionSource();
        var transfer = Invoke<Task<bool>>(view, "RunTransferAsync", "close",
            new Func<CancellationToken, Action<string, double>, Task>(async (token, _) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }));
        Wait(started.Task);
        Check(((FrameworkElement)Get(view, "StatusStrip")!).Visibility == Visibility.Visible, "embedded transfer progress visible");
        Check(((FrameworkElement)Get(view, "InlineCancelButton")!).Visibility == Visibility.Visible, "embedded transfer cancel visible");
        view.Measure(new Size(680, 460));
        view.Arrange(new Rect(0, 0, 680, 460));
        view.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(680, 460, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("outputs");
        using (var image = File.Create("outputs/audit-sftp.png")) encoder.Save(image);
        view.Dispose();
        Wait(transfer);
        Check(!transfer.Result, "closing cancels transfer without a dialog");
        Check(Get(view, "_transfer") is null, "closed transfer releases cancellation source");
    }

    // The fast delete hands a path to `rm -rf`. A chrooted SFTP subsystem shows
    // paths that mean something else to the shell, so the fast path must prove
    // the two agree and fall back to the protocol walk whenever it cannot.
    private static void ServerDelete()
    {
        var client = DispatchProxy.Create<ISftpClient, FakeSftp>();
        var fake = (FakeSftp)(object)client;

        var chroot = new FakeShell(sees: false);
        Check(!SftpView.TryDeleteOnServer("/srv/app/cache", client, chroot), "chroot mismatch refuses rm");
        Check(chroot.Commands.All(command => !command.StartsWith("rm")), "chroot mismatch never reaches rm");
        Check(fake.Files.Count == 0, "refused probe cleans its marker");

        var same = new FakeShell(sees: true);
        Check(SftpView.TryDeleteOnServer("/srv/app/cache", client, same), "matching namespace deletes on the server");
        Check(same.Commands[^1] == "rm -rf -- '/srv/app/cache'", "rm targets the confirmed path");
        Check(same.Commands[0].StartsWith("test -f '/srv/app/cache/.limen-rm-"), "marker probed where it was written");

        var quoting = new FakeShell(sees: true);
        Check(SftpView.TryDeleteOnServer("/srv/it's/a dir", client, quoting), "awkward names are quoted, not rejected");
        Check(quoting.Commands[^1] == "rm -rf -- '/srv/it'" + (char)92 + "''s/a dir'", "single quotes are escaped");

        fake.RefuseWrite = true;
        var unwritable = new FakeShell(sees: true);
        Check(!SftpView.TryDeleteOnServer("/srv/app/cache", client, unwritable), "unprovable directory refuses rm");
        Check(unwritable.Commands.Count == 0, "no marker means no shell command");
        fake.RefuseWrite = false;

        fake.Symlinks.Add("/srv/app/link");
        Check(!SftpView.TryDeleteOnServer("/srv/app/link", client, new FakeShell(sees: true)), "symlink is not walked by rm");

        foreach (var (path, why) in new[]
        {
            ("/", "root"), ("~", "home shorthand"), ("/etc", "top-level directory"),
            ("relative/path", "relative path"), ("/srv/*", "glob"),
            ("/srv/a" + (char)10 + "b", "embedded newline"), ("", "empty path")
        }) Check(!SftpView.TryDeleteOnServer(path, client, new FakeShell(sees: true)), $"refuses {why}");
        foreach (var path in new[] { "/srv/app", "/home/user/node_modules", "/var/tmp/build" })
            Check(SftpView.TryDeleteOnServer(path, client, new FakeShell(sees: true)), $"accepts {path}");
    }

    private static void Profiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "limen-profiles-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ProfileStore(path);
            var original = new SshProfile { Name = "old", Host = "example", UserName = "user" };
            store.AddOrUpdate(original);
            var edited = original.Clone();
            edited.Name = "edited";
            edited.RemoteDirectory = "/new";
            edited.ProtectedPassword = "new secret";
            store.AddOrUpdate(edited);
            original.HostKeyFingerprint = "SHA256:test";
            store.UpdateConnectionState(original);
            store.Load();
            Check(store.Profiles[0].Name == "edited" && store.Profiles[0].RemoteDirectory == "/new", "connection does not revert edits");
            Check(store.Profiles[0].ProtectedPassword == "new secret", "pin does not replace edited credentials");
            Check(store.Profiles[0].HostKeyFingerprint == "SHA256:test", "pin persists");
            store.Remove(store.Profiles[0]);
            store.UpdateConnectionState(original, includeCredentials: true);
            store.Load();
            Check(store.Profiles.Count == 0, "connection does not resurrect deleted profile");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Regression test timed out");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }

    private static object? Get(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static T Invoke<T>(object instance, string name, params object[] args) =>
        (T)instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args)!;
}

internal sealed class FakeShell(bool sees) : IRemoteShell
{
    public List<string> Commands { get; } = [];

    public bool TryRun(string command, out string error)
    {
        error = string.Empty;
        Commands.Add(command);
        return !command.StartsWith("test -f") || sees;
    }
}

public class FakeSftp : DispatchProxy
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public HashSet<string> Symlinks { get; } = new();
    public bool CancelUpload;
    public bool RefuseRename;
    public bool RefuseWrite;
    public ManualResetEventSlim Started { get; } = new();
    public ManualResetEventSlim Release { get; } = new();
    public int MissingRequests;
    private string _directory = "/";
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_IsConnected": return true;
            case "get_WorkingDirectory": return _directory;
            case "ChangeDirectory":
                var path = (string)args![0]!;
                if (path == "/slow") { Started.Set(); if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
                if (path == "/missing") { MissingRequests++; throw new DirectoryNotFoundException(path); }
                _directory = path;
                return null;
            case "ListDirectory": return new List<ISftpFile>();
            case "Exists": return Files.ContainsKey((string)args![0]!);
            case "GetAttributes": return (SftpFileAttributes)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SftpFileAttributes));
            case "ChangePermissions": return null;
            case "UploadFileAsync": return Upload((Stream)args![0]!, (string)args[1]!);
            case "RenameFile":
                if (RefuseRename) throw new NotSupportedException("posix-rename");
                Files[(string)args![1]!] = Files[(string)args[0]!];
                Files.Remove((string)args[0]!);
                return null;
            case "DeleteFile": Files.Remove((string)args![0]!); return null;
            case "WriteAllBytes":
                if (RefuseWrite) throw new UnauthorizedAccessException("permission denied");
                Files[(string)args![0]!] = (byte[])args[1]!;
                return null;
            case "Get":
                var file = DispatchProxy.Create<ISftpFile, FakeSftpFile>();
                ((FakeSftpFile)(object)file).IsLink = Symlinks.Contains((string)args![0]!);
                return file;
            case "Dispose": return null;
            default: throw new NotSupportedException(method.Name);
        }
    }

    private async Task Upload(Stream stream, string path)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        Files[path] = buffer.ToArray();
        if (CancelUpload) throw new OperationCanceledException();
    }
}

public class FakeSftpFile : DispatchProxy
{
    public bool IsLink;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_IsSymbolicLink" => IsLink,
        "get_IsDirectory" => !IsLink,
        _ => throw new NotSupportedException(method.Name)
    };
}
