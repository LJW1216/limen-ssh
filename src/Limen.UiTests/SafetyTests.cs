using Limen;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

/// Regressions found in the whole-program review: each check fails on the code
/// as it was before the matching fix.
internal static class SafetyTests
{
    private static int _checks;

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        _checks++;
    }

    internal static int Run()
    {
        ThemeManager.Apply(true);
        StringKeys();
        HostKeyFollowsEndpoint();
        CrashGuardKeepsRunning();
        Paste();
        LogFaultKeepsSession();
        DeleteRunsUnderSh();
        return _checks;
    }

    // Every key the code or markup asks for must exist in both tables. A key
    // that is missing shows up on screen as its own name — "Editor.Password" on
    // the session editor's radio button.
    private static void StringKeys()
    {
        var korean = Table("Korean");
        var english = Table("English");
        var reference = new Regex(@"Strings\.(?:Get|Format)\(""([A-Za-z0-9_.]+)""|\{loc:T ([A-Za-z0-9_.]+)\}");
        var root = SourceRoot();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".cs") && !file.EndsWith(".xaml")) continue;
            var relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("bin") || relative.StartsWith("obj")) continue;
            if (Path.GetFileName(file) is "Strings.cs" or "Strings.Tables.cs") continue;
            foreach (Match match in reference.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                Check(korean.ContainsKey(key) && english.ContainsKey(key), $"string key {key} missing ({relative})");
                scanned++;
            }
        }
        Check(scanned > 200, "string reference scan found the source");
        foreach (var key in new[] { "Editor.Password", "Editor.Passphrase", "Sftp.ConnectingTo", "Sftp.ConnectFailed" })
            Check(Strings.Get(key) != key, $"{key} resolves to text");
    }

    private static Dictionary<string, string> Table(string name) =>
        (Dictionary<string, string>)typeof(Strings).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            foreach (var candidate in new[] { Path.Combine(directory.FullName, "Limen"), Path.Combine(directory.FullName, "src", "Limen") })
                if (File.Exists(Path.Combine(candidate, "Limen.csproj"))) return candidate;
        throw new DirectoryNotFoundException("Limen source tree not found");
    }

    // A pinned host key belongs to one endpoint. Duplicating a session and
    // pointing it elsewhere must not greet the new host with a MITM warning.
    private static void HostKeyFollowsEndpoint()
    {
        SshProfile Edited(string host, int port, Action<ProfileEditorWindow> change)
        {
            var profile = new SshProfile { Name = "web", Host = host, Port = port, UserName = "app", HostKeyFingerprint = "SHA256:old" };
            var editor = new ProfileEditorWindow(profile, isNew: false, [profile]);
            change(editor);
            try
            {
                typeof(ProfileEditorWindow).GetMethod("Save_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(editor, [editor, new RoutedEventArgs()]);
            }
            // DialogResult needs a modal window; the profile is written before it.
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            editor.Close();
            return profile;
        }

        Check(Edited("web-1.example.com", 22, editor => editor.HostBox.Text = "web-2.example.com").HostKeyFingerprint == "",
            "new host drops the pinned key");
        Check(Edited("web-1.example.com", 22, editor => editor.PortBox.Text = "2222").HostKeyFingerprint == "",
            "new port drops the pinned key");
        Check(Edited("web-1.example.com", 22, editor => editor.HostBox.Text = "WEB-1.example.com").HostKeyFingerprint == "SHA256:old",
            "same host in another case keeps the key");
        Check(Edited("web-1.example.com", 22, editor => editor.NameBox.Text = "renamed").HostKeyFingerprint == "SHA256:old",
            "unrelated edit keeps the key");
    }

    // One unhandled exception on the UI thread used to close the window and
    // every SSH session in it.
    private static void CrashGuardKeepsRunning()
    {
        Exception? reported = null;
        var after = false;
        using (CrashGuard.Install(Application.Current, ex => reported = ex))
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("boom")));
            dispatcher.BeginInvoke(new Action(() => after = true));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!after && DateTime.UtcNow < deadline)
            {
                var frame = new DispatcherFrame();
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
        }
        Check(reported?.Message == "boom", "unhandled UI exception is reported");
        Check(after, "dispatcher keeps running after the fault");
    }

    private static void Paste()
    {
        Check(ConfirmPasteWindow.NeedsConfirmation("rm -rf ./build\n"), "trailing newline is confirmed");
        Check(ConfirmPasteWindow.NeedsConfirmation("rm -rf ./build\r\n"), "trailing CRLF is confirmed");
        Check(ConfirmPasteWindow.NeedsConfirmation("one\rtwo"), "bare CR is confirmed");
        Check(ConfirmPasteWindow.NeedsConfirmation("one\ntwo"), "multi-line is confirmed");
        Check(!ConfirmPasteWindow.NeedsConfirmation("ls -la"), "single line pastes straight through");

        Check(TerminalView.NormalizePaste("a\r\nb\nc\rd") == "a\rb\rc\rd", "CRLF and LF reach the shell as CR");
        Check(TerminalView.NormalizePaste("x\r\n\r\ny") == "x\r\ry", "blank lines are kept, not doubled");

        Check(new ConfirmPasteWindow("rm -rf ./build\n").HeadingText.Text == Strings.Get("Paste.TrailingNewline"),
            "single line with newline explains why it asks");
        Check(new ConfirmPasteWindow("a\nb\nc").HeadingText.Text == Strings.Format("Paste.LineCount", 3),
            "multi-line heading counts lines");
    }

    // A log file that stops accepting writes used to end the SSH session: the
    // IOException escaped into the read loop, which took it for a dead shell.
    private static void LogFaultKeepsSession()
    {
        var writer = new BreakingWriter();
        var faults = new List<Exception>();
        var log = new SessionLog(writer, "memory.log", "header");
        log.Faulted += faults.Add;

        var before = Encoding.UTF8.GetBytes("before\n");
        log.Append(before, before.Length);
        Check(writer.Text.ToString().Contains("before"), "log records output");

        writer.Broken = true;
        var during = Encoding.UTF8.GetBytes("during\n");
        log.Append(during, during.Length);
        log.Append(during, during.Length);
        Check(faults.Count == 1 && faults[0] is IOException, "write failure is reported once, not thrown");
        log.Dispose();
        Check(!writer.Text.ToString().Contains("during"), "nothing written after the fault");
    }

    private sealed class BreakingWriter : TextWriter
    {
        public readonly StringBuilder Text = new();
        public bool Broken;
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (Broken) throw new IOException("There is not enough space on the disk.");
            Text.Append(value);
        }
    }

    // The server runs an exec request through the account's login shell. csh
    // and fish cannot parse the script, and by then the folder is staged.
    private static void DeleteRunsUnderSh()
    {
        var command = ServerDelete.BuildCommand("/srv/.limen-delete-x", ".limen-proof-y", "nonce");
        Check(command.StartsWith("/bin/sh -c '") && command.EndsWith("'"), "delete script runs under /bin/sh");
        Check(!Regex.IsMatch(command, @"!\S"), "no csh history expansion inside the quoted script");
        Check(!ServerDelete.BuildScript("/srv/.limen-delete-x", ".limen-proof-y", "nonce").StartsWith("/bin/sh"),
            "script itself stays plain POSIX");
    }
}
