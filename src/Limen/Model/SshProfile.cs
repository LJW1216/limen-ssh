using System.Text.Json.Serialization;

namespace Limen;

public enum AuthMode
{
    Password,
    PrivateKey
}

public sealed class SshProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string UserName { get; set; } = string.Empty;
    public AuthMode Auth { get; set; } = AuthMode.Password;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string LoginCommand { get; set; } = string.Empty;
    public string RemoteDirectory { get; set; } = string.Empty;
    public string LocalDirectory { get; set; } = string.Empty;
    public string HostKeyFingerprint { get; set; } = string.Empty;
    public string JumpProfileId { get; set; } = string.Empty;

    /// Hex tag colour, empty when untagged. Marks risky hosts everywhere the
    /// session shows up so production is never mistaken for staging.
    public string Color { get; set; } = string.Empty;
    public JumpHostProfile? JumpHost { get; set; }

    /// DPAPI ciphertext scoped to the current Windows user. Never plaintext on disk.
    public string ProtectedPassword { get; set; } = string.Empty;
    public string ProtectedPassphrase { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasStoredPassword => ProtectedPassword.Length > 0;

    [JsonIgnore]
    public string Target => $"{(UserName.Length == 0 ? "?" : UserName)}@{Host}:{Port}";

    [JsonIgnore]
    public string RoutedTarget => JumpHost is null ? Target : $"{Target}  (via {JumpHost.Target})";

    [JsonIgnore]
    public string DisplayPath => Folder.Length == 0 ? Name : $"{Folder} / {Name}";

    public SshProfile Clone()
    {
        var clone = (SshProfile)MemberwiseClone();
        clone.JumpHost = JumpHost?.Clone();
        return clone;
    }

    public bool Matches(string query) => query.Length == 0
        || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Folder.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || Host.Contains(query, StringComparison.OrdinalIgnoreCase)
        || UserName.Contains(query, StringComparison.OrdinalIgnoreCase);
}

public sealed class JumpHostProfile
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string UserName { get; set; } = string.Empty;
    public AuthMode Auth { get; set; } = AuthMode.Password;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string ProtectedPassword { get; set; } = string.Empty;
    public string ProtectedPassphrase { get; set; } = string.Empty;
    public string HostKeyFingerprint { get; set; } = string.Empty;

    [JsonIgnore]
    public string Target => $"{(UserName.Length == 0 ? "?" : UserName)}@{Host}:{Port}";

    public JumpHostProfile Clone() => (JumpHostProfile)MemberwiseClone();
}
