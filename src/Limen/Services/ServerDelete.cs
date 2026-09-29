using Renci.SshNet;
using System.IO;
using System.Text;

namespace Limen;

internal enum ServerDeleteResult { NotApplicable, DeletedFast, DeletedViaSftp }

internal static class ServerDelete
{
    // Only this exit status promises that no recursive deletion was started.
    internal const int PreflightRefused = 40;

    internal static ServerDeleteResult TryDelete(string path, ISftpClient client,
        IRemoteShell shell, Action<string> fallback)
    {
        if (RemoteShell.IsDangerous(path)) return ServerDeleteResult.NotApplicable;
        path = path.TrimEnd('/');
        try
        {
            var entry = client.Get(path);
            if (entry.IsSymbolicLink || !entry.IsDirectory) return ServerDeleteResult.NotApplicable;
        }
        catch { return ServerDeleteResult.NotApplicable; }

        var nonce = Guid.NewGuid().ToString("N");
        var markerName = ".limen-proof-" + nonce;
        var marker = path + "/" + markerName;
        var stage = path[..path.LastIndexOf('/')] + "/.limen-delete-" + Guid.NewGuid().ToString("N");
        var payload = stage + "/payload";
        var madeStage = false;
        try
        {
            using var content = new MemoryStream(Encoding.ASCII.GetBytes(nonce));
            client.UploadFile(content, marker, canOverride: false);
            client.CreateDirectory(stage);
            madeStage = true;
            client.ChangePermissions(stage, Convert.ToInt16("700", 8));
        }
        catch
        {
            try { client.DeleteFile(marker); } catch { }
            if (madeStage) { try { client.DeleteDirectory(stage); } catch { } }
            return ServerDeleteResult.NotApplicable;
        }

        // From the first rename attempt onwards, failure may mean the server
        // acted but its reply was lost. Never fall back to the original path.
        try
        {
            client.RenameFile(path, payload);
            VerifyMarker(client, payload, markerName, nonce);
            var result = shell.Run(BuildCommand(stage, markerName, nonce));
            if (result.State == ShellState.NotStarted ||
                result.State == ShellState.Exited && result.ExitCode == PreflightRefused)
            {
                VerifyMarker(client, payload, markerName, nonce);
                fallback(payload);
                client.DeleteDirectory(stage);
                return ServerDeleteResult.DeletedViaSftp;
            }
            if (result.State != ShellState.Exited || result.ExitCode != 0)
                throw new IOException($"{result.State}, exit {result.ExitCode}: {result.Error}");

            // rm only empties the pinned working directory. SFTP verifies the
            // same marker and removes the empty directory; never recurse here.
            VerifyMarker(client, payload, markerName, nonce);
            client.DeleteFile(payload + "/" + markerName);
            client.DeleteDirectory(payload);
            client.DeleteDirectory(stage);
            return ServerDeleteResult.DeletedFast;
        }
        catch (Exception ex)
        {
            throw new IOException(Strings.Format("Sftp.DeleteIncomplete", stage, ex.Message), ex);
        }
    }

    private static void VerifyMarker(ISftpClient client, string payload, string marker, string nonce)
    {
        var directory = client.Get(payload);
        if (!directory.IsDirectory || directory.IsSymbolicLink ||
            client.Get(payload + "/" + marker).IsSymbolicLink ||
            client.ReadAllText(payload + "/" + marker) != nonce)
            throw new IOException("Deletion target changed; marker verification failed.");
    }

    // The server hands an exec request to the account's login shell, which need
    // not be POSIX: csh and tcsh (still a BSD default) reject `$(…)` and `cd -P`,
    // and by the time the script runs the folder already sits in the stage. A
    // login shell only has to start sh with one single-quoted argument. csh
    // expands `!` even inside single quotes unless a blank follows it, so the
    // script keeps every `!` followed by a space; a parent directory whose own
    // name holds one still trips csh and ends on the reported-error path.
    internal static string BuildCommand(string stage, string marker, string nonce) =>
        "/bin/sh -c " + RemoteShell.Quote(BuildScript(stage, marker, nonce));

    internal static string BuildScript(string stage, string marker, string nonce)
    {
        var qStage = RemoteShell.Quote(stage);
        var qMarker = RemoteShell.Quote("./" + marker);
        // cd pins the actual directory even if an ancestor is renamed later.
        // find batches argv to avoid ARG_MAX and passes ./ names to rm. Symlinks
        // are removed, never traversed. Keep the proof until SFTP checks again.
        return $"test ! -L {qStage} && cd -P {qStage} && " +
            "test ! -L ./payload && cd -P ./payload && " +
            $"test ! -L {qMarker} && test -f {qMarker} && " +
            $"test \"$(cat {qMarker})\" = {RemoteShell.Quote(nonce)} && " +
            "command -v find >/dev/null && command -v rm >/dev/null " +
            $"|| exit {PreflightRefused}; " +
            $"find . -mindepth 1 -maxdepth 1 ! -name {RemoteShell.Quote(marker)} -exec rm -rf -- {{}} + " +
            "|| exit 41; exit 0";
    }
}
