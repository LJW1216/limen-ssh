using Renci.SshNet;

namespace Limen;

/// The one thing a delete needs from a shell, narrow enough to fake in a test.
public interface IRemoteShell
{
    ShellResult Run(string command);
}

public enum ShellState { NotStarted, Exited, Unknown }
public sealed record ShellResult(ShellState State, int? ExitCode = null, string Error = "");

/// Runs a shell command on the same host an SFTP session is already talking to.
///
/// SFTP has no recursive delete: the protocol offers only "remove this file"
/// and "remove this empty directory", so a client has to walk the whole tree,
/// paying a round trip per entry. Deleting a node_modules over a 20 ms link
/// takes twenty minutes that way. One `rm -rf` on the server takes seconds.
public sealed class RemoteShell(ConnectionInfo connectionInfo, string expectedFingerprint) : IRemoteShell, IDisposable
{
    private SshClient? _client;
    private bool _unavailable;
    private bool _disposed;

    /// Connects on first use and keeps the channel; returns false once the host
    /// has proven it cannot give us a shell, so callers stop retrying.
    private bool TryConnect()
    {
        if (_disposed || _unavailable) return false;
        if (string.IsNullOrEmpty(expectedFingerprint)) return false;
        if (_client is { IsConnected: true }) return true;

        try
        {
            _client?.Dispose();
            _client = new SshClient(connectionInfo);
            // This is an auxiliary connection to the already-approved SFTP
            // server. Never prompt to replace its key during a delete.
            _client.HostKeyReceived += (_, e) =>
                e.CanTrust = MatchesHostKey(expectedFingerprint, e.FingerPrintSHA256);
            _client.Connect();
            return true;
        }
        catch (Exception)
        {
            _client?.Dispose();
            _client = null;
            _unavailable = true;
            return false;
        }
    }

    internal static bool MatchesHostKey(string expected, string offered) =>
        expected.Length > "SHA256:".Length && expected == "SHA256:" + offered;

    // A timeout/disconnection after dispatch does not prove the process stopped.
    // Callers must never start another recursive delete for an Unknown result.
    public ShellResult Run(string command)
    {
        if (!TryConnect()) return new(ShellState.NotStarted);

        try
        {
            using var run = _client!.CreateCommand(command);
            run.CommandTimeout = TimeSpan.FromMinutes(10);
            run.Execute();
            return run.ExitStatus is { } code
                ? new(ShellState.Exited, code, run.Error.Trim())
                : new(ShellState.Unknown, Error: "No exit status received");
        }
        catch (Exception ex)
        {
            _unavailable = true;
            return new(ShellState.Unknown, Error: ex.Message);
        }
    }

    /// Single-quotes a path for POSIX shells: everything inside is literal, and
    /// an embedded quote is closed, escaped and reopened.
    public static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    /// Paths a recursive delete must never touch, however the caller got here.
    public static bool IsDangerous(string path)
    {
        var trimmed = path;
        if (trimmed.Length == 0 || trimmed is "/" or "~" or "." or "..") return true;
        if (trimmed.Contains('\n') || trimmed.Contains('\r')) return true;
        if (trimmed.Contains('*') || trimmed.Contains('?')) return true;
        if (!trimmed.StartsWith('/')) return true;
        if (trimmed.Contains('\0')) return true;
        if (trimmed.Split('/').Any(part => part is "." or "..")) return true;

        // A single top-level component — /etc, /usr, /home — is almost never
        // what someone means to delete from a file browser.
        return trimmed.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _client?.Disconnect(); } catch { }
        _client?.Dispose();
        _client = null;
    }
}
