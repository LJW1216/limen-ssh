using Renci.SshNet;
using Renci.SshNet.Common;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Limen;

public sealed class SshCredentials
{
    public string Password { get; set; } = string.Empty;
    public string Passphrase { get; set; } = string.Empty;
    public bool Remember { get; set; }
}

public sealed class SshConnectionRoute(ConnectionInfo connectionInfo, SshClient? jumpClient = null, ForwardedPortLocal? forwardedPort = null) : IDisposable
{
    public ConnectionInfo ConnectionInfo { get; } = connectionInfo;

    public void Dispose()
    {
        try { forwardedPort?.Stop(); } catch { }
        forwardedPort?.Dispose();
        jumpClient?.Dispose();
    }
}

/// Builds authenticated SSH.NET connections from a profile and enforces
/// host-key pinning.
public sealed class SshConnector(ProfileStore store, Window owner)
{
    private readonly Dispatcher _dispatcher = owner.Dispatcher;

    /// Collects everything needed to authenticate, prompting only for what is
    /// not already stored. Returns null when the user cancels.
    public SshCredentials? Resolve(SshProfile profile)
    {
        var credentials = new SshCredentials
        {
            Password = Secret.Unprotect(profile.ProtectedPassword),
            Passphrase = Secret.Unprotect(profile.ProtectedPassphrase)
        };

        if (profile.Auth == AuthMode.Password && credentials.Password.Length == 0)
        {
            var prompt = new PromptWindow(
                Strings.Get("Connector.Password"),
                Strings.Format("Connector.AskPassword", profile.Target),
                canRemember: true) { Owner = owner };
            if (prompt.ShowDialog() != true) return null;
            credentials.Password = prompt.Value;
            credentials.Remember = prompt.Remember;
        }

        if (profile.Auth == AuthMode.PrivateKey && !File.Exists(profile.PrivateKeyPath))
            throw new FileNotFoundException(Strings.Get("Connector.NoKeyFile"), profile.PrivateKeyPath);

        return credentials;
    }

    public SshCredentials? ResolveJump(SshProfile profile)
    {
        var jump = store.ResolveJumpHost(profile);
        if (jump is null) return new SshCredentials();

        var credentials = new SshCredentials
        {
            Password = Secret.Unprotect(jump.ProtectedPassword),
            Passphrase = Secret.Unprotect(jump.ProtectedPassphrase)
        };
        if (jump.Auth == AuthMode.Password && credentials.Password.Length == 0)
        {
            var prompt = new PromptWindow(Strings.Get("Connector.BastionPassword"), Strings.Format("Connector.AskPassword", jump.Target), canRemember: true)
            {
                Owner = owner
            };
            if (prompt.ShowDialog() != true) return null;
            credentials.Password = prompt.Value;
            credentials.Remember = prompt.Remember;
        }
        if (jump.Auth == AuthMode.PrivateKey && !File.Exists(jump.PrivateKeyPath))
            throw new FileNotFoundException(Strings.Get("Connector.NoBastionKeyFile"), jump.PrivateKeyPath);
        return credentials;
    }

    public ConnectionInfo Build(SshProfile profile, SshCredentials credentials, string? host = null, int? port = null)
    {
        var methods = BuildAuthenticationMethods(profile, profile.UserName, profile.Auth, profile.PrivateKeyPath, credentials);
        return new ConnectionInfo(host ?? profile.Host, port ?? profile.Port, profile.UserName, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public SshConnectionRoute OpenRoute(SshProfile profile, SshCredentials targetCredentials, SshCredentials? jumpCredentials)
    {
        var jump = store.ResolveJumpHost(profile);
        if (jump is null) return new SshConnectionRoute(Build(profile, targetCredentials));
        if (jumpCredentials is null) throw new InvalidOperationException(Strings.Get("Connector.NoBastionCredentials"));

        SshClient? jumpClient = null;
        ForwardedPortLocal? forwardedPort = null;
        try
        {
            var methods = BuildAuthenticationMethods(profile, jump.UserName, jump.Auth, jump.PrivateKeyPath, jumpCredentials);
            var connectionInfo = new ConnectionInfo(jump.Host, jump.Port, jump.UserName, [.. methods])
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            jumpClient = new SshClient(connectionInfo) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            PinJumpHostKey(jumpClient, profile);
            jumpClient.Connect();

            forwardedPort = new ForwardedPortLocal("127.0.0.1", 0, profile.Host, (uint)profile.Port);
            jumpClient.AddForwardedPort(forwardedPort);
            forwardedPort.Start();
            var target = Build(profile, targetCredentials, "127.0.0.1", checked((int)forwardedPort.BoundPort));
            return new SshConnectionRoute(target, jumpClient, forwardedPort);
        }
        catch (Exception ex)
        {
            try { forwardedPort?.Stop(); } catch { }
            forwardedPort?.Dispose();
            jumpClient?.Dispose();
            throw new InvalidOperationException(
                Strings.Format("Connector.BastionFailed", jump.Target) + "\n" + ex.Message, ex);
        }
    }

    private List<AuthenticationMethod> BuildAuthenticationMethods(
        SshProfile profile, string userName, AuthMode auth, string privateKeyPath, SshCredentials credentials)
    {
        if (auth == AuthMode.PrivateKey)
            return [new PrivateKeyAuthenticationMethod(userName, LoadKey(privateKeyPath, credentials))];
        return
        [
            new PasswordAuthenticationMethod(userName, credentials.Password),
            BuildKeyboardInteractive(profile, userName, credentials)
        ];
    }

    private PrivateKeyFile LoadKey(string privateKeyPath, SshCredentials credentials)
    {
        try
        {
            return credentials.Passphrase.Length == 0
                ? new PrivateKeyFile(privateKeyPath)
                : new PrivateKeyFile(privateKeyPath, credentials.Passphrase);
        }
        catch (Exception e) when (e is SshPassPhraseNullOrEmptyException or SshException)
        {
            // Build 는 백그라운드 스레드에서 호출되므로 창은 UI 스레드에서 띄운다.
            var entered = _dispatcher.Invoke(() =>
            {
                var prompt = new PromptWindow(
                    Strings.Get("Connector.Passphrase"),
                    Strings.Format("Connector.AskPassphrase", Path.GetFileName(privateKeyPath)),
                    canRemember: true) { Owner = owner };
                return prompt.ShowDialog() == true ? (prompt.Value, prompt.Remember) : (null, false);
            });
            if (entered.Item1 is null) throw;
            credentials.Passphrase = entered.Item1;
            credentials.Remember = entered.Item2;
            return new PrivateKeyFile(privateKeyPath, credentials.Passphrase);
        }
    }

    /// Servers that only offer keyboard-interactive still ask for the password
    /// here; anything else (OTP, token) is forwarded to the user.
    private KeyboardInteractiveAuthenticationMethod BuildKeyboardInteractive(SshProfile profile, string userName, SshCredentials credentials)
    {
        var method = new KeyboardInteractiveAuthenticationMethod(userName);
        method.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (!prompt.IsEchoed && prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    prompt.Response = credentials.Password;
                    continue;
                }
                prompt.Response = AskOnUiThread(profile, prompt.Request, prompt.IsEchoed);
            }
        };
        return method;
    }

