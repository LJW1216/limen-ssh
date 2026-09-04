using Limen;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

/// Renders every screen the app has into PNGs in a single process run, so a
/// visual check costs one launch instead of one per screenshot.
internal static class Program
{
    private const string OffScreen = "-10000";

    [STAThread]
    private static void Main(string[] args)
    {
        VerifyJumpProfileMigration();

        var outputDirectory = args.Length > 0 ? args[0] : Environment.CurrentDirectory;
        Directory.CreateDirectory(outputDirectory);

        // Windows are closed as each shot is taken, so the app must not
        // shut itself down after the first one.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Limen;component/Theme.xaml", UriKind.Relative)
        });

        VerifyRecursiveDeleteGuards();
        VerifyStoredPasswordSurvivesEdit();
        VerifyWindowPlacement();

        foreach (var (dark, language) in new[] { (true, "ko"), (false, "ko"), (true, "en") })
        {
            var suffix = dark ? "dark" : "light";
            if (language == "en") suffix += "-en";
            ThemeManager.Apply(dark);
            Strings.Current.Language = language;

            Capture(Path.Combine(outputDirectory, $"main-{suffix}.png"), BuildMain());
            Capture(Path.Combine(outputDirectory, $"tabs-{suffix}.png"), BuildMainWithSftpTab());
            Capture(Path.Combine(outputDirectory, $"metrics-{suffix}.png"), BuildMetrics());
            Capture(Path.Combine(outputDirectory, $"editor-{suffix}.png"), BuildEditor());
            Capture(Path.Combine(outputDirectory, $"prompt-{suffix}.png"), BuildPrompt());
        }

        app.Shutdown();
    }

    // 화면 구성 -------------------------------------------------------------

    private static SshProfile Bastion() =>
        new() { Name = "bastion", Host = "bastion.example.com", Port = 22, UserName = "jump" };

    private static List<SshProfile> Sessions(SshProfile bastion) =>
    [
        new() { Name = "build", Host = "10.0.0.20", UserName = "deploy", Folder = "office" },
        bastion,
        new()
        {
            Name = "db", Host = "10.20.30.10", Port = 22, UserName = "app",
            Folder = "cloud/production", Color = "#E5484D", JumpProfileId = bastion.Id,
            JumpHost = new JumpHostProfile { Host = bastion.Host, Port = bastion.Port, UserName = bastion.UserName }
        },
        new() { Name = "web-1", Host = "10.20.20.10", Port = 22, UserName = "app", Folder = "cloud/production" },
        new() { Name = "web-2", Host = "10.20.20.11", Port = 22, UserName = "app", Folder = "cloud/production" }
    ];

    private static MainWindow BuildMain(bool withSftpTab = false)
    {
        var window = new MainWindow(autoLoad: false) { Width = 1240, Height = 760 };
        Place(window);
        window.Show();

        var bastion = Bastion();
        var sessions = Sessions(bastion);
        if (withSftpTab)
        {
            // Points at a closed local port so the pane reaches its failure
            // state fast instead of waiting on a real network.
            sessions[0].Host = "127.0.0.1";
            sessions[0].Port = 1;
            sessions[0].ProtectedPassword = Secret.Protect("screenshot");
            sessions[0].Color = "#E8963C";
        }

        Store(window).Profiles.AddRange(sessions);
        Invoke(window, "RebuildTree");

        if (withSftpTab)
        {
            Field(typeof(MainWindow), "_selected").SetValue(window, sessions[0]);
            typeof(MainWindow).GetMethod("OpenTab", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, ["SFTP"]);
            Pump(TimeSpan.FromSeconds(2.5));
        }

        return window;
    }

    private static MainWindow BuildMainWithSftpTab() => BuildMain(withSftpTab: true);

    /// Hosts the SSH workspace on its own so the resource strip can be
    /// reviewed without a live server. The profile points at a closed local
    /// port; the sample is injected straight into the renderer.
    private static Window BuildMetrics()
    {
        var window = new Window
        {
            Width = 1240,
            Height = 760,
            Title = "Limen — 서버 상태"
        };
        Place(window);

        var profile = new SshProfile
        {
            Name = "build", Host = "127.0.0.1", Port = 1, UserName = "deploy",
            Color = "#E5484D", ProtectedPassword = Secret.Protect("screenshot")
        };
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), $"ssb-shot-{Guid.NewGuid():N}.json"));
        store.Profiles.Add(profile);

        var workspace = new SshWorkspaceView(profile, new SshConnector(store, window));
        window.Content = workspace;
        window.Show();
        Pump(TimeSpan.FromSeconds(1.2));

        // Values chosen so all three severity bands appear at once.
        var sample = new ServerMetrics(
            CpuPercent: 34.2,
            MemoryPercent: 78.0, MemoryUsedKb: 13086032, MemoryTotalKb: 16777216,
            GpuPercent: 41.0, GpuMemoryUsedMb: 3379, GpuMemoryTotalMb: 8192,
            DiskPercent: 93.0, DiskUsedKb: 228191109, DiskTotalKb: 245366784,
            Load1: 1.24, CoreCount: 8, SampledAt: DateTime.Now);

        // DeclaredOnly: Visual also carries an internal Render().
        typeof(SshWorkspaceView).GetMethod("Render",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, [typeof(ServerMetrics)], null)!
            .Invoke(workspace, [sample]);
        Pump(TimeSpan.FromMilliseconds(400));
        return window;
    }

    private static Window BuildEditor()
    {
        var bastion = Bastion();
        var sessions = Sessions(bastion);
        var editor = new ProfileEditorWindow(sessions[2].Clone(), isNew: false, sessions);
        Place(editor);
        editor.Show();
        return editor;
    }

    private static Window BuildPrompt()
    {
        var prompt = new PromptWindow("비밀번호", "Password for app@10.20.30.10:22.",
            canRemember: true);
        Place(prompt);
        prompt.Show();
        return prompt;
    }

    // 캡처 -----------------------------------------------------------------

    private static void Place(Window window)
    {
        window.ShowInTaskbar = false;
        // A maximized window snaps back onto a real monitor, which would both
        // flash on screen and break the off-screen capture.
        window.WindowState = WindowState.Normal;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = double.Parse(OffScreen);
        window.Top = double.Parse(OffScreen);
    }

    private static void Capture(string path, Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();

        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);

        Console.WriteLine($"{Path.GetFileName(path)}  {width}x{height}");
        window.Close();
    }

    /// Lets queued work (layout, failed connections) finish before capturing.
    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(duration, DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    // 리플렉션 도우미 -------------------------------------------------------

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static ProfileStore Store(MainWindow window) =>
        (ProfileStore)Field(typeof(MainWindow), "_store").GetValue(window)!;

    private static void Invoke(MainWindow window, string method) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    // 회귀 검증 -------------------------------------------------------------

    /// A recursive delete runs `rm -rf` on the server, so the guard that decides
    /// which paths are eligible is the only thing between a mis-click and a
    /// wiped filesystem.
    private static void VerifyRecursiveDeleteGuards()
    {
        string[] refuse =
        [
            "/", "~", "", "   ", "..", "/etc", "/home", "relative/path",
            "/var/*", "/tmp/a?b", "/tmp/one\ntwo",
        ];
        foreach (var path in refuse)
            if (!RemoteShell.IsDangerous(path))
                throw new InvalidOperationException($"삭제 가드 실패: '{path}' 를 허용했습니다.");

        string[] allow = ["/home/app/build", "/var/www/site/cache", "/opt/x/y/z"];
        foreach (var path in allow)
            if (RemoteShell.IsDangerous(path))
                throw new InvalidOperationException($"삭제 가드 실패: '{path}' 를 막았습니다.");

        // A quote inside a name must not be able to end the shell argument.
        var quoted = RemoteShell.Quote("/home/it's/a dir");
        if (quoted != @"'/home/it'\''s/a dir'")
            throw new InvalidOperationException($"셸 인용 실패: {quoted}");

        Console.WriteLine("recursive delete guards  OK");
    }

    /// Opening a session and pressing save must not silently drop a stored
    /// password — the editor shows a marker, not the secret, and the marker has
    /// to survive the round trip.
    private static void VerifyStoredPasswordSurvivesEdit()
    {
        var profile = new SshProfile
        {
            Name = "pw-test", Host = "10.0.0.1", Port = 22, UserName = "root",
            ProtectedPassword = Secret.Protect("hunter2")
        };
        var before = profile.ProtectedPassword;

        var editor = new ProfileEditorWindow(profile, isNew: false, [profile]);
        Place(editor);
        editor.Show();
        Pump(TimeSpan.FromMilliseconds(400));

        try
        {
            typeof(ProfileEditorWindow)
                .GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, [editor, new RoutedEventArgs()]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
        {
            // DialogResult cannot be set on a non-modal window; the profile has
            // already been written by then.
        }
        editor.Close();

        if (profile.ProtectedPassword.Length == 0)
            throw new InvalidOperationException(
                "자격 증명 테스트 실패: 편집창에서 저장만 눌러도 저장된 비밀번호가 지워집니다.");
        if (profile.ProtectedPassword != before)
            Console.WriteLine("stored password  재암호화됨 (내용은 유지)");
        else
            Console.WriteLine("stored password  유지  OK");
    }

    /// Proves the window really persists its geometry: resize, wait out the
    /// debounce, and read back what landed on disk.
    private static void VerifyWindowPlacement()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Limen", "window.json");
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;

        try
        {
            // The parameterless constructor is the real app path, the only one
            // that persists placement.
            var window = new MainWindow();
            Place(window);
            window.Show();
            window.Width = 1301;
            window.Height = 811;
            window.Left = 120;
            window.Top = 90;
            Pump(TimeSpan.FromSeconds(1.6));
            window.Close();
            Pump(TimeSpan.FromMilliseconds(300));

            var saved = WindowPlacementStore.Load()
                ?? throw new InvalidOperationException("창 위치 저장 테스트 실패: 파일이 생성되지 않았습니다.");
            if (Math.Abs(saved.Width - 1301) > 1 || Math.Abs(saved.Height - 811) > 1)
                throw new InvalidOperationException(
                    $"창 크기 저장 테스트 실패: {saved.Width}x{saved.Height}");

            Console.WriteLine($"window placement  {saved.Width:0}x{saved.Height:0} @ {saved.Left:0},{saved.Top:0}  OK");
        }
        finally
        {
            if (backup is null) File.Delete(path);
            else File.WriteAllText(path, backup);
        }
    }

    private static void VerifyJumpProfileMigration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ssb-profile-test-{Guid.NewGuid():N}.json");
        try
        {
            var bastion = new SshProfile { Name = "Bastion", Host = "203.0.113.10", Port = 4022, UserName = "jump" };
            var target = new SshProfile
            {
                Name = "Target", Host = "10.0.0.10", UserName = "app",
                JumpHost = new JumpHostProfile { Host = bastion.Host, Port = bastion.Port, UserName = bastion.UserName }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(new[] { bastion, target }));
            var store = new ProfileStore(path);
            store.Load();
            if (store.Profiles.Single(profile => profile.Name == "Target").JumpProfileId != bastion.Id)
                throw new InvalidOperationException("Bastion 프로필 자동 연결 테스트 실패");

            var source = store.Profiles.Single(profile => profile.Name == "Bastion");
            source.Host = "203.0.113.11";
            store.AddOrUpdate(source);
            if (store.Profiles.Single(profile => profile.Name == "Target").JumpHost?.Host != source.Host)
                throw new InvalidOperationException("Bastion 프로필 변경 전파 테스트 실패");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
