using Limen;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

internal static class ServerDeleteTests
{
    private static int _checks;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        _checks++;
    }

    internal static int Run()
    {
        Check(RemoteShell.MatchesHostKey("SHA256:approved", "approved"), "approved key accepted");
        Check(!RemoteShell.MatchesHostKey("SHA256:approved", "other"), "changed key refused");
        Check(!RemoteShell.MatchesHostKey("", "other"), "missing pin refused");
        foreach (var path in new[] { "/", "/etc", "", "relative/a", "/srv/*", "/srv/a\nb", "/srv/../etc", "/srv/./app", "/srv/a\0b" })
            Check(RemoteShell.IsDangerous(path), "reject dangerous " + path);

        foreach (var result in new[] { new ShellResult(ShellState.NotStarted), new ShellResult(ShellState.Exited, 40) })
        {
            var (client, fake) = Fixture();
            string? fallback = null;
            var outcome = ServerDelete.TryDelete("/srv/cache", client, new TestShell(_ => result), path =>
            {
                fallback = path;
                fake.RemoveTree(path);
            });
            Check(outcome == ServerDeleteResult.DeletedViaSftp, "safe refusal falls back");
            Check(fallback is not null && fallback != "/srv/cache" && fallback.EndsWith("/payload"), "fallback only targets staged directory");
            Check(fake.Files.Count == 0 && fake.Directories.SetEquals(["/srv"]), "fallback cleans stage");
        }

        foreach (var result in new[] { new ShellResult(ShellState.Unknown, Error: "connection lost"), new ShellResult(ShellState.Exited, 41) })
        {
            var (client, fake) = Fixture();
            var fallback = false;
            try
            {
                ServerDelete.TryDelete("/srv/cache", client, new TestShell(_ => result), _ => fallback = true);
                throw new Exception("uncertain delete was accepted");
            }
            catch (IOException ex) { Check(ex.Message.Contains(".limen-delete-"), "error includes recovery path"); }
            Check(!fallback, "unknown/partial command never starts second deletion");
            Check(fake.Files.Keys.Any(path => path.EndsWith("/keep")), "uncertain deletion retains staged files");
        }

        {
            var (client, fake) = Fixture();
            var shell = new TestShell(command =>
            {
                // A deployment recreates the original name while deletion runs.
                fake.Directories.Add("/srv/cache");
                fake.Files["/srv/cache/new"] = "replacement";
                var payload = fake.Directories.Single(path => path.EndsWith("/payload"));
                fake.Files.Remove(payload + "/keep");
                Check(!command.Contains("'/srv/cache'"), "shell never recursively deletes original name");
                return new(ShellState.Exited, 0);
            });
            Check(ServerDelete.TryDelete("/srv/cache", client, shell, _ => throw new Exception("unexpected fallback")) == ServerDeleteResult.DeletedFast, "fast deletion completes");
            Check(fake.Files["/srv/cache/new"] == "replacement", "replacement at original name survives");
            Check(!fake.Directories.Any(path => path.Contains(".limen-delete-")), "successful stage removed");
        }

        {
            var (client, fake) = Fixture();
            fake.LoseRenameReply = true;
            var shell = new TestShell(_ => throw new Exception("must not dispatch"));
            try { ServerDelete.TryDelete("/srv/cache", client, shell, _ => throw new Exception("must not fall back")); }
            catch (IOException) { }
            Check(fake.Directories.Any(path => path.EndsWith("/payload")), "lost rename reply retains recoverable target");
            Check(shell.Calls == 0, "lost rename reply does not execute shell");
        }
        {
            var (client, fake) = Fixture();
            fake.ReplaceBeforeRename = true;
            var shell = new TestShell(_ => throw new Exception("must not dispatch"));
            try { ServerDelete.TryDelete("/srv/cache", client, shell, _ => throw new Exception("must not fall back")); }
            catch (IOException) { }
            Check(shell.Calls == 0, "changed target fails moved marker check");
            Check(fake.Files.Values.Contains("replacement"), "changed target retained for inspection");
        }
        {
            var (client, fake) = Fixture();
            var shell = new TestShell(_ =>
            {
                fake.Files.Remove(fake.Files.Keys.Single(path => path.Contains(".limen-proof-")));
                return new(ShellState.Exited, 0);
            });
            try { ServerDelete.TryDelete("/srv/cache", client, shell, _ => throw new Exception("unexpected fallback")); }
            catch (IOException) { }
            Check(fake.Files.Values.Contains("old"), "false shell success does not recurse through SFTP");
        }
        {
            var (client, fake) = Fixture();
            fake.RefuseUpload = true;
            var shell = new TestShell(_ => throw new Exception("must not dispatch"));
            Check(ServerDelete.TryDelete("/srv/cache", client, shell, _ => { }) == ServerDeleteResult.NotApplicable, "unwritable marker retains normal fallback");
            Check(fake.Directories.Contains("/srv/cache") && shell.Calls == 0, "unwritable folder not staged");
        }
        {
            var (client, fake) = Fixture();
            fake.Links.Add("/srv/cache");
            Check(ServerDelete.TryDelete("/srv/cache", client, new TestShell(_ => throw new Exception()), _ => { }) == ServerDeleteResult.NotApplicable, "SFTP symlink bypasses optimization");
        }

        ShellIntegration();
        return _checks;
    }

    private static (ISftpClient, DeleteSftp) Fixture()
    {
        var client = DispatchProxy.Create<ISftpClient, DeleteSftp>();
        var fake = (DeleteSftp)(object)client;
        fake.Directories.UnionWith(["/srv", "/srv/cache"]);
        fake.Files["/srv/cache/keep"] = "old";
        return (client, fake);
    }

    private static void ShellIntegration()
    {
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (!File.Exists(bash)) { Console.WriteLine("SKIP shell integration: Git Bash is not installed"); return; }
        var root = Path.GetFullPath(Path.Combine("outputs", "delete-shell-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(Path.GetFullPath("outputs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test directory escaped outputs");
        Directory.CreateDirectory(root);
        try
        {
            var stage = Path.Combine(root, "stage it's 한글");
            var payload = Path.Combine(stage, "payload");
            Directory.CreateDirectory(Path.Combine(payload, "nested"));
            File.WriteAllText(Path.Combine(payload, "nested", "keep"), "old");
            foreach (var name in new[] { "hidden", ".dot", "-option", "a b", "it's" })
                File.WriteAllText(Path.Combine(payload, name), "old");
            File.WriteAllText(Path.Combine(payload, ".proof"), "nonce");
            var original = Path.Combine(root, "original");
            Directory.CreateDirectory(original);
            File.WriteAllText(Path.Combine(original, "new"), "replacement");
            var command = ServerDelete.BuildCommand(Posix(stage), ".proof", "nonce");

            Check(RunBash(bash, ServerDelete.BuildCommand(Posix(stage), ".proof", "wrong")) == 40, "real shell rejects wrong nonce");
            Check(File.Exists(Path.Combine(payload, "nested", "keep")), "wrong nonce never reaches rm");
            Check(RunBash(bash, ServerDelete.BuildCommand(Posix(root + "-chroot"), ".proof", "nonce")) == 40, "real shell rejects namespace mismatch");
            Check(RunBash(bash, command) == 0, "real find/rm command succeeds");
            Check(Directory.GetFileSystemEntries(payload).Select(Path.GetFileName).SequenceEqual([".proof"]), "real rm removes nested and special names, keeps proof");
            Check(File.ReadAllText(Path.Combine(original, "new")) == "replacement", "real shell leaves replacement intact");

            // Exercise a payload symlink using native symlinks when available.
            var linkStage = Path.Combine(root, "link-stage");
            Directory.CreateDirectory(linkStage);
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(linkStage, "payload"), payload);
                Check(RunBash(bash, ServerDelete.BuildCommand(Posix(linkStage), ".proof", "nonce")) == 40, "real shell refuses payload symlink");
                Directory.Delete(Path.Combine(linkStage, "payload"));
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("SKIP native symlink integration: privilege unavailable"); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 1314)
            { Console.WriteLine("SKIP native symlink integration: privilege unavailable"); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Posix(string path) => "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/');
    private static int RunBash(string bash, string command)
    {
        var start = new ProcessStartInfo(bash) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("--noprofile");
        start.ArgumentList.Add("--norc");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("PATH=/usr/bin:/bin; " + command);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("shell test"); }
        var error = process.StandardError.ReadToEnd();
        if (error.Length > 0) Console.WriteLine(error);
        return process.ExitCode;
    }

    private sealed class TestShell(Func<string, ShellResult> execute) : IRemoteShell
    {
        public int Calls;
        public ShellResult Run(string command) { Calls++; return execute(command); }
    }
}

public class DeleteSftp : DispatchProxy
{
    public HashSet<string> Directories { get; } = [];
    public HashSet<string> Links { get; } = [];
    public Dictionary<string, string> Files { get; } = [];
    public bool RefuseUpload, LoseRenameReply, ReplaceBeforeRename;
    public void RemoveTree(string path)
    {
        foreach (var file in Files.Keys.Where(key => key.StartsWith(path + "/")).ToList()) Files.Remove(file);
        Directories.RemoveWhere(key => key == path || key.StartsWith(path + "/"));
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var path = args?.FirstOrDefault() as string ?? "";
        switch (method!.Name)
        {
            case "Get":
                if (!Directories.Contains(path) && !Files.ContainsKey(path)) throw new IOException("missing " + path);
                var file = DispatchProxy.Create<ISftpFile, DeleteFile>();
                var proxy = (DeleteFile)(object)file;
                proxy.Directory = Directories.Contains(path);
                proxy.Link = Links.Contains(path);
                return file;
            case "UploadFile":
                if (RefuseUpload) throw new UnauthorizedAccessException();
                using (var reader = new StreamReader((Stream)args![0]!, leaveOpen: true)) Files.Add((string)args[1]!, reader.ReadToEnd());
                return null;
            case "CreateDirectory": if (!Directories.Add(path)) throw new IOException("exists"); return null;
            case "ChangePermissions": return null;
            case "RenameFile":
                var target = (string)args![1]!;
                if (ReplaceBeforeRename)
                {
                    RemoveTree(path);
                    Directories.Add(path);
                    Files[path + "/new"] = "replacement";
                }
                foreach (var item in Files.Keys.Where(key => key.StartsWith(path + "/")).ToList())
                { Files[target + item[path.Length..]] = Files[item]; Files.Remove(item); }
                Directories.Remove(path); Directories.Add(target);
                if (LoseRenameReply) throw new IOException("rename reply lost");
                return null;
            case "ReadAllText": return Files[path];
            case "DeleteFile": Files.Remove(path); return null;
            case "DeleteDirectory":
                if (Files.Keys.Any(key => key.StartsWith(path + "/")) || Directories.Any(key => key.StartsWith(path + "/"))) throw new IOException("not empty");
                if (!Directories.Remove(path)) throw new IOException("missing directory");
                return null;
            default: throw new NotSupportedException(method.Name);
        }
    }
}

public class DeleteFile : DispatchProxy
{
    public bool Directory, Link;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_IsDirectory" => Directory,
        "get_IsSymbolicLink" => Link,
        _ => throw new NotSupportedException(method.Name)
    };
}