    private string AskOnUiThread(SshProfile profile, string request, bool echoed) =>
        _dispatcher.Invoke(() =>
        {
            var prompt = new PromptWindow(Strings.Format("Connector.ExtraAuth", profile.Name), request.Trim(), canRemember: false, masked: !echoed)
            {
                Owner = owner
            };
            return prompt.ShowDialog() == true ? prompt.Value : string.Empty;
        });

    /// Pins the server key on first connect and blocks silent key changes.
    public void PinHostKey(BaseClient client, SshProfile profile)
        => PinHostKey(client, profile, profile.Host, profile.Port,
            () => profile.HostKeyFingerprint,
            fingerprint => profile.HostKeyFingerprint = fingerprint);

    private void PinJumpHostKey(BaseClient client, SshProfile profile)
    {
        var jump = store.ResolveJumpHost(profile)!;
        var source = store.GetJumpProfile(profile);
        PinHostKey(client, profile, jump.Host, jump.Port,
            () => source?.HostKeyFingerprint ?? profile.JumpHost?.HostKeyFingerprint ?? string.Empty,
            fingerprint =>
            {
                if (source is not null) source.HostKeyFingerprint = fingerprint;
                if (profile.JumpHost is not null) profile.JumpHost.HostKeyFingerprint = fingerprint;
            });
    }

    private void PinHostKey(BaseClient client, SshProfile profile, string host, int port,
        Func<string> getFingerprint, Action<string> setFingerprint)
    {
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = $"SHA256:{e.FingerPrintSHA256}";
            if (getFingerprint() == fingerprint)
            {
                e.CanTrust = true;
                return;
            }

            var currentFingerprint = getFingerprint();
            var first = currentFingerprint.Length == 0;
            var message = first
                ? Strings.Format("Connector.HostKeyBody", host, port) + "\n\n" + Strings.Format("Connector.HostKeyFingerprint", e.HostKeyName) + "\n" + fingerprint + "\n\n" + Strings.Get("Connector.HostKeyTrustAsk")
                : Strings.Format("Connector.HostKeyChanged", host, port) + "\n\n" + Strings.Get("Connector.HostKeyStored") + "\n" + currentFingerprint + "\n\n" + Strings.Get("Connector.HostKeyOffered") + "\n" + fingerprint + "\n\n" + Strings.Get("Connector.HostKeyMitm");

            e.CanTrust = _dispatcher.Invoke(() => MessageBox.Show(
                owner, message, first ? Strings.Get("Connector.HostKeyTitle") : Strings.Get("Connector.HostKeyChangedTitle"),
                MessageBoxButton.YesNo,
                first ? MessageBoxImage.Question : MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes);

            if (!e.CanTrust) return;
            setFingerprint(fingerprint);
            _dispatcher.Invoke(() => store.AddOrUpdate(profile));
        };
    }

    /// Persists credentials the user asked to remember, after a connection succeeded.
    public void Remember(SshProfile profile, SshCredentials credentials)
    {
        if (!credentials.Remember) return;
        if (credentials.Password.Length > 0) profile.ProtectedPassword = Secret.Protect(credentials.Password);
        if (credentials.Passphrase.Length > 0) profile.ProtectedPassphrase = Secret.Protect(credentials.Passphrase);
        credentials.Remember = false;
        store.AddOrUpdate(profile);
    }

    public void RememberJump(SshProfile profile, SshCredentials credentials)
    {
        var source = store.GetJumpProfile(profile);
        var jump = source is null ? profile.JumpHost : null;
        if (source is not null && credentials.Remember)
        {
            if (credentials.Password.Length > 0) source.ProtectedPassword = Secret.Protect(credentials.Password);
            if (credentials.Passphrase.Length > 0) source.ProtectedPassphrase = Secret.Protect(credentials.Passphrase);
            credentials.Remember = false;
            store.AddOrUpdate(source);
            return;
        }
        if (jump is null || !credentials.Remember) return;
        if (credentials.Password.Length > 0) jump.ProtectedPassword = Secret.Protect(credentials.Password);
        if (credentials.Passphrase.Length > 0) jump.ProtectedPassphrase = Secret.Protect(credentials.Passphrase);
        credentials.Remember = false;
        store.AddOrUpdate(profile);
    }
}
