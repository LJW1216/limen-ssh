using Renci.SshNet;

namespace Limen;

/// Runs a shell command on the same host an SFTP session is already talking to.
///
/// SFTP has no recursive delete: the protocol offers only "remove this file"
/// and "remove this empty directory", so a client has to walk the whole tree,
/// paying a round trip per entry. Deleting a node_modules over a 20 ms link
/// takes twenty minutes that way. One `rm -rf` on the server takes seconds.
public sealed class RemoteShell(ConnectionInfo connectionInfo) : IDisposable
{
    private SshClient? _client;
    private bool _unavailable;
    private bool _disposed;

    /// Connects on first use and keeps the channel; returns false once the host
    /// has proven it cannot give us a shell, so callers stop retrying.
    private bool TryConnect()
    {
        if (_disposed || _unavailable) return false;
        if (_client is { IsConnected: true }) return true;

        try
        {
            _client?.Dispose();
            _client = new SshClient(connectionInfo);
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

    /// True when the command ran and reported success. False means the caller
    /// should fall back — no shell, a restricted shell, or rm refused.
    public bool TryRun(string command, out string error)
    {
        error = string.Empty;
        if (!TryConnect()) return false;

        try
        {
            using var run = _client!.CreateCommand(command);
            run.CommandTimeout = TimeSpan.FromMinutes(10);
            run.Execute();
            if (run.ExitStatus == 0) return true;

            error = run.Error.Trim().Length > 0 ? run.Error.Trim() : $"exit {run.ExitStatus}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _unavailable = true;
            return false;
        }
    }

    /// Single-quotes a path for POSIX shells: everything inside is literal, and
    /// an embedded quote is closed, escaped and reopened.
    public static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    /// Paths a recursive delete must never touch, however the caller got here.
    public static bool IsDangerous(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0 || trimmed is "/" or "~" or "." or "..") return true;
        if (trimmed.Contains('\n') || trimmed.Contains('\r')) return true;
        if (trimmed.Contains('*') || trimmed.Contains('?')) return true;
        if (!trimmed.StartsWith('/')) return true;

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
